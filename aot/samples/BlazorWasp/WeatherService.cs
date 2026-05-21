using System;
using System.Collections.Generic;
using System.Text;
using Wasp.IcCdk;

namespace WaspSample.BlazorWasp;

/// <summary>
/// Weather forecasts persisted in stable memory at offset 64.
/// The list is generated ONCE on first read (deterministic seed = canister
/// time at first call) and never regenerated. Subsequent reads return
/// the cached bytes — same forecasts across all callers, canister
/// upgrades, days. The stock dotnet new blazor Weather page
/// regenerates fresh random data on every component init; here the
/// render-as-query model would discard that immediately. Persisting
/// in stable memory makes the data stable per canister lifetime.
///
/// Layout at stable offset 64:
///   uint32  count
///   for each forecast:
///     uint32  utf8-byte-length of the encoded record
///     bytes   "yyyy-MM-dd|tempC|summary" UTF-8
/// </summary>
public sealed unsafe class WeatherService
{
    private const ulong StableOffset = 64;
    private const ulong MagicOffset = 60;
    private const uint Magic = 0x57454148;  // "WEAH"

    public sealed record Forecast(string Date, int TemperatureC, string Summary)
    {
        public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
    }

    private List<Forecast>? _cached;

    public IReadOnlyList<Forecast> Forecasts
    {
        get
        {
            if (_cached is null)
            {
                EnsureStable();
                if (ReadMagic() != Magic)
                {
                    var fresh = Generate();
                    Write(fresh);
                    WriteMagic();
                    _cached = fresh;
                }
                else
                {
                    _cached = Read();
                }
            }
            return _cached;
        }
    }

    private static List<Forecast> Generate()
    {
        // Deterministic: use canister time as the seed so two replicas
        // produce identical data.
        var seed = unchecked((int)(global::Wasp.IcCdk.Ic0.time() >> 16));
        var rng = new Random(seed);
        var summaries = new[]
        {
            "Freezing", "Bracing", "Chilly", "Cool", "Mild",
            "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
        };
        var startDate = new DateTime(2026, 1, 1);
        var list = new List<Forecast>(5);
        for (int i = 0; i < 5; i++)
        {
            var date = startDate.AddDays(i);
            list.Add(new Forecast(
                date.ToString("yyyy-MM-dd"),
                rng.Next(-20, 55),
                summaries[rng.Next(summaries.Length)]));
        }
        return list;
    }

    private static List<Forecast> Read()
    {
        var sizeBuf = new byte[4];
        ReadBytes(StableOffset, sizeBuf);
        int count = BitConverter.ToInt32(sizeBuf);
        ulong cursor = StableOffset + 4;
        var list = new List<Forecast>(count);
        for (int i = 0; i < count; i++)
        {
            ReadBytes(cursor, sizeBuf);
            int len = BitConverter.ToInt32(sizeBuf);
            cursor += 4;
            var data = new byte[len];
            ReadBytes(cursor, data);
            cursor += (ulong)len;
            var parts = Encoding.UTF8.GetString(data).Split('|');
            list.Add(new Forecast(parts[0], int.Parse(parts[1]), parts[2]));
        }
        return list;
    }

    private static void Write(List<Forecast> list)
    {
        var sizeBuf = BitConverter.GetBytes(list.Count);
        WriteBytes(StableOffset, sizeBuf);
        ulong cursor = StableOffset + 4;
        foreach (var f in list)
        {
            var s = $"{f.Date}|{f.TemperatureC}|{f.Summary}";
            var data = Encoding.UTF8.GetBytes(s);
            WriteBytes(cursor, BitConverter.GetBytes(data.Length));
            cursor += 4;
            WriteBytes(cursor, data);
            cursor += (ulong)data.Length;
        }
    }

    private static uint ReadMagic()
    {
        var b = new byte[4];
        ReadBytes(MagicOffset, b);
        return BitConverter.ToUInt32(b);
    }

    private static void WriteMagic()
    {
        WriteBytes(MagicOffset, BitConverter.GetBytes(Magic));
    }

    private static void EnsureStable()
    {
        if (Ic0.stable64_size() == 0) Ic0.stable64_grow(1);
    }

    private static void ReadBytes(ulong offset, byte[] buf)
    {
        fixed (byte* p = buf) Ic0.stable64_read((ulong)(nint)p, offset, (ulong)buf.Length);
    }

    private static void WriteBytes(ulong offset, byte[] buf)
    {
        fixed (byte* p = buf) Ic0.stable64_write(offset, (ulong)(nint)p, (ulong)buf.Length);
    }
}
