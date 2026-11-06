#!/usr/bin/env python3
"""
wasm-relax-simd-binary — directly walk a wasm binary and rewrite every
v128.load* / v128.store* memarg's alignment immediate to 0 (= 1 byte
aligned). Bypasses wasm-tools text round-trips entirely.

For SIMD memarg ops (prefix 0xFD, multi-byte LEB128 secondary opcode),
the encoding is:
   0xFD  <subop:u32 LEB>  <align:u32 LEB>  <offset:u32 LEB>  [<lane:u8>]

We do a proper instruction-level walk of the Code section. All other
sections are byte-copied unchanged.

Usage:
    relax_binary.py <input.wasm> <output.wasm>
"""

from __future__ import annotations

import sys
from pathlib import Path


SIMD_MEMARG_OPCODES = {
    0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
    0x08, 0x09, 0x0A, 0x0B, 0x5C, 0x5D,
    # lane variants — also have memarg + 1-byte lane index after offset
    0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B,
}
LANE_OPCODES = {0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x5B}

# Plain (non-SIMD) memarg opcodes (regular i32/i64/f32/f64 loads/stores).
MEMARG_OPCODES = set(range(0x28, 0x3F))


def read_uleb(data: bytes, i: int) -> tuple[int, int]:
    val = 0
    shift = 0
    n = 0
    while True:
        b = data[i + n]
        n += 1
        val |= (b & 0x7F) << shift
        if not (b & 0x80):
            return val, n
        shift += 7
        if n > 16:
            raise ValueError("uLEB128 too long")


def skip_uleb(data: bytes, i: int) -> int:
    n = 0
    while True:
        if not (data[i + n] & 0x80):
            return n + 1
        n += 1
        if n > 16:
            raise ValueError("uLEB128 too long")


def skip_sleb(data: bytes, i: int) -> int:
    """Skip a signed LEB128 (length-only, value not needed)."""
    return skip_uleb(data, i)  # same length encoding


def encode_uleb(val: int) -> bytes:
    out = bytearray()
    while True:
        b = val & 0x7F
        val >>= 7
        if val:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


# ---------------------------------------------------------------------------
# Module / section walk
# ---------------------------------------------------------------------------

def relax_wasm(data: bytes) -> tuple[bytes, int]:
    if data[0:4] != b"\x00asm":
        raise ValueError("not a wasm module")

    out = bytearray(data[0:8])
    n_relaxed = 0
    i = 8
    while i < len(data):
        sec_id = data[i]
        i += 1
        sec_size, n = read_uleb(data, i)
        i += n
        sec_body = data[i : i + sec_size]
        if sec_id == 10:  # Code section
            new_body, count = relax_code_section(sec_body)
            n_relaxed += count
            out.append(sec_id)
            out += encode_uleb(len(new_body))
            out += new_body
        else:
            out.append(sec_id)
            out += encode_uleb(sec_size)
            out += sec_body
        i += sec_size

    return bytes(out), n_relaxed


def relax_code_section(body: bytes) -> tuple[bytes, int]:
    out = bytearray()
    n_funcs, n = read_uleb(body, 0)
    out += encode_uleb(n_funcs)
    pos = n
    total = 0
    for _ in range(n_funcs):
        fbody_size, fn = read_uleb(body, pos)
        pos += fn
        fbody = body[pos : pos + fbody_size]
        pos += fbody_size
        new_fbody, count = relax_func_body(fbody)
        total += count
        out += encode_uleb(len(new_fbody))
        out += new_fbody
    return bytes(out), total


def relax_func_body(fbody: bytes) -> tuple[bytes, int]:
    out = bytearray()
    n_local_decls, n = read_uleb(fbody, 0)
    out += encode_uleb(n_local_decls)
    pos = n
    for _ in range(n_local_decls):
        cnt_n = skip_uleb(fbody, pos)
        out += fbody[pos : pos + cnt_n]
        pos += cnt_n
        out.append(fbody[pos])
        pos += 1
    new_instrs, count = walk_instructions(fbody, pos)
    out += new_instrs
    return bytes(out), count


# ---------------------------------------------------------------------------
# Instruction walk
# ---------------------------------------------------------------------------

