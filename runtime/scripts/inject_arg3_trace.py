#!/usr/bin/env python3
"""inject_arg3_trace — prepend `local.get 0; local.get 1; local.get 2;
call $<logger>` at the entry body of a single named function. Same
pattern as inject_arg_trace but for 3-arg functions where we want to
see all three args (e.g. mono_class_load_from_name(image, ns, name)).

Usage:
  inject_arg3_trace.py <in.wasm> <out.wasm> <fn_name> <logger>
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
    in_wasm, out_wasm, fn_name, logger = sys.argv[1:5]

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(["wasm-tools", "print", in_wasm, "-o", str(wat)], check=True)
        text = wat.read_text()

        hdr_re = re.compile(
            rf'^  \(func \${re.escape(fn_name)} \(;\d+;\)[^\n]*$',
            re.MULTILINE,
        )
        m = hdr_re.search(text)
        if not m:
            print(f"no func ${fn_name} found", file=sys.stderr)
            return 1
        body_start = m.end()

        after_hdr = text[body_start + 1:]
        first_line_end = after_hdr.find('\n')
        first_line = after_hdr[:first_line_end] if first_line_end > 0 else ''
        if '(local' in first_line:
            offset = body_start + 1 + first_line_end
        else:
            offset = body_start

        inject = (
            "\n    local.get 0"
            "\n    local.get 1"
            "\n    local.get 2"
            f"\n    call ${logger}"
        )
        new_text = text[:offset] + inject + text[offset:]
        out_wat.write_text(new_text)

        try:
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat), "-o", out_wasm],
                check=True,
            )
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(
            f"  injected `call ${logger}` (3 args) at start of ${fn_name}",
            file=sys.stderr,
        )
    return 0


if __name__ == "__main__":
    sys.exit(main())
