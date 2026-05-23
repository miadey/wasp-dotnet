using System;
using System.Collections.Generic;

namespace Tetris;

/// <summary>
/// Pure Tetris game logic — no Blazor / no JS dependencies.
/// 10×20 playfield, modern guideline rules: 7-bag randomiser, basic
/// SRS rotation (no wall kicks in v1 — invalid rotations just fail),
/// hold piece, ghost piece, NES scoring.
/// </summary>
public sealed class TetrisEngine
{
    public const int Cols = 10;
    public const int Rows = 20;

    /// <summary>Settled cells. 0 = empty, 1..7 = colour index.</summary>
    public int[,] Board { get; } = new int[Rows, Cols];

    public Piece Current;
    public int? Held;            // piece type currently in hold slot (null if empty)
    public bool CanHold = true;  // resets when piece locks
    public Queue<int> NextQueue { get; } = new();

    public int Score { get; private set; }
    public int Lines { get; private set; }
    public int Level => 1 + Lines / 10;
    public bool GameOver { get; private set; }
    public bool Paused { get; set; }

    /// <summary>1 → 60 ticks per cell at level 1; falls by 5 per level.</summary>
    public int GravityIntervalMs => Math.Max(50, 800 - (Level - 1) * 60);

    private long _lastFallMs;
    private readonly Random _rng;
    private readonly List<int> _bag = new();
    private int[]? _lineClearAnim;   // y-coords currently flashing (line clear)
    private long _lineClearAtMs;
    public int[]? LineClearRows => _lineClearAnim;

    public TetrisEngine(int seed = 0)
    {
        _rng = seed == 0 ? new Random() : new Random(seed);
        Refill();
        Spawn();
    }

    // ─── Input ────────────────────────────────────────────────────
    public void Move(int dx)
    {
        if (GameOver || Paused || _lineClearAnim is not null) return;
        if (CanPlace(Current.Type, Current.Rotation, Current.X + dx, Current.Y))
        {
            Current.X += dx;
        }
    }

    public void Rotate(int dir)
    {
        if (GameOver || Paused || _lineClearAnim is not null) return;
        var newRot = (Current.Rotation + dir + 4) % 4;
        // Try the rotation in place, then small wall-kick offsets so
        // I-piece against a wall still rotates. Full SRS kick table is
        // overkill for v1; ±1 ±2 column offsets cover the common cases.
        int[] kicks = { 0, -1, 1, -2, 2 };
        foreach (var kx in kicks)
        {
            if (CanPlace(Current.Type, newRot, Current.X + kx, Current.Y))
            {
                Current.Rotation = newRot;
                Current.X += kx;
                return;
            }
        }
    }

    public void SoftDrop()
    {
        if (GameOver || Paused || _lineClearAnim is not null) return;
        if (CanPlace(Current.Type, Current.Rotation, Current.X, Current.Y + 1))
        {
            Current.Y++;
            Score += 1;  // 1 point per soft-dropped cell
        }
        else
        {
            Lock();
        }
    }

    public void HardDrop()
    {
        if (GameOver || Paused || _lineClearAnim is not null) return;
        int dropped = 0;
        while (CanPlace(Current.Type, Current.Rotation, Current.X, Current.Y + 1))
        {
            Current.Y++;
            dropped++;
        }
        Score += dropped * 2;  // 2 points per hard-dropped cell (NES bonus)
        Lock();
    }

    public void Hold()
    {
        if (GameOver || Paused || !CanHold || _lineClearAnim is not null) return;
        var stash = Held;
        Held = Current.Type;
        if (stash is null) Spawn();
        else Spawn(stash.Value);
        CanHold = false;
    }

    // ─── Game tick ────────────────────────────────────────────────
    public void Tick(long nowMs)
    {
        if (GameOver || Paused) { _lastFallMs = nowMs; return; }
        // Finish line-clear animation after ~250 ms.
        if (_lineClearAnim is not null)
        {
            if (nowMs - _lineClearAtMs > 250)
            {
                CommitLineClears();
                _lineClearAnim = null;
                Spawn();
            }
            _lastFallMs = nowMs;
            return;
        }
        if (_lastFallMs == 0) _lastFallMs = nowMs;
        if (nowMs - _lastFallMs >= GravityIntervalMs)
        {
            if (CanPlace(Current.Type, Current.Rotation, Current.X, Current.Y + 1))
                Current.Y++;
            else
                Lock();
            _lastFallMs = nowMs;
        }
    }

    // ─── Ghost piece — the y-coord the piece would land at ────────
    public int GhostY()
    {
        int y = Current.Y;
        while (CanPlace(Current.Type, Current.Rotation, Current.X, y + 1)) y++;
        return y;
    }

    // ─── Internal helpers ─────────────────────────────────────────
    private void Refill()
    {
        if (NextQueue.Count > 3) return;
        // 7-bag: shuffle each of 0..6 once before reshuffling.
        if (_bag.Count == 0)
        {
            _bag.AddRange(new[] { 0, 1, 2, 3, 4, 5, 6 });
            // Fisher-Yates
            for (int i = _bag.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
            }
        }
        while (_bag.Count > 0 && NextQueue.Count < 7)
        {
            NextQueue.Enqueue(_bag[^1]);
            _bag.RemoveAt(_bag.Count - 1);
            if (_bag.Count == 0) Refill();
        }
    }

    private void Spawn(int? forceType = null)
    {
        int t;
        if (forceType.HasValue) t = forceType.Value;
        else { t = NextQueue.Dequeue(); Refill(); }
        Current = new Piece { Type = t, Rotation = 0, X = 3, Y = -2 };
        CanHold = true;
        // Game over if the spawn position is already blocked.
        if (!CanPlace(t, 0, 3, -2))
        {
            GameOver = true;
        }
    }

