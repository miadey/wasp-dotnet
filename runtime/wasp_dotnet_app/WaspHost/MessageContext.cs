namespace Wasp;

// Managed wrappers around the Ic0 trampoline calls. These are what
// user code is expected to use - never call `Ic0.*` directly.
public static unsafe class MessageContext
{
    public static byte[] ArgData()
    {
        uint len = Ic0.wasp_msg_arg_size();
        var buf = new byte[(int)len];
        if (len == 0) return buf;
        fixed (byte* p = buf) Ic0.wasp_msg_arg_copy(p, 0, len);
        return buf;
    }

    public static byte[] Caller()
    {
        uint len = Ic0.wasp_caller_size();
        var buf = new byte[(int)len];
        if (len == 0) return buf;
        fixed (byte* p = buf) Ic0.wasp_caller_copy(p, 0, len);
        return buf;
    }

    public static ulong Now() => Ic0.wasp_time();
}
