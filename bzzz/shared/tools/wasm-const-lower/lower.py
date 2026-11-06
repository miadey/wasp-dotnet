#!/usr/bin/env python3
"""
wasm-const-lower — replace data-segment offsets that use the wasm
extended-const proposal (`global.get $g [i32.const N i32.add]`) with
literal `i32.const` values.

Why: ICP's wasmtime doesn't accept the wasm extended-const proposal yet.
After `wasm-opt --multi-memory-lowering`, dotnet.native.wasm's data
segments end up with offsets shaped like
    (offset global.get 7 i32.const 1024 i32.add)
where global 7 holds a memory-base constant assigned at build time.
This script reads each global's initial value and rewrites the offsets
to `(offset i32.const N)` literals.

Usage:
    lower.py <input.wasm> <output.wasm>
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
        wat_path = Path(td) / "in.wat"
        out_wat_path = Path(td) / "out.wat"

        subprocess.run(
            ["wasm-tools", "print", str(in_wasm), "-o", str(wat_path)],
            check=True,
        )

        wat = wat_path.read_text()

        # Map global index → initial i32 value.
        global_init: dict[int, int] = {}
        global_re = re.compile(
            r"\(global \(;(?P<idx>\d+);\)(?: \(mut i32\)| i32) i32\.const (?P<val>-?\d+)\)"
        )
        for m in global_re.finditer(wat):
            global_init[int(m.group("idx"))] = int(m.group("val"))

        any_global_re = re.compile(
            r"\(global \(;(?P<idx>\d+);\)(?: \(mut [^\)]+\)| [if]\d+) "
        )
        all_global_indices: set[int] = {
            int(m.group("idx")) for m in any_global_re.finditer(wat)
        }

        print(
            f"  found {len(global_init)} i32 globals out of "
            f"{len(all_global_indices)} total",
            file=sys.stderr,
        )

        def repl_with_add(m: re.Match) -> str:
            idx = int(m.group("idx"))
            n = int(m.group("n"))
            base = global_init.get(idx)
            if base is None:
                raise RuntimeError(
                    f"data offset references global {idx} which is not an "
                    "i32-with-const-init; can't inline"
                )
            return f"(offset i32.const {base + n})"

        def repl_bare(m: re.Match) -> str:
            idx = int(m.group("idx"))
            base = global_init.get(idx)
            if base is None:
                raise RuntimeError(
                    f"data offset references global {idx} which is not an "
                    "i32-with-const-init; can't inline"
                )
            return f"(offset i32.const {base})"

        add_re = re.compile(
            r"\(offset global\.get (?P<idx>\d+) i32\.const (?P<n>-?\d+) i32\.add\)"
        )
        bare_re = re.compile(r"\(offset global\.get (?P<idx>\d+)\)")

        wat_new, n_add = add_re.subn(repl_with_add, wat)
        wat_new, n_bare = bare_re.subn(repl_bare, wat_new)

        elem_add_re = re.compile(
            r"global\.get (?P<idx>\d+) i32\.const (?P<n>-?\d+) i32\.add\) func"
        )
        elem_bare_re = re.compile(r"global\.get (?P<idx>\d+)\) func")

        def repl_elem_add(m: re.Match) -> str:
            idx = int(m.group("idx"))
            n = int(m.group("n"))
            base = global_init.get(idx)
            if base is None:
                raise RuntimeError(
                    f"element offset references global {idx} which is not "
                    "an i32-with-const-init"
                )
            return f"i32.const {base + n}) func"

        def repl_elem_bare(m: re.Match) -> str:
            idx = int(m.group("idx"))
            base = global_init.get(idx)
            if base is None:
                raise RuntimeError(
                    f"element offset references global {idx} which is not "
                    "an i32-with-const-init"
                )
            return f"i32.const {base}) func"

        wat_new, n_elem_add = elem_add_re.subn(repl_elem_add, wat_new)
        wat_new, n_elem_bare = elem_bare_re.subn(repl_elem_bare, wat_new)

        print(
            f"  rewrote {n_add} data(global.get+const), "
            f"{n_bare} data(global.get bare), "
            f"{n_elem_add} elem(global.get+const), "
            f"{n_elem_bare} elem(global.get bare)",
            file=sys.stderr,
        )

        # Fix __heap_base / __data_end: after multi-memory-lowering they
        # often point inside the merged static-data region rather than
        # past it. Find the highest data-segment OFFSET (regardless of
        # byte length, which is hard to count exactly in wat escape
        # syntax) and add a generous 4 MiB safety margin so malloc
        # always allocates past static data.
        offset_re = re.compile(
            r"^\s*\(data (?:\$[A-Za-z0-9_.]+ )?\(;\d+;\) \(i32\.const (?P<off>\d+)\)",
            re.MULTILINE,
        )
        max_offset = 0
        for m in offset_re.finditer(wat_new):
            off = int(m.group("off"))
            if off > max_offset:
                max_offset = off

        page = 65536
        safety_margin = 4 * 1024 * 1024  # 4 MiB
        new_heap_base = ((max_offset + safety_margin + page - 1) // page) * page
        max_data_end = max_offset + safety_margin  # for the print below
        print(
            f"  max data offset+len = {max_data_end}, "
            f"setting __heap_base/__data_end → {new_heap_base}",
            file=sys.stderr,
        )

        # Find global indices of __heap_base / __data_end via exports.
        export_re = re.compile(
            r'\(export "(?P<name>__heap_base|__data_end)" \(global (?P<idx>\d+)\)\)'
        )
        heap_globals: set[int] = {
            int(m.group("idx")) for m in export_re.finditer(wat_new)
        }

        # Rewrite their initialisers.
        for idx in heap_globals:
            pat = re.compile(
                rf"^(\s*)\(global \(;{idx};\)( (?:\(mut i32\)|i32)) i32\.const (-?\d+)\)",
                re.MULTILINE,
            )
            wat_new, n_fix = pat.subn(
                rf"\1(global (;{idx};)\2 i32.const {new_heap_base})",
                wat_new,
            )
            print(
                f"  rewrote global {idx} (__heap_base/__data_end): {n_fix} match",
                file=sys.stderr,
            )

        out_wat_path.write_text(wat_new)

        subprocess.run(
            ["wasm-tools", "parse", str(out_wat_path), "-o", str(out_wasm)],
            check=True,
        )

    return 0


if __name__ == "__main__":
    sys.exit(main())
