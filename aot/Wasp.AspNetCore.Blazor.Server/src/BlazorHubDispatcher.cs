using System;
using System.Threading.Tasks;

namespace Wasp.AspNetCore.Blazor.Server;

// Compile-time replacement for SignalR's reflective HubMethodDescriptor
// dispatch.
//
// Given an IBlazorHubFacade and a single inbound IcCircuitInboundMessage,
// `Dispatch` switches on the target name, unboxes positional args from
// `object?[]`, and invokes the matching facade method. Methods that return
// a value (StartCircuit, ConnectCircuit, ResumeCircuit, PauseCircuit,
// ReceiveJSDataChunk) ask the caller's `completionSink` for an outbound
// path: the dispatcher serializes a SignalR Completion frame via
// BlazorPackWriter.WriteCompletion and hands it off to the sink.
//
// This is intentionally hand-coded rather than Roslyn-generated: the 13
// ComponentHub method names + signatures are fixed by Blazor 10.x and
// don't vary per consumer. If we later expose user-defined hub methods,
// promoting this to a generator stays straightforward — the emitted shape
// is exactly the body of this switch.
//
// Wire-up sketch (S5 once Cecil-weaver lands):
//
//     registry.TransportConnected += transport =>
//     {
//         var hub = CircuitFactoryAccessor.CreateHubFacade(transport, services);
//         transport.InboundMessage += async msg =>
//         {
//             await BlazorHubDispatcher.Dispatch(hub, msg, transport);
//         };
//     };
public static class BlazorHubDispatcher
{
    /// <summary>
    /// Route one decoded inbound message into the facade. For Hub methods
    /// that return a value, write a Completion frame back through
    /// <paramref name="completionSink"/>.
    /// </summary>
    /// <param name="hub">User-implemented facade backed by a real CircuitHost.</param>
    /// <param name="message">Decoded BlazorPack Invocation (target + args).</param>
    /// <param name="completionSink">
    /// Invoked once per return-value method with the bytes of the Completion
    /// frame. In production this is bound to <c>IIcCircuitTransport</c>'s
    /// internal byte-sink (the same one SendCoreAsync uses). Null is allowed
    /// for fire-and-forget tests but throws if a return-value method fires.
    /// </param>
    /// <param name="invocationId">
    /// SignalR Completion frames must echo the inbound invocation id. The
    /// transport surfaces this via a separate side channel — for now the
    /// caller passes it in directly. (Most outbound CircuitHost-initiated
    /// invocations don't carry one — they're nil — so the corresponding
    /// inbound methods are fire-and-forget; only the 5 return-value methods
    /// require this.)
    /// </param>
    public static async ValueTask Dispatch(
        IBlazorHubFacade hub,
        IcCircuitInboundMessage message,
        Action<byte[]>? completionSink = null)
    {
        if (hub is null) throw new ArgumentNullException(nameof(hub));

        var args = message.Args;
        var invocationId = message.InvocationId;
        try
        {
            await DispatchCore(hub, args, invocationId, message.Target, completionSink);
        }
        catch (Exception ex)
        {
            // Surface failures back to the client as an error Completion so
            // it isn't left hanging on `await connection.invoke(...)`. Only
            // possible if the inbound message carries an invocationId.
            if (invocationId is not null && completionSink is not null)
            {
                completionSink(BlazorPackWriter.WriteCompletionError(
                    invocationId, $"{ex.GetType().Name}: {ex.Message}"));
            }
            // Bubble for visibility in canister logs.
            throw;
        }
    }