def walk_instructions(buf: bytes, start: int) -> tuple[bytes, int]:
    out = bytearray()
    i = start
    n_relaxed = 0

    while i < len(buf):
        op = buf[i]

        # Block intros + control flow
        if op in (0x02, 0x03, 0x04):
            # block, loop, if + blocktype (s33 LEB128 OR 0x40 OR valtype byte)
            out.append(op); i += 1
            bt = buf[i]
            if bt == 0x40 or bt in (0x6F, 0x70, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F):
                out.append(bt); i += 1
            else:
                n = skip_sleb(buf, i)
                out += buf[i : i + n]; i += n
            continue

        if op in (0x05, 0x0B):
            # else, end
            out.append(op); i += 1
            continue

        if op == 0x0E:
            # br_table: vec(label) + default
            out.append(op); i += 1
            cnt, n = read_uleb(buf, i)
            out += buf[i : i + n]; i += n
            for _ in range(cnt + 1):
                n = skip_uleb(buf, i)
                out += buf[i : i + n]; i += n
            continue

        if op == 0x41:
            # i32.const + sLEB
            out.append(op); i += 1
            n = skip_sleb(buf, i)
            out += buf[i : i + n]; i += n
            continue
        if op == 0x42:
            # i64.const + sLEB (up to 10 bytes)
            out.append(op); i += 1
            n = skip_sleb(buf, i)
            out += buf[i : i + n]; i += n
            continue
        if op == 0x43:
            # f32.const + 4 bytes
            out += buf[i : i + 5]; i += 5
            continue
        if op == 0x44:
            # f64.const + 8 bytes
            out += buf[i : i + 9]; i += 9
            continue

        if op in (0x10, 0x12):
            # call, return_call
            out.append(op); i += 1
            n = skip_uleb(buf, i)
            out += buf[i : i + n]; i += n
            continue
        if op in (0x11, 0x13):
            # call_indirect, return_call_indirect (type idx, table idx)
            out.append(op); i += 1
            for _ in range(2):
                n = skip_uleb(buf, i)
                out += buf[i : i + n]; i += n
            continue
        if op in (0x0C, 0x0D, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0xD2):
            # br/br_if/local.get/set/tee/global.get/set/table.get/set/ref.func
            out.append(op); i += 1
            n = skip_uleb(buf, i)
            out += buf[i : i + n]; i += n
            continue
        if op == 0xD0:
            # ref.null + reftype byte
            out += buf[i : i + 2]; i += 2
            continue
        if op == 0x1B:
            out.append(op); i += 1
            continue
        if op == 0x1C:
            # select with type vec
            out.append(op); i += 1
            cnt, n = read_uleb(buf, i)
            out += buf[i : i + n]; i += n
            out += buf[i : i + cnt]; i += cnt
            continue

        if op in MEMARG_OPCODES:
            # plain memarg: align uLEB + offset uLEB
            out.append(op); i += 1
            an = skip_uleb(buf, i)
            # rewrite align to 0
            out.append(0)
            i += an
            on = skip_uleb(buf, i)
            out += buf[i : i + on]; i += on
            n_relaxed += 1
            continue

        if op in (0x3F, 0x40):
            # memory.size, memory.grow + 0x00
            out += buf[i : i + 2]; i += 2
            continue

        if op == 0xFC:
            # 0xFC subops: bulk memory + table ops
            out.append(op); i += 1
            sub, sn = read_uleb(buf, i)
            out += buf[i : i + sn]; i += sn
            if sub in (0, 1, 2, 3, 4, 5, 6, 7):
                pass  # trunc_sat — no immediates
            elif sub == 8:
                # memory.init: data idx + 0x00
                n = skip_uleb(buf, i); out += buf[i : i + n]; i += n
                out.append(buf[i]); i += 1
            elif sub == 9:
                n = skip_uleb(buf, i); out += buf[i : i + n]; i += n
            elif sub == 10:
                out += buf[i : i + 2]; i += 2
            elif sub == 11:
                out.append(buf[i]); i += 1
            elif sub == 12:
                for _ in range(2):
                    n = skip_uleb(buf, i); out += buf[i : i + n]; i += n
            elif sub == 13:
                n = skip_uleb(buf, i); out += buf[i : i + n]; i += n
            elif sub == 14:
                for _ in range(2):
                    n = skip_uleb(buf, i); out += buf[i : i + n]; i += n
            elif sub in (15, 16, 17):
                n = skip_uleb(buf, i); out += buf[i : i + n]; i += n
            else:
                raise ValueError(f"unknown 0xFC subopcode {sub} at {i}")
            continue

        if op == 0xFD:
            out.append(op); i += 1
            sub, sn = read_uleb(buf, i)
            out += buf[i : i + sn]; i += sn
            if sub in SIMD_MEMARG_OPCODES:
                an = skip_uleb(buf, i)
                out.append(0)
                i += an
                on = skip_uleb(buf, i)
                out += buf[i : i + on]; i += on
                if sub in LANE_OPCODES:
                    out.append(buf[i]); i += 1
                n_relaxed += 1
            elif sub == 12:
                # v128.const: 16-byte literal
                out += buf[i : i + 16]; i += 16
            elif sub == 13:
                # i8x16.shuffle: 16-byte lane indices
                out += buf[i : i + 16]; i += 16
            elif sub in (21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34):
                # extract_lane / replace_lane: 1-byte lane index
                out.append(buf[i]); i += 1
            else:
                pass  # SIMD arithmetic — no immediates
            continue

        # Default: assume no immediates (most numeric/conversion ops).
        out.append(op); i += 1

    return bytes(out), n_relaxed


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    in_wasm = Path(sys.argv[1])
    out_wasm = Path(sys.argv[2])
    data = in_wasm.read_bytes()
    new_data, n = relax_wasm(data)
    out_wasm.write_bytes(new_data)
    print(f"  relaxed {n} memarg ops (binary patch)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
