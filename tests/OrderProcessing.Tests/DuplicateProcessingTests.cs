using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class DuplicateProcessingTests
{
    [Fact]
    public void TryBeginProcessing_FromManyThreads_SucceedsExactlyOnce()
    {
        var order = new Order(1, "Тест", "t@example.test", [new OrderItem("KB-01", "Клавиатура", 1, 3_500m)]);
        var winners = 0;

        Parallel.For(0, 1_000, _ =>
        {
            if (order.TryBeginProcessing())
            {
                Interlocked.Increment(ref winners);
            }
        });

        Assert.Equal(1, winners);
        Assert.Equal(1, order.Attempts);
    }

    [Fact]
    public async Task SameOrderInBatchSeveralTimes_IsProcessedOnce()
    {
        var payment = new ControlledPaymentService();
        var shop = new TestShop(payment);
        var order = shop.NewOrder();

        var batchTask = shop.Batch.ProcessParallelAsync(Enumerable.Repeat(order, 5));
        await payment.WaitForEntriesAsync(1);
        payment.ReleaseAll();
        var batch = await batchTask.WithTimeout();

        Assert.Equal(1, batch.Completed);
        Assert.Equal(4, batch.Skipped);
        Assert.Equal(1, payment.Charges);
        Assert.Equal(1, payment.MaxConcurrent);
        Assert.Equal(1, order.Attempts);
        Assert.Equal(4, shop.Statistics.Skipped);
    }

    [Fact]
    public async Task ConcurrentProcessCalls_OnSameOrder_ChargeOnlyOnce()
    {
        var payment = new ControlledPaymentService();
        var shop = new TestShop(payment);
        var order = shop.NewOrder();

        var calls = Enumerable.Range(0, 8).Select(_ => Task.Run(() => shop.Processor.ProcessAsync(order))).ToArray();
        await payment.WaitForEntriesAsync(1);
        payment.ReleaseAll();
        var results = await Task.WhenAll(calls).WithTimeout();

        Assert.Single(results, r => r.Outcome == ProcessingOutcome.Completed);
        Assert.Equal(7, results.Count(r => r.Outcome == ProcessingOutcome.Skipped));
        Assert.Equal(1, payment.Charges);
    }
}
