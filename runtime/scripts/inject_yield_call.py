#!/usr/bin/env python3
"""inject_yield_call — wat-only injection that prepends
`call $maybe_yield` at the top of mono's dn_simdhash insert leaf.
Combined with `wasm-opt --asyncify --pass-arg=asyncify-imports@env.maybe_yield`,
this gives mono code a yield point that can unwind/rewind across
IC update messages, splitting a single long add_assembly call when
the IC instruction budget runs low.

Pipeline order in 30_merge.sh:
  1. wasm-merge (resolves env.maybe_yield to wasp_canister export)
  2. multi-memory-lowering
  3. const-lower / table-merge / icp-publish / wasi-stub / relax-simd
  4. THIS script — inject `call $maybe_yield` into dn_simdhash insert leaf
  5. wasm-opt --asyncify (instruments leaf + transitive callers)
  6. patch_fn_to_global_get + assert defang

Strategy:
  - Locate dn_simdhash insert leaf by body fingerprint (same as
    find_dn_simdhash_leaves.py — see that file for criteria).
  - Find `maybe_yield` export's fn index.
  - Prepend `call <maybe_yield_fn>` to the leaf's body.

Usage:
  inject_yield_call.py <in.wasm> <out.wasm>
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


def find_insert_leaves(text):
    """Find ALL dn_simdhash insert leaves (ght / ptr_ptr / string_ptr /
    ptrpair_ptr / u32_ptr variants). Each is a 5-arg → i32 func with a
    body in the 5K-13K char range using h20+h8+rem_u arithmetic.

    Returns a list of (fn_idx, body_size, body_start_offset) tuples.
    All matches get a `call $maybe_yield` so whichever leaf the runtime
    actually exercises during a particular insert path will yield.
    """
    hdr_re = re.compile(
        r'^  \(func (?:\$[A-Za-z_][A-Za-z0-9_]* )?\(;(\d+);\) \(type \d+\) \(param i32 i32 i32 i32 i32\) \(result i32\)\s*$',
        re.MULTILINE,
    )
    matches = []
    for m in hdr_re.finditer(text):
        fn = int(m.group(1))
        end = text.find('\n  )\n', m.end())
        body = text[m.end():end]
        sz = len(body)
        if not (5000 <= sz <= 13000):
            continue
        if (
            'i32.load offset=20' in body
            and 'i32.load offset=8' in body
            and 'i32.rem_u' in body
        ):
            matches.append((fn, sz, m.end()))
    return matches


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

        # Find maybe_yield — either as an EXPORT (post-merge canister)
        # or as an IMPORT (pre-merge dotnet that we asyncify alone).
        # With -g preserved, refs use `$name` form.
        m = re.search(
            r'\(export "maybe_yield" \(func (\d+|\$[A-Za-z_][A-Za-z0-9_]*)\)\)',
            text,
        )
        if m:
            yield_fn = m.group(1)
        else:
            m = re.search(
                r'\(import "env" "maybe_yield" \(func (\$[A-Za-z_][A-Za-z0-9_]*)',
                text,
            )
            if not m:
                print(
                    "no `maybe_yield` import or export — must be present "
                    "(import in pre-merge dotnet, export in merged canister)",
                    file=sys.stderr,
                )
                return 1
            yield_fn = m.group(1)

        # Find ALL dn_simdhash insert leaves and inject into each.
        # Inject in REVERSE byte-position order so earlier offsets stay
        # valid as we splice.
        leaves = find_insert_leaves(text)
        if not leaves:
            print("no dn_simdhash insert leaves found", file=sys.stderr)
            return 1
        leaves.sort(key=lambda x: -x[2])  # by body_start desc

        new_text = text
        for leaf_fn, leaf_sz, body_start in leaves:
            body_end = new_text.find('\n  )\n', body_start)
            body = new_text[body_start:body_end]
            offset = 0
            first_nl = body.find('\n', 1)
            if first_nl > 0 and '(local' in body[1:first_nl]:
                offset = first_nl
            injected = f"\n    call {yield_fn}"
            new_body = body[:offset] + injected + body[offset:]
            new_text = new_text[:body_start] + new_body + new_text[body_end:]
            print(
                f"  injected `call {yield_fn}` into fn {leaf_fn} "
                f"({leaf_sz} chars body)",
                file=sys.stderr,
            )

        out_wat.write_text(new_text)
        try:
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)],
                check=True,
            )
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  injected `call {yield_fn}` into {len(leaves)} leaves "
            f"→ {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
