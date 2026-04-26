#!/usr/bin/env python3
"""
webcil_to_dll.py — Convert a .NET 10 Blazor webcil-wrapped wasm assembly
back into a standard MZ/PE managed-assembly .dll.

The .NET 10 Blazor publish output produces files like
    System.Runtime.hx5gh428tl.wasm
which are tiny WebAssembly modules wrapping a single managed assembly that
has been re-encoded into the "Webcil" framing (a slimmed-down PE/COFF
variant with a 28- or 32-byte header).  The original PE optional header,
DOS stub, etc. are *thrown away* during conversion, so rebuilding a real
.dll requires synthesizing the PE scaffolding around the surviving
section payload.

Pipeline:
  1) Parse the wasm container (sections starting at offset 8).
  2) Locate the Data section (id 11), read passive data segment 1 — that
     is the raw Webcil payload.
  3) Parse the Webcil header + Webcil section directory; copy each
     section's raw data.
  4) Emit a minimal PE32 .dll: DOS header (with the canonical
     "This program cannot be run in DOS mode" stub), PE signature,
     COFF header, optional header, section table, then the section
     payloads at the same RVAs they had in the webcil image.

Reference: dotnet/runtime
  src/tasks/Microsoft.NET.WebAssembly.Webcil/WebcilWasmWrapper.cs
  src/tasks/Microsoft.NET.WebAssembly.Webcil/WebcilConverter.cs
  src/tasks/Microsoft.NET.WebAssembly.Webcil/WebcilReader.cs
  src/coreclr/tools/Common/Wasm/Webcil.cs
"""

from __future__ import annotations

import argparse
import struct
import sys
from dataclasses import dataclass
from typing import List, Tuple

# ---------------------------------------------------------------------------
# Wasm container parsing
# ---------------------------------------------------------------------------

WASM_MAGIC = b"\x00asm"
WASM_VERSION = 1

SECTION_DATA = 11


def _read_uleb128(buf: bytes, pos: int) -> Tuple[int, int]:
    """Decode an unsigned LEB128 integer. Returns (value, new_pos)."""
    result = 0
    shift = 0
    while True:
        b = buf[pos]
        pos += 1
        result |= (b & 0x7F) << shift
        if (b & 0x80) == 0:
            break
        shift += 7
        if shift > 63:
            raise ValueError("ULEB128 too large")
    return result, pos


def extract_webcil_payload(wasm_bytes: bytes) -> bytes:
    """Walk the wasm module, find the Data section, and return passive
    data segment 1 (the webcil payload). Segment 0 is just a 4-byte
    little-endian payload size."""
    if wasm_bytes[:4] != WASM_MAGIC:
        raise ValueError("not a wasm module (bad magic)")
    version = struct.unpack_from("<I", wasm_bytes, 4)[0]
    if version != WASM_VERSION:
        raise ValueError(f"unexpected wasm version {version}")

    pos = 8
    while pos < len(wasm_bytes):
        section_id = wasm_bytes[pos]
        pos += 1
        section_size, pos = _read_uleb128(wasm_bytes, pos)
        section_end = pos + section_size

        if section_id != SECTION_DATA:
            pos = section_end
            continue

        # Inside the Data section.
        num_segments, pos = _read_uleb128(wasm_bytes, pos)
        if num_segments < 2:
            raise ValueError(
                f"webcil-wrapped wasm should have >=2 data segments, got {num_segments}"
            )

        # Segment 0: passive, contains a 4-byte uint32 payload size (+padding).
        flags, pos = _read_uleb128(wasm_bytes, pos)
        if flags != 1:
            raise ValueError(f"segment 0 not passive (flags={flags})")
        seg0_size, pos = _read_uleb128(wasm_bytes, pos)
        declared_payload_size = struct.unpack_from("<I", wasm_bytes, pos)[0]
        pos += seg0_size  # skip 4-byte size + any padding

        # Segment 1: passive, contains the webcil payload.
        flags, pos = _read_uleb128(wasm_bytes, pos)
        if flags != 1:
            raise ValueError(f"segment 1 not passive (flags={flags})")
        seg1_size, pos = _read_uleb128(wasm_bytes, pos)
        payload = wasm_bytes[pos : pos + seg1_size]
        if len(payload) != seg1_size:
            raise ValueError("truncated webcil payload")
        if seg1_size != declared_payload_size:
            # Not fatal — the declared size in segment 0 is informational —
            # but a mismatch is suspicious.
            print(
                f"warning: declared payload size {declared_payload_size} "
                f"!= segment 1 size {seg1_size}",
                file=sys.stderr,
            )
        return payload

    raise ValueError("no Data section found in wasm module")


