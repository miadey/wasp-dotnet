#!/usr/bin/env python3
"""inject_checkpoints — sprinkle `call ic0.debug_print` checkpoints at
N evenly-spaced positions inside a target function's body. Each
checkpoint prints "ckp:NNNN".

Use to bisect WHERE inside a long function a trap fires: run the
canister, find the last "ckp:NNNN" log line — the trap is between it
and the next checkpoint (or the function end).

Constraint: a checkpoint can only be inserted at an instruction that
leaves the wasm operand stack EMPTY. We approximate by only injecting
at lines that look like statement boundaries: lines starting with one
of `local.set`, `local.tee` (when not on a stack-preserving expression),
`drop`, `br`, `br_if`, `if`, `else`, `end`, `loop`, `block`, or that
are bare `call N` where the called fn returns void. A heuristic; for
fn 555 (no `call` instructions, scalar-only, lots of local.set/tee),
we just inject at the START of every `block`/`loop`/`if`/`else`/`end`
and at every `local.set` line. These are stack-empty boundaries.

Usage:
  inject_checkpoints.py <in.wasm> <out.wasm> <fn_idx> <every_n>
  every_n: insert checkpoint every Nth eligible line (default 25)

Each checkpoint prints "ckp:NNNN\\n" (10 bytes) where NNNN is the
checkpoint index (4 ASCII digits).
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

SCRATCH_BASE_BASE = 1_500_000


def main():
    if len(sys.argv) not in (4, 5):
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    fn_idx = int(sys.argv[3])
    every_n = int(sys.argv[4]) if len(sys.argv) == 5 else 25

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

        scratch_off = SCRATCH_BASE_BASE + (fn_idx % 256) * 4096

        # Heuristic: insert checkpoint AFTER lines that match these patterns.
        # These are stack-empty boundaries (statement-level transitions).
        eligible_re = re.compile(
            r"^\s*(local\.set|drop|br|br_if|loop|block|if|else|end)(\s|$)"
        )

        lines = body.split("\n")
        new_lines = []
        ckp_idx = 0
        eligible_count = 0
        markers = []  # (offset, msg_bytes)

        for i, line in enumerate(lines):
            new_lines.append(line)
            if eligible_re.match(line):
                eligible_count += 1
                if eligible_count % every_n == 0:
                    # insert checkpoint AFTER this line
                    msg = f"ckp:{ckp_idx:04d}\n".encode("ascii")
                    off = scratch_off + ckp_idx * 16
                    markers.append((off, msg))
                    indent = re.match(r"^(\s*)", line).group(1)
                    new_lines.append(f"{indent}i32.const {off}")
                    new_lines.append(f"{indent}i32.const {len(msg)}")
                    new_lines.append(f"{indent}call {debug_print_idx}")
                    ckp_idx += 1

        new_body = "\n".join(new_lines)
        new_text = text[:start] + new_body + text[end:]

        # Append data segments.
        data_inits = ""
        for offset, msg in markers:
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
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(f"  fn {fn_idx} got {ckp_idx} checkpoints (every {every_n}th eligible) → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
