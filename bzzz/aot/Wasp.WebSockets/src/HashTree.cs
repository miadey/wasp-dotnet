using System;
using System.Collections.Generic;
using System.Text;

namespace Wasp.WebSockets;

// IC HashTree per the Internet Computer interface spec:
//   https://internetcomputer.org/docs/current/references/ic-interface-spec#certification-encoding
//
// Variants:
//   Empty            — hash = sha256( "ic-hashtree-empty"-domain )
//   Fork(l, r)       — hash = sha256( "ic-hashtree-fork"-domain || hash(l) || hash(r) )
//   Labeled(lab, t)  — hash = sha256( "ic-hashtree-labeled"-domain || lab || hash(t) )
//   Leaf(c)          — hash = sha256( "ic-hashtree-leaf"-domain || c )
//   Pruned(h)        — hash = h
//
// Each variant is encoded as a CBOR array tagged with its discriminator
// (0..4); the whole tree is prefixed with the CBOR self-describe tag.

public abstract class HashTree
{
    public abstract byte[] Hash { get; }
    internal abstract void EncodeTo(Cbor.Writer w);

    public byte[] EncodeWithSelfDescribe()
    {
        var w = new Cbor.Writer();
        w.SelfDescribeTag();
        EncodeTo(w);
        return w.ToArray();
    }

    // ─── Variants ────────────────────────────────────────────────────────
    public sealed class Empty : HashTree
    {
        public static readonly Empty Instance = new();
        private static readonly byte[] _hash = Sha256.Hash(DomainSep("ic-hashtree-empty"));
        public override byte[] Hash => _hash;
        internal override void EncodeTo(Cbor.Writer w)
        {
            w.WriteArrayHeader(1);
            w.WriteUInt(0);
        }
    }

    public sealed class Fork : HashTree
    {
        public Fork(HashTree left, HashTree right) { Left = left; Right = right; }
        public HashTree Left { get; }
        public HashTree Right { get; }
        private byte[]? _hash;
        public override byte[] Hash
        {
            get
            {
                if (_hash is null)
                {
                    var prefix = DomainSep("ic-hashtree-fork");
                    var buf = new byte[prefix.Length + 32 + 32];
                    Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
                    Buffer.BlockCopy(Left.Hash, 0, buf, prefix.Length, 32);
                    Buffer.BlockCopy(Right.Hash, 0, buf, prefix.Length + 32, 32);
                    _hash = Sha256.Hash(buf);
                }
                return _hash;
            }
        }
        internal override void EncodeTo(Cbor.Writer w)
        {
            w.WriteArrayHeader(3);
            w.WriteUInt(1);
            Left.EncodeTo(w);
            Right.EncodeTo(w);
        }
    }

    public sealed class Labeled : HashTree
    {
        public Labeled(byte[] label, HashTree subtree) { Label = label; Subtree = subtree; }
        public byte[] Label { get; }
        public HashTree Subtree { get; }
        private byte[]? _hash;
        public override byte[] Hash
        {
            get
            {
                if (_hash is null)
                {
                    var prefix = DomainSep("ic-hashtree-labeled");
                    var buf = new byte[prefix.Length + Label.Length + 32];
                    Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
                    Buffer.BlockCopy(Label, 0, buf, prefix.Length, Label.Length);
                    Buffer.BlockCopy(Subtree.Hash, 0, buf, prefix.Length + Label.Length, 32);
                    _hash = Sha256.Hash(buf);
                }
                return _hash;
            }
        }
        internal override void EncodeTo(Cbor.Writer w)
        {
            w.WriteArrayHeader(3);
            w.WriteUInt(2);
            w.WriteByteString(Label);
            Subtree.EncodeTo(w);
        }
    }

    public sealed class Leaf : HashTree
    {
        public Leaf(byte[] content) { Content = content; }
        public byte[] Content { get; }
        private byte[]? _hash;
        public override byte[] Hash
        {
            get
            {
                if (_hash is null)
                {
                    var prefix = DomainSep("ic-hashtree-leaf");
                    var buf = new byte[prefix.Length + Content.Length];
                    Buffer.BlockCopy(prefix, 0, buf, 0, prefix.Length);
                    Buffer.BlockCopy(Content, 0, buf, prefix.Length, Content.Length);
                    _hash = Sha256.Hash(buf);
                }
                return _hash;
            }
        }
        internal override void EncodeTo(Cbor.Writer w)
        {
            w.WriteArrayHeader(2);
            w.WriteUInt(3);
            w.WriteByteString(Content);
        }
    }

    public sealed class Pruned : HashTree
    {
        public Pruned(byte[] hash) { Hash = hash; }
        public override byte[] Hash { get; }
        internal override void EncodeTo(Cbor.Writer w)
        {
            w.WriteArrayHeader(2);
            w.WriteUInt(4);
            w.WriteByteString(Hash);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────
    internal static byte[] DomainSep(string name)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var buf = new byte[1 + nameBytes.Length];
        buf[0] = (byte)nameBytes.Length;
        Buffer.BlockCopy(nameBytes, 0, buf, 1, nameBytes.Length);
        return buf;
    }

    /// <summary>Build a balanced HashTree from a sorted list of (label, subtree) pairs.</summary>
    public static HashTree BuildBalancedLabeled(IReadOnlyList<KeyValuePair<byte[], HashTree>> sortedPairs)
    {
        if (sortedPairs.Count == 0) return Empty.Instance;
        return BuildRange(sortedPairs, 0, sortedPairs.Count);
    }

    private static HashTree BuildRange(IReadOnlyList<KeyValuePair<byte[], HashTree>> pairs, int lo, int hi)
    {
        if (hi - lo == 1)
        {
            var p = pairs[lo];
            return new Labeled(p.Key, p.Value);
        }
        int mid = lo + (hi - lo) / 2;
        return new Fork(BuildRange(pairs, lo, mid), BuildRange(pairs, mid, hi));
    }
}
