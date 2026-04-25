using System;

namespace Wasp.IcCdk;

// Helpers to read the current message's argument blob and caller principal.
public static unsafe class MessageContext
{
    public static byte[] ArgData()
    {
        uint len = Ic0.msg_arg_data_size();
        if (len == 0) return Array.Empty<byte>();
        var buf = new byte[(int)len];
        fixed (byte* p = buf) Ic0.msg_arg_data_copy((nint)p, 0, len);
        return buf;
    }

    public static byte[] CallerPrincipal()
    {
        uint len = Ic0.msg_caller_size();
        if (len == 0) return Array.Empty<byte>();
        var buf = new byte[(int)len];
        fixed (byte* p = buf) Ic0.msg_caller_copy((nint)p, 0, len);
        return buf;
    }
}
