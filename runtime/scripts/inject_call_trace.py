#!/usr/bin/env python3
"""inject_call_trace — instrument mono_wasm_add_assembly with debug
prints before every internal `call` instruction.

Approach:
  1. Print canister.wasm to wat
  2. Find the mono_wasm_add_assembly function body (whatever index
     it's been assigned post-merge)
  3. For each `call N` inside it, INJECT a sequence of instructions
     before the call that prints "addsm: pre call N" via ic0.debug_print
  4. After each call, INJECT a print "addsm: post call N"
  5. Parse back to wasm

The print uses a static buffer in wasp_canister's data section.
Each inject site uses a unique scratch offset to avoid collision.

Each marker string is fixed-length: "addsm: pre call NNNNN\\n" = 23 bytes.
Pre-built buffers: scratch_base + 32*site_idx for "pre", + (32*site_idx + 16) for "post".
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path

SCRATCH_BASE_BASE = 1_500_000  # between wasp_canister data (1MB) and dotnet's data (2.7MB)
# Per-fn scratch at SCRATCH_BASE_BASE + fn_idx % 256 * 4096 to keep within the gap.
# Per-function scratch base = SCRATCH_BASE_BASE + fn_idx * 4096 (4 KiB per fn)
# avoids collisions when multiple functions are instrumented in succession.


def main():
    if len(sys.argv) not in (3, 4):
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        # Optional: target a specific function index from cli arg 3.
        if len(sys.argv) > 3:
            fn_idx = int(sys.argv[3])
            print(f"  targeting fn idx (from cli) = {fn_idx}", file=sys.stderr)
        else:
            m = re.search(r'\(export "mono_wasm_add_assembly" \(func (\d+)\)\)', text)
            if not m:
                print("no mono_wasm_add_assembly export found", file=sys.stderr)
                return 1
            fn_idx = int(m.group(1))
            print(f"  mono_wasm_add_assembly fn idx = {fn_idx}", file=sys.stderr)
        scratch_base = SCRATCH_BASE_BASE + (fn_idx % 256) * 4096
        print(f"  scratch_base = {scratch_base}", file=sys.stderr)

        # Find ic0.debug_print fn idx.
        m = re.search(r'\(import "ic0" "debug_print" \(func \(;(\d+);\)', text)
        if not m:
            print("no ic0.debug_print import found", file=sys.stderr)
            return 1
        debug_print_idx = int(m.group(1))
        print(f"  ic0.debug_print = fn {debug_print_idx}", file=sys.stderr)

        # Find function body bounds.
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

        # Find every "call N" inside the body. Capture which N's are called.
        # Each call gets an instrument: print marker BEFORE the call.
        # We don't try to instrument AFTER the call (we'd need to handle
        # the function's return value, which has variable shape).
        call_pattern = re.compile(r"^(\s*)call (\d+)$", re.MULTILINE)

        # We pre-build print sequences. Each sequence is:
        #   i32.const SCRATCH+i*32  ;; ptr to bytes
        #   i32.const len            ;; len
        #   call DEBUG_PRINT_IDX
        # And we need to populate the data section with the marker strings.
        site_idx = 0
        marker_strs = []  # (scratch_offset, bytes)

        def build_print_call(msg: bytes, offset: int) -> str:
            return (
                f"      i32.const {offset}\n"
                f"      i32.const {len(msg)}\n"
                f"      call {debug_print_idx}\n"
            )

        def repl(m):
            nonlocal site_idx
            indent = m.group(1)
            n = int(m.group(2))
            msg = f"addsm: pre call {n} (site {site_idx})".encode("ascii")
            offset = scratch_base + site_idx * 64
            marker_strs.append((offset, msg))
            site_idx += 1
            return (
                build_print_call(msg, offset)
                + f"{indent}call {n}"
            )

        new_body = call_pattern.sub(repl, body)

        # Build a data segment that initialises all marker bytes at
        # their scratch offsets. We use one (data ...) per marker for
        # clarity, all with explicit (i32.const offset).
        data_inits = ""
        for offset, msg in marker_strs:
            # Convert msg to wat string (escape non-printables).
            wat_str = ""
            for b in msg:
                if 0x20 <= b < 0x7F and b not in (0x22, 0x5C):
                    wat_str += chr(b)
                else:
                    wat_str += f"\\{b:02x}"
            data_inits += f'  (data (i32.const {offset}) "{wat_str}")\n'

        new_text = text[:start] + new_body + text[end:]

        # Insert data segments at end of file (after last existing data).
        # Find the last `(data ...)` occurrence and append after.
        last_data = new_text.rfind("\n  (data ")
        if last_data >= 0:
            # Find end of that data segment line
            line_end = new_text.find("\n", last_data + 1)
            new_text = new_text[: line_end + 1] + data_inits + new_text[line_end + 1 :]
        else:
            # Insert before last close paren of module
            module_close = new_text.rfind("\n)")
            new_text = new_text[:module_close] + "\n" + data_inits + new_text[module_close:]

        out_wat.write_text(new_text)
        print(f"  instrumented {site_idx} call sites", file=sys.stderr)

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
