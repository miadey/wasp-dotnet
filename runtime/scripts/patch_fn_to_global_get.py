#!/usr/bin/env python3
"""patch_fn_to_global_get — replace a function's body with a single
`global.get N` instruction. The function must take 0 params and
return one i32. Use case: post-merge, replace the placeholder Rust
body of `wasp_get_g7` with a live `global.get 7` so dotnet's data
base is read at runtime (it shifts when fn 5236's grow wrapper runs).

Usage:
  patch_fn_to_global_get.py <in.wasm> <out.wasm> <fn_idx> <global_idx>
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


def main():
    if len(sys.argv) != 5:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    fn_idx = int(sys.argv[3])
    global_idx = int(sys.argv[4])

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

        first_nl = body.find("\n")
        header = body[: first_nl]

        if "(result i32)" not in header:
            print(f"fn {fn_idx} doesn't return i32", file=sys.stderr)
            return 1

        new_body = f"{header}\n    global.get {global_idx}\n  )"
        new_text = text[:start] + new_body + text[end:]
        out_wat.write_text(new_text)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(f"  patched fn {fn_idx} → global.get {global_idx} → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