    private bool CanPlace(int type, int rotation, int x, int y)
    {
        var shape = PieceShapes.Cells(type, rotation);
        foreach (var (cx, cy) in shape)
        {
            int bx = x + cx, by = y + cy;
            if (bx < 0 || bx >= Cols) return false;
            if (by >= Rows) return false;
            if (by < 0) continue;        // above-the-top is fine on spawn
            if (Board[by, bx] != 0) return false;
        }
        return true;
    }

    private void Lock()
    {
        // Stamp current piece into the board.
        var shape = PieceShapes.Cells(Current.Type, Current.Rotation);
        foreach (var (cx, cy) in shape)
        {
            int bx = Current.X + cx, by = Current.Y + cy;
            if (by < 0)
            {
                GameOver = true;
                return;
            }
            Board[by, bx] = Current.Type + 1;   // 1..7
        }
        DetectAndStartLineClears();
        if (_lineClearAnim is null) Spawn();
    }

    private void DetectAndStartLineClears()
    {
        var rowsToClear = new List<int>();
        for (int r = 0; r < Rows; r++)
        {
            bool full = true;
            for (int c = 0; c < Cols; c++)
                if (Board[r, c] == 0) { full = false; break; }
            if (full) rowsToClear.Add(r);
        }
        if (rowsToClear.Count == 0) return;
        _lineClearAnim = rowsToClear.ToArray();
        _lineClearAtMs = NowMs();
    }

    private void CommitLineClears()
    {
        if (_lineClearAnim is null) return;
        int cleared = _lineClearAnim.Length;
        // Drop everything above each cleared row.
        var set = new HashSet<int>(_lineClearAnim);
        var newBoard = new int[Rows, Cols];
        int writeRow = Rows - 1;
        for (int r = Rows - 1; r >= 0; r--)
        {
            if (set.Contains(r)) continue;
            for (int c = 0; c < Cols; c++) newBoard[writeRow, c] = Board[r, c];
            writeRow--;
        }
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
                Board[r, c] = newBoard[r, c];
        // NES scoring: 40, 100, 300, 1200 × (level)
        int[] pts = { 0, 40, 100, 300, 1200 };
        Score += pts[Math.Min(cleared, 4)] * Level;
        Lines += cleared;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public struct Piece
{
    public int Type;
    public int Rotation;
    public int X;
    public int Y;
}

/// <summary>Piece shape tables. Cell coords are (col, row) offsets
/// from the piece's bounding-box top-left.</summary>
public static class PieceShapes
{
    // Each piece: 4 rotations, each a list of (col, row) filled cells.
    private static readonly (int x, int y)[][][] _shapes = new (int x, int y)[][][]
    {
        // I — 4-block straight; rotates in a 4×4 box
        new (int,int)[][] {
            new [] { (0,1), (1,1), (2,1), (3,1) },
            new [] { (2,0), (2,1), (2,2), (2,3) },
            new [] { (0,2), (1,2), (2,2), (3,2) },
            new [] { (1,0), (1,1), (1,2), (1,3) },
        },
        // O — 2×2, no rotation
        new (int,int)[][] {
            new [] { (1,0), (2,0), (1,1), (2,1) },
            new [] { (1,0), (2,0), (1,1), (2,1) },
            new [] { (1,0), (2,0), (1,1), (2,1) },
            new [] { (1,0), (2,0), (1,1), (2,1) },
        },
        // T
        new (int,int)[][] {
            new [] { (1,0), (0,1), (1,1), (2,1) },
            new [] { (1,0), (1,1), (2,1), (1,2) },
            new [] { (0,1), (1,1), (2,1), (1,2) },
            new [] { (1,0), (0,1), (1,1), (1,2) },
        },
        // S
        new (int,int)[][] {
            new [] { (1,0), (2,0), (0,1), (1,1) },
            new [] { (1,0), (1,1), (2,1), (2,2) },
            new [] { (1,1), (2,1), (0,2), (1,2) },
            new [] { (0,0), (0,1), (1,1), (1,2) },
        },
        // Z
        new (int,int)[][] {
            new [] { (0,0), (1,0), (1,1), (2,1) },
            new [] { (2,0), (1,1), (2,1), (1,2) },
            new [] { (0,1), (1,1), (1,2), (2,2) },
            new [] { (1,0), (0,1), (1,1), (0,2) },
        },
        // J
        new (int,int)[][] {
            new [] { (0,0), (0,1), (1,1), (2,1) },
            new [] { (1,0), (2,0), (1,1), (1,2) },
            new [] { (0,1), (1,1), (2,1), (2,2) },
            new [] { (1,0), (1,1), (0,2), (1,2) },
        },
        // L
        new (int,int)[][] {
            new [] { (2,0), (0,1), (1,1), (2,1) },
            new [] { (1,0), (1,1), (1,2), (2,2) },
            new [] { (0,1), (1,1), (2,1), (0,2) },
            new [] { (0,0), (1,0), (1,1), (1,2) },
        },
    };

    public static (int x, int y)[] Cells(int type, int rotation) =>
        _shapes[type][rotation & 3];

    public static readonly string[] Colors = new[]
    {
        "#00f0f0",   // I — cyan
        "#f0f000",   // O — yellow
        "#a000f0",   // T — purple
        "#00f000",   // S — green
        "#f00000",   // Z — red
        "#0000f0",   // J — blue
        "#f0a000",   // L — orange
    };
}
