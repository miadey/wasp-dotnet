#!/usr/bin/env python3
"""inject_entry_args — inject a debug_print at the entry of a target
function that prints "fnNNNN: a0=hex a1=hex ..." for each i32 param.

Usage:
  inject_entry_args.py <in.wasm> <out.wasm> <fn_idx> [<fn_idx> ...]

Each call adds an extra local pair (FNIDX_SAVE, TMP) and a static data
segment with the message template.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

SCRATCH_BASE_BASE = 1_500_000


def main():
    if len(sys.argv) < 4:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    fn_idxs = [int(x) for x in sys.argv[3:]]

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        m = re.search(r'\(import "ic0" "debug_print" \(func \(;(\d+);\)', text)
        if not m:
            print("no ic0.debug_print import", file=sys.stderr)
            return 1
        debug_print_idx = int(m.group(1))
        print(f"  ic0.debug_print = fn {debug_print_idx}", file=sys.stderr)

        scratch_used = []  # list of (offset, msg_bytes)
        edits = []  # list of (start, end, replacement)

        for fn_idx in fn_idxs:
            marker = f"  (func (;{fn_idx};)"
            start = text.find(marker)
            if start < 0:
                print(f"  fn {fn_idx} not found", file=sys.stderr)
                continue
            end = text.find("\n  )\n", start)
            if end < 0:
                print(f"  end of fn {fn_idx} not found", file=sys.stderr)
                continue
            end += len("\n  )")
            body = text[start:end]

            # Parse param i32 count from header.
            header_re = re.compile(r"\(func \(;\d+;\) \(type \d+\) \(param ([^)]*)\)")
            hm = header_re.search(body)
            if not hm:
                print(f"  fn {fn_idx} no param", file=sys.stderr)
                continue
            params = hm.group(1).strip().split()
            num_params = len(params)
            if not all(p == "i32" for p in params):
                print(f"  fn {fn_idx} non-i32 params not supported: {params}", file=sys.stderr)
                continue

            # Locals: parse existing line, extend with one new TMP local.
            local_line_re = re.compile(r"\n    \(local ([^)]*)\)\n")
            lm = local_line_re.search(body)
            if lm:
                existing_locals = lm.group(1).strip().split()
                num_existing = len(existing_locals)
                new_locals = existing_locals + ["i32"]
                body = (body[: lm.start()]
                        + f"\n    (local {' '.join(new_locals)})\n"
                        + body[lm.end() :])
                TMP = num_params + num_existing
                # Insertion point: after the new local line.
                lm2 = local_line_re.search(body)
                insert_at = lm2.end()
            else:
                # No locals — add one. Insert after the (func ...) header line.
                hdr_end = body.find(")\n", body.find("(func (;")) + 2  # after ")\n"
                body = (body[:hdr_end]
                        + "    (local i32)\n"
                        + body[hdr_end:])
                TMP = num_params
                insert_at = hdr_end + len("    (local i32)\n")

            # Build template message.
            scratch_off = SCRATCH_BASE_BASE + (fn_idx % 256) * 4096
            label = f"fn{fn_idx:04d}".encode("ascii")
            # template: "fnNNNN:" then for each param "a0=AABBCCDD " (12 bytes), trailing "\n"
            template = label + b": "
            arg_offsets = []
            for i in range(num_params):
                template += f"a{i}=".encode("ascii")
                # 8 hex chars
                arg_offsets.append(scratch_off + len(template))
                template += b"........"
                template += b" "
            template += b"\n"

            scratch_used.append((scratch_off, template))

            # Build the entry sequence:
            indent = "    "  # body indent
            lines = []
            for i in range(num_params):
                arg_off = arg_offsets[i]
                # Save param i to TMP, then dump 8 hex digits.
                lines.append(f"{indent}local.get {i}")
                lines.append(f"{indent}local.set {TMP}")
                # We can't reuse TMP for nibble work AND keep arg in TMP.
                # Use a different scratch: reload arg by shifting.
                for ni in range(8):
                    shift = (7 - ni) * 4
                    char_off = arg_off + ni
                    lines.append(f"{indent}i32.const 0")
                    lines.append(f"{indent}local.get {TMP}")
                    lines.append(f"{indent}i32.const {shift}")
                    lines.append(f"{indent}i32.shr_u")
                    lines.append(f"{indent}i32.const 15")
                    lines.append(f"{indent}i32.and")
                    # Stack: [0, nibble]; need to compute char and store8 at offset=char_off with addr=0.
                    # Use a lambda-style: dup nibble via local? Tricky. Use simpler: drop and recompute? Inefficient.
                    # Instead: store nibble to a fresh "subtmp" — but we don't have that.
                    # Workaround: just load with both branches via if/else producing the char.
                    lines.append(f"{indent}local.tee {TMP+0}")  # keeps stack [0, nibble], TMP overwritten
                    # Wait — we need TMP intact for next nibble. So we need a separate local.
                    # Refactor below.
                    pass
            # The above approach has a TMP collision. Switch to a cleaner design:
            # Use TWO new locals (TMP_ARG and TMP_NIBBLE).
            # I'll regenerate `lines` here.

            # Re-extend locals: replace single i32 with two i32s.
            # Easiest: rebuild from current body state.
            # Find current local line again, replace.
            lm3 = local_line_re.search(body)
            if lm3:
                existing = lm3.group(1).strip().split()
                # remove last i32 we just added
                existing.pop()
                existing += ["i32", "i32"]
                body = (body[: lm3.start()]
                        + f"\n    (local {' '.join(existing)})\n"
                        + body[lm3.end() :])
                TMP_ARG = num_params + (len(existing) - 2)
                TMP_NIB = TMP_ARG + 1
                lm4 = local_line_re.search(body)
                insert_at = lm4.end()
            else:
                # shouldn't happen
                continue

            # Now generate clean sequence using TMP_ARG and TMP_NIB.
            lines = []
            for i in range(num_params):
                arg_off = arg_offsets[i]
                lines.append(f"{indent}local.get {i}")
                lines.append(f"{indent}local.set {TMP_ARG}")
                for ni in range(8):
                    shift = (7 - ni) * 4
                    char_off = arg_off + ni
                    lines.append(f"{indent}i32.const 0")
                    lines.append(f"{indent}local.get {TMP_ARG}")
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
            # Final debug_print(scratch_off, len(template))
            lines.append(f"{indent}i32.const {scratch_off}")
            lines.append(f"{indent}i32.const {len(template)}")
            lines.append(f"{indent}call {debug_print_idx}")

            insertion = "\n".join(lines) + "\n"
            new_body = body[:insert_at] + insertion + body[insert_at:]
            edits.append((start, end, new_body))
            print(f"  fn {fn_idx}: {num_params} params, scratch {scratch_off}, msg len {len(template)}", file=sys.stderr)

        # Apply edits in reverse.
        new_text = text
        for start, end, new_body in sorted(edits, key=lambda e: -e[0]):
            new_text = new_text[:start] + new_body + new_text[end:]

        # Append data segments.
        data_inits = ""
        for offset, msg in scratch_used:
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
        print(f"  → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
