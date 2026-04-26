#!/usr/bin/env python3
"""
wasm-relax-simd — round-trip a wasm through wasm-tools to add `align=1`
(i.e. byte-aligned) hints to every bare `v128.load*` instruction. Some
runtimes treat the absence of align as "natural alignment 16" and trap
on misaligned access; explicit align=1 forces relaxed semantics.

Usage:
    relax.py <input.wasm> <output.wasm>
"""

from __future__ import annotations

import re
import subprocess
import sys
import tempfile
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])

    with tempfile.TemporaryDirectory() as td:
        wat_in = Path(td) / "in.wat"
        wat_out = Path(td) / "out.wat"
        subprocess.run(
            ["wasm-tools", "print", str(in_wasm), "-o", str(wat_in)],
            check=True,
        )
        wat = wat_in.read_text()

        # Add `align=1` to every v128 load/store that doesn't already
        # have an explicit align hint. The wasm-tools printer omits the
        # alignment when it equals the natural alignment; we want to
        # force it to 0 so runtimes never enforce alignment.
        # Patterns to relax (each followed by optional offset/align):
        #   v128.load                  -> v128.load align=1
        #   v128.load8_splat           -> v128.load8_splat align=1
        #   v128.load{16,32,64}_splat  -> ... align=1
        #   v128.load*x*_{s,u}         -> ... align=1
        #   v128.store                 -> v128.store align=1

        # Skip *_lane variants — their textual form has the lane index
        # AFTER the align hint, and inserting align here would corrupt
        # the syntax. Lane ops are rare in the simdhash hot path; the
        # bare loads/stores below are what matter. Use \b for word
        # boundaries so "v128.store" doesn't also match "v128.store32_lane".
        ops = [
            r"v128\.load(?:8_splat|16_splat|32_splat|64_splat|"
            r"32_zero|64_zero|8x8_s|8x8_u|16x4_s|16x4_u|32x2_s|32x2_u)?\b",
            r"v128\.store\b",
        ]

        n_added = 0
        # Process line by line.
        lines = wat.split("\n")
        for i, line in enumerate(lines):
            for op_pat in ops:
                # Match the op then optional 'offset=N' then optional 'align=N'.
                # We add align=1 after the op only if no align is present.
                regex = re.compile(
                    rf"(?P<op>{op_pat})"
                    r"(?P<rest>(?:\s+offset=\d+)?)"
                    r"(?P<has_align>\s+align=\d+)?"
                )
                def repl(m: re.Match) -> str:
                    nonlocal n_added
                    if m.group("has_align"):
                        return m.group(0)
                    n_added += 1
                    return f'{m.group("op")}{m.group("rest") or ""} align=1'
                lines[i] = regex.sub(repl, line)

        wat_new = "\n".join(lines)
        print(f"  added align=1 to {n_added} simd ops", file=sys.stderr)

        wat_out.write_text(wat_new)
        subprocess.run(
            ["wasm-tools", "parse", str(wat_out), "-o", str(out_wasm)],
            check=True,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
