using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Wasp.IcCdk;

namespace Wasp.WebSockets;

// Maintains a sorted set of (message_key → sha256(cbor_bytes_of_message))
// pairs, computes the IC HashTree root over them, and pushes the
// labeled root hash into ic0.certified_data_set.
//
// On each ws_get_messages query we build a witness covering a contiguous
// range of message keys. Phase 3 v0.1: the witness includes EVERY key
// (no pruning). That's correct but inefficient for large queues.
public sealed class CertTree
{
    private const string LabelText = "websocket";
    private static readonly byte[] Label = Encoding.UTF8.GetBytes(LabelText);

    // Use SortedDictionary so iteration order matches lex byte order of keys.
    private readonly SortedDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public void Insert(string key, byte[] hash32)
    {
        _entries[key] = hash32;
        UpdateCertifiedData();
    }

    public void Remove(string key)
    {
        if (_entries.Remove(key)) UpdateCertifiedData();
    }

    public bool Contains(string key) => _entries.ContainsKey(key);

    private unsafe void UpdateCertifiedData()
    {
        var root = BuildFullTree();
        // labeled("websocket", root) hash → certified_data_set
        var labeled = new HashTree.Labeled(Label, root);
        var rootHash = labeled.Hash;
        fixed (byte* p = rootHash) Ic0.certified_data_set((nint)p, (uint)rootHash.Length);
    }

    private HashTree BuildFullTree()
    {
        if (_entries.Count == 0) return HashTree.Empty.Instance;
        var pairs = new List<KeyValuePair<byte[], HashTree>>(_entries.Count);
        foreach (var kvp in _entries)
        {
            var labelBytes = Encoding.UTF8.GetBytes(kvp.Key);
            pairs.Add(new KeyValuePair<byte[], HashTree>(labelBytes, new HashTree.Leaf(kvp.Value)));
        }
        return HashTree.BuildBalancedLabeled(pairs);
    }

    /// <summary>Build a CBOR-encoded HashTree witness that reveals every
    /// known key wrapped with the "websocket" label. The IC's data
    /// certificate plus this witness lets the client verify each
    /// message's hash.</summary>
    public byte[] BuildFullTreeCbor()
    {
        var labeled = new HashTree.Labeled(Label, BuildFullTree());
        return labeled.EncodeWithSelfDescribe();
    }

    /// <summary>Read the IC system certificate covering certified_data.</summary>
    public unsafe byte[] GetCertificate()
    {
        if (Ic0.data_certificate_present() == 0) return Array.Empty<byte>();
        uint size = Ic0.data_certificate_size();
        var dst = new byte[(int)size];
        fixed (byte* p = dst) Ic0.data_certificate_copy((nint)p, 0, size);
        return dst;
    }
}
