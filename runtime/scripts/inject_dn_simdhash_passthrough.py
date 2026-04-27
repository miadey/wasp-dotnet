#!/usr/bin/env python3
"""inject_dn_simdhash_passthrough — wat-only PRE-MERGE tool that
PRESERVES the original dn_simdhash insert leaf in
`dotnet.native.wasm` as a NEW exported function
`wasp_dn_simdhash_insert_original`, so wasp_canister can declare
it as an `extern "C"` import (from the "dotnet" namespace) and
wasm-merge will resolve the linkage.

Without this preservation, our wasp_simdhash_insert can never let
even one insert reach mono's real bundled-resources table — every
single insert is intercepted by the shadow map. This means
mono's `mono_bundled_resources_get_assembly_resource_values` always
finds an empty hash and corlib loading fails.

Strategy:
  1. Locate the dn_simdhash insert leaf in pristine dotnet.native.wasm
     by body fingerprint (5-arg → i32, ~10K body, h20+h8+rem_u, no h44).
  2. Clone its body into a NEW function appended at the end.
  3. Export the new function as `wasp_dn_simdhash_insert_original`.
  4. Leave the original fn untouched (so the post-merge patch_fn_to_call
     can still rewrite it to call wasp_simdhash_insert).

After merge, wasp_canister's `extern "C" wasp_dn_simdhash_insert_original`
import resolves to the cloned fn (still has the real bucket-scan body).
wasp_simdhash_insert can selectively call it for the first N inserts
(enough to land corelib in the real bundled-resources hash) before
falling back to the shadow-map bypass for later calls that would
trip the dn_simdhash 3rd-distinct-pointer bug.

Usage:
  inject_dn_simdhash_passthrough.py <in.wasm> <out.wasm>

Apply this to dotnet.native.wasm BEFORE wasm-merge.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


def find_insert_leaf(text):
    """Locate the str_ptr-style dn_simdhash insert leaf — empirically
    THIS is what gets called by register_all (despite upstream src
    suggesting bundled_resources uses GHT). Body 7-9K chars in pristine,
    10-12K in merged. No h44, no call_indirect type 6 (hash precomputed)."""
    hdr_re = re.compile(
        r'^  \(func \(;(\d+);\) \(type \d+\) \(param i32 i32 i32 i32 i32\) \(result i32\)\s*$',
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
            and 'call_indirect (type 6)' not in body
        ):
            matches.append((fn, sz, body, m.end(), end))
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
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        leaf = find_insert_leaf(text)
        if not leaf:
            print("could not locate dn_simdhash insert leaf", file=sys.stderr)
            return 1
        leaf_fn, leaf_sz, leaf_body, leaf_body_start, leaf_body_end = leaf

        # Find leaf's TYPE so we can declare the new fn with the same
        # signature and the same locals.
        # Header is at text[?:leaf_body_start - 1]. Find the start.
        # Actually we already know the signature is type 24 (5 i32).
        type_match = re.search(
            r'^  \(func \(;' + str(leaf_fn) + r';\) \((type \d+)\)',
            text, re.MULTILINE,
        )
        leaf_type = type_match.group(1) if type_match else "type 24"

        # Find next free fn index.
        fn_re = re.compile(r"^  \(func \(;(\d+);\)", re.MULTILINE)
        highest = -1
        for m in fn_re.finditer(text):
            highest = max(highest, int(m.group(1)))
        new_fn = highest + 1

        # Build the new function: same signature, body copied verbatim.
        new_body = (
            f"\n  (func (;{new_fn};) ({leaf_type}) (param i32 i32 i32 i32 i32) (result i32){leaf_body}\n  )"
        )
        new_export = f'\n  (export "wasp_dn_simdhash_insert_original" (func {new_fn}))'

        # Insert the new function at the end of the func section
        # (just before the (data (;0;) ...) declaration).
        insert_at = text.find('\n  (data (;0;)')
        if insert_at < 0:
            print("no data section boundary", file=sys.stderr)
            return 1
        with_fn = text[:insert_at] + new_body + text[insert_at:]

        # Add the export. Find the first (export ...) to insert before.
        export_at = with_fn.find('\n  (export ')
        if export_at < 0:
            print("no exports section", file=sys.stderr)
            return 1
        final = with_fn[:export_at] + new_export + with_fn[export_at:]

        out_wat.write_text(final)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  preserved dn_simdhash insert leaf (fn {leaf_fn}) as "
            f"fn {new_fn}, exported as wasp_dn_simdhash_insert_original "
            f"({leaf_sz} chars body) → {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
