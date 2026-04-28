#!/usr/bin/env python3
"""inject_maybe_yield_import — add an `(import "env" "maybe_yield"
(func $maybe_yield (param) (result)))` declaration to dotnet.native.wasm
BEFORE the merge step.

Why: wasm-opt --asyncify --pass-arg=asyncify-imports@env.maybe_yield
only treats env.maybe_yield as an unwind trigger if it is an actual
IMPORT in the module being processed. Without an import declaration,
asyncify creates no saved-points around its call sites, and rewind
re-runs the function body from the top instead of fast-forwarding to
the post-unwind point.

The injected import fits at the end of the existing import list. With
-g preservation all calls in dotnet reference `$<name>`, so wat-tools
re-parsing handles the function-index shift automatically.

Usage:
  inject_maybe_yield_import.py <in.wasm> <out.wasm>
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
        subprocess.run(
            ["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True
        )
        text = wat.read_text()

        # Already has it?
        if re.search(r'\(import "env" "maybe_yield"', text):
            print("  env.maybe_yield import already present", file=sys.stderr)
            out_wat.write_text(text)
        else:
            # Insert after the LAST `(import ...)` line.
            imports = list(re.finditer(r'^  \(import "[^"]+" "[^"]+"', text,
                                        re.MULTILINE))
            if not imports:
                print("no imports found in input wat", file=sys.stderr)
                return 1
            # Find end of last import statement (closing paren on same line
            # or next).
            last = imports[-1]
            # Find `\n` that ends the last import declaration.
            nl = text.find('\n', last.start())
            # The import declaration may span multiple lines if complex;
            # find the closing `))` matching its `(`.
            # Simpler: the import is one line `(import "x" "y" (func ...))`.
            insertion_point = nl + 1
            new_import = (
                '  (import "env" "maybe_yield" (func $maybe_yield))\n'
            )
            text = text[:insertion_point] + new_import + text[insertion_point:]
            out_wat.write_text(text)

        try:
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)],
                check=True,
            )
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  injected env.maybe_yield import → {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
