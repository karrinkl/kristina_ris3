using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Core.Services;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class BatchAggregationTests
{
    private static (TestShop Shop, IReadOnlyList<Order> Orders) CreateScenario()
    {
        var shop = new TestShop();
        var id = 0;
        var orders = DemoOrderFactory.CreateScenarioOrders(() => ++id);
        shop.Register(orders);
        return (shop, orders);
    }

    [Fact]
    public async Task ParallelBatch_CollectsResultForEveryOrder_EvenWhenOneTaskFaults()
    {
        var (shop, orders) = CreateScenario();

        var batch = await shop.Batch.ProcessParallelAsync(orders).WithTimeout();

        Assert.Equal(orders.Count, batch.Results.Count);
        Assert.Equal(orders.Select(o => o.Id).Order(), batch.Results.Select(r => r.OrderId).Order());
        Assert.Equal(5, batch.Completed);   // 1, 2, 3, 11, 12
        Assert.Equal(7, batch.Failed);      // 4..10
        Assert.Equal(0, batch.Cancelled);
        var unexpected = Assert.Single(batch.UnexpectedErrors);
        Assert.IsType<InvalidOperationException>(unexpected);
        Assert.All(orders, o => Assert.NotEqual(OrderStatus.Processing, o.Status));
    }

    [Fact]
    public async Task SequentialAndParallel_ProduceSameOutcomes()
    {
        var (sequentialShop, sequentialOrders) = CreateScenario();
        var (parallelShop, parallelOrders) = CreateScenario();

        var sequential = await sequentialShop.Batch.ProcessSequentialAsync(sequentialOrders).WithTimeout();
        var parallel = await parallelShop.Batch.ProcessWithLimitAsync(parallelOrders, 4).WithTimeout();

        Assert.Equal(
            sequential.Results.OrderBy(r => r.OrderId).Select(r => (r.OrderId, r.Outcome, r.Stage)),
            parallel.Results.OrderBy(r => r.OrderId).Select(r => (r.OrderId, r.Outcome, r.Stage)));
        Assert.Equal(1, sequential.MaxObservedConcurrency);
    }

    [Fact]
    public async Task WhenAll_ExposesAllExceptions_ThroughTaskExceptionProperty()
    {
        var (shop, orders) = CreateScenario();
        var declined = orders.Single(o => o.PaymentMethod == PaymentMethods.DeclinedCard);

        var tasks = new[]
        {
            shop.RealPayment.ChargeAsync(declined, CancellationToken.None),
            Task.FromException<PaymentReceipt>(new InvalidOperationException("вторая ошибка")),
        };
        var all = Task.WhenAll(tasks);

        await Assert.ThrowsAsync<PaymentException>(() => all).WithTimeout(); // await — только первое
        Assert.Equal(2, all.Exception!.InnerExceptions.Count);               // а в задаче — все
    }

    [Fact]
    public void WaitAll_WrapsAllExceptions_InAggregateException()
    {
        var tasks = new[]
        {
            Task.Run(() => throw new InvalidOperationException("a")),
            Task.Run(() => throw new PaymentException("b", isRetryable: false)),
            Task.CompletedTask,
        };

        var aggregate = Assert.Throws<AggregateException>(() => Task.WaitAll(tasks, TestTimeouts.Default));

        Assert.Equal(2, aggregate.Flatten().InnerExceptions.Count);
        var remaining = Assert.Throws<AggregateException>(() => aggregate.Flatten().Handle(ex => ex is PaymentException));
        Assert.IsType<InvalidOperationException>(Assert.Single(remaining.InnerExceptions));
    }

    [Fact]
    public async Task WhenAny_ReturnsFirstCompletedTask_WithoutThrowing()
    {
        var slowGate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedFirst = Task.FromException<int>(new InvalidOperationException("ошибка"));

        var first = await Task.WhenAny(slowGate.Task, failedFirst).WithTimeout();

        Assert.Same(failedFirst, first);
        Assert.True(first.IsFaulted);
        slowGate.SetResult(42);
        Assert.Equal(42, await slowGate.Task);
    }
}
