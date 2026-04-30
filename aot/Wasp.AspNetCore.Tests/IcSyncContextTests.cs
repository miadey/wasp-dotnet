using System;
using System.Threading.Tasks;
using Wasp.AspNetCore;
using Xunit;

namespace Wasp.AspNetCore.Tests;

public class IcSyncContextTests
{
    // -----------------------------------------------------------------------
    // 1. Sync-completing await passes
    // -----------------------------------------------------------------------
    [Fact]
    public void SyncCompletedTask_Passes()
    {
        static async Task Run() => await Task.CompletedTask;
        IcSyncContext.RunUntilComplete((Func<Task>)Run); // must not throw
    }

    // -----------------------------------------------------------------------
    // 2. Task.Yield works — posts one continuation back to the SyncCtx
    // -----------------------------------------------------------------------
    [Fact]
    public void TaskYield_Completes()
    {
        static async Task Run() => await Task.Yield();
        IcSyncContext.RunUntilComplete((Func<Task>)Run); // must not throw
    }

    // -----------------------------------------------------------------------
    // 3. Multiple continuations work — three Yields drain correctly
    // -----------------------------------------------------------------------
    [Fact]
    public void MultipleYields_Complete()
    {
        static async Task Run()
        {
            await Task.Yield();
            await Task.Yield();
            await Task.Yield();
        }
        IcSyncContext.RunUntilComplete((Func<Task>)Run); // must not throw
    }

    // -----------------------------------------------------------------------
    // 4. Task.Delay traps with the expected error message
    // -----------------------------------------------------------------------
    [Fact]
    public void TaskDelay_Traps()
    {
        static async Task Run() => await Task.Delay(100);
        var ex = Assert.Throws<InvalidOperationException>(
            () => IcSyncContext.RunUntilComplete((Func<Task>)Run));
        Assert.Contains("non-deterministic await", ex.Message);
        Assert.Contains("real I/O", ex.Message);
    }

    // -----------------------------------------------------------------------
    // 5. Task.Run with blocking work traps — the thread-pool work takes time,
    //    so on the first drain iteration the queue is empty (Post hasn't been
    //    called yet) and the context detects the non-deterministic await.
    // -----------------------------------------------------------------------
    [Fact]
    public void TaskRun_WithBlockingWork_Traps()
    {
        static async Task Run()
        {
            // Thread.Sleep inside Task.Run means the pool thread holds the result
            // for 500 ms. Our synchronous drain loop finds an empty queue immediately
            // and must trap rather than block the canister thread.
            var result = await Task.Run(() =>
            {
                System.Threading.Thread.Sleep(500);
                return 42;
            });
            _ = result;
        }
        var ex = Assert.Throws<InvalidOperationException>(
            () => IcSyncContext.RunUntilComplete((Func<Task>)Run));
        Assert.Contains("non-deterministic await", ex.Message);
    }

    // -----------------------------------------------------------------------
    // 6. Result is returned correctly
    // -----------------------------------------------------------------------
    [Fact]
    public void GenericOverload_ReturnsResult()
    {
        var result = IcSyncContext.RunUntilComplete(Task.FromResult(42));
        Assert.Equal(42, result);
    }

    // -----------------------------------------------------------------------
    // 7. Exceptions propagate (not wrapped in AggregateException)
    // -----------------------------------------------------------------------
    [Fact]
    public void ExceptionAfterYield_Propagates()
    {
        static async Task Run()
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        }
        var ex = Assert.Throws<InvalidOperationException>(
            () => IcSyncContext.RunUntilComplete((Func<Task>)Run));
        Assert.Equal("boom", ex.Message);
    }
}
