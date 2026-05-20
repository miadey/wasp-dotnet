using System;
using Wasp.IcCdk;

namespace WaspSample.BlazorVanilla;

/// <summary>
/// Singleton service that owns the canister-wide click count and
/// notifies subscribers when it changes. Two effects layered together:
///
///   1. <b>Persistence</b>: every <see cref="Increment"/> writes the new
///      value to stable memory via <see cref="ClickCounterStableStore"/>,
///      so the count survives canister upgrades.
///
///   2. <b>Reactivity across circuits</b>: <see cref="Changed"/> fires on
///      every increment. Each <c>Counter.razor</c> instance subscribes
///      from its own circuit, so when user A clicks, user B's counter
///      gets a render-diff too — without B doing anything. This relies
///      on the framework's render-diff serialization being correct,
///      which the gh #80 JIT patch finally delivered.
///
/// Registered as a DI singleton in Program.cs.
/// </summary>
public sealed class CounterService
{
    private int _count;

    public CounterService()
    {
        // Lazy-read persisted count. Stable memory may be empty on first
        // boot — Read() returns 0 in that case which matches the
        // user-visible "Current count: 0" baseline.
        _count = ClickCounterStableStore.Read();
    }

    /// <summary>Current count. Reads are lock-free (single-threaded canister).</summary>
    public int Count => _count;

    /// <summary>
    /// Fired after every successful <see cref="Increment"/>. Subscribers
    /// typically call <c>InvokeAsync(StateHasChanged)</c> to refresh.
    /// </summary>
    public event Action? Changed;

    /// <summary>
    /// Increment + persist + notify. Must run from an update-call context
    /// (Blazor @onclick handlers always do, as do POST endpoints). In a
    /// query-call context the stable-memory write is rolled back, but the
    /// in-memory bump and the Changed event still fire.
    /// </summary>
    public int Increment()
    {
        _count++;
        try
        {
            ClickCounterStableStore.Write(_count);
        }
        catch (Exception ex)
        {
            // Query-context callers can't mutate stable memory; swallow
            // the trap rather than break the render.
            Reply.Print("[counter] stable write failed: " + ex.Message);
        }
        Changed?.Invoke();
        return _count;
    }
}
