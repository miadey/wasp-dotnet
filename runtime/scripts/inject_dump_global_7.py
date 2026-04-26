#!/usr/bin/env python3
"""inject_dump_global_7 — modify the merged canister.wasm to add a
`canister_query dump_global_7` export that prints global 7's runtime
value via ic0.debug_print.

Strategy: print the wasm to wat, find the existing canister_query_ping
function, REPLACE its body with code that:
  1. Reads global.get 7
  2. Formats it as 8 hex chars
  3. Calls ic0.debug_print
  4. Replies with the formatted string via the existing reply_blob

We hijack ping rather than adding a new function to avoid having to
manage the wasm function index space.
"""

import re
import subprocess
import sys
import tempfile
from pathlib import Path


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

        # Find ping export → function index.
        m = re.search(r'\(export "canister_query ping" \(func (\d+)\)\)', text)
        if not m:
            print("no canister_query ping export found", file=sys.stderr)
            return 1
        ping_idx = int(m.group(1))
        print(f"  ping fn idx = {ping_idx}", file=sys.stderr)

        # Find the function definition. Pattern: "  (func (;NNN;) ..." then body until matching ")".
        # We want to overwrite the body. Find "(func (;{ping_idx};)" and the matching closing ).
        marker = f"  (func (;{ping_idx};)"
        start = text.find(marker)
        if start < 0:
            print(f"no func {ping_idx}", file=sys.stderr)
            return 1
        # Walk forward to find the matching close paren at the same indent (2 spaces + ')').
        end = text.find("\n  )\n", start)
        if end < 0:
            print(f"no end for func {ping_idx}", file=sys.stderr)
            return 1
        end += len("\n  )")  # include the closing paren

        # Find imports we need: ic0.debug_print, ic0.msg_reply_data_append, ic0.msg_reply.
        def find_import(module, name):
            m = re.search(
                rf'\(import "{module}" "{name}" \(func \$?(\S+)?\s*\(?;?(\d+)?;?\)?',
                text,
            )
            return m

        # Use simpler regex: imports look like
        #   (import "ic0" "debug_print" (func (;N;) (type T)))
        def find_func_import_idx(module, name):
            m = re.search(
                rf'\(import "{module}" "{name}" \(func \(;(\d+);\)',
                text,
            )
            return int(m.group(1)) if m else None

        debug_print_idx = find_func_import_idx("ic0", "debug_print")
        reply_append_idx = find_func_import_idx("ic0", "msg_reply_data_append")
        reply_idx = find_func_import_idx("ic0", "msg_reply")
        print(
            f"  ic0.debug_print = fn {debug_print_idx}, "
            f"msg_reply_data_append = fn {reply_append_idx}, "
            f"msg_reply = fn {reply_idx}",
            file=sys.stderr,
        )

        # Build the replacement function body. Reads global 7, formats
        # 10 ASCII chars "0xNNNNNNNN", reserves a fixed scratch region
        # in wasp_canister's data section starting at offset 1056000
        # (above our static literals). Replies as candid blob.
        # Layout at that scratch:
        #   offset 0..6: "DIDL\01\6d"
        #   offset 6..9: "\7b\01\00"
        #   offset 9   : LEB(10) = 0x0a (length of payload)
        #   offset 10..20: "0x" + 8 hex chars
        scratch_base = 1056000
        scratch_end = scratch_base + 20
        # DIDL bytes: 44 49 44 4c 01 6d 7b 01 00 0a (10 bytes header), then 10 payload bytes.
        replacement = f"""  (func (;{ping_idx};) (type 0)
    (local i32 i32 i32)
    ;; Byte-by-byte DIDL header at scratch.
    i32.const {scratch_base + 0}  i32.const 0x44 i32.store8
    i32.const {scratch_base + 1}  i32.const 0x49 i32.store8
    i32.const {scratch_base + 2}  i32.const 0x44 i32.store8
    i32.const {scratch_base + 3}  i32.const 0x4c i32.store8
    i32.const {scratch_base + 4}  i32.const 0x01 i32.store8
    i32.const {scratch_base + 5}  i32.const 0x6d i32.store8
    i32.const {scratch_base + 6}  i32.const 0x7b i32.store8
    i32.const {scratch_base + 7}  i32.const 0x01 i32.store8
    i32.const {scratch_base + 8}  i32.const 0x00 i32.store8
    i32.const {scratch_base + 9}  i32.const 0x0a i32.store8

    ;; "0x" prefix
    i32.const {scratch_base + 10} i32.const 0x30 i32.store8
    i32.const {scratch_base + 11} i32.const 0x78 i32.store8

    ;; Format global 7 as 8 hex chars at scratch+12..20.
    global.get 7
    local.set 0
    i32.const {scratch_base + 12}
    local.set 1
    block $exit
    loop $hex
        local.get 1
        i32.const {scratch_end}
        i32.ge_s
        br_if $exit
        ;; nibble = (val >> 28) & 0xF
        local.get 0
        i32.const 28
        i32.shr_u
        i32.const 15
        i32.and
        local.set 2
        ;; addr = local 1
        local.get 1
        ;; ascii = (nibble<10) ? '0'+nibble : 'a'-10+nibble
        local.get 2
        i32.const 10
        i32.lt_s
        if (result i32)
            local.get 2
            i32.const 0x30
            i32.add
        else
            local.get 2
            i32.const 0x57
            i32.add
        end
        i32.store8
        ;; val <<= 4
        local.get 0
        i32.const 4
        i32.shl
        local.set 0
        ;; addr += 1
        local.get 1
        i32.const 1
        i32.add
        local.set 1
        br $hex
    end
    end

    ;; Append the 20 bytes and reply.
    i32.const {scratch_base}
    i32.const 20
    call {reply_append_idx}
    call {reply_idx}
  )"""

        new_text = text[:start] + replacement + text[end:]
        out_wat.write_text(new_text)
        try:
            subprocess.run(["wasm-tools", "parse", str(out_wat), "-o", str(out_wasm)], check=True)
        except subprocess.CalledProcessError as e:
            # Print first error from stderr if any
            print("wasm-tools parse failed", file=sys.stderr)
            return 1
        print(f"  injected dump_global_7 → {out_wasm}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
