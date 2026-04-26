#!/usr/bin/env python3
"""inject_post_call_dump — instrument the call sites of a target
function so that AFTER specific call N's, print the call's return
value (top of stack just after the call) as 8 hex chars.

Usage:
  inject_post_call_dump.py <in.wasm> <out.wasm> <fn_idx> <callee_idx>[,<callee_idx>...]

For each `call <callee>` matched inside the target function, AFTER the
call returns, the script:
  1. dups the return value (uses local.tee into a fresh local)
  2. converts to 8 hex chars in a per-site scratch buffer
  3. calls ic0.debug_print(buf, 22)

This requires the called function to RETURN exactly one i32; otherwise
the stack would be malformed. Caller checks the wat first.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

SCRATCH_BASE_BASE = 1_500_000


def main():
    if len(sys.argv) != 5:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    fn_idx = int(sys.argv[3])
    targets = set(int(x) for x in sys.argv[4].split(","))

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        m = re.search(r'\(import "ic0" "debug_print" \(func \(;(\d+);\)', text)
        if not m:
            print("no ic0.debug_print", file=sys.stderr)
            return 1
        debug_print_idx = int(m.group(1))

        marker = f"  (func (;{fn_idx};)"
        start = text.find(marker)
        if start < 0:
            print(f"no func {fn_idx}", file=sys.stderr)
            return 1
        end = text.find("\n  )\n", start)
        if end < 0:
            return 1
        end += len("\n  )")
        body = text[start:end]

        # Param count
        header_re = re.compile(r"\(func \(;\d+;\) \(type \d+\) \(param ([^)]*)\)")
        hm = header_re.search(body)
        num_params = len(hm.group(1).strip().split()) if hm else 0

        # Add 2 new locals: VAL_SAVE, NIB
        local_line_re = re.compile(r"\n    \(local ([^)]*)\)\n")
        lm = local_line_re.search(body)
        if lm:
            existing = lm.group(1).strip().split()
            num_existing = len(existing)
            new_locals = existing + ["i32", "i32"]
            body = (body[: lm.start()]
                    + f"\n    (local {' '.join(new_locals)})\n"
                    + body[lm.end() :])
            VAL = num_params + num_existing
            NIB = VAL + 1
        else:
            hdr_end = body.find(")\n", body.find("(func (;")) + 2
            body = body[:hdr_end] + "    (local i32 i32)\n" + body[hdr_end:]
            VAL = num_params
            NIB = num_params + 1

        scratch_off = SCRATCH_BASE_BASE + (fn_idx % 256) * 4096

        # Find all `call N` sites and instrument the matching ones.
        call_pattern = re.compile(r"^(\s*)call (\d+)$", re.MULTILINE)
        site_idx = 0
        marker_strs = []  # (offset, template)

        def gen_post_print(indent: str, callee: int, site_off: int, label: bytes) -> str:
            lines = []
            # Stack now has return value on top. Save it via local.tee.
            lines.append(f"{indent}local.tee {VAL}")
            for i in range(8):
                shift = (7 - i) * 4
                char_off = site_off + len(label) + i
                lines.append(f"{indent}i32.const 0")
                lines.append(f"{indent}local.get {VAL}")
                lines.append(f"{indent}i32.const {shift}")
                lines.append(f"{indent}i32.shr_u")
                lines.append(f"{indent}i32.const 15")
                lines.append(f"{indent}i32.and")
                lines.append(f"{indent}local.set {NIB}")
                lines.append(f"{indent}i32.const 87")
                lines.append(f"{indent}i32.const 48")
                lines.append(f"{indent}local.get {NIB}")
                lines.append(f"{indent}i32.const 9")
                lines.append(f"{indent}i32.gt_u")
                lines.append(f"{indent}select")
                lines.append(f"{indent}local.get {NIB}")
                lines.append(f"{indent}i32.add")
                lines.append(f"{indent}i32.store8 offset={char_off}")
            tot_len = len(label) + 8 + 1  # + "\n"
            lines.append(f"{indent}i32.const {site_off}")
            lines.append(f"{indent}i32.const {tot_len}")
            lines.append(f"{indent}call {debug_print_idx}")
            return "\n".join(lines) + "\n"

        def repl(m):
            nonlocal site_idx
            indent = m.group(1)
            callee = int(m.group(2))
            if callee not in targets:
                return m.group(0)  # leave unchanged
            site_off = scratch_off + site_idx * 32
            label = f"call{callee:05d}=".encode("ascii")
            template = label + b"........\n"
            assert len(template) == len(label) + 9
            marker_strs.append((site_off, template))
            seq = gen_post_print(indent, callee, site_off, label)
            site_idx += 1
            return f"{indent}call {callee}\n" + seq.rstrip("\n")

        new_body = call_pattern.sub(repl, body)
        new_text = text[:start] + new_body + text[end:]

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
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print("wasm-tools parse failed", file=sys.stderr)
            return 1
        print(f"  fn {fn_idx} instrumented {site_idx} post-call sites for callees {targets}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
