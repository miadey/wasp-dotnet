#!/usr/bin/env python3
"""patch_fn_return_zero — replace a function's body with `i32.const 0`
that returns immediately.

Use case: Mono's bundled_resources_get_assembly_resource (fn 5662 in
the merged canister) returns a false-positive pointer on the 3rd
add_assembly call with real PE bytes, triggering the assertion-trap
path in mono_wasm_add_assembly. Forcing it to always return NULL
makes every add_assembly take the "insert new entry" path.

Usage:
  patch_fn_return_zero.py <in.wasm> <out.wasm> <fn_idx>

Function MUST take any number of i32 params and return one i32. The
locals declaration is replaced with `(local i32)` (none) and the body
becomes `i32.const 0`.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


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

        m = re.search(rf'^  \(func (?:\$\S+ )?\(;{fn_idx};\)', text, re.MULTILINE)
        if not m:
            print(f"no func {fn_idx}", file=sys.stderr)
            return 1
        start = m.start()
        end = text.find("\n  )\n", start)
        if end < 0:
            return 1
        end += len("\n  )")
        body = text[start:end]

        # Extract the header line (first line of body).
        first_nl = body.find("\n")
        header = body[: first_nl]
        # body should end with ")\n  )" — we want to keep the header and replace
        # everything between with our minimal body.
        # Verify it has (result i32).
        if "(result i32)" not in header:
            print(f"fn {fn_idx} doesn't return i32, header: {header!r}", file=sys.stderr)
            return 1

        new_body = header + "\n    i32.const 0\n  )"
        new_text = text[:start] + new_body + text[end:]
        out_wat.write_text(new_text)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print("wasm-tools parse failed", file=sys.stderr)
            return 1
        print(f"  patched fn {fn_idx} to return 0 → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
