using System;

namespace Wasp.IcCdk;

// Convenience helpers around ic0.msg_reply / msg_reply_data_append /
// trap / debug_print. Pointer arithmetic is hidden so user code can
// stay in safe(-ish) C#.
public static unsafe class Reply
{
    public static void Bytes(ReadOnlySpan<byte> payload)
    {
        if (!payload.IsEmpty)
        {
            fixed (byte* p = payload)
            {
                Ic0.msg_reply_data_append((nint)p, payload.Length);
            }
        }
        Ic0.msg_reply();
    }

    public static void Empty() => Bytes(Candid.Empty);

    public static void Trap(string reason)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(reason);
        fixed (byte* p = bytes) Ic0.trap((nint)p, bytes.Length);
    }

    public static void Print(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        fixed (byte* p = utf8) Ic0.debug_print((nint)p, utf8.Length);
    }

    public static void Print(string message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        fixed (byte* p = bytes) Ic0.debug_print((nint)p, bytes.Length);
    }
}
