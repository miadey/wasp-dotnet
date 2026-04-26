namespace Wasp;

// Convenience helpers around the reply / trap / debug-print
// trampolines. UTF-8 conversion lives here so callers can pass plain
// `string`s for the textual entry points.
public static unsafe class Reply
{
    public static void Bytes(byte[] payload)
    {
        if (payload.Length == 0)
        {
            Ic0.wasp_reply((byte*)0, 0);
            return;
        }
        fixed (byte* p = payload) Ic0.wasp_reply(p, (uint)payload.Length);
    }

    public static void Trap(string reason)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(reason);
        if (bytes.Length == 0)
        {
            Ic0.wasp_trap((byte*)0, 0);
            return;
        }
        fixed (byte* p = bytes) Ic0.wasp_trap(p, (uint)bytes.Length);
    }

    public static void Print(string msg)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(msg);
        if (bytes.Length == 0) return;
        fixed (byte* p = bytes) Ic0.wasp_debug_print(p, (uint)bytes.Length);
    }
}
