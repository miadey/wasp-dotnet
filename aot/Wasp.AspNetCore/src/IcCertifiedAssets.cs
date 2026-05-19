using System;
using System.Collections.Generic;
using System.Text;
using Wasp.IcCdk;
using Wasp.WebSockets;

namespace Wasp.AspNetCore;

/// <summary>
/// HTTP asset certification for IC canisters, certification protocol v1.
///
/// The IC HTTP boundary node on the certified subdomain
/// (https://&lt;canister-id&gt;.icp0.io, no `.raw.`) verifies that every
/// query response was produced from data the canister itself certified.
/// On the `.raw.` subdomain this is skipped, but to serve traffic on the
/// canonical URL we must:
///
///   1. Maintain a labeled hash tree of (path → sha256(body)) entries.
///   2. Push the root hash into <c>ic0.certified_data_set</c> from an
///      update call (typically canister init or whenever the asset set
///      changes; queries can't mutate certified data).
///   3. On every query response, include the <c>IC-Certificate</c> header
///      carrying the system certificate plus a tree witness that lets
///      the boundary verify path → body without trusting the replica.
///
/// v1 format (legacy but still accepted by mainnet as of 2026-05):
///   - Label: "http_assets"
///   - Inner sub-labels: the URL path bytes (e.g. "/", "/counter",
///     "/_framework/blazor.web.js"). The label sort order is the
///     IC HashTree byte-lex requirement.
///   - Leaf: sha256(response body)
///   - <c>IC-Certificate: certificate=:BASE64:, tree=:BASE64:</c>
///     where <c>tree</c> is CBOR(labeled("http_assets", &lt;asset-tree&gt;))
///     with the self-describe tag.
///
/// v0.1 simplification: the witness reveals EVERY known asset's path +
/// hash on every response (no pruning). Correct, but each cert response
/// grows linearly with asset count. Fine for our ~6-asset BlazorVanilla
/// sample; revisit when we have hundreds of assets.
/// </summary>
public static class IcCertifiedAssets
{
    private const string LabelText = "http_assets";
    private static readonly byte[] Label = Encoding.UTF8.GetBytes(LabelText);

    // path-byte-lex–sorted map. SortedDictionary with the appropriate
    // comparer gives us insertion + the iteration order the HashTree spec
    // requires.
    private static readonly SortedDictionary<string, byte[]> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Register a path → body certification. Computes sha256(body), stores
    /// it under the path, rebuilds the labeled tree, and pushes the new
    /// root hash into the IC system via <c>certified_data_set</c>. The
    /// boundary will accept any subsequent query response for this path
    /// whose body hashes to the value we just registered.
    ///
    /// MUST be called from an update-call context (canister init,
    /// http_request_update, or canister_post_upgrade) — <c>certified_data_set</c>
    /// trapping in query mode would otherwise crash the dispatch.
    /// </summary>
    public static void Insert(string path, byte[] body)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (body is null) throw new ArgumentNullException(nameof(body));
        _entries[path] = Sha256.Hash(body);
        UpdateCertifiedData();
    }

    /// <summary>Number of certified asset entries currently registered.</summary>
    public static int Count => _entries.Count;

    /// <summary>True if at least one asset has been certified.</summary>
    public static bool HasEntries => _entries.Count > 0;

    // We retain pending entries that arrived in a non-update context (e.g.
    // a ModuleInitializer firing on the first query) so the next update
    // call (canister_init / post_upgrade / any http_request_update) can
    // flush them to certified_data_set. This avoids the trap that
    // certified_data_set produces in query mode while still recovering
    // certification once the canister handles its first update.
    private static bool _pendingFlush;

    private static unsafe void UpdateCertifiedData()
    {
        // certified_data_set TRAPS in query mode (no recoverable exception).
        // Detect via in_replicated_execution; defer the set when not in
        // update.
        uint replicated = 0;
        try { replicated = Ic0.in_replicated_execution(); }
        catch { /* old IC API surface — be permissive */ }
        if (replicated == 0)
        {
            _pendingFlush = true;
            return;
        }

        var labeled = new HashTree.Labeled(Label, BuildAssetTree());
        var rootHash = labeled.Hash;
        fixed (byte* p = rootHash)
        {
            Ic0.certified_data_set((nint)p, (uint)rootHash.Length);
        }
        _pendingFlush = false;
    }

    /// <summary>
    /// Flush a deferred certified-data update if Insert was called from a
    /// non-update context (e.g. during ModuleInitializer triggered by a
    /// query first-touch). Safe to call from any update entry point;
    /// no-op when there is nothing pending.
    /// </summary>
    public static void FlushPendingIfUpdate()
    {
        if (!_pendingFlush) return;
        UpdateCertifiedData();
    }

    private static HashTree BuildAssetTree()
    {
        if (_entries.Count == 0) return HashTree.Empty.Instance;
        var pairs = new List<KeyValuePair<byte[], HashTree>>(_entries.Count);
        foreach (var kvp in _entries)
        {
            var labelBytes = Encoding.UTF8.GetBytes(kvp.Key);
            pairs.Add(new KeyValuePair<byte[], HashTree>(
                labelBytes, new HashTree.Leaf(kvp.Value)));
        }
        return HashTree.BuildBalancedLabeled(pairs);
    }

    /// <summary>
    /// Build the <c>IC-Certificate</c> response header value for the given
    /// request path. The witness reveals every registered asset (no pruning)
    /// — the boundary still verifies only the requested path matches the
    /// claimed body hash.
    ///
    /// Returns null if no assets are registered OR the IC system
    /// certificate isn't available (e.g. called from canister init before
    /// the first replica certified the tree). Caller should serve the
    /// response without the header in that case — the boundary will
    /// reject it on a non-raw domain, but the .raw. fallback still works.
    /// </summary>
    public static unsafe string? BuildHeaderValue(string path)
    {
        if (_entries.Count == 0) return null;
        if (Ic0.data_certificate_present() == 0) return null;

        uint certSize = Ic0.data_certificate_size();
        var cert = new byte[(int)certSize];
        fixed (byte* p = cert) Ic0.data_certificate_copy((nint)p, 0, certSize);

        var labeled = new HashTree.Labeled(Label, BuildAssetTree());
        var treeBytes = labeled.EncodeWithSelfDescribe();

        // Structured-field syntax: each member is a binary value enclosed
        // in colons (`:base64...:`), parameter-style. We don't include the
        // version=1 parameter — boundary defaults to v1 when absent.
        var sb = new StringBuilder(cert.Length * 2 + treeBytes.Length * 2 + 64);
        sb.Append("certificate=:");
        sb.Append(Convert.ToBase64String(cert));
        sb.Append(":, tree=:");
        sb.Append(Convert.ToBase64String(treeBytes));
        sb.Append(':');
        return sb.ToString();
    }
}
