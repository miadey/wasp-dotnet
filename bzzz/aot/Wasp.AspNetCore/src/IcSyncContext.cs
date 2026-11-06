using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wasp.AspNetCore;

/// <summary>
/// A single-threaded <see cref="SynchronizationContext"/> that pumps async continuations
/// to completion within one canister message. If the queue empties before the awaited task
/// completes, it means middleware has blocked on real I/O (network, timer, thread-pool),
/// which is forbidden inside an ICP canister message. In that case the context throws
/// <see cref="InvalidOperationException"/> so the IServer dispatcher can surface it as a
/// trap or a 500 response.
/// </summary>
internal sealed class IcSyncContext : SynchronizationContext
{
    internal static readonly string BlockedMessage =
        "Wasp.AspNetCore: non-deterministic await detected — middleware blocked on real I/O. " +
        "Mid-pipeline I/O is forbidden inside a canister message; " +
        "use IcOutcallsHttpMessageHandler from terminal endpoints only.";

    // Single-threaded: plain Queue is fine. All access happens on the one canister thread.
    private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

    // The thread that called RunUntilComplete — used to detect cross-thread Send attempts.
    private int _ownerThreadId;

    // -------------------------------------------------------------------------
    // SynchronizationContext overrides
    // -------------------------------------------------------------------------

    public override void Post(SendOrPostCallback d, object? state)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        _queue.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));

        // If we're on the owner thread, execute inline (avoids deadlock / re-entrancy issue).
        if (Thread.CurrentThread.ManagedThreadId == _ownerThreadId)
        {
            d(state);
            return;
        }

        // Cross-thread Send is impossible in a single-threaded canister environment.
        throw new InvalidOperationException(
            "Wasp.AspNetCore: SynchronizationContext.Send called from a thread other than the " +
            "canister message thread. Cross-thread synchronous calls are not supported.");
    }

    /// <summary>
    /// Returns <c>this</c> because the context is intentionally shared across the request
    /// pipeline; creating a fresh copy would lose the queue reference.
    /// </summary>
    public override SynchronizationContext CreateCopy() => this;

    // -------------------------------------------------------------------------
    // Static entry points
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sets this instance as the current <see cref="SynchronizationContext"/>, invokes
    /// <paramref name="taskFactory"/> (so async continuations are captured on our context),
    /// then drains the queue until the returned task completes. Throws
    /// <see cref="InvalidOperationException"/> if the queue empties before the task finishes
    /// (indicates the middleware blocked on real I/O — forbidden in a canister message).
    /// </summary>
    /// <remarks>
    /// The factory overload is the primary entry point: by invoking the factory AFTER
    /// installing the context, we guarantee that every <c>await</c> inside the pipeline
    /// posts its continuation back to this context rather than to a thread-pool or to
    /// the test runner's own SynchronizationContext.
    /// </remarks>
    public static void RunUntilComplete(Func<Task> taskFactory)
    {
        if (taskFactory is null) throw new ArgumentNullException(nameof(taskFactory));

        var prevCtx = Current;
        var ctx = new IcSyncContext();
        ctx._ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        SetSynchronizationContext(ctx);

        try
        {
            // Start the work AFTER the context is installed so the first yield
            // posts back to us rather than to the caller's context.
            var task = taskFactory();
            Drain(ctx, task);
        }
        finally
        {
            SetSynchronizationContext(prevCtx);
        }
    }

    /// <summary>
    /// Variant of <see cref="RunUntilComplete(Func{Task})"/> that returns the task result.
    /// </summary>
    public static T RunUntilComplete<T>(Func<Task<T>> taskFactory)
    {
        if (taskFactory is null) throw new ArgumentNullException(nameof(taskFactory));

        var prevCtx = Current;
        var ctx = new IcSyncContext();
        ctx._ownerThreadId = Thread.CurrentThread.ManagedThreadId;
        SetSynchronizationContext(ctx);

        try
        {
            var task = taskFactory();
            Drain(ctx, task);
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(prevCtx);
        }
    }

    /// <summary>
    /// Convenience overload for already-completed tasks (e.g. <c>Task.FromResult(x)</c>).
    /// Sets the context and drains; no continuations will be posted for an already-completed task.
    /// </summary>
    public static void RunUntilComplete(Task task)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        RunUntilComplete(() => task);
    }

    /// <summary>
    /// Convenience overload for already-completed tasks (e.g. <c>Task.FromResult(x)</c>).
    /// </summary>
    public static T RunUntilComplete<T>(Task<T> task)
    {
        if (task is null) throw new ArgumentNullException(nameof(task));
        return RunUntilComplete(() => task);
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private static void Drain(IcSyncContext ctx, Task task)
    {
        while (!task.IsCompleted)
        {
            if (!ctx._queue.TryDequeue(out var item))
            {
                throw new InvalidOperationException(BlockedMessage);
            }

            item.Callback(item.State);
        }

        // Surface any exception stored in the task without AggregateException wrapping.
        task.GetAwaiter().GetResult();
    }
}
