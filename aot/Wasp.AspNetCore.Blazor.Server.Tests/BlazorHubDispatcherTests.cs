using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wasp.AspNetCore.Blazor.Server;
using Xunit;

namespace Wasp.AspNetCore.Blazor.Server.Tests;

// Exercises every branch of BlazorHubDispatcher.Dispatch using a recording
// fake of IBlazorHubFacade. Each test feeds an IcCircuitInboundMessage as
// it would come off the wire (Args shapes match BlazorPackReader output:
// long for ints, string for strs, byte[] for bins, bool for bools).
public sealed class BlazorHubDispatcherTests
{
    [Fact]
    public async Task Dispatches_StartCircuit_and_emits_Completion_with_returned_secret()
    {
        var fake = new RecordingFacade
        {
            StartCircuitImpl = (_, _, _, _) => new ValueTask<string>("secret-123"),
        };
        byte[]? completionBytes = null;
        var msg = new IcCircuitInboundMessage(
            "StartCircuit",
            new object?[] { "https://x", "https://x/", "rec", "state" },
            "inv-1");

        await BlazorHubDispatcher.Dispatch(fake, msg, b => completionBytes = b);

        Assert.Single(fake.Calls);
        Assert.Equal("StartCircuit", fake.Calls[0].method);
        Assert.NotNull(completionBytes);

        var decoded = BlazorPackReader.ReadCompletion(completionBytes!);
        Assert.Equal("inv-1", decoded.InvocationId);
        Assert.True(decoded.HasResult);
        Assert.Equal("secret-123", decoded.Result);
    }

