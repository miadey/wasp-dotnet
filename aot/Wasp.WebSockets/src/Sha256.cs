using System;
using System.Buffers.Binary;

namespace Wasp.WebSockets;

// Hand-rolled SHA-256 because System.Security.Cryptography.SHA256 is
// "PlatformNotSupported" under NativeAOT-LLVM wasi-wasm. Pure managed
// code — no syscalls, no allocations beyond the 32-byte output buffer.
internal static class Sha256
{
    private static readonly uint[] K =
    {
        0x428a2f98u, 0x71374491u, 0xb5c0fbcfu, 0xe9b5dba5u, 0x3956c25bu, 0x59f111f1u, 0x923f82a4u, 0xab1c5ed5u,
        0xd807aa98u, 0x12835b01u, 0x243185beu, 0x550c7dc3u, 0x72be5d74u, 0x80deb1feu, 0x9bdc06a7u, 0xc19bf174u,
        0xe49b69c1u, 0xefbe4786u, 0x0fc19dc6u, 0x240ca1ccu, 0x2de92c6fu, 0x4a7484aau, 0x5cb0a9dcu, 0x76f988dau,
        0x983e5152u, 0xa831c66du, 0xb00327c8u, 0xbf597fc7u, 0xc6e00bf3u, 0xd5a79147u, 0x06ca6351u, 0x14292967u,
        0x27b70a85u, 0x2e1b2138u, 0x4d2c6dfcu, 0x53380d13u, 0x650a7354u, 0x766a0abbu, 0x81c2c92eu, 0x92722c85u,
        0xa2bfe8a1u, 0xa81a664bu, 0xc24b8b70u, 0xc76c51a3u, 0xd192e819u, 0xd6990624u, 0xf40e3585u, 0x106aa070u,
        0x19a4c116u, 0x1e376c08u, 0x2748774cu, 0x34b0bcb5u, 0x391c0cb3u, 0x4ed8aa4au, 0x5b9cca4fu, 0x682e6ff3u,
        0x748f82eeu, 0x78a5636fu, 0x84c87814u, 0x8cc70208u, 0x90befffau, 0xa4506cebu, 0xbef9a3f7u, 0xc67178f2u,
    };

    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        Span<uint> H = stackalloc uint[8]
        {
            0x6a09e667u, 0xbb67ae85u, 0x3c6ef372u, 0xa54ff53au,
            0x510e527fu, 0x9b05688cu, 0x1f83d9abu, 0x5be0cd19u,
        };

        // Pad: append 0x80, then zeros, then 8-byte big-endian bit length.
        long bitLength = (long)data.Length * 8;
        int padLen = 64 - ((data.Length + 9) & 63);
        if (padLen == 64) padLen = 0;
        int totalLen = data.Length + 1 + padLen + 8;
        // For typical small inputs (<512 bytes here), avoid heap alloc.
        byte[] tmp = new byte[totalLen];
        data.CopyTo(tmp);
        tmp[data.Length] = 0x80;
        BinaryPrimitives.WriteInt64BigEndian(tmp.AsSpan(totalLen - 8), bitLength);

        Span<uint> W = stackalloc uint[64];
        for (int chunk = 0; chunk < totalLen; chunk += 64)
        {
            for (int i = 0; i < 16; i++)
                W[i] = BinaryPrimitives.ReadUInt32BigEndian(tmp.AsSpan(chunk + i * 4, 4));
            for (int i = 16; i < 64; i++)
            {
                uint s0 = RotR(W[i - 15], 7) ^ RotR(W[i - 15], 18) ^ (W[i - 15] >> 3);
                uint s1 = RotR(W[i - 2], 17) ^ RotR(W[i - 2], 19) ^ (W[i - 2] >> 10);
                W[i] = W[i - 16] + s0 + W[i - 7] + s1;
            }

            uint a = H[0], b = H[1], c = H[2], d = H[3];
            uint e = H[4], f = H[5], g = H[6], h = H[7];
            for (int i = 0; i < 64; i++)
            {
                uint S1 = RotR(e, 6) ^ RotR(e, 11) ^ RotR(e, 25);
                uint ch = (e & f) ^ ((~e) & g);
                uint temp1 = h + S1 + ch + K[i] + W[i];
                uint S0 = RotR(a, 2) ^ RotR(a, 13) ^ RotR(a, 22);
                uint maj = (a & b) ^ (a & c) ^ (b & c);
                uint temp2 = S0 + maj;
                h = g; g = f; f = e; e = d + temp1;
                d = c; c = b; b = a; a = temp1 + temp2;
            }

            H[0] += a; H[1] += b; H[2] += c; H[3] += d;
            H[4] += e; H[5] += f; H[6] += g; H[7] += h;
        }

        var output = new byte[32];
        for (int i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(i * 4, 4), H[i]);
        return output;
    }

    private static uint RotR(uint x, int n) => (x >> n) | (x << (32 - n));
}
