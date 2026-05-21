using System;
using System.Text;
using Wasp.AspNetCore.Blazor.Wasp;
using Wasp.IcCdk;
using Wasp.WebSockets;

namespace WaspSample.BlazorWasp;

/// <summary>
/// v1 demonstration of the IC-native render-as-query model (gh #118).
/// Implements <see cref="IWaspRenderer"/> by hand — no Razor, no
/// component tree. Same shape a future Blazor-Renderer-subclass will
/// produce, just inlined for the proof of concept.
///
/// Counter state lives in stable memory (offset 0, i32 little-endian).
/// Reads are pure (safe for canister_query). Writes happen only in
/// <see cref="DispatchEvent"/>, which runs on canister_update.
/// </summary>
public sealed class CounterRenderer : IWaspRenderer
{
    public WaspRenderBatch Render(WaspRenderRequest req)
    {
        int count = ReadCount();
        return BuildBatch(count, req.Path);
    }

    public WaspRenderBatch DispatchEvent(WaspEventRequest req)
    {
        if (req.HandlerId == "increment")
        {
            int next = ReadCount() + 1;
            WriteCount(next);
            return BuildBatch(next, req.Path);
        }
        // Unknown handler — return current state unchanged.
        return BuildBatch(ReadCount(), req.Path);
    }

    private static WaspRenderBatch BuildBatch(int count, string path)
    {
        // v1 wire format: a single anchor + raw HTML. The batchId is
        // sha256(path|count) so identical state returns identical bytes
        // (boundary v2-cert query response caching is happy with that).
        var batchInput = Encoding.UTF8.GetBytes(path + "|" + count);
        var hashBytes = Sha256.Hash(batchInput);
        var batchId = BytesToHex(hashBytes, 16);

        var html = new StringBuilder(256);
        html.Append("<h1>Counter</h1>");
        html.Append("<p role=\"status\">Current count: ");
        html.Append(count);
        html.Append("</p>");
        html.Append("<button class=\"btn btn-primary\" data-wasp-evt-click=\"increment\">Click me</button>");

        return new WaspRenderBatch
        {
            BatchId = batchId,
            Html = html.ToString(),
            Anchor = "#wasp-root",
        };
    }

    // ─── Counter state in stable memory ──────────────────────────────
    private const ulong CounterOffset = 0;

    private static unsafe int ReadCount()
    {
        if (Ic0.stable64_size() == 0) return 0;
        int value = 0;
        Ic0.stable64_read((ulong)(nint)(&value), CounterOffset, sizeof(int));
        return value;
    }

    private static unsafe void WriteCount(int value)
    {
        if (Ic0.stable64_size() == 0)
        {
            Ic0.stable64_grow(1);
        }
        Ic0.stable64_write(CounterOffset, (ulong)(nint)(&value), sizeof(int));
    }

    private static string BytesToHex(byte[] bytes, int n)
    {
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n && i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
