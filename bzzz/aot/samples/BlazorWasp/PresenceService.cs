using System;
using System.Collections.Generic;
using System.Linq;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Heap-only "who's currently viewing this CRM record" tracker.
/// Resets across canister upgrades — that's fine, presence is by
/// nature transient and clients re-heartbeat within seconds.
///
/// Stale entries (no heartbeat in 30 s) are pruned lazily on read.
/// </summary>
public sealed class PresenceService
{
    private const long StaleAfterMs = 30_000;

    public sealed record Entry(string Principal, string Name, long LastSeenMs);

    private readonly Dictionary<string, Dictionary<string, Entry>> _byRecord = new();

    public void Heartbeat(string recordId, string principal, string name)
    {
        if (string.IsNullOrEmpty(recordId) || string.IsNullOrEmpty(principal)) return;
        principal = principal.Length > 96 ? principal.Substring(0, 96) : principal;
        name = string.IsNullOrEmpty(name) ? "Guest" : (name.Length > 32 ? name.Substring(0, 32) : name);
        if (!_byRecord.TryGetValue(recordId, out var dict))
        {
            dict = new Dictionary<string, Entry>();
            _byRecord[recordId] = dict;
        }
        dict[principal] = new Entry(principal, name, NowMs());
    }

    public IReadOnlyList<Entry> Viewers(string recordId) => Viewers(recordId, StaleAfterMs);

    /// <summary>Viewers with a caller-chosen stale window. Typing
    /// indicators pass a short window (~6 s) so "X is typing…" clears
    /// promptly once the user stops, rather than lingering the full 30 s
    /// presence window.</summary>
    public IReadOnlyList<Entry> Viewers(string recordId, long staleAfterMs)
    {
        if (!_byRecord.TryGetValue(recordId, out var dict)) return Array.Empty<Entry>();
        var cutoff = NowMs() - staleAfterMs;
        // Lazy prune stale entries on each read.
        var stale = dict.Where(kv => kv.Value.LastSeenMs < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in stale) dict.Remove(k);
        return dict.Values.OrderByDescending(v => v.LastSeenMs).ToList();
    }

    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);
}
