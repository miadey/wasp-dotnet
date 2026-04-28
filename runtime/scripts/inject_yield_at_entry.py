#!/usr/bin/env python3
"""inject_yield_at_entry — prepend `call $maybe_yield` to the body of
every function whose name matches a regex (default: the asyncify
onlylist functions: dn_simdhash inserts + mono_bundled_resources*).

Each function entry then becomes a yield point, giving asyncify a
chance to chunk work no matter where in the call chain the runtime
spends its instructions.

Run AFTER asyncify (so the prologue check is preserved by asyncify's
state machine wrap; we add the call inside the (state==0) branch in
practice — but for non-import calls asyncify ignores them, so we just
add right at body start).

Usage:
  inject_yield_at_entry.py <in.wasm> <out.wasm>
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


# Targets: ONLY the outer mono functions in the add_assembly chain.
# We rely on the dn_simdhash insert leaves having their own injected
# yield (via inject_yield_call.py) — adding entry yields here just
# gives us a few more yield sites at a higher level so we catch work
# that's done outside the inner-most insert path.
PATTERNS = [
    r'^mono_wasm_add_assembly$',
    r'^mono_bundled_resources_add_assembly_resource$',
    r'^mono_bundled_resources_add_assembly_symbol_resource$',
    r'^mono_bundled_resources_add$',
    r'^dn_simdhash_ensure_capacity_internal$',
]


def main():
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm, out_wasm = Path(sys.argv[1]), Path(sys.argv[2])

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(
            ["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True
        )
        text = wat.read_text()

        # Find maybe_yield (export or func definition).
        m = re.search(
            r'\(export "maybe_yield" \(func (\d+|\$[A-Za-z_][A-Za-z0-9_]*)\)\)',
            text,
        )
        if m:
            yield_fn = m.group(1)
        else:
            m = re.search(
                r'^  \(func (\$maybe_yield) ', text, re.MULTILINE
            )
            if not m:
                print(
                    "no `maybe_yield` found (export or func definition)",
                    file=sys.stderr,
                )
                return 1
            yield_fn = m.group(1)

        # Find candidate functions. Process in REVERSE byte order so
        # offsets stay valid as we splice.
        compiled = [re.compile(p) for p in PATTERNS]
        # Match `(func $name (;N;) ...` to capture each function header.
        hdr_re = re.compile(
            r'^  \(func \$([A-Za-z_][A-Za-z0-9_]*) \(;(\d+);\)',
            re.MULTILINE,
        )
        targets = []
        for hm in hdr_re.finditer(text):
            name = hm.group(1)
            if any(p.match(name) for p in compiled):
                # Find the END of the header line (after the (param/result)).
                hdr_end = text.find('\n', hm.end())
                # body_start is right after the header line newline.
                # The body may start with (local ...) which we skip.
                body_start = hdr_end
                targets.append((name, hm.start(), body_start))

        if not targets:
            print("no target functions matched", file=sys.stderr)
            return 1

        # Splice in REVERSE position order.
        targets.sort(key=lambda x: -x[2])
        for name, _hdr_start, body_start in targets:
            # Look ahead to the first content line; if it's `(local ...)`,
            # inject AFTER it.
            after_hdr = text[body_start + 1:]  # +1 to skip the \n
            first_line_end = after_hdr.find('\n')
            if first_line_end < 0:
                continue
            first_line = after_hdr[:first_line_end]
            if '(local' in first_line:
                offset = body_start + 1 + first_line_end
            else:
                offset = body_start
            inject = f"\n    call {yield_fn}"
            text = text[:offset] + inject + text[offset:]

        out_wat.write_text(text)
        try:
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)],
                check=True,
            )
        except subprocess.CalledProcessError:
            print(
                f"wasm-tools parse failed; check {out_wat}", file=sys.stderr
            )
            return 1
        names = ", ".join(t[0] for t in targets[:10])
        more = "" if len(targets) <= 10 else f" (+{len(targets) - 10} more)"
        print(
            f"  injected `call {yield_fn}` at start of {len(targets)} fns: "
            f"{names}{more}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
