using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wasp.AspNetCore.Blazor.Server;

// The 13 inbound ComponentHub method signatures, projected as an interface
// the consumer (eventually a Cecil-woven CircuitHost facade) implements.
//
// This is the AOT-clean replacement for SignalR's reflective
// HubMethodDescriptor dispatch. `BlazorHubDispatcher.Dispatch` takes one of
// these + a decoded IcCircuitInboundMessage and routes to the right method
// via a hand-coded switch (no Type.GetMethods, no MethodInfo.Invoke).
//
// Signatures lifted verbatim from
// dotnet/aspnetcore release/10.0 src/Components/Server/src/ComponentHub.cs
// — see docs/m4-s2-circuit-coupling.md "Inbound seam" for the full table.
public interface IBlazorHubFacade
{
    ValueTask<string> StartCircuit(
        string baseUri, string uri, string serializedComponentRecords, string applicationState);

    Task UpdateRootComponents(string serializedComponentOperations, string applicationState);

    ValueTask<bool> ConnectCircuit(string circuitIdSecret);

    ValueTask<string> ResumeCircuit(
        string circuitIdSecret, string baseUri, string uri, string rootComponents, string applicationState);

    ValueTask<bool> PauseCircuit();

    ValueTask BeginInvokeDotNetFromJS(
        string callId, string assemblyName, string methodIdentifier, long dotNetObjectId, string argsJson);

    ValueTask EndInvokeJSFromDotNet(long asyncHandle, bool succeeded, string arguments);

    ValueTask ReceiveByteArray(int id, byte[] data);

    ValueTask<bool> ReceiveJSDataChunk(long streamId, long chunkId, byte[] chunk, string error);

    IAsyncEnumerable<ArraySegment<byte>> SendDotNetStreamToJS(long streamId);

    ValueTask OnRenderCompleted(long renderId, string? errorMessageOrNull);

    ValueTask OnLocationChanged(string uri, string? state, bool intercepted);

    ValueTask OnLocationChanging(int callId, string uri, string? state, bool intercepted);
}
