using System;
using System.Collections.Generic;
using System.Text;
using Wasp.IcCdk;
using Wasp.WebSockets;

namespace Wasp.AspNetCore;

/// <summary>
/// HTTP response certification protocol v2 — dynamic response support.
///
/// v1 (in <see cref="IcCertifiedAssets"/>) certifies fixed bodies whose
/// hash is known at update time. The negotiate endpoint and the
/// long-poll GET return per-request bodies; their hash can't be
/// pre-registered. v2 lets the canister certify an EXPRESSION instead
/// of a body — for our case a "no-certification" expression that tells
/// the boundary "this path's responses are not body-certified, accept
/// the canister's word for them." Spec:
/// https://internetcomputer.org/docs/references/http-gateway-protocol-spec#response-certification-v2
///
/// Tree shape on the canister side:
/// <code>
///   "http_expr"
///     └── path segment 1
///          └── path segment 2
///               └── "&lt;$&gt;"          ← spec terminal marker (literal "&lt;$&gt;")
///                    └── expr_hash    ← labeled by raw 32 bytes
///                         └── ":"     ← labeled empty (no-cert leaf)
///                              └── Empty
/// </code>
/// The combined canister root hash forks this v2 subtree with the v1
/// "http_assets" subtree managed by <see cref="IcCertifiedAssets"/>.
///
/// On every response the canister emits:
/// <code>
///   IC-CertificateExpression: default_certification ( ValidationArgs { certification: Empty {} } )
///   IC-Certificate: certificate=:&lt;sys-cert&gt;:, tree=:&lt;witness&gt;:, version=2, expr_path=:&lt;cbor-path&gt;:
/// </code>
/// where the witness reveals the path-down-to-expr_hash chain and prunes
/// every sibling.
/// </summary>
public static class IcResponseCertV2
{
    private const string ExprLabelText = "http_expr";
    private static readonly byte[] ExprLabel = Encoding.UTF8.GetBytes(ExprLabelText);
    private static readonly byte[] TerminalMarker = Encoding.UTF8.GetBytes("<$>");
    private static readonly byte[] EmptyValueLabel = Encoding.UTF8.GetBytes(":");

    // Pass-through expression: no certification at all. The canonical CEL
    // form (whitespace-stripped) is what the boundary node hashes from
    // the IC-CertificateExpression header to derive expr_hash; we MUST
    // send the same bytes we registered in the tree.
    //
    // Field name is `no_certification`, not `certification: Empty` — the
    // dfinity/response-verification reference uses `no_certification:
    // Empty {}` as the absence marker. Format verified against
    // ic-response-verification v2_validation tests.
    private const string PassThroughExpression =
        "default_certification(ValidationArgs{no_certification:Empty{}})";
    private static readonly byte[] PassThroughExpressionBytes =
        Encoding.UTF8.GetBytes(PassThroughExpression);

    // expr_hash = sha256(canonical CEL bytes). The reference verifier
    // calls hash() over the header value bytes directly (see
    // verify_request_response_pair.rs: `hash(header.as_bytes())`).
    private static readonly byte[] PassThroughExprHash =
        Sha256.Hash(PassThroughExpressionBytes);

