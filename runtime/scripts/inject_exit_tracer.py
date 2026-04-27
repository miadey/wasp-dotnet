#!/usr/bin/env python3
"""inject_exit_tracer — post-merge wat surgery that:

  1. Injects two NEW functions appended at the end:
       * `$exit_print_marker` (i32) → ()
         — writes a fixed marker string to memory then calls
           ic0.debug_print so each call emits "[exit-site#N]".
       * `$exit_with_site(i32, i32)` (param i32 i32) (result -)
         — wrapper that calls $exit_print_marker(site_id) then exit(code).
  2. Replaces every `call <exit>` site with `i32.const N; call <wrapper>`.

Each rewritten exit site emits a uniquely-numbered `[exit-site#N]`
log line *before* the trap fires, so we can pin down WHICH g_assert
or runtime exit() actually fired.

This injector touches only the wat — no Rust rebuild needed — so it
does not shift any pre-existing function indices (it only appends).

Usage:
  inject_exit_tracer.py <in.wasm> <out.wasm>

Requirements: the wasm must already export `wasp_debug_print` (which
forwards to ic0.debug_print). We re-use that helper instead of
importing ic0 directly so the injector stays type-agnostic.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        m = re.search(r'\(export "exit" \(func (\d+)\)\)', text)
        if not m:
            print("no `exit` export found", file=sys.stderr)
            return 1
        exit_fn = int(m.group(1))

        m = re.search(r'\(export "wasp_debug_print" \(func (\d+)\)\)', text)
        if not m:
            print("no `wasp_debug_print` export found", file=sys.stderr)
            return 1
        wasp_print_fn = int(m.group(1))

        # Collect all func indices to determine the next free index.
        fn_re = re.compile(r"^  \(func \(;(\d+);\)", re.MULTILINE)
        highest = -1
        for fm in fn_re.finditer(text):
            highest = max(highest, int(fm.group(1)))
        marker_fn = highest + 1
        wrapper_fn = highest + 2

        # We need a writable scratch region. Use a fresh data segment at
        # a fixed address well above dotnet's image (use linear-mem
        # offset 0x00200000 which is in wasp_canister's own static area
        # and unlikely to collide). Actually safer: keep buffer in
        # .data via a new (data ...) declaration anchored to a constant
        # address. Use 0x00010000 (64KB into wasp linear mem; tiny
        # buffer). Pre-write "[exit-site#" prefix and reserve room for
        # 5-digit decimal + "]\0".
        scratch_addr = 0x10000
        prefix = b"[exit-site#"
        # Format will be: prefix + decimal(site_id) + "]" written
        # dynamically inside marker_fn. Start with prefix as a (data)
        # segment so we don't have to write it at runtime.
        prefix_data = ''.join(f"\\{b:02x}" for b in prefix)
        new_data = (
            f'\n  (data $exit_marker (i32.const {scratch_addr}) "{prefix_data}")'
        )

        # marker_fn (i32) — write site_id decimal after prefix, append "]",
        # call wasp_debug_print(scratch_addr, total_len).
        marker_body = f"""
  (func (;{marker_fn};) (type 5) (param i32)
    (local i32 i32 i32)
    ;; locals: 1=cursor, 2=tmp digit count, 3=value
    i32.const {scratch_addr + len(prefix)}
    local.set 1                      ;; cursor = scratch+prefix_len
    local.get 0
    local.set 3                      ;; value = site_id
    ;; if value == 0, write '0' and skip loop
    local.get 3
    i32.eqz
    if ;; label = @1
      local.get 1
      i32.const 48
      i32.store8
      local.get 1
      i32.const 1
      i32.add
      local.set 1
    else
      ;; reverse-write decimal digits into a small staging area
      ;; at scratch_addr+32 so we can flip them.
      i32.const 0
      local.set 2                    ;; digit count
      block ;; label = @2
        loop ;; label = @3
          local.get 3
          i32.eqz
          br_if 1 (;@2;)
          i32.const {scratch_addr + 32}
          local.get 2
          i32.add
          local.get 3
          i32.const 10
          i32.rem_u
          i32.const 48
          i32.add
          i32.store8
          local.get 3
          i32.const 10
          i32.div_u
          local.set 3
          local.get 2
          i32.const 1
          i32.add
          local.set 2
          br 0 (;@3;)
        end
      end
      ;; copy reversed digits forward into cursor
      block ;; label = @4
        loop ;; label = @5
          local.get 2
          i32.eqz
          br_if 1 (;@4;)
          local.get 2
          i32.const 1
          i32.sub
          local.set 2
          local.get 1
          i32.const {scratch_addr + 32}
          local.get 2
          i32.add
          i32.load8_u
          i32.store8
          local.get 1
          i32.const 1
          i32.add
          local.set 1
          br 0 (;@5;)
        end
      end
    end
    ;; append ']'
    local.get 1
    i32.const 93
    i32.store8
    local.get 1
    i32.const 1
    i32.add
    local.set 1
    ;; call wasp_debug_print(scratch_addr, cursor - scratch_addr)
    i32.const {scratch_addr}
    local.get 1
    i32.const {scratch_addr}
    i32.sub
    call {wasp_print_fn}
  )"""

        # Wrapper calls marker(site_id) then exit(code).
        wrapper_body = f"""
  (func (;{wrapper_fn};) (param i32 i32)
    local.get 1
    call {marker_fn}
    local.get 0
    call {exit_fn}
  )"""

        # IMPORTANT: rewrite all `call exit_fn` sites BEFORE we splice
        # in the wrapper body — otherwise the regex would also rewrite
        # the `call exit_fn` inside the wrapper itself, turning it into
        # an infinite self-recursion.
        site_counter = [0]
        call_re = re.compile(r'^(\s+)call ' + str(exit_fn) + r'\s*$', re.MULTILINE)

        def rewrite(m):
            indent = m.group(1)
            site_counter[0] += 1
            n = site_counter[0]
            return f"{indent}i32.const {n}\n{indent}call {wrapper_fn}"

        rewritten = call_re.sub(rewrite, text)

        # Insert new fns AT THE END of the function section so existing
        # function indices are preserved. wasm-tools print orders the wat
        # as: types, imports, table, memory, globals, exports, funcs,
        # data. The (data (;0;) ...) declaration marks the boundary
        # right after the last func.
        insert_at = rewritten.find('\n  (data (;0;)')
        if insert_at < 0:
            print("no `(data (;0;)` boundary found", file=sys.stderr)
            return 1
        new_text = rewritten[:insert_at] + marker_body + wrapper_body + new_data + rewritten[insert_at:]

        out_wat.write_text(new_text)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  injected exit tracer: marker fn {marker_fn}, wrapper fn {wrapper_fn}; "
            f"tagged {site_counter[0]} call-exit sites → {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
