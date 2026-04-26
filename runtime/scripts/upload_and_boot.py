#!/usr/bin/env python3
"""Chunked upload of .NET assemblies into the wasp_runtime canister,
then boot Mono.

The canister now uses a raw binary protocol (no candid) so each chunk
is a single blob with this layout:

    [u32 LE name_len] [name bytes] [u8 final_flag] [chunk bytes]

The canister replies with the running [u64 LE total_bytes_for_assembly].
"""

from __future__ import annotations

import argparse
import struct
import subprocess
import sys
import tempfile
from pathlib import Path

CHUNK_SIZE = 256 * 1024  # candid blob arg has overhead; 256 KiB is comfy
CANISTER = "wasp_runtime"


def candid_blob_literal(data: bytes) -> str:
    parts = ["blob \""]
    for b in data:
        if b == 0x22 or b == 0x5C:
            parts.append("\\")
            parts.append(chr(b))
        elif 0x20 <= b < 0x7F:
            parts.append(chr(b))
        else:
            parts.append(f"\\{b:02x}")
    parts.append("\"")
    return "".join(parts)


def upload(name: str, path: Path) -> None:
    data = path.read_bytes()
    size = len(data)
    n_chunks = (size + CHUNK_SIZE - 1) // CHUNK_SIZE
    print(f"[upload] {name}  size={size}  chunks={n_chunks}")

    name_bytes = name.encode("utf-8")
    name_len = len(name_bytes)
    if name_len > 0xFFFFFFFF:
        raise SystemExit("name too long")

    for i in range(n_chunks):
        offset = i * CHUNK_SIZE
        end = min(offset + CHUNK_SIZE, size)
        chunk = data[offset:end]
        final_flag = 1 if end == size else 0

        payload = (
            struct.pack("<I", name_len)
            + name_bytes
            + struct.pack("<I", size)
            + bytes([final_flag])
            + chunk
        )

        arg = "(\n  " + candid_blob_literal(payload) + "\n)\n"

        with tempfile.NamedTemporaryFile(
            mode="w", suffix=".did", delete=False
        ) as tf:
            tf.write(arg)
            arg_path = tf.name

        try:
            r = subprocess.run(
                [
                    "dfx", "canister", "call", CANISTER, "upload_chunk",
                    "--argument-file", arg_path, "--output", "raw",
                ],
                capture_output=True, text=True, check=False,
            )
        finally:
            Path(arg_path).unlink(missing_ok=True)

        if r.returncode != 0:
            print(
                f"  chunk {i+1}/{n_chunks} ERROR rc={r.returncode}:"
                f" {r.stderr.strip()}",
                file=sys.stderr,
            )
            sys.exit(1)

        # raw output is hex; the candid blob reply is wrapped as a record
        # with Vec<u8>. dfx --output raw emits the bytes hex-encoded.
        try:
            reply_bytes = bytes.fromhex(r.stdout.strip())
            # Reply layout = candid serialised (blob N). Unwrap by
            # taking the trailing 8 bytes (u64 LE) we wrote.
            if len(reply_bytes) >= 8:
                running = struct.unpack(
                    "<Q", reply_bytes[-8:]
                )[0]
            else:
                running = -1
        except ValueError:
            running = -1

        print(
            f"  chunk {i+1}/{n_chunks}  offset={offset}"
            f"  len={end - offset}  final={final_flag}  running={running}"
        )


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("--name", action="append", required=True)
    p.add_argument("--file", action="append", required=True)
    args = p.parse_args()

    if len(args.name) != len(args.file):
        print("--name and --file count mismatch", file=sys.stderr)
        return 2

    for name, file_str in zip(args.name, args.file):
        upload(name, Path(file_str))

    print("==== boot ====")
    subprocess.run(
        ["dfx", "canister", "call", CANISTER, "boot"], check=False
    )

    print("==== logs (last 60 lines) ====")
    r = subprocess.run(
        ["dfx", "canister", "logs", CANISTER],
        capture_output=True, text=True, check=False,
    )
    for line in r.stdout.splitlines()[-60:]:
        print(line)

    return 0


if __name__ == "__main__":
    sys.exit(main())
