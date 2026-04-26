#!/usr/bin/env python3
"""patch_fn_to_call — replace a function's body with a forward `call`
to another function. Both source and destination must have identical
parameter and result types.

Use case: replace Mono internal helpers (e.g. `mono_wasm_add_assembly`,
`mono_bundled_resources_get_assembly_resource`) with calls into
Rust-side replacements exported from `wasp_canister`.

Usage:
  patch_fn_to_call.py <in.wasm> <out.wasm> <src_fn_idx> <dst_fn_idx>

The new src body becomes:
  (local i32 ...)        ;; preserved from header — we keep zero locals
  local.get 0
  local.get 1
  ...
  local.get N            ;; one local.get per param
  call <dst_fn_idx>      ;; result (if any) is left on stack as implicit return
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
    src_idx = int(sys.argv[3])
    dst_idx = int(sys.argv[4])

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        marker = f"  (func (;{src_idx};)"
        start = text.find(marker)
        if start < 0:
            print(f"no func {src_idx}", file=sys.stderr)
            return 1
        end = text.find("\n  )\n", start)
        if end < 0:
            return 1
        end += len("\n  )")
        body = text[start:end]

        first_nl = body.find("\n")
        header = body[: first_nl]

        # Parse params from header.
        m = re.search(r"\(param ([^)]*)\)", header)
        params = m.group(1).strip().split() if m else []
        num_params = len(params)
        if not all(p == "i32" for p in params):
            print(f"non-i32 params not supported: {params}", file=sys.stderr)
            return 1

        # Build the new body: forward all params to dst_idx via call.
        lines = [header]
        for i in range(num_params):
            lines.append(f"    local.get {i}")
        lines.append(f"    call {dst_idx}")
        lines.append("  )")
        new_body = "\n".join(lines)

        new_text = text[:start] + new_body + text[end:]
        out_wat.write_text(new_text)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(f"  patched fn {src_idx} → call {dst_idx} (passing {num_params} i32 params) → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
