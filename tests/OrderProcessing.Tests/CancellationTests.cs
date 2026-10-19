using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class CancellationTests
{
    [Fact]
    public async Task PreCancelledToken_OrderIsNotStarted()
    {
        var shop = new TestShop();
        var order = shop.NewOrder();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await shop.Processor.ProcessAsync(order, cts.Token).WithTimeout();

        Assert.Equal(ProcessingOutcome.Cancelled, result.Outcome);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(0, order.Attempts);
        Assert.Equal(0, shop.Statistics.Started);
    }

    [Fact]
    public async Task CancellationDuringPayment_CancelsOrder_AndReleasesStock()
    {
        var payment = new ControlledPaymentService();
        var shop = new TestShop(payment);
        var order = shop.NewOrder("MN-27", 2);
        var before = shop.Inventory.GetAvailable("MN-27");
        using var cts = new CancellationTokenSource();

        var processing = shop.Processor.ProcessAsync(order, cts.Token);
        await payment.WaitForEntriesAsync(1);          // заказ дошёл до оплаты и ждёт шлюз
        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Equal(before - 2, shop.Inventory.GetAvailable("MN-27"));

        cts.Cancel();
        var result = await processing.WithTimeout();

        Assert.Equal(ProcessingOutcome.Cancelled, result.Outcome);
        Assert.Equal(ProcessingStage.Payment, result.Stage);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Null(order.PaymentId);
        Assert.Equal(before, shop.Inventory.GetAvailable("MN-27"));
        Assert.Equal(0, payment.Charges);
        Assert.Equal(0, shop.Statistics.Completed);
        Assert.Equal(1, shop.Statistics.Cancelled);
    }

    [Fact]
    public async Task BatchCancellation_KeepsCompletedOrders_AndDoesNotStartWaitingOnes()
    {
        // Заказы с чётным номером «зависают» на оплате, нечётные проходят сразу.
        var payment = new ControlledPaymentService(order => order.Id % 2 == 0);
        var shop = new TestShop(payment);
        var orders = shop.NewOrders(10);
        using var cts = new CancellationTokenSource();

        // Лимит 4: нечётные заказы 1, 3, 5, 7 завершаются и освобождают слоты,
        // чётные 2, 4, 6, 8 занимают все четыре слота, 9 и 10 ждут свободного слота.
        var batchTask = shop.Batch.ProcessWithLimitAsync(orders, 4, cts.Token);
        await payment.WaitForEntriesAsync(8);

        cts.Cancel();
        var batch = await batchTask.WithTimeout();

        Assert.Equal(orders.Count, batch.Results.Count);
        foreach (var order in orders)
        {
            if (order.Attempts == 0)
            {
                Assert.Equal(OrderStatus.Cancelled, order.Status); // после отмены не запускался
            }
            else if (order.Id % 2 == 0)
            {
                Assert.Equal(OrderStatus.Cancelled, order.Status);
                Assert.Equal(ProcessingStage.Payment, order.FailedStage);
                Assert.Null(order.PaymentId);
            }
            else
            {
                Assert.Equal(OrderStatus.Completed, order.Status); // завершённые сохраняют статус
            }
        }

        Assert.Equal(new[] { 1, 3, 5, 7 }, orders.Where(o => o.Status == OrderStatus.Completed).Select(o => o.Id));
        Assert.Equal(new[] { 9, 10 }, orders.Where(o => o.Attempts == 0).Select(o => o.Id));
        Assert.Equal(4, batch.Completed);
        Assert.Equal(6, batch.Cancelled);
        Assert.Equal(4, payment.Charges);
        Assert.Equal(4, shop.Statistics.Completed);
        Assert.All(batch.Results.Where(r => r.Outcome == ProcessingOutcome.Cancelled),
            r => Assert.NotEqual(OrderStatus.Completed, r.FinalStatus));
    }

    [Fact]
    public async Task SequentialBatch_StopsStartingOrdersAfterCancellation()
    {
        var payment = new ControlledPaymentService(order => order.Id == 2);
        var shop = new TestShop(payment);
        var orders = shop.NewOrders(5);
        using var cts = new CancellationTokenSource();

        var batchTask = shop.Batch.ProcessSequentialAsync(orders, cts.Token);
        await payment.WaitForEntriesAsync(2);
        cts.Cancel();
        var batch = await batchTask.WithTimeout();

        Assert.Equal(OrderStatus.Completed, orders[0].Status);
        Assert.Equal(OrderStatus.Cancelled, orders[1].Status);
        Assert.All(orders.Skip(2), o => Assert.Equal(0, o.Attempts));
        Assert.Equal(1, batch.Completed);
        Assert.Equal(4, batch.Cancelled);
    }

    [Fact]
    public async Task CancelAfter_Timeout_CancelsLongRunningProcessing()
    {
        var payment = new ControlledPaymentService();
        var shop = new TestShop(payment);
        var order = shop.NewOrder();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await shop.Processor.ProcessAsync(order, cts.Token).WithTimeout();

        Assert.Equal(ProcessingOutcome.Cancelled, result.Outcome);
        Assert.True(cts.IsCancellationRequested);
    }
}
