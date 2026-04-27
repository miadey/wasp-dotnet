#!/usr/bin/env python3
"""inject_log_handler_dump — wat-only patch that prepends a dump of
all 5 args to mono's fatal-log handler (fn 6972 in c2e5f83 builds).

The handler signature is (domain, level, message, fatal, user_data).
We prepend instructions at the start of its body that:
  1. Compute the message strlen (g7+message, scan until NUL).
  2. Call wasp_debug_print(g7+message, len) so the trap log shows
     the actual message string content.
  3. Also dump level/fatal as a small hex line.

Locates fn 6972 by body fingerprint: a 5-arg fn whose body is
exactly `local.get 0..4; call <X>; local.get 3; if ... call <Y> ...`
where <Y> is the `exit` export.

This is wat-level surgery — no Rust rebuild — so existing function
indices stay stable.

Usage:
  inject_log_handler_dump.py <in.wasm> <out.wasm>
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
            print("no `exit` export", file=sys.stderr)
            return 1
        exit_fn = int(m.group(1))

        m = re.search(r'\(export "wasp_debug_print" \(func (\d+)\)\)', text)
        if not m:
            print("no `wasp_debug_print` export", file=sys.stderr)
            return 1
        wasp_print_fn = int(m.group(1))

        # Find the fatal-log handler: a 5-arg func whose body forwards
        # all 5 locals to a single call, then conditionally calls exit
        # if param 3 (fatal) is non-zero.
        handler_re = re.compile(
            r'^  \(func \(;(\d+);\) \(type \d+\) \(param i32 i32 i32 i32 i32\)\s*$'
            r'(?P<body>(?:.|\n)*?)\n  \)\n',
            re.MULTILINE,
        )
        target_fn = None
        for hm in handler_re.finditer(text):
            body = hm.group('body')
            if (
                'local.get 0\n    local.get 1\n    local.get 2\n    local.get 3\n    local.get 4'
                in body
                and f'call {exit_fn}' in body
                and 'local.get 3' in body
            ):
                target_fn = int(hm.group(1))
                target_match = hm
                break

        if target_fn is None:
            print("no fatal-log handler matching fingerprint", file=sys.stderr)
            return 1

        # We need a NUL-scanning routine. Inject TWO new helper
        # functions at the end:
        #   * $strlen_at(p) → (i32) returning length of NUL-terminated
        #     string at absolute address p.
        #   * $dump_msg(p) → () that computes len then calls
        #     wasp_debug_print(p, len).
        fn_re = re.compile(r"^  \(func \(;(\d+);\)", re.MULTILINE)
        highest = -1
        for fm in fn_re.finditer(text):
            highest = max(highest, int(fm.group(1)))
        strlen_fn = highest + 1
        dump_msg_fn = highest + 2

        strlen_body = f"""
  (func (;{strlen_fn};) (type 6) (param i32) (result i32)
    (local i32)
    i32.const 0
    local.set 1
    block ;; label = @1
      loop ;; label = @2
        local.get 0
        local.get 1
        i32.add
        i32.load8_u
        i32.eqz
        br_if 1 (;@1;)
        local.get 1
        i32.const 1
        i32.add
        local.set 1
        local.get 1
        i32.const 4096
        i32.eq
        br_if 1 (;@1;)
        br 0 (;@2;)
      end
    end
    local.get 1
  )"""

        dump_msg_body = f"""
  (func (;{dump_msg_fn};) (type 5) (param i32)
    local.get 0
    local.get 0
    call {strlen_fn}
    call {wasp_print_fn}
  )"""

        # Insert helper funcs before (data (;0;) ...).
        # IMPORTANT: do this BEFORE we patch the target fn body so the
        # patched instructions can reference the new fn indices.
        insert_at = text.find('\n  (data (;0;)')
        if insert_at < 0:
            print("no data section boundary", file=sys.stderr)
            return 1
        with_helpers = text[:insert_at] + strlen_body + dump_msg_body + text[insert_at:]

        # Now patch target_fn's body. Re-find it (positions shifted).
        target_re = re.compile(
            r'^(  \(func \(;' + str(target_fn) + r';\) \(type \d+\) \(param i32 i32 i32 i32 i32\))\s*$',
            re.MULTILINE,
        )
        m = target_re.search(with_helpers)
        if not m:
            print(f"could not relocate fn {target_fn} after helper insert", file=sys.stderr)
            return 1

        # Inject right after the header line: read message arg (param 2),
        # add g7, call dump_msg.
        inject = (
            "\n    ;; --- log_handler_dump diagnostic ---\n"
            "    global.get 7\n"
            "    local.get 2\n"
            "    i32.add\n"
            f"    call {dump_msg_fn}"
        )
        patched = with_helpers[:m.end()] + inject + with_helpers[m.end():]

        out_wat.write_text(patched)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  injected log_handler_dump: target fn {target_fn}, "
            f"strlen fn {strlen_fn}, dump fn {dump_msg_fn} → {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
