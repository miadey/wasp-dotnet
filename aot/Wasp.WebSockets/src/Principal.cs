using System;
using System.Text;

namespace Wasp.WebSockets;

// Principal text encoding per the IC interface spec:
//   text(p) = lowercase(base32-no-pad(crc32-be(p) || p)) with '-' every 5 chars.
public static class Principal
{
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string ToText(byte[] principal)
    {
        // CRC32 (IEEE 802.3, polynomial 0xEDB88320, initial 0xFFFFFFFF, final XOR 0xFFFFFFFF).
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < principal.Length; i++)
        {
            crc ^= principal[i];
            for (int b = 0; b < 8; b++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        crc = ~crc;

        // payload = crc(big-endian) || principal
        var payload = new byte[4 + principal.Length];
        payload[0] = (byte)(crc >> 24);
        payload[1] = (byte)(crc >> 16);
        payload[2] = (byte)(crc >> 8);
        payload[3] = (byte)crc;
        Buffer.BlockCopy(principal, 0, payload, 4, principal.Length);

        // Base32 encode.
        var sb = new StringBuilder();
        int bitBuffer = 0;
        int bitCount = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            bitBuffer = (bitBuffer << 8) | payload[i];
            bitCount += 8;
            while (bitCount >= 5)
            {
                int idx = (bitBuffer >> (bitCount - 5)) & 0x1F;
                sb.Append(Base32Alphabet[idx]);
                bitCount -= 5;
            }
        }
        if (bitCount > 0)
        {
            int idx = (bitBuffer << (5 - bitCount)) & 0x1F;
            sb.Append(Base32Alphabet[idx]);
        }

        // Insert '-' every 5 chars.
        var raw = sb.ToString();
        var grouped = new StringBuilder(raw.Length + raw.Length / 5);
        for (int i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % 5 == 0) grouped.Append('-');
            grouped.Append(raw[i]);
        }
        return grouped.ToString();
    }
}
