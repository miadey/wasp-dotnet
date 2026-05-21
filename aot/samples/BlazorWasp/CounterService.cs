using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Persistent counter — the render-as-query model re-instantiates the
/// Counter component on every call so any local <c>int count</c> field
/// would reset. State that survives across calls must live in a DI
/// singleton (this) or stable memory (this one's backing store).
/// </summary>
public sealed unsafe class CounterService
{
    private const ulong StableOffset = 0;

    public int Count
    {
        get
        {
            if (Ic0.stable64_size() == 0) return 0;
            int value = 0;
            Ic0.stable64_read((ulong)(nint)(&value), StableOffset, sizeof(int));
            return value;
        }
    }

    public void Increment()
    {
        int value = Count + 1;
        if (Ic0.stable64_size() == 0) Ic0.stable64_grow(1);
        Ic0.stable64_write(StableOffset, (ulong)(nint)(&value), sizeof(int));
    }
}
