using System;
using System.Collections.Generic;

namespace WaspSample.BlazorWasp;

/// <summary>
/// HEAP-ONLY per-principal token-bucket rate limiter — NO stable-memory region
/// at all, so it resets on upgrade (acceptable, and avoids any corruption risk).
/// The canister is single-threaded, so no locks are needed.
///
/// <para>Each (bucket, principal) pair gets a token bucket with a per-bucket
/// capacity and refill rate. <see cref="TryConsume"/> returns true when a token
/// was available and consumed (ALLOW), false when over budget (caller returns
/// HTTP 429).</para>
///
/// <para>The dictionary is capped at <see cref="MaxBuckets"/> entries to bound
/// heap usage; when full and a new key arrives, the call is allowed (fail-open)
/// rather than stored, preventing unbounded growth.</para>
/// </summary>
public sealed class RateService
{
    /// <summary>Sentinel key used in place of a null/empty principal (anonymous).</summary>
    public static readonly byte[] AnonKey = System.Text.Encoding.ASCII.GetBytes("__anon__");

    private const int MaxBuckets = 50_000;

    private sealed class Bucket
    {
        public double Tokens;
        public long LastMs;
    }

    private readonly Dictionary<string, Bucket> _buckets = new();

    /// <summary>
    /// Attempt to consume one token for (<paramref name="bucket"/>,
    /// <paramref name="principal"/>) at wall-clock time <paramref name="nowMs"/>
    /// (milliseconds). Returns true when allowed (token consumed), false when the
    /// bucket is exhausted.
    /// </summary>
    public bool TryConsume(byte[] principal, string bucket, long nowMs)
    {
        var p = (principal is null || principal.Length == 0) ? AnonKey : principal;
        var key = bucket + "|" + Convert.ToHexString(p);

        var (cap, perSec) = Policy(bucket);

        if (!_buckets.TryGetValue(key, out var b))
        {
            // Cap heap growth: when full, fail-open (allow) without storing.
            if (_buckets.Count >= MaxBuckets) return true;
            b = new Bucket { Tokens = cap, LastMs = nowMs };
            _buckets[key] = b;
        }

        double elapsedSec = Math.Max(0, (nowMs - b.LastMs) / 1000.0);
        b.LastMs = nowMs;
        b.Tokens = Math.Min(cap, b.Tokens + elapsedSec * perSec);

        if (b.Tokens < 1) return false;
        b.Tokens -= 1;
        return true;
    }

    /// <summary>Per-bucket (capacity, tokens-per-second) policy.</summary>
    private static (double cap, double perSec) Policy(string bucket) => bucket switch
    {
        "react"  => (30, 5),
        "vote"   => (30, 5),
        "ping"   => (20, 2),
        "dm"     => (10, 1),
        "create" => (5, 0.1),   // server / channel creation: genuinely heavy + rare
        "topic"  => (10, 0.5),  // forum topic creation: a NORMAL member verb — far more generous than "create"
        "key"    => (20, 1),    // key publish/replenish: device + prekey upkeep, modest steady-state
        "keyfetch" => (5, 0.2), // /api/keys/fetch POPS a scarce one-time prekey — tight bucket only SLOWS
                                // OTPK depletion (a determined caller still drains over time); the real
                                // mitigation is client-side replenish when /api/keys/devices shows low otpkCount
        _        => (12, 1),   // default / "write"
    };
}
