#!/usr/bin/env python3
"""inject_global_probe — inject a debug_print at the very start of a
target function body that emits "label=AABBCCDD\n" where AABBCCDD is
the current value of a chosen wasm global (in hex).

Useful for tracking __stack_pointer (typically global 0) drift across
IC update calls.

Usage:
  inject_global_probe.py <in.wasm> <out.wasm> <fn_idx> <global_idx> <label>

The label must be exactly 4 ASCII chars.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

SCRATCH_BASE_BASE = 1_500_000


def main():
    if len(sys.argv) != 6:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    fn_idx = int(sys.argv[3])
    global_idx = int(sys.argv[4])
    label = sys.argv[5]
    if len(label) != 4:
        print("label must be exactly 4 ASCII chars", file=sys.stderr)
        return 2

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
            print(f"no end for func {fn_idx}", file=sys.stderr)
            return 1
        end += len("\n  )")
        body = text[start:end]

        # Find param count.
        header_re = re.compile(r"\(func \(;\d+;\) \(type \d+\) \(param ([^)]*)\)")
        hm = header_re.search(body)
        num_params = len(hm.group(1).strip().split()) if hm else 0

        # Locals: extend with two TMP locals.
        local_line_re = re.compile(r"\n    \(local ([^)]*)\)\n")
        lm = local_line_re.search(body)
        if lm:
            existing = lm.group(1).strip().split()
            num_existing = len(existing)
            new_locals = existing + ["i32", "i32"]
            body = (body[: lm.start()]
                    + f"\n    (local {' '.join(new_locals)})\n"
                    + body[lm.end() :])
            TMP_VAL = num_params + num_existing
            TMP_NIB = TMP_VAL + 1
            insert_at = local_line_re.search(body).end()
        else:
            hdr_end = body.find(")\n", body.find("(func (;")) + 2
            body = body[:hdr_end] + "    (local i32 i32)\n" + body[hdr_end:]
            TMP_VAL = num_params
            TMP_NIB = num_params + 1
            insert_at = hdr_end + len("    (local i32 i32)\n")

        scratch_off = SCRATCH_BASE_BASE + (fn_idx % 256) * 4096
        # Template: "LLLL=AABBCCDD\n" (14 bytes)
        template = (label + "=........\n").encode("ascii")
        assert len(template) == 14

        indent = "    "
        lines = []
        # save global to TMP_VAL
        lines.append(f"{indent}global.get {global_idx}")
        lines.append(f"{indent}local.set {TMP_VAL}")
        for i in range(8):
            shift = (7 - i) * 4
            char_off = scratch_off + 5 + i  # after "LLLL="
            lines.append(f"{indent}i32.const 0")
            lines.append(f"{indent}local.get {TMP_VAL}")
            lines.append(f"{indent}i32.const {shift}")
            lines.append(f"{indent}i32.shr_u")
            lines.append(f"{indent}i32.const 15")
            lines.append(f"{indent}i32.and")
            lines.append(f"{indent}local.set {TMP_NIB}")
            lines.append(f"{indent}i32.const 87")
            lines.append(f"{indent}i32.const 48")
            lines.append(f"{indent}local.get {TMP_NIB}")
            lines.append(f"{indent}i32.const 9")
            lines.append(f"{indent}i32.gt_u")
            lines.append(f"{indent}select")
            lines.append(f"{indent}local.get {TMP_NIB}")
            lines.append(f"{indent}i32.add")
            lines.append(f"{indent}i32.store8 offset={char_off}")
        lines.append(f"{indent}i32.const {scratch_off}")
        lines.append(f"{indent}i32.const {len(template)}")
        lines.append(f"{indent}call {debug_print_idx}")

        insertion = "\n".join(lines) + "\n"
        new_body = body[:insert_at] + insertion + body[insert_at:]
        new_text = text[:start] + new_body + text[end:]

        # Append data segment.
        wat_str = ""
        for b in template:
            if 0x20 <= b < 0x7F and b not in (0x22, 0x5C):
                wat_str += chr(b)
            else:
                wat_str += f"\\{b:02x}"
        data_inits = f'  (data (i32.const {scratch_off}) "{wat_str}")\n'

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
        print(f"  fn {fn_idx} prologue prints '{label}=<global {global_idx} hex>' → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
