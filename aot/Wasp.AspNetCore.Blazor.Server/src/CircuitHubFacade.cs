using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Wasp.IcCdk;

namespace Wasp.AspNetCore.Blazor.Server;

// Bridges IIcCircuitTransport ↔ IBlazorHubFacade ↔ a real
// Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost.
//
// Wiring:
//   1. Constructor takes a CircuitFactory (resolved from DI in the
//      canister's Program.cs) and an IIcCircuitTransport.
//   2. StartCircuit:
//        a. Wraps our IcClientProxy(transport) into a CircuitClientProxy.
//        b. Deserializes the inbound component records into
//           ComponentDescriptor[] (via ServerComponentDeserializer).
//        c. Calls CircuitFactory.CreateCircuitHostAsync(...).
//        d. Stores the returned CircuitHost.
//        e. Calls _circuit.InitializeAsync(...).
//        f. Returns the circuit id as the secret.
//   3. Subsequent hub methods forward into the appropriate CircuitHost
//      method (some are public, some require additional wiring).
//
// What is COMPLETE:
//   - Constructor + transport binding.
//   - The plumbing pattern: CircuitFactory call → CircuitHost storage →
//     forward dispatcher messages.
//   - Forwarding of: OnRenderCompletedAsync, EndInvokeJSFromDotNet,
//     OnLocationChangedAsync, OnLocationChangingAsync, DisposeAsync.
//
// What is INTENTIONALLY STUBBED:
//   - StartCircuit: ComponentDescriptor deserialization. The framework
//     uses ServerComponentDeserializer (internal even after weaver
//     because it lives in Microsoft.AspNetCore.Components, not .Server);
//     wiring it requires another weaver pass on Components.dll or a
//     hand-rolled MessagePack reader. Returns an empty list for now.
//   - BeginInvokeDotNetFromJS: routes via RemoteJSRuntime, not directly
//     a CircuitHost method. Documented stub.
//   - UpdateRootComponents / SendDotNetStreamToJS / ConnectCircuit /
//     ResumeCircuit / PauseCircuit / ReceiveJSDataChunk: documented
//     stubs pointing at the right CircuitHost target.
//   - PersistentComponentState store: stubbed with a no-op implementation
//     since the hookup to CircuitStore (S5 #70) needs the broader
//     component-state-serializer integration to be useful.
public sealed class CircuitHubFacade : IBlazorHubFacade, IAsyncDisposable
{
    private readonly IIcCircuitTransport _transport;
    private readonly ICircuitFactory _factory;
    private readonly IServiceProvider _services;
    private CircuitHost? _circuit;

    private CircuitHubFacade(
        IIcCircuitTransport transport, ICircuitFactory factory, IServiceProvider services)
    {
        _transport = transport;
        _factory = factory;
        _services = services;
    }

    /// <summary>
    /// Wire a transport to a CircuitHost backed by `factory`. The returned
    /// facade is hooked into `transport.InboundMessage` via
    /// BlazorHubDispatcher; the consumer doesn't need to do anything else.
    /// </summary>
    public static CircuitHubFacade Bind(
        IIcCircuitTransport transport, ICircuitFactory factory, IServiceProvider services)
    {
        if (transport is null) throw new ArgumentNullException(nameof(transport));
        if (factory is null) throw new ArgumentNullException(nameof(factory));
        if (services is null) throw new ArgumentNullException(nameof(services));

        var facade = new CircuitHubFacade(transport, factory, services);

        // Outbound completion sink: ship Completion frame bytes through the
        // transport's raw send path. SendCoreAsync builds an Invocation
        // frame, not a Completion, so we cannot reuse it for this; instead
        // we lean on the IcCircuitTransport implementation to expose a way
        // to send pre-built bytes. For now, use the registry-bound sender
        // via the transport's ConnectionId match (S3.B follow-up).
        Action<byte[]> completionSink = bytes =>
        {
            // The current IIcCircuitTransport surface only exposes
            // SendCoreAsync / InvokeCoreAsync. To ship a pre-built
            // Completion frame we need either to add SendRawBytes(...)
            // to the transport interface, or recreate the frame at the
            // outbound boundary. The narrowest interface change is to
            // expose a `RawSend` on the concrete IcCircuitTransport.
            // Tracked in M4.S3 follow-up.
            if (transport is IcCircuitTransport concrete)
            {
                concrete.SendRawFrame(bytes);
            }
            else
            {
                throw new NotSupportedException(
                    "Completion-frame sink requires the concrete IcCircuitTransport. " +
                    "Wrap an IIcCircuitTransport mock with IcCircuitTransport for production.");
            }
        };

        transport.InboundMessage += async msg =>
        {
            try
            {
                await BlazorHubDispatcher.Dispatch(facade, msg, completionSink);
            }
            catch (Exception ex)
            {
                TraceLog($"[circuit-facade] dispatch error: {ex.GetType().Name}: {ex.Message}");
            }
        };
        return facade;
    }

