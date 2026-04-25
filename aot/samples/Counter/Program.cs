using Wasp.IcCdk;

// User-facing canister authored with Wasp.IcCdk.
// No [UnmanagedCallersOnly], no manual Candid encoding, no ic0 imports —
// the source generator emits the thunks from these attributes.

namespace WaspSample.Counter;

public static partial class CounterCanister
{
    private static readonly StableCell<ulong> _counter = new(memoryId: 0);

    [CanisterQuery]
    public static string greet(string who)
    {
        Reply.Print($"[counter] greet({who})");
        return $"Hello, {who}, from .NET 10 on ICP (UPGRADED) — counter is at {_counter.Value}";
    }

    [CanisterQuery]
    public static ulong count()
    {
        Reply.Print("[counter] count");
        return _counter.Value;
    }

    [CanisterUpdate]
    public static ulong increment()
    {
        Reply.Print("[counter] increment");
        var v = _counter.Value;
        _counter.Value = v + 1;
        return v + 1;
    }

    [CanisterUpdate]
    public static ulong add(ulong delta)
    {
        Reply.Print($"[counter] add({delta})");
        var v = _counter.Value + delta;
        _counter.Value = v;
        return v;
    }

    [CanisterUpdate]
    public static void reset()
    {
        Reply.Print("[counter] reset");
        _counter.Value = 0;
    }
}
