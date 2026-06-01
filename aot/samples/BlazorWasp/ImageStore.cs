using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Append-only image blob store backed by stable memory.
///
/// Layout (starting at <see cref="BaseOffset"/>):
///   uint32 magic "IMGS"
///   uint32 version (1)
///   uint64 nextOffset    — where the next record will be written
///   uint64 nextId        — id to assign to the next uploaded image
///   --- records ---
///   foreach record:
///     int64  id
///     int32  contentTypeLen
///     bytes  contentType (utf-8, e.g. "image/png")
///     int32  dataLen
///     bytes  data (raw image bytes)
///
/// Heap dict keeps a fast lookup id → (offset, contentTypeLen, dataLen);
/// re-built by scanning records on first Load() after a canister restart
/// or upgrade. Image data itself lives in stable memory; we only
/// fetch it on demand when someone GETs /_chat/img.
/// </summary>
public sealed unsafe class ImageStore
{
    /// <summary>1 MB — safely past chat (~120 KB) and pixel (~4 KB).</summary>
    private const ulong BaseOffset  = 1_000_000;
    private const ulong HeaderSize  = 4 + 4 + 8 + 8;
    private const ulong DataStart   = BaseOffset + HeaderSize;
    private const uint  Magic       = 0x494D4753;       // "IMGS"
    private const uint  Version     = 1;
    /// <summary>Browser already gates this; double-check on the canister
    /// so a hostile client can't blow past the IC ingress budget.</summary>
    public const int MaxBytes = 1_500_000;

    private sealed record Entry(ulong Offset, int ContentTypeLen, int DataLen);

    private readonly Dictionary<long, Entry> _index = new();
    private ulong _nextOffset;
    private long  _nextId;
    private bool  _loaded;

    public int Count { get { EnsureLoaded(); return _index.Count; } }

    /// <summary>Image ids in upload order. Used by the /stable explorer.</summary>
    public IReadOnlyList<long> Ids
    {
        get { EnsureLoaded(); return _index.Keys.OrderBy(k => k).ToArray(); }
    }

    /// <summary>Metadata only — no stable-memory read of the blob body.
    /// Returns null if the id is unknown.</summary>
    public (string contentType, int dataLen, ulong offset)? MetaOf(long id)
    {
        EnsureLoaded();
        if (!_index.TryGetValue(id, out var e)) return null;
        var ct = new byte[e.ContentTypeLen];
        ReadBytes(e.Offset + 8 + 4, ct);
        return (Encoding.UTF8.GetString(ct), e.DataLen, e.Offset);
    }

    public long Add(string contentType, byte[] data)
    {
        if (data is null || data.Length == 0) throw new ArgumentException("empty image");
        if (data.Length > MaxBytes) throw new ArgumentException("image too large");
        if (string.IsNullOrEmpty(contentType)) contentType = "application/octet-stream";
        EnsureLoaded();
        var ctBytes = Encoding.UTF8.GetBytes(contentType);
        var id = _nextId++;
        var offset = _nextOffset;
        var record = new byte[8 + 4 + ctBytes.Length + 4 + data.Length];
        BitConverter.GetBytes(id).CopyTo(record, 0);
        BitConverter.GetBytes(ctBytes.Length).CopyTo(record, 8);
        ctBytes.CopyTo(record, 12);
        BitConverter.GetBytes(data.Length).CopyTo(record, 12 + ctBytes.Length);
        data.CopyTo(record, 12 + ctBytes.Length + 4);
        WriteBytes(offset, record);
        _nextOffset = offset + (ulong)record.Length;
        _index[id] = new Entry(offset, ctBytes.Length, data.Length);
        WriteHeader();
        return id;
    }

    public bool TryGet(long id, out string contentType, out byte[] data)
    {
        EnsureLoaded();
        contentType = ""; data = Array.Empty<byte>();
        if (!_index.TryGetValue(id, out var e)) return false;
        var ct = new byte[e.ContentTypeLen];
        ReadBytes(e.Offset + 8 + 4, ct);
        contentType = Encoding.UTF8.GetString(ct);
        data = new byte[e.DataLen];
        ReadBytes(e.Offset + 8 + 4 + (ulong)e.ContentTypeLen + 4, data);
        return true;
    }

    // ─── storage ─────────────────────────────────────────────────────
    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        EnsurePages(BaseOffset + HeaderSize);
        var hdr = new byte[HeaderSize];
        ReadBytes(BaseOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != Magic || ver != Version)
        {
            _nextOffset = DataStart;
            _nextId = 1;
            WriteHeader();
            return;
        }
        _nextOffset = BitConverter.ToUInt64(hdr, 8);
        _nextId     = BitConverter.ToInt64(hdr, 16);
        // Scan existing records from DataStart up to _nextOffset.
        ulong cursor = DataStart;
        while (cursor + 16 <= _nextOffset)
        {
            var hdrR = new byte[8 + 4];
            ReadBytes(cursor, hdrR);
            var id = BitConverter.ToInt64(hdrR, 0);
            var ctLen = BitConverter.ToInt32(hdrR, 8);
            if (ctLen < 0 || ctLen > 64) break;     // sanity guard
            var lenBytes = new byte[4];
            ReadBytes(cursor + 8 + 4 + (ulong)ctLen, lenBytes);
            var dataLen = BitConverter.ToInt32(lenBytes, 0);
            if (dataLen < 0 || dataLen > MaxBytes) break;
            _index[id] = new Entry(cursor, ctLen, dataLen);
            cursor += 8 + 4 + (ulong)ctLen + 4 + (ulong)dataLen;
        }
    }

    private void WriteHeader()
    {
        var hdr = new byte[HeaderSize];
        BitConverter.GetBytes(Magic).CopyTo(hdr, 0);
        BitConverter.GetBytes(Version).CopyTo(hdr, 4);
        BitConverter.GetBytes(_nextOffset).CopyTo(hdr, 8);
        BitConverter.GetBytes(_nextId).CopyTo(hdr, 16);
        WriteBytes(BaseOffset, hdr);
    }

    private static void EnsurePages(ulong needed)
    {
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes)
            Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
    }

    private static void ReadBytes(ulong offset, byte[] buf)
    {
        fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length);
    }

    private static void WriteBytes(ulong offset, byte[] buf)
    {
        EnsurePages(offset + (ulong)buf.Length);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }
}
