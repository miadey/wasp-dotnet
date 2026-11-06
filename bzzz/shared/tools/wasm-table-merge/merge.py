#!/usr/bin/env python3
"""
wasm-table-merge — drop wasp_canister's unused table from a wasm-merge'd
canister so ICP wasmtime accepts the module.

Why: ICP allows only one function table per canister. After
`wasm-merge`'ing wasp_canister + dotnet.native.wasm, two tables survive:
  table 0 — Rust's default funcref table (273 entries)
  table 1 — dotnet's __indirect_function_table (4429 entries)
ICP rejects with "table count too high at 2".

Empirically (verified on the Phase A pipeline output) all 3963
`call_indirect` instructions target table 1, and table 0's element
segment references functions that are also exported by name and never
indirectly called. So the canister-correct fix is: drop table 0 and
its dead element segment.

If a future build introduces a real call_indirect into table 0, this
script will fail loudly (rather than silently break the canister) and
we'll need the full merge-and-shift algorithm.

Usage:
    merge.py <input.wasm> <output.wasm>
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

        table_re = re.compile(
            r"^(\s*)\(table \(;(?P<idx>\d+);\) (?P<size>\d+(?: \d+)?) funcref\)",
            re.MULTILINE,
        )
        tables = [(int(m.group("idx")), int(m.group("size").split()[0]))
                  for m in table_re.finditer(wat)]
        if len(tables) <= 1:
            print(f"  only {len(tables)} table(s); nothing to do", file=sys.stderr)
            out_wat_path.write_text(wat)
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat_path), "-o", str(out_wasm)],
                check=True,
            )
            return 0

        if len(tables) > 2:
            print(f"  ! found {len(tables)} tables; only handle 2", file=sys.stderr)
            return 1

        ci_targets = set(re.findall(r"call_indirect (\d+) ", wat))
        if not ci_targets:
            print("  no call_indirect found; defaulting canonical = table 1",
                  file=sys.stderr)
            canonical_idx = 1
        elif len(ci_targets) > 1:
            print(f"  ! call_indirect targets multiple tables {ci_targets}; "
                  "this script can't handle that", file=sys.stderr)
            return 1
        else:
            canonical_idx = int(next(iter(ci_targets)))

        dropped_idx = next(t[0] for t in tables if t[0] != canonical_idx)
        dropped_size = next(t[1] for t in tables if t[0] == dropped_idx)
        ci_count = len(re.findall(r"call_indirect \d+ ", wat))
        print(
            f"  canonical = table {canonical_idx} "
            f"(all {ci_count} call_indirect), dropping table {dropped_idx} "
            f"(size {dropped_size})",
            file=sys.stderr,
        )

        for op in ("table.get", "table.set", "table.size", "table.grow",
                   "table.fill", "table.copy", "table.init"):
            pat = re.compile(rf"{re.escape(op)} (\d+)")
            uses = {int(m.group(1)) for m in pat.finditer(wat)}
            if dropped_idx in uses:
                print(
                    f"  ! table {dropped_idx} is referenced by `{op}` somewhere "
                    "— full merge-and-renumber needed", file=sys.stderr,
                )
                return 1

        # Drop the dropped-table declaration.
        wat_new = re.sub(
            rf"^\s*\(table \(;{dropped_idx};\) \d+(?: \d+)? funcref\)\n",
            "",
            wat,
            count=1,
            flags=re.MULTILINE,
        )

        # Drop element segments targeting the dropped table.
        n_default = 0
        n_explicit = 0
        if dropped_idx == 0:
            default_re = re.compile(
                r"^\s*\(elem \(;\d+;\) \(i32\.const \d+\) func [^\)]*\)\n",
                re.MULTILINE,
            )
            wat_new, n_default = default_re.subn("", wat_new)
        explicit_re = re.compile(
            rf"^\s*\(elem \(;\d+;\) \(table {dropped_idx}\) "
            r"\(i32\.const \d+\) func [^\)]*\)\n",
            re.MULTILINE,
        )
        wat_new, n_explicit = explicit_re.subn("", wat_new)

        print(
            f"  removed {n_default} default-table elem + "
            f"{n_explicit} explicit-table elem",
            file=sys.stderr,
        )

        # Renumber if needed.
        if dropped_idx < canonical_idx:
            old_canonical = canonical_idx
            new_canonical = canonical_idx - 1
            print(
                f"  renumbering: table {old_canonical} → {new_canonical}",
                file=sys.stderr,
            )
            wat_new = re.sub(
                rf"\bcall_indirect {old_canonical}\b",
                f"call_indirect {new_canonical}",
                wat_new,
            )
            wat_new = re.sub(
                rf"\(table {old_canonical}\)",
                f"(table {new_canonical})",
                wat_new,
            )
            wat_new = re.sub(
                rf'\(export "([^"]+)" \(table {old_canonical}\)\)',
                rf'(export "\1" (table {new_canonical}))',
                wat_new,
            )
            for op in ("table.get", "table.set", "table.size", "table.grow",
                       "table.fill", "table.copy", "table.init"):
                wat_new = re.sub(
                    rf"\b{re.escape(op)} {old_canonical}\b",
                    f"{op} {new_canonical}",
                    wat_new,
                )

        out_wat_path.write_text(wat_new)
        subprocess.run(
            ["wasm-tools", "parse", str(out_wat_path), "-o", str(out_wasm)],
            check=True,
        )

    return 0


if __name__ == "__main__":
    sys.exit(main())
