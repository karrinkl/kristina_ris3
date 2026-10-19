using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class ConcurrencyLimitTests
{
    [Fact]
    public async Task ProcessWithLimit_NeverExceedsLimit()
    {
        var payment = new ControlledPaymentService();
        var shop = new TestShop(payment);
        var orders = shop.NewOrders(10);

        var batchTask = shop.Batch.ProcessWithLimitAsync(orders, 3, CancellationToken.None);
        await payment.WaitForEntriesAsync(3);

        Assert.Equal(3, payment.Current);
        Assert.Equal(3, orders.Count(o => o.Status == OrderStatus.Processing));
        Assert.Equal(7, orders.Count(o => o.Status == OrderStatus.Pending));

        payment.ReleaseAll();
        var batch = await batchTask.WithTimeout();

        Assert.Equal(10, batch.Completed);
        Assert.Equal(3, payment.MaxConcurrent);
        Assert.Equal(3, batch.MaxObservedConcurrency);
    }

    [Fact]
    public async Task ProcessParallel_WithoutLimit_RunsAllConcurrently()
    {
        var payment = new ControlledPaymentService();
        var shop = new TestShop(payment);
        var orders = shop.NewOrders(6);

        var batchTask = shop.Batch.ProcessParallelAsync(orders);
        await payment.WaitForEntriesAsync(6);
        payment.ReleaseAll();
        var batch = await batchTask.WithTimeout();

        Assert.Equal(6, payment.MaxConcurrent);
        Assert.Equal(6, batch.Completed);
    }

    [Fact]
    public async Task Limiter_DoesNotLeakPermits_OnExceptionAndCancellation()
    {
        using var limiter = new ConcurrencyLimiter(2);
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var failing = limiter.RunAsync<int>(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("сбой");
        }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing).WithTimeout();
        Assert.Equal(2, limiter.AvailableSlots);

        var blocked1 = limiter.RunAsync(async token => { await gate.Task.WaitAsync(token); return 1; }, cts.Token);
        var blocked2 = limiter.RunAsync(async token => { await gate.Task.WaitAsync(token); return 2; }, cts.Token);
        var waiting = limiter.RunAsync(token => Task.FromResult(3), cts.Token); // ждёт слота
        Assert.Equal(0, limiter.AvailableSlots);
        Assert.False(waiting.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked1).WithTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked2).WithTimeout();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).WithTimeout();

        Assert.Equal(2, limiter.AvailableSlots);
    }

    [Fact]
    public void Limiter_RejectsInvalidLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencyLimiter(0));
}
