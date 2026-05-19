using Wasp.IcCdk;

namespace WaspSample.BlazorVanilla;

/// <summary>
/// Global click counter persisted at stable-memory offset 0
/// (i32, little-endian). Shared across all callers — every browser
/// session sees the same monotonically increasing value, and the count
/// survives canister upgrades.
///
/// Read() works in any context (query or update). Increment() mutates
/// stable memory so it only persists when called from an update call
/// (i.e. POST /api/click — the IC HTTP gateway routes all POSTs through
/// http_request_update, which runs through consensus).
/// </summary>
internal static unsafe class ClickCounterStableStore
{
    private const ulong CounterOffset = 0;

    public static int Read()
    {
        if (Ic0.stable64_size() == 0) return 0;
        int value = 0;
        Ic0.stable64_read((ulong)(nint)(&value), CounterOffset, sizeof(int));
        return value;
    }

    public static int Increment()
    {
        if (Ic0.stable64_size() == 0)
        {
            Ic0.stable64_grow(1);
        }
        int value = Read() + 1;
        Ic0.stable64_write(CounterOffset, (ulong)(nint)(&value), sizeof(int));
        return value;
    }
}