# ---------------------------------------------------------------------------
# Webcil parsing
# ---------------------------------------------------------------------------

WEBCIL_MAGIC = 0x4C496257  # 'WbIL' little-endian
V0_HEADER_SIZE = 28
V1_HEADER_SIZE = 32
WEBCIL_SECTION_HEADER_SIZE = 16
WEBCIL_SECTION_ALIGNMENT = 16


@dataclass
class WebcilHeader:
    id: int
    version_major: int
    version_minor: int
    coff_sections: int
    reserved0: int
    pe_cli_header_rva: int
    pe_cli_header_size: int
    pe_debug_rva: int
    pe_debug_size: int
    table_base: int  # only meaningful for V1


@dataclass
class WebcilSection:
    virtual_size: int
    virtual_address: int
    size_of_raw_data: int
    pointer_to_raw_data: int


def parse_webcil(payload: bytes) -> Tuple[WebcilHeader, List[WebcilSection], bytes]:
    """Return the header, section directory, and the raw payload
    (so callers can index into it via PointerToRawData)."""
    if len(payload) < V0_HEADER_SIZE:
        raise ValueError("webcil payload too small for header")

    fields = struct.unpack_from("<IHHHHIIII", payload, 0)
    header = WebcilHeader(
        id=fields[0],
        version_major=fields[1],
        version_minor=fields[2],
        coff_sections=fields[3],
        reserved0=fields[4],
        pe_cli_header_rva=fields[5],
        pe_cli_header_size=fields[6],
        pe_debug_rva=fields[7],
        pe_debug_size=fields[8],
        table_base=0xFFFFFFFF,
    )

    if header.id != WEBCIL_MAGIC:
        raise ValueError(f"bad webcil magic 0x{header.id:08x} (expected 'WbIL')")
    if header.version_major not in (0, 1):
        raise ValueError(f"unsupported webcil major version {header.version_major}")

    if header.version_major >= 1:
        if len(payload) < V1_HEADER_SIZE:
            raise ValueError("webcil V1 payload too small for extended header")
        header.table_base = struct.unpack_from("<I", payload, V0_HEADER_SIZE)[0]
        header_size = V1_HEADER_SIZE
    else:
        header_size = V0_HEADER_SIZE

    sections: List[WebcilSection] = []
    pos = header_size
    for _ in range(header.coff_sections):
        vs, va, srd, prd = struct.unpack_from("<IIII", payload, pos)
        sections.append(WebcilSection(vs, va, srd, prd))
        pos += WEBCIL_SECTION_HEADER_SIZE

    return header, sections, payload


# ---------------------------------------------------------------------------
# PE/COFF synthesis
# ---------------------------------------------------------------------------

# Canonical 128-byte DOS header + stub: "MZ", standard fields, e_lfanew=0x80,
# followed by the classic "This program cannot be run in DOS mode." stub.
# This matches what csc.exe / ilasm produce. The PE signature lands at offset 0x80.
def _build_dos_header_and_stub() -> bytes:
    # IMAGE_DOS_HEADER is 64 bytes:
    #   e_magic    "MZ"      (2)
    #   e_cblp     0x90      (2)
    #   e_cp       0x03      (2)
    #   e_crlc     0x00      (2)
    #   e_cparhdr  0x04      (2)
    #   e_minalloc 0x00      (2)
    #   e_maxalloc 0xffff    (2)
    #   e_ss       0x00      (2)
    #   e_sp       0xb8      (2)
    #   e_csum     0x00      (2)
    #   e_ip       0x00      (2)
    #   e_cs       0x00      (2)
    #   e_lfarlc   0x40      (2)
    #   e_ovno     0x00      (2)
    #   e_res[4]   0         (8)
    #   e_oemid    0         (2)
    #   e_oeminfo  0         (2)
    #   e_res2[10] 0         (20)
    #   e_lfanew   0x80      (4)
    dos_header = struct.pack(
        "<2sHHHHHHHHHHHHH8sHH20sI",
        b"MZ", 0x90, 3, 0, 4, 0, 0xFFFF, 0, 0xB8, 0, 0, 0, 0x40, 0,
        b"\x00" * 8, 0, 0, b"\x00" * 20, 0x80,
    )
    assert len(dos_header) == 64, len(dos_header)
    # 64-byte DOS stub: standard "This program cannot be run in DOS mode." program.
    dos_stub = bytes.fromhex(
        "0e1fba0e00b409cd21b8014ccd21"
        "5468697320"
        "70726f6772616d2063616e6e6f74"
        "2062652072756e20696e20444f53"
        "206d6f64652e0d0d0a"
        "2400000000000000"
    )
    assert len(dos_stub) == 64, len(dos_stub)
    return dos_header + dos_stub


