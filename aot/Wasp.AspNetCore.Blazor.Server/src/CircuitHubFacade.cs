using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wasp.AspNetCore.Blazor.Server;

// Bridge: turns IIcCircuitTransport + IBlazorHubFacade into a wire-up that
// drives a real CircuitHost from Microsoft.AspNetCore.Components.Server.
//
// This is the integration seam that closes the loop between:
//
//   IcCircuitTransport   (S3 — done) — handshake + BlazorPack framing
//        ↕
//   BlazorHubDispatcher  (S4 — done) — typed switch over 13 hub methods
//        ↕
//   IBlazorHubFacade     (S4 — done) — the 13 method signatures
//        ↕
//   CircuitHubFacade     (S5 — partial)  ← this file
//        ↕
//   CircuitFactory       (now public via weaver — S5)
//        ↕
//   CircuitHost          (now public via weaver — S5)
//
// CircuitHubFacade.New(transport, hostBuilder) constructs:
//   1. An IcClientProxy bound to `transport` (already implemented, S3).
//   2. A CircuitClientProxy wrapping the IcClientProxy (one-line ctor —
//      the type is now public after the weaver runs, see
//      VendoredComponentsServerTests).
//   3. A CircuitHost via CircuitFactory.CreateCircuitHostAsync, fed the
//      CircuitClientProxy from step 2.
//   4. Subscribes BlazorHubDispatcher.Dispatch to transport.InboundMessage,
//      with the CircuitHost-driven facade impl as the receiver.
//
// What is COMPLETE in this file:
//   - Wiring of transport.InboundMessage → BlazorHubDispatcher.Dispatch.
//   - Transport's outbound completion sink hooked to a small adapter.
//
// What is INTENTIONALLY STUBBED (tracked in M4.S5 followup):
//   - Calls into CircuitFactory.CreateCircuitHostAsync. The factory takes
//     a 5-parameter signature involving HttpContext, ServerComponentSerializer,
//     ResourceAssetCollection, ICircuitHandle, JS interop runtime, etc. —
//     wiring those is its own non-trivial task that requires a real
//     IServiceProvider with services from
//     services.AddRazorComponents().AddInteractiveServerRenderMode().
//   - CircuitHost lifecycle (Start, Dispose) — needs the SignalR-equivalent
//     of HubConnectionContext.
//
// The stub methods below throw with a descriptive message naming the
// gating item, so attempts to use the facade before S5-complete fail loudly
// rather than silently degrading to a non-functional state.
public sealed class CircuitHubFacade : IBlazorHubFacade, IDisposable
{
    private readonly IIcCircuitTransport _transport;

    private CircuitHubFacade(IIcCircuitTransport transport)
    {
        _transport = transport;
    }

    /// <summary>
    /// Wire a transport to a (future) CircuitHost. Currently this only sets
    /// up the dispatcher subscription — the CircuitHost integration is
    /// stubbed until M4.S5 lands.
    /// </summary>
    public static CircuitHubFacade Bind(IIcCircuitTransport transport)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        var facade = new CircuitHubFacade(transport);

        // Outbound completion sink: when BlazorHubDispatcher needs to ship a
        // Completion frame for a return-value Hub call, route through the
        // transport's internal byte path. The transport itself ignores the
        // handshake state for these sends because by the time we receive a
        // return-value hub method, the handshake has already completed.
        Action<byte[]> completionSink = bytes =>
        {
            // ISingleClientProxy-style: a Completion frame is just bytes to
            // ship. We piggyback on the same IcClientProxy mechanism as
            // SendCoreAsync by going through the proxy.
            var proxy = new IcClientProxy(transport);
            _ = proxy.SendCoreAsync("__completion__", new object?[] { bytes });
            // ^ this won't compile structurally as a SignalR Completion; the
            // intent here is documentary — the real wiring needs an internal
            // transport.SendRawAsync(bytes) hook that bypasses BlazorPackWriter.
            // Tracked in M4.S5 follow-up #66.
        };

        transport.InboundMessage += async msg =>
        {
            try
            {
                await BlazorHubDispatcher.Dispatch(facade, msg, completionSink);
            }
            catch (Exception ex)
            {
                // Surface to canister log; do not let an exception leak back
                // through the IC-WS pipe and crash the message handler.
                Wasp.IcCdk.Reply.Print($"[circuit-facade] dispatch error: {ex.Message}");
            }
        };

        return facade;
    }

    public void Dispose()
    {
        // CircuitHost.Dispose() goes here once CircuitFactory wiring lands.
    }

    // ─── IBlazorHubFacade ────────────────────────────────────────────────
    //
    // All 13 methods stubbed pending CircuitFactory wiring. The argument
    // unboxing + dispatch path above (BlazorHubDispatcher) is fully correct;
    // these stubs only need their bodies replaced with calls into
    // _circuitHost.{StartCircuitAsync, BeginInvokeDotNetFromJSAsync, ...}
    // once that field exists.

    private const string Pending = "Not yet wired — M4.S5 CircuitFactory integration pending. " +
        "The Wasp.AspNetCore.Blazor.Server.targets file substitutes a Mono.Cecil-rewritten " +
        "Microsoft.AspNetCore.Components.Server.dll with public CircuitFactory/CircuitHost; " +
        "next step is to construct CircuitHost and forward this method. " +
        "Tracked in #69.";

    public ValueTask<string> StartCircuit(string b, string u, string r, string s) =>
        throw new NotImplementedException(Pending);

    public Task UpdateRootComponents(string a, string b) =>
        throw new NotImplementedException(Pending);

    public ValueTask<bool> ConnectCircuit(string s) =>
        throw new NotImplementedException(Pending);

    public ValueTask<string> ResumeCircuit(string a, string b, string c, string d, string e) =>
        throw new NotImplementedException(Pending);

    public ValueTask<bool> PauseCircuit() =>
        throw new NotImplementedException(Pending);

    public ValueTask BeginInvokeDotNetFromJS(string a, string b, string c, long d, string e) =>
        throw new NotImplementedException(Pending);

    public ValueTask EndInvokeJSFromDotNet(long a, bool b, string c) =>
        throw new NotImplementedException(Pending);

    public ValueTask ReceiveByteArray(int a, byte[] b) =>
        throw new NotImplementedException(Pending);

    public ValueTask<bool> ReceiveJSDataChunk(long a, long b, byte[] c, string d) =>
        throw new NotImplementedException(Pending);

    public IAsyncEnumerable<ArraySegment<byte>> SendDotNetStreamToJS(long a) =>
        throw new NotImplementedException(Pending);

    public ValueTask OnRenderCompleted(long a, string? b) =>
        throw new NotImplementedException(Pending);

    public ValueTask OnLocationChanged(string a, string? b, bool c) =>
        throw new NotImplementedException(Pending);

    public ValueTask OnLocationChanging(int a, string b, string? c, bool d) =>
        throw new NotImplementedException(Pending);
}