    private static async ValueTask DispatchCore(
        IBlazorHubFacade hub,
        object?[] args,
        string? invocationId,
        string target,
        Action<byte[]>? completionSink)
    {
        var message = new IcCircuitInboundMessage(target, args, invocationId);
        switch (message.Target)
        {
            case "StartCircuit":
            {
                EnsureArgs(args, 4, message.Target);
                string result = await hub.StartCircuit(
                    Str(args[0]), Str(args[1]), Str(args[2]), Str(args[3]));
                EmitCompletion(completionSink, invocationId, result, message.Target);
                break;
            }
            case "UpdateRootComponents":
            {
                EnsureArgs(args, 2, message.Target);
                await hub.UpdateRootComponents(Str(args[0]), Str(args[1]));
                break;
            }
            case "ConnectCircuit":
            {
                EnsureArgs(args, 1, message.Target);
                bool ok = await hub.ConnectCircuit(Str(args[0]));
                EmitCompletion(completionSink, invocationId, ok, message.Target);
                break;
            }
            case "ResumeCircuit":
            {
                EnsureArgs(args, 5, message.Target);
                string result = await hub.ResumeCircuit(
                    Str(args[0]), Str(args[1]), Str(args[2]), Str(args[3]), Str(args[4]));
                EmitCompletion(completionSink, invocationId, result, message.Target);
                break;
            }
            case "PauseCircuit":
            {
                EnsureArgs(args, 0, message.Target);
                bool ok = await hub.PauseCircuit();
                EmitCompletion(completionSink, invocationId, ok, message.Target);
                break;
            }
            case "BeginInvokeDotNetFromJS":
            {
                EnsureArgs(args, 5, message.Target);
                await hub.BeginInvokeDotNetFromJS(
                    Str(args[0]), Str(args[1]), Str(args[2]), Long(args[3]), Str(args[4]));
                break;
            }
            case "EndInvokeJSFromDotNet":
            {
                EnsureArgs(args, 3, message.Target);
                await hub.EndInvokeJSFromDotNet(Long(args[0]), Bool(args[1]), Str(args[2]));
                break;
            }
            case "ReceiveByteArray":
            {
                EnsureArgs(args, 2, message.Target);
                await hub.ReceiveByteArray((int)Long(args[0]), Bytes(args[1]));
                break;
            }
            case "ReceiveJSDataChunk":
            {
                EnsureArgs(args, 4, message.Target);
                bool ok = await hub.ReceiveJSDataChunk(
                    Long(args[0]), Long(args[1]), Bytes(args[2]), Str(args[3]));
                EmitCompletion(completionSink, invocationId, ok, message.Target);
                break;
            }
            case "SendDotNetStreamToJS":
            {
                // SignalR server-stream: each item ships as a separate
                // StreamItem frame. Not yet supported — surface explicitly.
                throw new NotSupportedException(
                    "SendDotNetStreamToJS (SignalR server-stream) is not yet implemented " +
                    "by BlazorHubDispatcher. Tracked in M4.S3 follow-up; needs StreamItem " +
                    "frame writer in BlazorPackWriter.");
            }
            case "OnRenderCompleted":
            {
                EnsureArgs(args, 2, message.Target);
                await hub.OnRenderCompleted(Long(args[0]), NullableStr(args[1]));
                break;
            }
            case "OnLocationChanged":
            {
                EnsureArgs(args, 3, message.Target);
                await hub.OnLocationChanged(Str(args[0]), NullableStr(args[1]), Bool(args[2]));
                break;
            }
            case "OnLocationChanging":
            {
                EnsureArgs(args, 4, message.Target);
                await hub.OnLocationChanging(
                    (int)Long(args[0]), Str(args[1]), NullableStr(args[2]), Bool(args[3]));
                break;
            }
            default:
                throw new NotSupportedException(
                    $"Unknown ComponentHub method '{message.Target}'. Allowed names: " +
                    "StartCircuit, UpdateRootComponents, ConnectCircuit, ResumeCircuit, " +
                    "PauseCircuit, BeginInvokeDotNetFromJS, EndInvokeJSFromDotNet, " +
                    "ReceiveByteArray, ReceiveJSDataChunk, SendDotNetStreamToJS, " +
                    "OnRenderCompleted, OnLocationChanged, OnLocationChanging.");
        }
    }

    // ─── Arg unboxing helpers ────────────────────────────────────────────
    // BlazorPackReader produces: long for ints, string for strs, byte[] for
    // bins, bool for bools, null for nil. These coerce + nil-check.

    private static void EnsureArgs(object?[] args, int expected, string target)
    {
        if (args.Length != expected)
            throw new ArgumentException(
                $"{target} expected {expected} args, got {args.Length}");
    }

    private static string Str(object? v) => v switch
    {
        string s => s,
        null => throw new ArgumentNullException(nameof(v), "expected non-null string"),
        _ => throw new InvalidCastException($"expected string, got {v.GetType().Name}"),
    };

    private static string? NullableStr(object? v) => v switch
    {
        string s => s,
        null => null,
        _ => throw new InvalidCastException($"expected string or null, got {v.GetType().Name}"),
    };

    private static long Long(object? v) => v switch
    {
        long l => l,
        int i => i,
        null => throw new ArgumentNullException(nameof(v), "expected non-null integer"),
        _ => throw new InvalidCastException($"expected integer, got {v.GetType().Name}"),
    };

    private static bool Bool(object? v) => v switch
    {
        bool b => b,
        null => throw new ArgumentNullException(nameof(v), "expected non-null bool"),
        _ => throw new InvalidCastException($"expected bool, got {v.GetType().Name}"),
    };

    private static byte[] Bytes(object? v) => v switch
    {
        byte[] arr => arr,
        null => Array.Empty<byte>(),
        _ => throw new InvalidCastException($"expected byte[], got {v.GetType().Name}"),
    };

    private static void EmitCompletion(
        Action<byte[]>? sink, string? invocationId, object result, string target)
    {
        if (sink is null)
            throw new InvalidOperationException(
                $"{target} returns a value (needs Completion frame) but no completionSink was provided.");
        if (invocationId is null)
            throw new InvalidOperationException(
                $"{target} returns a value but the inbound message had no invocationId.");
        sink(BlazorPackWriter.WriteCompletion(invocationId, result));
    }
}