    /// <summary>
    /// Registered (method-set, path) entries. For each registered path the
    /// v2 tree contains the pass-through expression at every method's
    /// terminal node. Method is currently informational — the v2
    /// pass-through expression doesn't require us to constrain by method.
    /// </summary>
    private static readonly SortedDictionary<string, HashSet<string>> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Register a path as v2-certified with the pass-through (no-cert)
    /// expression. Must be called from a context where
    /// <see cref="IcCertifiedAssets.NotifyChanged"/> can ultimately reach
    /// an update call — see the deferred-flush mechanism in v1; same
    /// _pendingFlush flag is reused via NotifyChanged.
    /// </summary>
    public static void RegisterPassThroughPath(string path, string method = "GET")
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (!_entries.TryGetValue(path, out var methods))
        {
            methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _entries[path] = methods;
        }
        methods.Add(method);
        // Hook into the combined-root computation so v1 module folds our
        // subtree in. Idempotent — setting the same provider repeatedly
        // is fine.
        IcCertifiedAssets.AdditionalSubtreeProvider = BuildV2Subtree;
        IcCertifiedAssets.NotifyChanged();
    }

    /// <summary>True if <paramref name="path"/> has a v2 expression registered.</summary>
    public static bool IsRegistered(string path) => _entries.ContainsKey(path);

    /// <summary>
    /// Build the v2 `"http_expr"` labeled subtree. Returns null if no
    /// paths are registered (so the combined-root computation skips the
    /// fork).
    /// </summary>
    public static HashTree.Labeled? BuildV2Subtree()
    {
        if (_entries.Count == 0) return null;
        // Group registered paths by their first segment, recursively, to
        // build the nested labeled tree.
        var paths = new List<string[]>(_entries.Count);
        foreach (var kvp in _entries) paths.Add(SplitPath(kvp.Key));
        var inner = BuildSubtree(paths, depth: 0);
        return new HashTree.Labeled(ExprLabel, inner);
    }

    private static string[] SplitPath(string path)
    {
        // "/_blazor/negotiate" → ["_blazor", "negotiate"]
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    private static HashTree BuildSubtree(List<string[]> paths, int depth)
    {
        // At a given depth, group by the segment at that depth. Paths
        // whose length == depth land at the terminal-marker level.
        // For this skeleton we only register fully-distinct paths (no
        // shared prefixes among them yet), so depth-grouping is trivial.
        // Generalize when prefix sharing arrives.
        var groups = new SortedDictionary<string, List<string[]>>(StringComparer.Ordinal);
        bool terminalHere = false;
        foreach (var p in paths)
        {
            if (depth == p.Length)
            {
                terminalHere = true;
                continue;
            }
            var seg = p[depth];
            if (!groups.TryGetValue(seg, out var bucket))
            {
                bucket = new List<string[]>();
                groups[seg] = bucket;
            }
            bucket.Add(p);
        }

        var labeledChildren = new List<KeyValuePair<byte[], HashTree>>();
        if (terminalHere)
        {
            // Confirmed by ic-response-verification v2_validation tests:
            //   label("<$>", label(expr_hash, leaf("")))
            // Note: leaf("") is HashTree.Leaf with empty-byte content,
            // NOT HashTree.Empty.Instance — these hash to different
            // values (different domain separators).
            var exprHashLevel = new HashTree.Labeled(
                PassThroughExprHash,
                new HashTree.Leaf(Array.Empty<byte>()));
            labeledChildren.Add(new KeyValuePair<byte[], HashTree>(
                TerminalMarker,
                exprHashLevel));
        }
        foreach (var kvp in groups)
        {
            var segBytes = Encoding.UTF8.GetBytes(kvp.Key);
            var sub = BuildSubtree(kvp.Value, depth + 1);
            labeledChildren.Add(new KeyValuePair<byte[], HashTree>(segBytes, sub));
        }
        // Sort by label bytes (HashTree spec). SortedDictionary above gave
        // us segment-order; the terminal marker "<$>" has bytes 3C 24 3E,
        // and segment names are typically letters starting at 0x61+. So
        // "<$>" sorts BEFORE any letter segment. Use a final sort to be
        // safe.
        labeledChildren.Sort((a, b) => CompareBytes(a.Key, b.Key));
        return HashTree.BuildBalancedLabeled(labeledChildren);
    }

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>The textual expression carried in IC-CertificateExpression.</summary>
    public static string ExpressionHeader => PassThroughExpression;

    /// <summary>
    /// Build the IC-Certificate header value (v2 form) for the given path.
    /// Witness reveals only the path-down-to-no-cert-leaf chain; siblings
    /// at every level are pruned. Returns null when v2 isn't registered
    /// for the path or the system certificate isn't available yet.
    /// </summary>
    public static unsafe string? BuildHeaderValue(string path)
    {
        if (!IsRegistered(path)) return null;
        if (Ic0.data_certificate_present() == 0) return null;

        uint certSize = Ic0.data_certificate_size();
        var cert = new byte[(int)certSize];
        fixed (byte* p = cert) Ic0.data_certificate_copy((nint)p, 0, certSize);

        var segments = SplitPath(path);
        // Build the witness: at the root level, fork(Pruned(v1), labeled("http_expr", path-witness))
        // — and at the http_expr subtree, the requested path is revealed
        // while all sibling branches are pruned (their hashes only).
        var v1Subtree = IcCertifiedAssets.BuildV1Subtree();
        var fullV2Inner = BuildSubtree(CollectPathArrays(), depth: 0);
        HashTree pathWitness = PruneToPath(fullV2Inner, segments, depth: 0);
        HashTree witness = string.CompareOrdinal("http_assets", "http_expr") <= 0
            ? new HashTree.Fork(new HashTree.Pruned(v1Subtree.Hash), new HashTree.Labeled(ExprLabel, pathWitness))
            : new HashTree.Fork(new HashTree.Labeled(ExprLabel, pathWitness), new HashTree.Pruned(v1Subtree.Hash));
        var treeBytes = witness.EncodeWithSelfDescribe();

        // expr_path is a CBOR array of byte strings: the path segments
        // followed by "<$>" and the (empty) status segment ":" — exactly
        // the labels traversed in the tree to reach the no-cert leaf.
        var exprPath = BuildExprPathCbor(segments);

        var sb = new StringBuilder(cert.Length * 2 + treeBytes.Length * 2 + exprPath.Length * 2 + 96);
        sb.Append("certificate=:");
        sb.Append(Convert.ToBase64String(cert));
        sb.Append(":, tree=:");
        sb.Append(Convert.ToBase64String(treeBytes));
        sb.Append(":, version=2, expr_path=:");
        sb.Append(Convert.ToBase64String(exprPath));
        sb.Append(':');
        return sb.ToString();
    }

    private static List<string[]> CollectPathArrays()
    {
        var paths = new List<string[]>(_entries.Count);
        foreach (var kvp in _entries) paths.Add(SplitPath(kvp.Key));
        return paths;
    }

    /// <summary>
    /// Walk the full v2 inner subtree, keeping nodes on the witness path
    /// and replacing every other branch with Pruned(node.Hash). Siblings
    /// at each level are preserved (pruned) so the boundary can
    /// recompute the subtree's hash and match the certified root.
    /// </summary>
    private static HashTree PruneToPath(HashTree node, string[] segments, int depth)
    {
        switch (node)
        {
            case HashTree.Fork fork:
                // Forks aren't on the path themselves — recurse into both
                // children with the same depth; each subtree decides
                // whether to keep or prune itself.
                return new HashTree.Fork(
                    PruneToPath(fork.Left, segments, depth),
                    PruneToPath(fork.Right, segments, depth));
            case HashTree.Labeled labeled:
                // Is this label the next step on our witness path?
                bool onPath = false;
                if (depth < segments.Length)
                {
                    var want = Encoding.UTF8.GetBytes(segments[depth]);
                    onPath = ByteEquals(labeled.Label, want);
                }
                else if (depth == segments.Length)
                {
                    // After the last segment, the next labels on the path
                    // are "<$>" then PassThroughExprHash, then the leaf.
                    onPath = ByteEquals(labeled.Label, TerminalMarker);
                }
                else if (depth == segments.Length + 1)
                {
                    onPath = ByteEquals(labeled.Label, PassThroughExprHash);
                }
                if (onPath)
                {
                    return new HashTree.Labeled(
                        labeled.Label,
                        PruneToPath(labeled.Subtree, segments, depth + 1));
                }
                // Not on path → prune entirely.
                return new HashTree.Pruned(labeled.Hash);
            default:
                // Leaf / Empty / Pruned terminals on the witness path are
                // kept verbatim; siblings have already been pruned by the
                // Labeled case above.
                return node;
        }
    }

    private static bool ByteEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    private static byte[] BuildExprPathCbor(string[] segments)
    {
        var w = new Cbor.Writer();
        w.SelfDescribeTag();
        // Per ic-response-verification: expr_path =
        //   ["http_expr", ...path_segments..., "<$>"]
        // Must include the "http_expr" prefix and terminate with "<$>".
        // No request/response/expr_hash trailer for the no-cert case.
        w.WriteArrayHeader(1 + segments.Length + 1);
        w.WriteByteString(ExprLabel);
        foreach (var s in segments)
        {
            w.WriteByteString(Encoding.UTF8.GetBytes(s));
        }
        w.WriteByteString(TerminalMarker);
        return w.ToArray();
    }
}
