#!/usr/bin/env python3
"""inject_corlib_loader — wat-only patch that hijacks mono's
`mono_assembly_load_corlib` (currently fn 1967 in our build) so it
returns whatever `wasp_get_assembly("System.Private.CoreLib.dll")`
returns.

Approach:
  1. Append a (data) segment with the literal byte string
     "System.Private.CoreLib.dll\0" at a fixed scratch address.
  2. Append a new helper function $get_corlib_resource() → i32 that:
       * loads the scratch address (an absolute pointer in our linear
         memory),
       * subtracts global.get 7 to make it dotnet-relative (the
         convention wasp_get_assembly expects),
       * tail-calls wasp_get_assembly.
  3. Replace fn 1967's body with `call $get_corlib_resource; return`.

Locates fn 1967 by body fingerprint:
  * `() → i32`
  * Reads a global at offset ~0x896892 (cached corlib slot)
  * Calls call_indirect (the preload hook list)
  * Has the trailing g_assert pattern
But by default the script accepts an explicit `--target FN` flag so
the user can override if the fingerprint resolution fails.

Usage:
  inject_corlib_loader.py <in.wasm> <out.wasm> [--target FN]
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


def main():
    args = sys.argv[1:]
    target_override = None
    if "--target" in args:
        idx = args.index("--target")
        target_override = int(args[idx + 1])
        args = args[:idx] + args[idx + 2 :]
    if len(args) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(args[0])
    out_wasm = Path(args[1])

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", str(in_wasm), "-o", str(wat)], check=True)
        text = wat.read_text()

        m = re.search(r'\(export "wasp_get_assembly" \(func (\d+)\)\)', text)
        if not m:
            print("no `wasp_get_assembly` export", file=sys.stderr)
            return 1
        wasp_get_assembly_fn = int(m.group(1))

        if target_override is not None:
            target_fn = target_override
        else:
            # Heuristic: () → i32 fn whose body has both the cached-slot
            # global access (i32.const 896892) and a call_indirect for
            # the preload hook (type 5).
            hdr_re = re.compile(
                r'^  \(func \(;(\d+);\) \(type \d+\) \(result i32\)\s*$'
                r'(?P<body>(?:.|\n)*?)\n  \)\n',
                re.MULTILINE,
            )
            target_fn = None
            for hm in hdr_re.finditer(text):
                body = hm.group('body')
                if 'i32.const 896892' in body and 'call_indirect (type 5)' in body:
                    target_fn = int(hm.group(1))
                    break
            if target_fn is None:
                print(
                    "could not auto-locate mono_assembly_load_corlib; "
                    "pass --target FN explicitly",
                    file=sys.stderr,
                )
                return 1

        # Append helper and data at the end of the func section.
        fn_re = re.compile(r"^  \(func \(;(\d+);\)", re.MULTILINE)
        highest = -1
        for fm in fn_re.finditer(text):
            highest = max(highest, int(fm.group(1)))
        helper_fn = highest + 1
        scratch_addr = 0x10500
        corlib_str = b"System.Private.CoreLib.dll\0"
        corlib_data = ''.join(f"\\{b:02x}" for b in corlib_str)

        helper_body = f"""
  (func (;{helper_fn};) (type 1) (result i32)
    ;; wasp_get_assembly takes a dotnet-RELATIVE name pointer
    ;; (mono code adds g7 to deref). Our scratch address is
    ;; absolute, so subtract g7 before passing.
    i32.const {scratch_addr}
    global.get 7
    i32.sub
    call {wasp_get_assembly_fn}
  )"""
        new_data = f'\n  (data $corlib_name (i32.const {scratch_addr}) "{corlib_data}")'

        # Patch target fn's body. Re-find header position first; we'll
        # then replace from header.end() to the corresponding `\n  )\n`.
        target_re = re.compile(
            r'^(  \(func \(;' + str(target_fn) + r';\)[^\n]*)\n', re.MULTILINE
        )
        m = target_re.search(text)
        if not m:
            print(f"could not relocate fn {target_fn}", file=sys.stderr)
            return 1
        body_start = m.end()
        body_end_marker = text.find('\n  )\n', body_start)
        if body_end_marker < 0:
            print(f"could not find end of fn {target_fn}", file=sys.stderr)
            return 1
        body_end = body_end_marker + 1  # keep newline before `)`

        # New body: drop all locals, just call helper and return.
        new_body = (
            f"\n    call {helper_fn}\n"
            f"    return\n"
        )
        rewritten = text[:body_start] + new_body + text[body_end:]

        # Insert helper + data before (data (;0;) ...).
        insert_at = rewritten.find('\n  (data (;0;)')
        if insert_at < 0:
            print("no data section boundary", file=sys.stderr)
            return 1
        final = rewritten[:insert_at] + helper_body + new_data + rewritten[insert_at:]

        out_wat.write_text(final)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  injected corlib_loader: target fn {target_fn}, "
            f"helper fn {helper_fn} → wasp_get_assembly fn {wasp_get_assembly_fn} "
            f"with literal name at 0x{scratch_addr:x} → {out_wasm}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
