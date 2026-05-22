using System;
using System.Collections.Generic;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// r/place-clone canvas: a shared <see cref="Width"/>×<see cref="Height"/>
/// pixel grid where each pixel is a 4-bit colour index into a fixed
/// 16-colour palette. Backed entirely by stable memory.
///
/// Stable-memory layout at offset 16384:
///   uint32 magic ("PIXL")
///   uint32 version (1)
///   uint32 width
///   uint32 height
///   uint64 placedCount         // total placements lifetime
///   bytes  grid[width × height]  one byte per pixel (4 high bits unused)
/// </summary>
public sealed unsafe class PixelService
{
    public const int Width  = 40;
    public const int Height = 40;
    public const int CooldownMs = 3_000;

    private const ulong MagicOffset = 16384;
    private const ulong HeaderSize  = 4 + 4 + 4 + 4 + 8;   // magic+ver+w+h+placedCount
    private const ulong GridOffset  = MagicOffset + HeaderSize;
    private const uint  Magic       = 0x5049584C;          // "PIXL"
    private const uint  Version     = 1;

    /// <summary>Discord-inspired 16-colour palette. Index 0 = canvas background.</summary>
    public static readonly string[] Palette = new[]
    {
        "#ffffff", "#d4d7d9", "#80848e", "#23272a",  // whites + grey + near-black
        "#ed4245", "#f57731", "#faa61a", "#fee75c",  // red, orange, amber, yellow
        "#57f287", "#3ba55d", "#2ecc71", "#1abc9c",  // greens
        "#5865f2", "#3b88c3", "#9b59b6", "#eb459e",  // blue → purple → pink
    };

    private byte[]? _grid;
    private long _placedCount;
    // Per-username last-placed timestamp (in-canister volatile memory —
    // resets across canister upgrades; that's intentional for a demo).
    private readonly Dictionary<string, long> _cooldowns = new(StringComparer.OrdinalIgnoreCase);

    public int PaletteSize => Palette.Length;
    public long PlacedCount => Load().count;

    public byte[] Snapshot()
    {
        var (grid, _) = Load();
        var copy = new byte[grid.Length];
        Array.Copy(grid, copy, grid.Length);
        return copy;
    }

    public byte Get(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height) return 0;
        var (grid, _) = Load();
        return grid[y * Width + x];
    }

    /// <summary>Milliseconds remaining on the user's cooldown — 0 if ready.</summary>
    public long CooldownRemainingMs(string username)
    {
        if (string.IsNullOrEmpty(username)) return 0;
        if (!_cooldowns.TryGetValue(username, out var last)) return 0;
        var diff = CooldownMs - (NowMs() - last);
        return diff > 0 ? diff : 0;
    }

    public bool TryPlace(int x, int y, int colorIndex, string username, out string? error)
    {
        error = null;
        if ((uint)x >= Width || (uint)y >= Height)
        {
            error = "out of bounds";
            return false;
        }
        if ((uint)colorIndex >= (uint)Palette.Length)
        {
            error = "bad colour";
            return false;
        }
        username = SanitiseUsername(username);
        var rem = CooldownRemainingMs(username);
        if (rem > 0)
        {
            error = $"cooldown {rem}ms";
            return false;
        }
        var (grid, count) = Load();
        var idx = y * Width + x;
        if (grid[idx] == (byte)colorIndex)
        {
            error = "same colour";
            return false;
        }
        grid[idx] = (byte)colorIndex;
        _placedCount = count + 1;
        _grid = grid;
        Persist(grid, _placedCount);
        _cooldowns[username] = NowMs();
        return true;
    }

    // ─── storage ─────────────────────────────────────────────────────
    private (byte[] grid, long count) Load()
    {
        if (_grid is not null) return (_grid, _placedCount);
        EnsureGrown();
        var hdr = new byte[HeaderSize];
        ReadBytes(MagicOffset, hdr);
        var magic = BitConverter.ToUInt32(hdr, 0);
        var ver   = BitConverter.ToUInt32(hdr, 4);
        if (magic != Magic || ver != Version)
        {
            _grid = new byte[Width * Height];        // all index 0 (white)
            _placedCount = 0;
            Persist(_grid, _placedCount);
            return (_grid, _placedCount);
        }
        var w = BitConverter.ToUInt32(hdr, 8);
        var h = BitConverter.ToUInt32(hdr, 12);
        _placedCount = BitConverter.ToInt64(hdr, 16);
        if (w != Width || h != Height)
        {
            // Dimensions changed across deploys — reinit.
            _grid = new byte[Width * Height];
            _placedCount = 0;
            Persist(_grid, _placedCount);
            return (_grid, _placedCount);
        }
        _grid = new byte[Width * Height];
        ReadBytes(GridOffset, _grid);
        return (_grid, _placedCount);
    }

    private void Persist(byte[] grid, long placedCount)
    {
        var hdr = new byte[HeaderSize];
        BitConverter.GetBytes(Magic).CopyTo(hdr, 0);
        BitConverter.GetBytes(Version).CopyTo(hdr, 4);
        BitConverter.GetBytes((uint)Width).CopyTo(hdr, 8);
        BitConverter.GetBytes((uint)Height).CopyTo(hdr, 12);
        BitConverter.GetBytes(placedCount).CopyTo(hdr, 16);
        WriteBytes(MagicOffset, hdr);
        WriteBytes(GridOffset, grid);
    }

    private static long NowMs() => (long)(Ic0.time() / 1_000_000UL);

    private static string SanitiseUsername(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Anonymous";
        raw = raw.Trim();
        return raw.Length > 24 ? raw.Substring(0, 24) : raw;
    }

    private static void EnsureGrown()
    {
        ulong needed = GridOffset + (ulong)(Width * Height);
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes) Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
    }

    private static void ReadBytes(ulong offset, byte[] buf)
    { fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length); }
    private static void WriteBytes(ulong offset, byte[] buf)
    {
        ulong needed = offset + (ulong)buf.Length;
        ulong haveBytes = Ic0.stable64_size() * 65536UL;
        if (needed > haveBytes) Ic0.stable64_grow(((needed - haveBytes) + 65535UL) / 65536UL);
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }
}