_DOS_HEADER_AND_STUB = _build_dos_header_and_stub()
assert len(_DOS_HEADER_AND_STUB) == 128, len(_DOS_HEADER_AND_STUB)

# COFF machine types
IMAGE_FILE_MACHINE_I386 = 0x014C
IMAGE_FILE_MACHINE_AMD64 = 0x8664

# COFF characteristics flags
IMAGE_FILE_EXECUTABLE_IMAGE = 0x0002
IMAGE_FILE_LINE_NUMS_STRIPPED = 0x0004
IMAGE_FILE_LOCAL_SYMS_STRIPPED = 0x0008
IMAGE_FILE_LARGE_ADDRESS_AWARE = 0x0020
IMAGE_FILE_32BIT_MACHINE = 0x0100
IMAGE_FILE_DEBUG_STRIPPED = 0x0200
IMAGE_FILE_DLL = 0x2000

PE_OPTIONAL_HEADER_SIZE = 224  # PE32 (NOT PE32+); 16 directory entries
SECTION_HEADER_SIZE = 40
FILE_ALIGNMENT = 0x200
SECTION_ALIGNMENT = 0x2000  # matches what Roslyn emits for managed assemblies

# Section flag bits
IMAGE_SCN_CNT_CODE = 0x00000020
IMAGE_SCN_CNT_INITIALIZED_DATA = 0x00000040
IMAGE_SCN_MEM_DISCARDABLE = 0x02000000
IMAGE_SCN_MEM_EXECUTE = 0x20000000
IMAGE_SCN_MEM_READ = 0x40000000
IMAGE_SCN_MEM_WRITE = 0x80000000


def _section_characteristics(name: bytes) -> int:
    """Reasonable defaults for the standard managed-assembly sections."""
    n = name.rstrip(b"\x00")
    if n == b".text":
        return IMAGE_SCN_CNT_CODE | IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ
    if n == b".rsrc":
        return IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ
    if n == b".reloc":
        return (
            IMAGE_SCN_CNT_INITIALIZED_DATA
            | IMAGE_SCN_MEM_DISCARDABLE
            | IMAGE_SCN_MEM_READ
        )
    if n == b".sdata":
        return (
            IMAGE_SCN_CNT_INITIALIZED_DATA
            | IMAGE_SCN_MEM_READ
            | IMAGE_SCN_MEM_WRITE
        )
    return IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ


def _align_up(value: int, alignment: int) -> int:
    return (value + alignment - 1) & ~(alignment - 1)


# Webcil throws away the original section names. Reconstruct what a
# normal C# managed assembly would have: .text, .rsrc, .reloc, in that
# order. If there are more sections than expected, fall back to ".sec%d".
def _guess_section_name(index: int, total: int) -> bytes:
    canonical = [b".text", b".rsrc", b".reloc"]
    if total <= len(canonical) and index < len(canonical):
        # When there are 1-3 sections, assume the standard suffix mapping:
        # 1 section -> [.text]; 2 -> [.text, .reloc]; 3 -> [.text, .rsrc, .reloc].
        if total == 1:
            mapping = [b".text"]
        elif total == 2:
            mapping = [b".text", b".reloc"]
        else:
            mapping = [b".text", b".rsrc", b".reloc"]
        return mapping[index].ljust(8, b"\x00")
    return f".sec{index}".encode().ljust(8, b"\x00")