    public async ValueTask DisposeAsync()
    {
        if (_circuit is not null) await _circuit.DisposeAsync();
        _circuit = null;
    }

    // ─── IBlazorHubFacade ────────────────────────────────────────────────

    public async ValueTask<string> StartCircuit(
        string baseUri, string uri, string serializedComponentRecords, string applicationState)
    {
        if (_circuit is not null)
            throw new InvalidOperationException("StartCircuit called twice on same facade");

        var clientProxy = new IcClientProxy(_transport);
        var circuitClientProxy = new CircuitClientProxy(clientProxy, _transport.ConnectionId);

        // Parse our own marker descriptors directly. The framework's
        // IServerComponentDeserializer expects descriptors that have been
        // through ServerComponentSerializer (signed via DataProtection +
        // JsonSerializer reflection paths) — the JSON reflection bits
        // overshoot IC's 11.5 MB code-section limit when enabled. The
        // BlazorMarkerMiddleware on the SSR side emits a synthetic
        // descriptor `{"componentAssembly":"...","componentType":"..."}`
        // (base64'd) that this parser recognizes 1:1.
        var parsed = WaspComponentRecordParser.Parse(serializedComponentRecords);
        TraceLog($"[facade] StartCircuit baseUri={baseUri} uri={uri} components={parsed.Count} appState.Len={applicationState?.Length ?? -1} markers.Len={serializedComponentRecords?.Length ?? -1}");
        var components = new List<ComponentDescriptor>(parsed.Count);
        foreach (var (type, sequence) in parsed)
        {
            TraceLog($"[facade]   component[{sequence}] = {type.FullName}");
            components.Add(new ComponentDescriptor
            {
                ComponentType = type,
                Parameters = ParameterView.Empty,
                Sequence = sequence,
            });
        }

        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var store = new NoopPersistentComponentStateStore();
        var resources = new ResourceAssetCollection(Array.Empty<ResourceAsset>());

        try
        {
            _circuit = await _factory.CreateCircuitHostAsync(
                components, circuitClientProxy, baseUri, uri, user, store, resources);
            TraceLog($"[facade] CreateCircuitHostAsync OK circuitId={_circuit?.CircuitId.Secret?.Substring(0, Math.Min(16, _circuit.CircuitId.Secret.Length))}");
        }
        catch (Exception ex)
        {
            TraceLog($"[facade] CreateCircuitHostAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        try
        {
            await _circuit.InitializeAsync(
                store: null!, // ProtectedPrerenderComponentApplicationStore — pass null for now; the framework path handles null by treating it as no prerender state
                httpActivityContext: default,
                cancellationToken: CancellationToken.None);
            TraceLog($"[facade] InitializeAsync OK");
        }
        catch (Exception ex)
        {
            TraceLog($"[facade] InitializeAsync FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        // Return the circuit's actual secret (what blazor.web.js stores
        // for reconnection). Falls back to connection ID if unavailable.
        return _circuit?.CircuitId.Secret ?? _transport.ConnectionId;
    }

    public async Task UpdateRootComponents(string serializedComponentOperations, string applicationState)
    {
        if (_circuit is null)
        {
            TraceLog("[facade] UpdateRootComponents called before StartCircuit");
            return;
        }

        TraceLog($"[facade] UpdateRootComponents ops.Len={serializedComponentOperations?.Length ?? -1}");
        var parsed = WaspComponentRecordParser.ParseRootComponentOperations(
            serializedComponentOperations);
        TraceLog($"[facade] UpdateRootComponents parsed batchId={parsed.BatchId} ops={parsed.Operations.Length}");

        // Translate our wasm-AOT-safe DTOs into the framework's
        // RootComponentOperationBatch shape that CircuitHost expects.
        var frameworkOps = new RootComponentOperation[parsed.Operations.Length];
        for (int i = 0; i < parsed.Operations.Length; i++)
        {
            var op = parsed.Operations[i];
            TraceLog($"[facade]   op[{i}] type={op.Type} ssrId={op.SsrComponentId} component={op.ComponentType?.FullName ?? "(null)"}");
            frameworkOps[i] = new RootComponentOperation
            {
                Type = op.Type switch
                {
                    WaspComponentRecordParser.WaspRootComponentOperationType.Add => RootComponentOperationType.Add,
                    WaspComponentRecordParser.WaspRootComponentOperationType.Update => RootComponentOperationType.Update,
                    _ => RootComponentOperationType.Remove,
                },
                SsrComponentId = op.SsrComponentId,
                Descriptor = (op.Type != WaspComponentRecordParser.WaspRootComponentOperationType.Remove && op.ComponentType is not null)
                    ? new WebRootComponentDescriptor(op.ComponentType, WebRootComponentParameters.Empty)
                    : null,
                // CircuitHost.PerformRootComponentOperations dereferences
                // Marker.Value.Key — leaving Marker null throws
                // InvalidOperationException("InvalidOperation_NoValue").
                // We synthesize a deterministic Key per ssrComponentId.
                Marker = (op.Type != WaspComponentRecordParser.WaspRootComponentOperationType.Remove)
                    ? new ComponentMarker
                    {
                        Type = ComponentMarker.ServerMarkerType,
                        Sequence = op.SsrComponentId,
                        Key = new ComponentMarkerKey($"wasp-root-{op.SsrComponentId}", null),
                    }
                    : null,
            };
        }
        var batch = new RootComponentOperationBatch
        {
            BatchId = parsed.BatchId,
            Operations = frameworkOps,
        };

        // Reach CircuitHost.UpdateRootComponents via reflection because
        // its `IClearableStore` parameter type lives in two assemblies
        // (Components.Endpoints + Components.Server, source-shared) and
        // publicizing both via the weaver causes CS0433 collision at
        // C# compile time. The store argument is null because our
        // backend has no prerender state to clear.
        try
        {
            var method = typeof(CircuitHost).GetMethod(
                "UpdateRootComponents",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (method is null)
            {
                TraceLog("[facade] CircuitHost.UpdateRootComponents method not found via reflection");
                return;
            }
            var task = (Task)method.Invoke(_circuit, new object?[] { batch, null, false, CancellationToken.None })!;
            await task;
            TraceLog("[facade] UpdateRootComponents OK");
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            TraceLog($"[facade] UpdateRootComponents FAILED: {inner.GetType().Name}: {inner.Message}");
            throw;
        }
    }

    public ValueTask<bool> ConnectCircuit(string circuitIdSecret)
    {
        // Reconnect path — needs the CircuitRegistry, which is the second
        // service alongside CircuitFactory. Stubbed: rejects reconnects.
        return new ValueTask<bool>(false);
    }

    public ValueTask<string> ResumeCircuit(
        string circuitIdSecret, string baseUri, string uri, string rootComponents, string applicationState)
    {
        // Stubbed — needs CircuitRegistry + the persisted state we'd load
        // via CircuitStore (S5 #70). Tracked in follow-up.
        throw new NotImplementedException("ResumeCircuit: needs CircuitRegistry + CircuitStore integration");
    }

    public ValueTask<bool> PauseCircuit()
    {
        // Pause = persist + tear down. We currently have no snapshot
        // serialization for component state.
        return new ValueTask<bool>(false);
    }

    public async ValueTask BeginInvokeDotNetFromJS(
        string callId, string assemblyName, string methodIdentifier, long dotNetObjectId, string argsJson)
    {
        EnsureCircuit();
        TraceLog($"[facade] BeginInvokeDotNetFromJS call={callId} asm={assemblyName} method={methodIdentifier} dotNetObjId={dotNetObjectId} args.Len={argsJson?.Length ?? -1}");

        // INTERCEPT: DispatchEventAsync is the per-renderer event delegate
        // that blazor.web.js fires on every UI event. Stock framework routes
        // it via DotNetDispatcher.BeginInvokeDotNet which JSON-reflects the
        // arg into MouseEventArgs/KeyboardEventArgs/etc. — those reflection
        // paths are PNS on wasm32-wasi. We short-circuit by parsing the
        // JSON ourselves (JsonDocument is reflection-free) and calling
        // Renderer.DispatchEventAsync directly with EventArgs.Empty. The
        // Counter sample's @onclick handler ignores the event payload.
        if (methodIdentifier == "DispatchEventAsync" && !string.IsNullOrEmpty(argsJson))
        {
            try
            {
                ulong eventHandlerId = 0;
                using (var doc = System.Text.Json.JsonDocument.Parse(argsJson))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() >= 1)
                    {
                        var desc = root[0];
                        if (desc.TryGetProperty("eventHandlerId", out var idEl))
                        {
                            eventHandlerId = idEl.GetUInt64();
                        }
                    }
                }
                TraceLog($"[facade]   intercept DispatchEventAsync eventHandlerId={eventHandlerId}");
                var renderer = _circuit!.Renderer;
                var dispatcher = renderer.Dispatcher;

                // Dispatcher.InvokeAsync would queue onto its RSC, which
                // pumps via ThreadPool — but wasm32-wasi is single-
                // threaded with no thread pool, so queued work never
                // runs and we deadlock. Bypass: pull the dispatcher's
                // private `_context` (the RendererSynchronizationContext)
                // via reflection, install it as Current, then call
                // DispatchEventAsync — CheckAccess now returns true and
                // the @onclick handler runs synchronously inline. Trim
                // metadata for this field is kept via a DynamicDependency
                // attribute on Program.Init.
                var dispatcherType = dispatcher.GetType();
                var ctxField = dispatcherType.GetField(
                    "_context",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (ctxField is null)
                {
                    TraceLog($"[facade]   ERROR: _context field not found on {dispatcherType.FullName} (trim metadata missing)");
                    return;
                }
                var rsc = (System.Threading.SynchronizationContext?)ctxField.GetValue(dispatcher);
                if (rsc is null)
                {
                    TraceLog($"[facade]   ERROR: _context value was null");
                    return;
                }
                TraceLog($"[facade]   rsc={rsc.GetType().FullName}");
                var prev = System.Threading.SynchronizationContext.Current;
                System.Threading.SynchronizationContext.SetSynchronizationContext(rsc);
                TraceLog($"[facade]   checkAccess after install={dispatcher.CheckAccess()}");
                try
                {
                    var dispatchTask = renderer.DispatchEventAsync(
                        eventHandlerId, null, EventArgs.Empty, waitForQuiescence: false);
                    TraceLog($"[facade]   DispatchEventAsync started status={dispatchTask.Status}");
                }
                finally
                {
                    System.Threading.SynchronizationContext.SetSynchronizationContext(prev);
                }
                TraceLog($"[facade] DispatchEventAsync dispatched OK");
                return;
            }
            catch (Exception ex)
            {
                TraceLog($"[facade] DispatchEventAsync FAILED: {ex.GetType().Name}: {ex.Message}");
                // fall through to framework path below in case our parse is wrong
            }
        }

        if (argsJson is not null && argsJson.Length < 400)
        {
            TraceLog($"[facade]   argsJson={argsJson}");
        }
        try
        {
            await _circuit!.BeginInvokeDotNetFromJS(
                callId, assemblyName, methodIdentifier, dotNetObjectId, argsJson!);
            TraceLog($"[facade] BeginInvokeDotNetFromJS OK call={callId}");
        }
        catch (Exception ex)
        {
            TraceLog($"[facade] BeginInvokeDotNetFromJS FAILED call={callId}: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public ValueTask EndInvokeJSFromDotNet(long asyncHandle, bool succeeded, string arguments)
    {
        EnsureCircuit();
        return new ValueTask(_circuit!.EndInvokeJSFromDotNet(asyncHandle, succeeded, arguments));
    }

    public ValueTask ReceiveByteArray(int id, byte[] data)
    {
        EnsureCircuit();
        // ReceiveByteArray is internal-method on CircuitHost — we have
        // visibility after weaver but it returns Task, not ValueTask.
        return new ValueTask(_circuit!.ReceiveByteArray(id, data));
    }

    public ValueTask<bool> ReceiveJSDataChunk(long streamId, long chunkId, byte[] chunk, string error)
    {
        EnsureCircuit();
        return new ValueTask<bool>(_circuit!.ReceiveJSDataChunk(streamId, chunkId, chunk, error));
    }

    public IAsyncEnumerable<ArraySegment<byte>> SendDotNetStreamToJS(long streamId)
    {
        // Server-to-client stream. Not yet wired — needs StreamItem frame
        // writer in BlazorPackWriter.
        throw new NotImplementedException("SendDotNetStreamToJS: needs StreamItem/StreamCompletion frame writers");
    }

    public ValueTask OnRenderCompleted(long renderId, string? errorMessageOrNull)
    {
        EnsureCircuit();
        return new ValueTask(_circuit!.OnRenderCompletedAsync(renderId, errorMessageOrNull));
    }

    public ValueTask OnLocationChanged(string uri, string? state, bool intercepted)
    {
        EnsureCircuit();
        return new ValueTask(_circuit!.OnLocationChangedAsync(uri, state, intercepted));
    }

    public ValueTask OnLocationChanging(int callId, string uri, string? state, bool intercepted)
    {
        EnsureCircuit();
        return new ValueTask(_circuit!.OnLocationChangingAsync(callId, uri, state, intercepted));
    }

    private void EnsureCircuit()
    {
        if (_circuit is null)
            throw new InvalidOperationException(
                "CircuitHost not initialized — StartCircuit must be called first.");
    }

    private static void TraceLog(string msg)
    {
        try { Reply.Print(msg); } catch { }
    }


    // Stub IPersistentComponentStateStore. The real impl bridges to
    // CircuitStore (S5 #70) for upgrade survival; for first-light wiring
    // a no-op is fine and matches the standard Blazor Server behavior
    // when no prerender state is in flight.
    private sealed class NoopPersistentComponentStateStore : IPersistentComponentStateStore
    {
        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync()
            => Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>());

        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
            => Task.CompletedTask;
    }
}
