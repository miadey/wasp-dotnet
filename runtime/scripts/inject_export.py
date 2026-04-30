#!/usr/bin/env python3
"""inject_export — add an `(export "<name>" (func $<name>))` line to
a wasm so that wasm-merge can resolve another module's import of
`<name>`. Used to expose internal mono helpers (e.g.
`mono_class_get_checked`) that aren't exported from the upstream
`dotnet.native.wasm` build.

Usage:
  inject_export.py <in.wasm> <out.wasm> <fn_name>

The function is looked up by its `$<fn_name>` symbol (kept by the
`-g` flag during build / `wasm-tools` print). If the symbol is
missing from the binary or the export already exists, the script
no-ops with exit code 0 so it stays idempotent.
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
    in_wasm, out_wasm, fn_name = sys.argv[1], sys.argv[2], sys.argv[3]

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", in_wasm, "-o", str(wat)], check=True)
        text = wat.read_text()

        # No-op if export already present.
        if re.search(rf'\(export "{re.escape(fn_name)}" ', text):
            Path(out_wasm).write_bytes(Path(in_wasm).read_bytes())
            print(f"  {fn_name} already exported — no-op", file=sys.stderr)
            return 0

        # Locate the function by symbol.
        m = re.search(
            rf'^  \(func \${re.escape(fn_name)} \(;(\d+);\)',
            text,
            re.MULTILINE,
        )
        if not m:
            # Function symbol not present (stripped build). No-op so
            # build doesn't break — wasm-merge will fail later if the
            # symbol is actually needed, with a clear error.
            Path(out_wasm).write_bytes(Path(in_wasm).read_bytes())
            print(f"  ${fn_name} not found in wasm — no-op", file=sys.stderr)
            return 0

        fn_idx = int(m.group(1))
        export_line = f'\n  (export "{fn_name}" (func {fn_idx}))'
        first_export = text.find('\n  (export ')
        if first_export < 0:
            print("no existing export to anchor before", file=sys.stderr)
            return 1
        new = text[:first_export] + export_line + text[first_export:]
        out_wat.write_text(new)
        subprocess.run(
            ["wasm-tools", "parse", str(out_wat), "-o", out_wasm],
            check=True,
        )
        print(f"  exported {fn_name} → fn {fn_idx}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
