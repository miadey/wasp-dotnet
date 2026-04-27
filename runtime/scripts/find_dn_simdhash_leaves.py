#!/usr/bin/env python3
"""find_dn_simdhash_leaves — locate the dn_simdhash get and insert
internal helper functions in a merged canister wasm by body
fingerprint. These indices shift between builds whenever
wasp_canister adds/removes exports, so hardcoding them is fragile.

Get-leaf signature pattern (`(i32, i32) → i32`, body):
  * `global.get 7` + `i32.load offset=44` (load hash function ptr
    from the table struct).
  * `call_indirect (type 6)` (call the table's hash function).
  * `i32.load offset=20` (load buckets array ptr).
  * `i32.load offset=8` (load bucket count).
  * `i32.rem_u` (modulo bucket count).
  * Bucket scan loop with byte comparisons via `i32.load8_u`.

Insert-leaf signature pattern (`(i32, i32, i32, i32, i32) → i32`):
  * Same `i32.load offset=44` + `call_indirect (type 6)` for hash.
  * Same `i32.load offset=20` for buckets.
  * Multi-mode insert handling (writes new value into bucket, may
    grow buckets array).

Selection rule: among all candidate functions matching the regexes,
pick the one with the LARGEST body — the leaf has the most code
because of the bucket scan / probe loop. Other matches are usually
inlined wrappers that are much smaller.

Usage:
  find_dn_simdhash_leaves.py <wat_file>
prints two lines: `get=<idx>` and `insert=<idx>`.
"""

import re
import sys
from pathlib import Path


def find_leaf(text, params_pattern, body_min, body_max):
    """Find the (param ...) → i32 fn matching dn_simdhash leaf body.

    Pattern: must have `i32.load offset=44` (table's hash function
    pointer) AND `call_indirect (type 6)` (calling that hash fn) AND
    `i32.load offset=20` (buckets array pointer) AND `i32.load
    offset=8` (bucket count) AND `i32.rem_u` (modulo for bucket
    index).

    Constrained to a body-size band [body_min, body_max] to filter
    out monolithic functions that incidentally share these idioms.
    Among remaining candidates pick the largest."""
    hdr_re = re.compile(
        r'^  \(func \(;(\d+);\) \(type \d+\) \(param '
        + params_pattern
        + r'\) \(result i32\)\s*$',
        re.MULTILINE,
    )
    matches = []
    for m in hdr_re.finditer(text):
        fn = int(m.group(1))
        end = text.find('\n  )\n', m.end())
        body = text[m.end():end]
        sz = len(body)
        if not (body_min <= sz <= body_max):
            continue
        if (
            'i32.load offset=44 align=1' in body
            and 'call_indirect (type 6)' in body
            and 'i32.load offset=20 align=1' in body
            and 'i32.load offset=8 align=1' in body
            and 'i32.rem_u' in body
        ):
            matches.append((fn, sz))
    if not matches:
        return None
    matches.sort(key=lambda x: -x[1])
    return matches[0][0]


def main():
    if len(sys.argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    text = Path(sys.argv[1]).read_text()
    # `dn_simdhash_get_value_or_default(table, key)` — the GET leaf
    # has a small body (~5–6K chars) that:
    #   1. loads the per-table hash function pointer (offset=44),
    #   2. calls it via `call_indirect (type 6)` to compute hash,
    #   3. loads buckets array (offset=20) and bucket count (offset=8),
    #   4. computes bucket index via `i32.rem_u`,
    #   5. probes the bucket.
    get_fn = find_leaf(text, "i32 i32", 3000, 8000)

    # str_ptr-style INSERT leaf (no h44, no cind6 — hash is precomputed
    # and passed as arg 2). Body ~10-11K chars in merged. This is what
    # mono actually invokes during register_all from inside the
    # mono_wasm_add_assembly path (despite agent research suggesting
    # GHT — empirically only the str_ptr bypass keeps register_all
    # from trapping).
    def find_insert(text):
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
            if not (10000 <= sz <= 12000):
                continue
            if (
                'i32.load offset=20 align=1' in body
                and 'i32.load offset=8 align=1' in body
                and 'i32.rem_u' in body
                and 'call_indirect (type 6)' not in body
            ):
                matches.append((fn, sz))
        if not matches:
            return None
        matches.sort(key=lambda x: -x[1])
        return matches[0][0]
    ins_fn = find_insert(text)
    if get_fn is None or ins_fn is None:
        print(
            f"could not resolve dn_simdhash leaves "
            f"(get={get_fn}, insert={ins_fn})",
            file=sys.stderr,
        )
        return 1
    print(f"get={get_fn}")
    print(f"insert={ins_fn}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