    [Fact]
    public async Task Dispatches_UpdateRootComponents_fire_and_forget()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage(
            "UpdateRootComponents", new object?[] { "ops", "state" }, null);
        await BlazorHubDispatcher.Dispatch(fake, msg);
        Assert.Single(fake.Calls);
        Assert.Equal("UpdateRootComponents", fake.Calls[0].method);
        Assert.Equal(new object?[] { "ops", "state" }, fake.Calls[0].args);
    }

    [Fact]
    public async Task Dispatches_ConnectCircuit_with_bool_Completion()
    {
        var fake = new RecordingFacade { ConnectCircuitImpl = _ => new ValueTask<bool>(true) };
        byte[]? completionBytes = null;
        var msg = new IcCircuitInboundMessage("ConnectCircuit", new object?[] { "secret" }, "inv-2");

        await BlazorHubDispatcher.Dispatch(fake, msg, b => completionBytes = b);

        var decoded = BlazorPackReader.ReadCompletion(completionBytes!);
        Assert.Equal(true, decoded.Result);
    }

    [Fact]
    public async Task Dispatches_BeginInvokeDotNetFromJS_with_long_arg_unboxed()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage(
            "BeginInvokeDotNetFromJS",
            new object?[] { "cid", "asm", "method", 99L, "[]" },
            null);

        await BlazorHubDispatcher.Dispatch(fake, msg);

        Assert.Single(fake.Calls);
        Assert.Equal(99L, fake.Calls[0].args[3]);
    }

    [Fact]
    public async Task Dispatches_EndInvokeJSFromDotNet_unboxes_long_and_bool()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage(
            "EndInvokeJSFromDotNet", new object?[] { 7L, true, "[\"ok\"]" }, null);
        await BlazorHubDispatcher.Dispatch(fake, msg);
        Assert.Equal(7L, fake.Calls[0].args[0]);
        Assert.Equal(true, fake.Calls[0].args[1]);
    }

    [Fact]
    public async Task Dispatches_ReceiveByteArray_with_byte_array_arg()
    {
        var fake = new RecordingFacade();
        var data = new byte[] { 1, 2, 3, 4, 5 };
        // BlazorPackReader emits ReceiveByteArray's id as long.
        var msg = new IcCircuitInboundMessage("ReceiveByteArray", new object?[] { 4L, data }, null);

        await BlazorHubDispatcher.Dispatch(fake, msg);

        Assert.Equal(4L, fake.Calls[0].args[0]);
        Assert.Same(data, fake.Calls[0].args[1]);
    }

    [Fact]
    public async Task Dispatches_OnRenderCompleted_with_nullable_error_message()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage("OnRenderCompleted", new object?[] { 42L, null }, null);
        await BlazorHubDispatcher.Dispatch(fake, msg);
        Assert.Equal(42L, fake.Calls[0].args[0]);
        Assert.Null(fake.Calls[0].args[1]);
    }

    [Fact]
    public async Task Dispatches_OnLocationChanged_with_nullable_state()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage(
            "OnLocationChanged", new object?[] { "/x", "{\"s\":1}", true }, null);
        await BlazorHubDispatcher.Dispatch(fake, msg);
        Assert.Equal("/x", fake.Calls[0].args[0]);
        Assert.Equal("{\"s\":1}", fake.Calls[0].args[1]);
        Assert.Equal(true, fake.Calls[0].args[2]);
    }

    [Fact]
    public async Task Dispatches_OnLocationChanging_with_int_callId()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage(
            "OnLocationChanging", new object?[] { 11L, "/y", null, false }, null);
        await BlazorHubDispatcher.Dispatch(fake, msg);
        Assert.Equal(11L, fake.Calls[0].args[0]);
        Assert.Null(fake.Calls[0].args[2]);
    }

    [Fact]
    public async Task Dispatches_PauseCircuit_no_args_writes_bool_Completion()
    {
        var fake = new RecordingFacade { PauseCircuitImpl = () => new ValueTask<bool>(false) };
        byte[]? completionBytes = null;
        var msg = new IcCircuitInboundMessage("PauseCircuit", Array.Empty<object?>(), "inv-3");
        await BlazorHubDispatcher.Dispatch(fake, msg, b => completionBytes = b);
        var decoded = BlazorPackReader.ReadCompletion(completionBytes!);
        Assert.Equal(false, decoded.Result);
    }

    [Fact]
    public async Task Dispatches_ResumeCircuit_string_Completion()
    {
        var fake = new RecordingFacade
        {
            ResumeCircuitImpl = (_, _, _, _, _) => new ValueTask<string>("resumed-secret"),
        };
        byte[]? completionBytes = null;
        var msg = new IcCircuitInboundMessage(
            "ResumeCircuit",
            new object?[] { "secret", "https://x", "https://x/a", "components", "state" },
            "inv-4");

        await BlazorHubDispatcher.Dispatch(fake, msg, b => completionBytes = b);

        var decoded = BlazorPackReader.ReadCompletion(completionBytes!);
        Assert.Equal("resumed-secret", decoded.Result);
    }

    [Fact]
    public async Task Dispatches_ReceiveJSDataChunk_bytes_arg_and_bool_Completion()
    {
        var fake = new RecordingFacade
        {
            ReceiveJSDataChunkImpl = (_, _, _, _) => new ValueTask<bool>(true),
        };
        byte[]? completionBytes = null;
        var msg = new IcCircuitInboundMessage(
            "ReceiveJSDataChunk",
            new object?[] { 5L, 1L, new byte[] { 0xDE, 0xAD }, "" },
            "inv-5");

        await BlazorHubDispatcher.Dispatch(fake, msg, b => completionBytes = b);

        var decoded = BlazorPackReader.ReadCompletion(completionBytes!);
        Assert.Equal(true, decoded.Result);
    }

    [Fact]
    public async Task SendDotNetStreamToJS_explicitly_not_supported()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage("SendDotNetStreamToJS", new object?[] { 1L }, "inv-6");
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await BlazorHubDispatcher.Dispatch(fake, msg, _ => { }));
    }

    [Fact]
    public async Task Unknown_target_throws_NotSupported()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage("MadeUpHubMethod", Array.Empty<object?>(), null);
        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await BlazorHubDispatcher.Dispatch(fake, msg));
    }

    [Fact]
    public async Task ReturnValue_method_without_invocationId_throws()
    {
        var fake = new RecordingFacade();
        // ConnectCircuit needs a Completion frame; missing invocationId is a
        // protocol violation we should surface, not silently swallow.
        var msg = new IcCircuitInboundMessage("ConnectCircuit", new object?[] { "x" }, null);
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await BlazorHubDispatcher.Dispatch(fake, msg, _ => { }));
    }

    [Fact]
    public async Task ReturnValue_method_without_completionSink_throws()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage("ConnectCircuit", new object?[] { "x" }, "inv-7");
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await BlazorHubDispatcher.Dispatch(fake, msg));
    }

    [Fact]
    public async Task Wrong_arg_count_throws_ArgumentException()
    {
        var fake = new RecordingFacade();
        var msg = new IcCircuitInboundMessage(
            "OnLocationChanged", new object?[] { "/only-one" }, null);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await BlazorHubDispatcher.Dispatch(fake, msg));
    }

    // ─── Test double ─────────────────────────────────────────────────────
    private sealed class RecordingFacade : IBlazorHubFacade
    {
        public List<(string method, object?[] args)> Calls { get; } = new();

        public Func<string, string, string, string, ValueTask<string>>? StartCircuitImpl;
        public Func<string, ValueTask<bool>>? ConnectCircuitImpl;
        public Func<string, string, string, string, string, ValueTask<string>>? ResumeCircuitImpl;
        public Func<ValueTask<bool>>? PauseCircuitImpl;
        public Func<long, long, byte[], string, ValueTask<bool>>? ReceiveJSDataChunkImpl;

        public ValueTask<string> StartCircuit(string a, string b, string c, string d)
        {
            Calls.Add(("StartCircuit", new object?[] { a, b, c, d }));
            return StartCircuitImpl?.Invoke(a, b, c, d) ?? new ValueTask<string>("");
        }

        public Task UpdateRootComponents(string a, string b)
        {
            Calls.Add(("UpdateRootComponents", new object?[] { a, b }));
            return Task.CompletedTask;
        }

        public ValueTask<bool> ConnectCircuit(string a)
        {
            Calls.Add(("ConnectCircuit", new object?[] { a }));
            return ConnectCircuitImpl?.Invoke(a) ?? new ValueTask<bool>(false);
        }

        public ValueTask<string> ResumeCircuit(string a, string b, string c, string d, string e)
        {
            Calls.Add(("ResumeCircuit", new object?[] { a, b, c, d, e }));
            return ResumeCircuitImpl?.Invoke(a, b, c, d, e) ?? new ValueTask<string>("");
        }

        public ValueTask<bool> PauseCircuit()
        {
            Calls.Add(("PauseCircuit", Array.Empty<object?>()));
            return PauseCircuitImpl?.Invoke() ?? new ValueTask<bool>(false);
        }

        public ValueTask BeginInvokeDotNetFromJS(string a, string b, string c, long d, string e)
        {
            Calls.Add(("BeginInvokeDotNetFromJS", new object?[] { a, b, c, d, e }));
            return ValueTask.CompletedTask;
        }

        public ValueTask EndInvokeJSFromDotNet(long a, bool b, string c)
        {
            Calls.Add(("EndInvokeJSFromDotNet", new object?[] { a, b, c }));
            return ValueTask.CompletedTask;
        }

        public ValueTask ReceiveByteArray(int a, byte[] b)
        {
            Calls.Add(("ReceiveByteArray", new object?[] { (long)a, b }));
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> ReceiveJSDataChunk(long a, long b, byte[] c, string d)
        {
            Calls.Add(("ReceiveJSDataChunk", new object?[] { a, b, c, d }));
            return ReceiveJSDataChunkImpl?.Invoke(a, b, c, d) ?? new ValueTask<bool>(false);
        }

        public async IAsyncEnumerable<ArraySegment<byte>> SendDotNetStreamToJS(long a)
        {
            Calls.Add(("SendDotNetStreamToJS", new object?[] { a }));
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask OnRenderCompleted(long a, string? b)
        {
            Calls.Add(("OnRenderCompleted", new object?[] { a, b }));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnLocationChanged(string a, string? b, bool c)
        {
            Calls.Add(("OnLocationChanged", new object?[] { a, b, c }));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnLocationChanging(int a, string b, string? c, bool d)
        {
            Calls.Add(("OnLocationChanging", new object?[] { (long)a, b, c, d }));
            return ValueTask.CompletedTask;
        }
    }
}
