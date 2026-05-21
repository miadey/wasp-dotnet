using Wasp.IcCdk;

namespace BlazorOnIcp;

/// <summary>
/// Persistent counter — render-as-query re-instantiates the Counter
/// component each call, so any local `int count` field would reset.
/// State lives in this DI singleton, backed by stable memory so it
/// survives canister upgrades.
/// </summary>
public sealed unsafe class CounterService
{
    private const ulong Offset = 0;

    public int Count
    {
        get
        {
            if (Ic0.stable64_size() == 0) return 0;
            int value = 0;
            Ic0.stable64_read((ulong)(nint)(&value), Offset, sizeof(int));
            return value;
        }
    }

    public void Increment()
    {
        int value = Count + 1;
        if (Ic0.stable64_size() == 0) Ic0.stable64_grow(1);
        Ic0.stable64_write(Offset, (ulong)(nint)(&value), sizeof(int));
    }
}