def webcil_to_dll(wasm_bytes: bytes) -> bytes:
    """Top-level: webcil-wrapped wasm bytes -> standard managed PE32 .dll bytes."""
    payload = extract_webcil_payload(wasm_bytes)
    header, sections, payload_bytes = parse_webcil(payload)

    num_sections = len(sections)
    headers_size = (
        len(_DOS_HEADER_AND_STUB)
        + 4  # PE\0\0
        + 20  # COFF file header
        + PE_OPTIONAL_HEADER_SIZE
        + SECTION_HEADER_SIZE * num_sections
    )
    size_of_headers = _align_up(headers_size, FILE_ALIGNMENT)

    # Compute the new PE PointerToRawData for each section. The RVAs (and
    # hence VirtualAddress) are kept identical, so the CLI header /
    # metadata-tables / IL bodies all stay valid.
    pe_section_layout: List[Tuple[int, int]] = []  # (pointer_to_raw_data, size_of_raw_data)
    pe_pos = size_of_headers
    for sec in sections:
        raw_size = _align_up(sec.size_of_raw_data, FILE_ALIGNMENT)
        pe_section_layout.append((pe_pos, raw_size))
        pe_pos += raw_size

    size_of_image = _align_up(
        max(sec.virtual_address + max(sec.virtual_size, sec.size_of_raw_data)
            for sec in sections),
        SECTION_ALIGNMENT,
    )

    size_of_code = sum(
        sec.size_of_raw_data
        for sec, name_idx in zip(sections, range(num_sections))
        if _guess_section_name(name_idx, num_sections).rstrip(b"\x00") == b".text"
    )
    size_of_initialized_data = sum(
        sec.size_of_raw_data
        for sec, name_idx in zip(sections, range(num_sections))
        if _guess_section_name(name_idx, num_sections).rstrip(b"\x00") != b".text"
    )

    # Find the .text section RVA -> BaseOfCode.
    base_of_code = 0
    base_of_data = 0
    for idx, sec in enumerate(sections):
        nm = _guess_section_name(idx, num_sections).rstrip(b"\x00")
        if nm == b".text" and base_of_code == 0:
            base_of_code = sec.virtual_address
        elif nm != b".text" and base_of_data == 0:
            base_of_data = sec.virtual_address

    # Build COFF file header (20 bytes).
    coff = struct.pack(
        "<HHIIIHH",
        IMAGE_FILE_MACHINE_I386,                   # Machine
        num_sections,                              # NumberOfSections
        0,                                         # TimeDateStamp
        0,                                         # PointerToSymbolTable
        0,                                         # NumberOfSymbols
        PE_OPTIONAL_HEADER_SIZE,                   # SizeOfOptionalHeader
        IMAGE_FILE_EXECUTABLE_IMAGE                # Characteristics
        | IMAGE_FILE_LINE_NUMS_STRIPPED
        | IMAGE_FILE_LOCAL_SYMS_STRIPPED
        | IMAGE_FILE_LARGE_ADDRESS_AWARE
        | IMAGE_FILE_DLL,
    )

    # Build PE32 optional header (224 bytes incl. 16 directory entries).
    # Layout (PE32):
    #  HH BB II IIIIIII HH HH HH HH II II II II HH HH II II IIII II II
    # That's tricky; build it field-by-field.
    opt_std = struct.pack(
        "<HBBIIIIII",
        0x010B,                # Magic (PE32)
        8, 0,                  # Linker major/minor
        size_of_code,
        size_of_initialized_data,
        0,                     # SizeOfUninitializedData
        0,                     # AddressOfEntryPoint (0 for managed DLLs)
        base_of_code,
        base_of_data,
    )

    opt_win = struct.pack(
        "<IIIHHHHHHIIIIHHIIII",
        0x10000000,            # ImageBase
        SECTION_ALIGNMENT,
        FILE_ALIGNMENT,
        4, 0,                  # OS major/minor
        0, 0,                  # Image major/minor
        4, 0,                  # Subsystem major/minor
        0,                     # Win32VersionValue
        size_of_image,
        size_of_headers,
        0,                     # CheckSum
        3,                     # Subsystem (IMAGE_SUBSYSTEM_WINDOWS_CUI)
        0x8540,                # DllCharacteristics (NX_COMPAT | NO_SEH | DYNAMIC_BASE | TERMINAL_SERVER_AWARE)
        0x00100000,            # SizeOfStackReserve
        0x00001000,            # SizeOfStackCommit
        0x00100000,            # SizeOfHeapReserve
        0x00001000,            # SizeOfHeapCommit
    )

    opt_misc = struct.pack(
        "<II",
        0,                     # LoaderFlags
        16,                    # NumberOfRvaAndSizes
    )

    # Data directories (16 * 8 bytes). Only fill in CLI header and Debug.
    DIR_DEBUG = 6
    DIR_CLI = 14
    directories = [(0, 0)] * 16
    if header.pe_cli_header_rva and header.pe_cli_header_size:
        directories[DIR_CLI] = (header.pe_cli_header_rva, header.pe_cli_header_size)
    if header.pe_debug_rva and header.pe_debug_size:
        directories[DIR_DEBUG] = (header.pe_debug_rva, header.pe_debug_size)
    dirs_blob = b"".join(struct.pack("<II", rva, sz) for rva, sz in directories)

    optional_header = opt_std + opt_win + opt_misc + dirs_blob
    if len(optional_header) != PE_OPTIONAL_HEADER_SIZE:
        raise AssertionError(
            f"optional header is {len(optional_header)} bytes, expected {PE_OPTIONAL_HEADER_SIZE}"
        )

    # Build the section table.
    section_table_parts = []
    for idx, sec in enumerate(sections):
        name = _guess_section_name(idx, num_sections)
        ptr, raw = pe_section_layout[idx]
        chars = _section_characteristics(name)
        # The raw size we record in the header is the *aligned* size that
        # actually lives in the file. (Some assemblers write the unaligned
        # size; either is accepted by the loader.)
        section_table_parts.append(
            struct.pack(
                "<8sIIIIIIHHI",
                name,
                max(sec.virtual_size, sec.size_of_raw_data),  # VirtualSize
                sec.virtual_address,                          # VirtualAddress
                raw,                                          # SizeOfRawData (aligned)
                ptr,                                          # PointerToRawData
                0,                                            # PointerToRelocations
                0,                                            # PointerToLinenumbers
                0,                                            # NumberOfRelocations
                0,                                            # NumberOfLinenumbers
                chars,
            )
        )
    section_table = b"".join(section_table_parts)

    # Assemble headers and pad to file alignment.
    pe_headers = (
        _DOS_HEADER_AND_STUB
        + b"PE\x00\x00"
        + coff
        + optional_header
        + section_table
    )
    pe_headers += b"\x00" * (size_of_headers - len(pe_headers))

    # Append section payloads (copied straight from the webcil image, then
    # zero-padded out to FILE_ALIGNMENT).
    output = bytearray(pe_headers)
    for sec, (ptr, raw) in zip(sections, pe_section_layout):
        if len(output) != ptr:
            raise AssertionError(
                f"section layout mismatch: at {len(output)}, expected {ptr}"
            )
        chunk = payload_bytes[
            sec.pointer_to_raw_data : sec.pointer_to_raw_data + sec.size_of_raw_data
        ]
        if len(chunk) != sec.size_of_raw_data:
            raise ValueError(
                f"webcil section truncated: wanted {sec.size_of_raw_data} bytes "
                f"at offset {sec.pointer_to_raw_data}, got {len(chunk)}"
            )
        output.extend(chunk)
        pad = raw - sec.size_of_raw_data
        if pad:
            output.extend(b"\x00" * pad)

    return bytes(output)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv: List[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Convert a webcil-wrapped wasm assembly back into a standard PE32 .dll."
    )
    parser.add_argument("input", help="Path to a .wasm file produced by Blazor publish.")
    parser.add_argument("output", help="Path to write the reconstructed .dll to.")
    args = parser.parse_args(argv)

    with open(args.input, "rb") as f:
        wasm_bytes = f.read()

    dll_bytes = webcil_to_dll(wasm_bytes)

    with open(args.output, "wb") as f:
        f.write(dll_bytes)

    print(
        f"wrote {len(dll_bytes)} bytes to {args.output} "
        f"(input: {len(wasm_bytes)} bytes wasm)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
