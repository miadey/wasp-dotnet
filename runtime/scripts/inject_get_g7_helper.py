#!/usr/bin/env python3
"""inject_get_g7_helper — append a tiny `wasp_get_g7() -> i32` function
to the merged canister wasm that returns the current value of
`global 7` (dotnet's data base, which shifts when fn 5236's grow
wrapper fires).

Then patch the EXISTING `wasp_get_g7` Rust import (declared but
unimplemented in wasp_canister) to call this new exported function.

Usage:
  inject_get_g7_helper.py <in.wasm> <out.wasm>

After this runs the canister has a callable `wasp_get_g7()` that
returns the live global 7 value, allowing read_cstr_rel to translate
dotnet-relative pointers to absolute addresses correctly even after
memory grows.
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

        # Strategy: locate an EXISTING function that returns `global.get 7 / 64K`
        # (already present in the binary as fn 8290 in some builds). If found,
        # we just expose it. If not, we inject a new function.
        #
        # Easier: just find `(global (;7;)` declaration to confirm it's mut i32,
        # then inject a function that reads it.

        # Find the highest function index. Add new fn at end.
        fn_re = re.compile(r"^  \(func \(;(\d+);\)", re.MULTILINE)
        highest = -1
        for m in fn_re.finditer(text):
            idx = int(m.group(1))
            if idx > highest:
                highest = idx
        new_fn_idx = highest + 1

        # Type 1 = `(func (result i32))` — declared at the top of every wat we've
        # seen. Use it for our helper.
        # Find the last (func ...) end (the last `\n  )\n` followed by exports/etc.)
        # Simpler: insert just before the first (export ...) line.
        last_fn_close = text.rfind("\n  )\n  (func ")
        if last_fn_close < 0:
            # fall back: insert before first export
            insert_pos = text.find("\n  (export ")
        else:
            # Find the very last `\n  )\n` that closes a func — search forward from last_fn_close
            # Actually simpler: find the last `\n  )\n` that is followed by `(export ` or `(table `.
            search_pos = last_fn_close + 1
            while True:
                end = text.find("\n  )\n", search_pos)
                if end < 0:
                    break
                next_marker = text[end + 5 : end + 30]
                if next_marker.startswith("(func ") or next_marker.startswith("  (func "):
                    search_pos = end + 1
                    continue
                # Hit non-func content — insert here
                insert_pos = end + len("\n  )")
                break

        helper = f"\n  (func (;{new_fn_idx};) (type 1) (result i32) global.get 7)"

        # Add an export for the helper
        export_line = f'\n  (export "wasp_get_g7" (func {new_fn_idx}))'

        new_text = text[:insert_pos] + helper + text[insert_pos:]

        # Now insert export. Find the first export line and put ours right before it.
        first_export = new_text.find('\n  (export ')
        if first_export < 0:
            print("no exports found; cannot add export", file=sys.stderr)
            return 1
        new_text = new_text[:first_export] + export_line + new_text[first_export:]

        out_wat.write_text(new_text)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(f"  added fn {new_fn_idx} as wasp_get_g7 → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
