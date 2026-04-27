#!/usr/bin/env python3
"""patch_disable_g_assert — wat-only patch that defangs mono's
g_assert trap at one or more failing call sites.

Replaces the standard 4-instruction assert epilogue:
    i32.const <file_addr>
    i32.const <line>
    call <assert_helper>
    unreachable
with a single `return` so the surrounding function exits cleanly
instead of triggering mono's fatal-log → exit chain.

Use when you've identified a specific assert by line number that is
firing erroneously due to incomplete embedding (e.g. corelib not
findable via mono's normal lookup paths) and you want to let
execution continue past it to see what fails next.

Usage:
  patch_disable_g_assert.py <in.wasm> <out.wasm> --line <N>
                                                 [--line <M> ...]
"""

import argparse
import re
import subprocess
import sys
import tempfile
from pathlib import Path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("in_wasm")
    ap.add_argument("out_wasm")
    ap.add_argument("--line", type=int, action="append", required=True,
                    help="line number(s) to disable; can be repeated")
    args = ap.parse_args()

    with tempfile.TemporaryDirectory() as td:
        wat = Path(td) / "in.wat"
        out_wat = Path(td) / "out.wat"
        subprocess.run(
            ["wasm-tools", "print", args.in_wasm, "-o", str(wat)], check=True
        )
        text = wat.read_text()

        # Match: <indent>i32.const <file>\n<same>i32.const <line>\n<same>call <fn>\n<same>unreachable
        # File addr varies; assert helper fn varies between builds. Match by line number.
        total = 0
        for line_no in args.line:
            count = 0

            def replace_simple(m):
                nonlocal count
                count += 1
                # Push i32.const 0 then return — covers fns that return
                # i32 (most). For void fns the const is dead before
                # return and validation accepts it. For fns returning
                # different types, this would still fail — but the
                # asserts we target are reached via paths where the
                # return value would be an i32 NULL/0.
                indent = m.group('indent')
                return f"{indent}i32.const 0\n{indent}return"

            # Form A — symbol-stripped SIMD build:
            #   i32.const <file_addr> ; i32.const <line> ; call <fn> ; unreachable
            pat_a = re.compile(
                r'(?P<indent>[ \t]+)i32\.const \d+\s*\n'
                r'(?P=indent)i32\.const ' + str(line_no) + r'\s*\n'
                r'(?P=indent)call \d+\s*\n'
                r'(?P=indent)unreachable\s*$',
                re.MULTILINE,
            )
            text = pat_a.sub(replace_simple, text)

            # Form B — symbol-preserved no-SIMD build:
            #   global.get N ; i32.const <off> ; i32.add ; i32.const <line> ; call <fn> ; unreachable
            pat_b = re.compile(
                r'(?P<indent>[ \t]+)global\.get \d+\s*\n'
                r'(?P=indent)i32\.const \d+\s*\n'
                r'(?P=indent)i32\.add\s*\n'
                r'(?P=indent)i32\.const ' + str(line_no) + r'\s*\n'
                r'(?P=indent)call \d+\s*\n'
                r'(?P=indent)unreachable\s*$',
                re.MULTILINE,
            )
            text = pat_b.sub(replace_simple, text)
            print(
                f"  defanged {count} assert site(s) at line {line_no}",
                file=sys.stderr,
            )
            total += count
        if total == 0:
            print("no matching assert sites found", file=sys.stderr)
            return 1
        out_wat.write_text(text)
        try:
            subprocess.run(
                ["wasm-tools", "parse", str(out_wat), "-o", args.out_wasm],
                check=True,
            )
        except subprocess.CalledProcessError:
            print(f"wasm-tools parse failed; check {out_wat}", file=sys.stderr)
            return 1
        print(f"  wrote {args.out_wasm} ({total} assert(s) defanged)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
