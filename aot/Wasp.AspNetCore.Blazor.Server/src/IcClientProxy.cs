using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Wasp.AspNetCore.Blazor.Server;

// Adapter that satisfies SignalR's ISingleClientProxy by funneling
// SendCoreAsync / InvokeCoreAsync through an IIcCircuitTransport. The S5
// Cecil weaver will swap one of these into CircuitClientProxy.Client so
// CircuitHost emits its outbound calls (JS.RenderBatch, JS.BeginInvokeJS,
// etc.) over WaspWs.Send instead of SignalR.
//
// Two-arg constructor preserved for tests that don't want a full transport
// stack: pass a raw byte-sink and InvokeCoreAsync<T> is unavailable.
public sealed class IcClientProxy : ISingleClientProxy
{
    private readonly IIcCircuitTransport? _transport;
    private readonly Action<byte[]>? _rawSend;

    /// <summary>Production constructor — adapts an IIcCircuitTransport.</summary>
    public IcClientProxy(IIcCircuitTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>Test-only constructor — bypasses transport, captures wire bytes.</summary>
    public IcClientProxy(Action<byte[]> send)
    {
        _rawSend = send ?? throw new ArgumentNullException(nameof(send));
    }

    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(method)) throw new ArgumentException("method required", nameof(method));
        if (args is null) throw new ArgumentNullException(nameof(args));
        cancellationToken.ThrowIfCancellationRequested();

        if (_transport is not null)
            return _transport.SendCoreAsync(method, args, cancellationToken).AsTask();

        _rawSend!(BlazorPackWriter.WriteInvocation(method, args));
        return Task.CompletedTask;
    }

    public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
    {
        if (_transport is null)
            throw new NotSupportedException(
                "InvokeCoreAsync<T> requires the transport-backed constructor; the raw-send " +
                "constructor cannot correlate Completion frames.");
        return _transport.InvokeCoreAsync<T>(method, args, cancellationToken).AsTask();
    }
}
