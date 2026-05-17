using System;
using System.Collections.Generic;
using System.Text;
using Wasp.IcCdk;
using Wasp.WebSockets;

namespace Wasp.AspNetCore;

// IC HTTP certification v1, scoped to static assets so the IC HTTP
// gateway accepts the response from a *query* call (which is ~50 ms
// vs ~3 s for an upgrade-to-update on local dfx).
//
// Tree shape:
//
//   Labeled("http_assets", Fork[ Labeled(path1, Leaf(SHA256(body1))) ... ])
//
// On every query that hits a certified path we:
//   1. Lookup the body (held in-memory, identical to what the SSR /
//      static-files middleware would emit on the update path).
//   2. Return it with an `IC-Certificate` header of the form
//        certificate=:base64:, tree=:base64:
//      where `certificate` is the IC system certificate covering our
//      ic0.certified_data_set, and `tree` is the CBOR-encoded HashTree
//      (currently the whole tree — no pruning).
//
// Pruning the tree per-request to ship only the matched path + its
// neighbors' Pruned hashes is a follow-up; it shrinks the witness from
// ~entire-tree to ~log(N) leaves and matters more once the asset count
// is large.
public sealed class IcHttpCertTree
{
    public const string LabelText = "http_assets";
    private static readonly byte[] Label = Encoding.UTF8.GetBytes(LabelText);

    private readonly SortedDictionary<string, (byte[] Body, byte[] Hash)> _entries =
        new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public bool TryGetBody(string path, out byte[] body)
    {
        if (_entries.TryGetValue(path, out var entry))
        {
            body = entry.Body;
            return true;
        }
        body = Array.Empty<byte>();
        return false;
    }

    public void Insert(string path, byte[] body)
    {
        var hash = Sha256.Hash(body);
        _entries[path] = (body, hash);
    }

    /// <summary>
    /// Recompute the labeled root hash over all entries and push it into
    /// ic0.certified_data_set so the IC system certifies it. Call once
    /// after all <see cref="Insert"/> calls have run.
    /// </summary>
    public unsafe void Commit()
    {
        var labeled = new HashTree.Labeled(Label, BuildFullTree());
        var rootHash = labeled.Hash;
        fixed (byte* p = rootHash) Ic0.certified_data_set((nint)p, (uint)rootHash.Length);
    }

    /// <summary>
    /// Build the IC-Certificate header value for a single asset path.
    /// Caller is responsible for matching the path against
    /// <see cref="TryGetBody"/> first; we don't re-check here.
    /// </summary>
    public unsafe string? BuildCertificateHeader(string path)
    {
        if (Ic0.data_certificate_present() == 0) return null;
        uint size = Ic0.data_certificate_size();
        var systemCert = new byte[(int)size];
        fixed (byte* p = systemCert) Ic0.data_certificate_copy((nint)p, 0, size);

        var labeled = new HashTree.Labeled(Label, BuildFullTree());
        var treeCbor = labeled.EncodeWithSelfDescribe();

        // Header format per IC spec:
        //   IC-Certificate: certificate=:<b64>:, tree=:<b64>:
        var sb = new StringBuilder(64 + systemCert.Length * 2 + treeCbor.Length * 2);
        sb.Append("certificate=:");
        sb.Append(Convert.ToBase64String(systemCert));
        sb.Append(":, tree=:");
        sb.Append(Convert.ToBase64String(treeCbor));
        sb.Append(':');
        return sb.ToString();
    }

    private HashTree BuildFullTree()
    {
        if (_entries.Count == 0) return HashTree.Empty.Instance;
        var pairs = new List<KeyValuePair<byte[], HashTree>>(_entries.Count);
        foreach (var kvp in _entries)
        {
            var pathBytes = Encoding.UTF8.GetBytes(kvp.Key);
            pairs.Add(new KeyValuePair<byte[], HashTree>(pathBytes, new HashTree.Leaf(kvp.Value.Hash)));
        }
        return HashTree.BuildBalancedLabeled(pairs);
    }
}
