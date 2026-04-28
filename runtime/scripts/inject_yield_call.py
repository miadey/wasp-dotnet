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


def find_insert_leaf(text):
    """Same heuristic as find_dn_simdhash_leaves.py. Both SIMD-build
    pattern (str_ptr-style: h20+h8+rem_u, no call_indirect) and
    no-SIMD-build pattern handled."""
    # Handle both numeric-only `(func (;N;) ...)` and named
    # `(func $name (;N;) ...)` forms (the latter when -g preserved names).
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
    if not matches:
        return None
    matches.sort(key=lambda x: -x[1])
    return matches[0]


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

        # Find maybe_yield export — handle both numeric (func N) and
        # named (func $maybe_yield) forms (the latter when -g is preserved).
        m = re.search(r'\(export "maybe_yield" \(func (\d+|\$[A-Za-z_][A-Za-z0-9_]*)\)\)', text)
        if not m:
            print("no `maybe_yield` export — wasp_canister must export it", file=sys.stderr)
            return 1
        yield_token = m.group(1)
        # patch_fn_to_call style: emit `call <token>` directly (works for
        # both numeric indices and $named refs in wat).
        yield_fn = yield_token

        # Find dn_simdhash insert leaf.
        leaf = find_insert_leaf(text)
        if not leaf:
            print("no dn_simdhash insert leaf found", file=sys.stderr)
            return 1
        leaf_fn, leaf_sz, body_start = leaf

        # Inject `call <yield_fn>` AFTER the `(local ...)` declaration
        # line if present. Wat function bodies look like:
        #   (func (;N;) (type T) (param ...) (result ...)
        #     (local i32 i32 ...)        ← optional
        #     <body instructions>
        #   )
        # body_start points to just after the header newline. Find the
        # first newline within the body — if the next line is `(local`,
        # skip past it so the injected call goes at the start of actual
        # instructions.
        body_end = text.find('\n  )\n', body_start)
        body = text[body_start:body_end]
        # Find first `\n    ` in body (instruction indentation).
        # Skip the (local ...) line if present.
        offset = 0
        # body[0] is '\n' (newline after header). body[1:] starts with
        # 4-space indent. Look for `(local` on first content line.
        first_nl = body.find('\n', 1)  # find newline after first content line
        if first_nl > 0 and '(local' in body[1:first_nl]:
            offset = first_nl
        injected = f"\n    call {yield_fn}"
        new_body = body[:offset] + injected + body[offset:]
        new_text = text[:body_start] + new_body + text[body_end:]

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
            f"  injected `call {yield_fn}` (maybe_yield) at start of fn "
            f"{leaf_fn} (dn_simdhash insert leaf, {leaf_sz} chars body) "
            f"→ {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
