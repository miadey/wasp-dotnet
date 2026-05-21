using System;
using System.Collections.Generic;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Chat messages persisted in stable memory at offset 4096.
///
/// Layout:
///   uint32  count
///   for each message:
///     uint64  unix-time-ms
///     uint32  utf8-length of sender || "|" || text
///     bytes   "sender|text" (sender = short principal id prefix)
///
/// Capacity: last 50 messages. Older messages are dropped from the
/// front when the buffer fills, so the canister doesn't grow without
/// bound and the renderer doesn't return megabytes of HTML.
/// </summary>
public sealed unsafe class ChatService
{
    private const ulong Offset = 4096;
    private const ulong MagicOffset = 4092;
    private const uint Magic = 0x43484154; // "CHAT"
    private const int MaxMessages = 50;

    public sealed record Message(long AtMs, string Sender, string Text);

    public IReadOnlyList<Message> Messages
    {
        get
        {
            EnsureGrown();
            if (ReadMagic() != Magic) return Array.Empty<Message>();
            return Read();
        }
    }

    public void Post(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
            return;
        text = text.Length > 280 ? text.Substring(0, 280) : text;
        var sender = ShortPrincipal();
        var atMs = (long)(Ic0.time() / 1_000_000UL);
        Append(new Message(atMs, sender, text));
    }

    // ─── Storage ─────────────────────────────────────────────────────
    private static List<Message> Read()
    {
        var sizeBuf = new byte[4];
        ReadBytes(Offset, sizeBuf);
        int count = BitConverter.ToInt32(sizeBuf);
        ulong cursor = Offset + 4;
        var list = new List<Message>(count);
        for (int i = 0; i < count; i++)
        {
            var timeBuf = new byte[8];
            ReadBytes(cursor, timeBuf);
            long atMs = BitConverter.ToInt64(timeBuf);
            cursor += 8;
            ReadBytes(cursor, sizeBuf);
            int len = BitConverter.ToInt32(sizeBuf);
            cursor += 4;
            var data = new byte[len];
            ReadBytes(cursor, data);
            cursor += (ulong)len;
            var s = Encoding.UTF8.GetString(data);
            int sep = s.IndexOf('|');
            var sender = sep > 0 ? s.Substring(0, sep) : "anon";
            var text = sep > 0 ? s.Substring(sep + 1) : s;
            list.Add(new Message(atMs, sender, text));
        }
        return list;
    }

    private static void Append(Message m)
    {
        EnsureGrownExplicit();
        var existing = ReadMagic() == Magic ? Read() : new List<Message>();
        existing.Add(m);
        if (existing.Count > MaxMessages)
        {
            existing = existing.GetRange(existing.Count - MaxMessages, MaxMessages);
        }
        WriteAll(existing);
        WriteMagic();
    }

    private static void WriteAll(IReadOnlyList<Message> messages)
    {
        var sizeBuf = BitConverter.GetBytes(messages.Count);
        WriteBytes(Offset, sizeBuf);
        ulong cursor = Offset + 4;
        foreach (var m in messages)
        {
            WriteBytes(cursor, BitConverter.GetBytes(m.AtMs));
            cursor += 8;
            var data = Encoding.UTF8.GetBytes(m.Sender + "|" + m.Text);
            WriteBytes(cursor, BitConverter.GetBytes(data.Length));
            cursor += 4;
            WriteBytes(cursor, data);
            cursor += (ulong)data.Length;
        }
    }

    private static uint ReadMagic()
    {
        var b = new byte[4];
        ReadBytes(MagicOffset, b);
        return BitConverter.ToUInt32(b);
    }

    private static void WriteMagic()
    {
        WriteBytes(MagicOffset, BitConverter.GetBytes(Magic));
    }

    private static void EnsureGrown()
    {
        if (Ic0.stable64_size() == 0) return; // read paths can early-return
    }

    private static void EnsureGrownExplicit()
    {
        // Need at least 64KB (1 page). Magic is at 4092 → write touches
        // through 4099+ at minimum. Plus message data. Grow generously.
        if (Ic0.stable64_size() < 1) Ic0.stable64_grow(1);
    }

    private static void ReadBytes(ulong offset, byte[] buf)
    {
        fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length);
    }

    private static void WriteBytes(ulong offset, byte[] buf)
    {
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }

    private static string ShortPrincipal()
    {
        // Caller principal — first 5 chars for readability.
        try
        {
            var size = Ic0.msg_caller_size();
            if (size == 0) return "anon";
            var buf = new byte[size];
            fixed (byte* p = buf) Ic0.msg_caller_copy((nint)p, 0, (uint)size);
            // Hex-prefix is fine for a display tag.
            var hex = new StringBuilder(5);
            for (int i = 0; i < 3 && i < buf.Length; i++)
            {
                hex.Append(buf[i].ToString("x2"));
            }
            return hex.Length > 5 ? hex.ToString(0, 5) : hex.ToString();
        }
        catch { return "anon"; }
    }
}
