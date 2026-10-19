using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class StatisticsConsistencyTests
{
    [Fact]
    public async Task MixedBatch_StatisticsMatchActualOrderStatuses()
    {
        // Оплата заказа «Сидоров П.» (№3) зависает, его отменяем; остальные проходят свои сценарии.
        var payment = new ControlledPaymentService(order => order.Id == 3);
        var shop = new TestShop(payment);
        var id = 0;
        var orders = DemoOrderFactory.CreateScenarioOrders(() => ++id)
            .Where(o => o.PaymentMethod == PaymentMethods.Card) // контролируемая подмена оплаты для карт
            .ToArray();
        shop.Register(orders);
        using var cts = new CancellationTokenSource();

        var batchTask = shop.Batch.ProcessParallelAsync(orders, cts.Token);
        // Ждём, пока все дошедшие до оплаты вошли в неё (1, 3, 8, 11, 12), затем отменяем.
        await payment.WaitForEntriesAsync(5);
        cts.Cancel();
        var batch = await batchTask.WithTimeout();

        var counts = shop.Repository.CountByStatus();
        Assert.Equal(counts[OrderStatus.Completed], batch.Completed);
        Assert.Equal(counts[OrderStatus.Failed], batch.Failed);
        Assert.Equal(counts[OrderStatus.Cancelled], batch.Cancelled);
        Assert.Equal(0, counts[OrderStatus.Processing]);

        Assert.Equal(counts[OrderStatus.Completed], shop.Statistics.Completed);
        Assert.Equal(counts[OrderStatus.Failed], shop.Statistics.Failed);
        Assert.Equal(counts[OrderStatus.Cancelled], shop.Statistics.Cancelled);
        Assert.Equal(shop.Statistics.Started, shop.Statistics.Completed + shop.Statistics.Failed + shop.Statistics.Cancelled);
        Assert.Equal(0, shop.Statistics.InFlight);

        Assert.Equal(3, counts[OrderStatus.Completed]);  // 1, 11, 12
        Assert.Equal(1, counts[OrderStatus.Cancelled]);  // 3
        Assert.Equal(OrderStatus.Cancelled, orders.Single(o => o.Id == 3).Status);
        Assert.Equal(payment.Charges, counts[OrderStatus.Completed] + 1); // + №8: оплачен, но уведомление не прошло
    }

    [Fact]
    public async Task RetryAfterMixedBatch_KeepsStatisticsConsistent()
    {
        var shop = new TestShop();
        var id = 0;
        var orders = DemoOrderFactory.CreateScenarioOrders(() => ++id);
        shop.Register(orders);

        await shop.Batch.ProcessWithLimitAsync(orders, 3).WithTimeout();
        var retry = orders.Where(o => o.TryResetForRetry(out _)).ToArray();
        await shop.Batch.ProcessWithLimitAsync(retry, 3).WithTimeout();

        Assert.Equal(new[] { 7, 8 }, retry.Select(o => o.Id));
        var counts = shop.Repository.CountByStatus();
        Assert.Equal(7, counts[OrderStatus.Completed]);
        Assert.Equal(5, counts[OrderStatus.Failed]);
        Assert.Equal(counts[OrderStatus.Completed], shop.Statistics.Completed);
        Assert.Equal(14, shop.Statistics.Started); // 12 + 2 повтора
        Assert.Equal(7, shop.RealPayment.SuccessfulCharges);
    }
}
