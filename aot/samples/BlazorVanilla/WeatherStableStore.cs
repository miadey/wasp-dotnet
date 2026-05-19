using System;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorVanilla;

/// <summary>
/// Weather forecast persisted in canister stable memory at a fixed offset.
/// Format: u32 length (little-endian) + UTF-8 CSV bytes. Each CSV row is
/// "yyyy-MM-dd,tempC,Summary" newline-separated.
///
/// Reads work in query calls (~10 µs). Writes require an update call.
/// Survives canister upgrades. The "like a file" demonstration: the
/// weather table on /weather shows whatever's currently persisted; if
/// you POST /api/weather/seed it overwrites; if you upgrade the
/// canister the data stays.
/// </summary>
internal static unsafe class WeatherStableStore
{
    // Offset past CounterStableStore (single i32 at 0) and well under
    // the StableRegion header that Wasp.IcCdk.StableRegion starts at 8192.
    private const ulong BaseOffset = 4096;
    private const uint MaxCsvBytes = 8192;

    public static string ReadCsvOrEmpty()
    {
        if (Ic0.stable64_size() == 0) return string.Empty;
        uint len = 0;
        Ic0.stable64_read((ulong)(nint)(&len), BaseOffset, 4);
        if (len == 0 || len > MaxCsvBytes) return string.Empty;
        var buf = new byte[len];
        fixed (byte* p = buf)
        {
            Ic0.stable64_read((ulong)p, BaseOffset + 4, len);
        }
        return Encoding.UTF8.GetString(buf);
    }

    public static void WriteCsv(string csv)
    {
        if (Ic0.stable64_size() == 0)
        {
            // Grow by one 64 KB wasm page — plenty for the CSV blob.
            Ic0.stable64_grow(1);
        }
        var bytes = Encoding.UTF8.GetBytes(csv);
        if (bytes.Length > MaxCsvBytes)
        {
            throw new ArgumentException($"CSV exceeds {MaxCsvBytes} bytes");
        }
        uint len = (uint)bytes.Length;
        Ic0.stable64_write(BaseOffset, (ulong)(nint)(&len), 4);
        if (len > 0)
        {
            fixed (byte* p = bytes)
            {
                Ic0.stable64_write(BaseOffset + 4, (ulong)p, len);
            }
        }
    }

    public static string DefaultCsv()
    {
        // 5 forecasts starting tomorrow. Deterministic — canister query
        // calls can't be non-deterministic and seeding is one-shot anyway.
        var startDate = new DateOnly(2026, 5, 19);
        var summaries = new[]
        {
            "Bracing", "Cool", "Mild", "Warm", "Balmy",
        };
        var sb = new StringBuilder();
        for (int i = 0; i < summaries.Length; i++)
        {
            sb.Append(startDate.AddDays(i).ToString("yyyy-MM-dd"));
            sb.Append(',');
            sb.Append(10 + i * 4);
            sb.Append(',');
            sb.Append(summaries[i]);
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
