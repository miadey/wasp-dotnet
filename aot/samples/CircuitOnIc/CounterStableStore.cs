using System;
using Wasp.IcCdk;

namespace WaspSample.CircuitOnIc;

// Persistent backing store for the fast-click counter.
//
// Layout: a single int32 at stable memory offset 0. Reads are valid
// from query calls (~10 µs). Writes require an update call so we only
// fire them on the client's periodic sync (every few seconds) and on
// page unload — the hot-path clicks stay on the query side.
//
// On first call (canister has zero stable memory pages), grow by one
// 64 KB page so the read/write doesn't fault. The first read returns
// 0 because grown pages are zero-initialised.
internal static class CounterStableStore
{
    private const ulong CounterOffset = 0;

    public static unsafe int Read()
    {
        if (Ic0.stable64_size() == 0) return 0;
        int value = 0;
        Ic0.stable64_read((ulong)(nint)(&value), CounterOffset, sizeof(int));
        return value;
    }

    public static unsafe void Write(int value)
    {
        if (Ic0.stable64_size() == 0)
        {
            // Grow by one wasm page (64 KB). plenty for a single int.
            Ic0.stable64_grow(1);
        }
        Ic0.stable64_write(CounterOffset, (ulong)(nint)(&value), sizeof(int));
    }
}
