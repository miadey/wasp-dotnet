#!/usr/bin/env python3
"""inject_call_indirect_trace — instrument every `call_indirect` inside
a target function to print the target function index (as 8 hex chars)
via ic0.debug_print just before the actual indirect call.

Usage:
  inject_call_indirect_trace.py <in.wasm> <out.wasm> <fn_idx>

Output line per site (visible in dfx logs):
  fnidx=AABBCCDD\n        (where AABBCCDD is the call_indirect target)

We also tag each site with a marker "site<N>:" prefix so multiple
call_indirect inside the same fn can be distinguished.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

# Per-function scratch base (must be in the gap between wasp_canister
# data (~1 MB) and dotnet's data (~2.7 MB)).
SCRATCH_BASE_BASE = 1_500_000


def main():
    if len(sys.argv) != 4:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    fn_idx = int(sys.argv[3])

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        # Find ic0.debug_print fn idx.
        m = re.search(r'\(import "ic0" "debug_print" \(func \(;(\d+);\)', text)
        if not m:
            print("no ic0.debug_print import found", file=sys.stderr)
            return 1
        debug_print_idx = int(m.group(1))
        print(f"  ic0.debug_print = fn {debug_print_idx}", file=sys.stderr)

        # Locate target function body.
        marker = f"  (func (;{fn_idx};)"
        start = text.find(marker)
        if start < 0:
            print(f"no func {fn_idx}", file=sys.stderr)
            return 1
        end = text.find("\n  )\n", start)
        if end < 0:
            print(f"no end for func {fn_idx}", file=sys.stderr)
            return 1
        end += len("\n  )")

        body = text[start:end]

        # Parse the existing `(local i32 i32 ...)` line so we can extend it
        # with two new i32 locals: FNIDX_SAVE and TMP.
        # The function may have NO local declaration — handle that too.
        local_line_re = re.compile(r"\n    \(local ([^)]*)\)\n")
        lm = local_line_re.search(body)
        if lm:
            existing_locals = lm.group(1).strip().split()
            # existing_locals is e.g. ["i32", "i32", ...]
            num_existing = len(existing_locals)
            new_locals = existing_locals + ["i32", "i32"]
            new_local_line = f"\n    (local {' '.join(new_locals)})\n"
            body = body[: lm.start()] + new_local_line + body[lm.end() :]
            # Param count: extract from header (param i32 ...)
            header_re = re.compile(r"\(func \(;\d+;\) \(type \d+\) \(param ([^)]*)\)")
            hm = header_re.search(body)
            num_params = len(hm.group(1).strip().split()) if hm else 0
            FNIDX_SAVE = num_params + num_existing
            TMP = num_params + num_existing + 1
        else:
            # Inject a brand new locals line right after the (func ...) header.
            header_end = body.find(")\n", body.find("(func (;")) + 1
            new_local_line = "\n    (local i32 i32)"
            body = body[: header_end] + new_local_line + body[header_end :]
            header_re = re.compile(r"\(func \(;\d+;\) \(type \d+\) \(param ([^)]*)\)")
            hm = header_re.search(body)
            num_params = len(hm.group(1).strip().split()) if hm else 0
            FNIDX_SAVE = num_params
            TMP = num_params + 1

        print(f"  fn {fn_idx}: using locals FNIDX_SAVE={FNIDX_SAVE} TMP={TMP}", file=sys.stderr)

        scratch_base = SCRATCH_BASE_BASE + (fn_idx % 256) * 4096

        # Layout per call_indirect site (each 32 bytes apart):
        #   0..5  : "siteNN:fnidx=" (13 bytes — but pad to fixed)
        #   actually we'll use: "siteNN:fnidx=AABBCCDD\n" = 22 bytes
        #     site marker "siteNN:" 7 bytes (NN is decimal 2 digits)
        #     "fnidx=" 6 bytes
        #     8 hex chars
        #     "\n" 1 byte
        #   total = 22 bytes
        # Sites at scratch_base + site_idx*32.

        # Find every "call_indirect (type N)" occurrence and instrument.
        # Pattern matches: `      call_indirect (type 6)` (with leading whitespace).
        ci_pattern = re.compile(r"^(\s*)call_indirect \(type (\d+)\)$", re.MULTILINE)

        site_idx = 0
        marker_strs = []  # (offset, bytes) for static "siteNN:fnidx=" + trailing "\n"

        def gen_print_seq(indent: str, site_off: int, site_label: int) -> str:
            """Emit the wat sequence to:
              1. consume top-of-stack (the call_indirect target index)
              2. save it to local FNIDX_SAVE
              3. write 8 hex chars at site_off+13 .. site_off+20
              4. call ic0.debug_print(site_off, 22)
              5. push it back for the actual call_indirect
            """
            lines = []
            # save fnidx
            lines.append(f"{indent}local.set {FNIDX_SAVE}")

            # generate hex digits for nibbles 7..0 (high to low),
            # writing chars at site_off+13 .. site_off+20
            for i in range(8):
                shift = (7 - i) * 4
                char_off = site_off + 13 + i
                # nibble = (fnidx >> shift) & 0xf
                lines.append(f"{indent}i32.const 0  ;; ptr base for store8")
                lines.append(f"{indent}local.get {FNIDX_SAVE}")
                lines.append(f"{indent}i32.const {shift}")
                lines.append(f"{indent}i32.shr_u")
                lines.append(f"{indent}i32.const 15")
                lines.append(f"{indent}i32.and")
                lines.append(f"{indent}local.set {TMP}")
                lines.append(f"{indent}i32.const 87   ;; 'a' - 10")
                lines.append(f"{indent}i32.const 48   ;; '0'")
                lines.append(f"{indent}local.get {TMP}")
                lines.append(f"{indent}i32.const 9")
                lines.append(f"{indent}i32.gt_u")
                lines.append(f"{indent}select")
                lines.append(f"{indent}local.get {TMP}")
                lines.append(f"{indent}i32.add")
                lines.append(f"{indent}i32.store8 offset={char_off}")
            # debug_print(site_off, 22)
            lines.append(f"{indent}i32.const {site_off}")
            lines.append(f"{indent}i32.const 22")
            lines.append(f"{indent}call {debug_print_idx}")
            # push fnidx back for the call_indirect
            lines.append(f"{indent}local.get {FNIDX_SAVE}")
            return "\n".join(lines) + "\n"

        def repl(m):
            nonlocal site_idx
            indent = m.group(1)
            type_n = m.group(2)
            site_off = scratch_base + site_idx * 32
            # Build static template: "siteNN:fnidx=........\n" (22 bytes)
            label = f"site{site_idx:02d}"
            # Pad/truncate label to 6 chars exactly so siteNN: stays 7 bytes
            assert len(label) == 6, f"label {label!r} not 6 bytes"
            template = f"{label}:fnidx=........\n".encode("ascii")
            assert len(template) == 22, f"template len {len(template)}"
            marker_strs.append((site_off, template))
            seq = gen_print_seq(indent, site_off, site_idx)
            site_idx += 1
            return seq + f"{indent}call_indirect (type {type_n})"

        new_body = ci_pattern.sub(repl, body)

        # Splice back
        new_text = text[:start] + new_body + text[end:]

        # Build static data inits for the templates.
        data_inits = ""
        for offset, msg in marker_strs:
            wat_str = ""
            for b in msg:
                if 0x20 <= b < 0x7F and b not in (0x22, 0x5C):
                    wat_str += chr(b)
                else:
                    wat_str += f"\\{b:02x}"
            data_inits += f'  (data (i32.const {offset}) "{wat_str}")\n'

        last_data = new_text.rfind("\n  (data ")
        if last_data >= 0:
            line_end = new_text.find("\n", last_data + 1)
            new_text = new_text[: line_end + 1] + data_inits + new_text[line_end + 1 :]
        else:
            module_close = new_text.rfind("\n)")
            new_text = new_text[:module_close] + "\n" + data_inits + new_text[module_close:]

        out_wat.write_text(new_text)
        print(f"  instrumented {site_idx} call_indirect sites in fn {fn_idx}", file=sys.stderr)

        try:
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True
            )
        except subprocess.CalledProcessError:
            print("wasm-tools parse failed", file=sys.stderr)
            return 1
        print(f"  → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
