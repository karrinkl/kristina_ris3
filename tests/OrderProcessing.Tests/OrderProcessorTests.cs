using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Core.Services;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class OrderProcessorTests
{
    [Fact]
    public async Task SuccessfulOrder_PassesAllStages()
    {
        var shop = new TestShop();
        var order = shop.NewOrder("KB-01", 2);
        var stockBefore = shop.Inventory.GetAvailable("KB-01");

        var result = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Completed, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.NotNull(order.PaymentId);
        Assert.True(order.CustomerNotified);
        Assert.True(order.StockReserved);
        Assert.Equal(stockBefore - 2, shop.Inventory.GetAvailable("KB-01"));
        Assert.Single(shop.RealNotifications.Outbox);
        Assert.Equal(1, shop.Statistics.Completed);
        Assert.True(shop.Repository.TryGetLastResult(order.Id, out var saved));
        Assert.Equal(result, saved);
    }

    [Fact]
    public async Task SuccessfulOrder_StagesAreReportedInOrder()
    {
        var shop = new TestShop();
        var order = shop.NewOrder();

        await shop.Processor.ProcessAsync(order).WithTimeout();

        var stages = shop.Journal.Snapshot().Where(e => e.Kind == OrderEventKind.Step).Select(e => e.Stage).ToArray();
        Assert.Equal(
            new[] { ProcessingStage.Validation, ProcessingStage.Inventory, ProcessingStage.Payment, ProcessingStage.Notification },
            stages);
    }

    [Fact]
    public async Task BonusPayment_CompletesWithoutGateway()
    {
        var shop = new TestShop();
        var order = shop.NewOrder("CB-10", 1, PaymentMethods.Bonus);

        var result = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Completed, result.Outcome);
        Assert.StartsWith("BONUS-", order.PaymentId);
    }

    [Fact]
    public async Task EmptyOrder_FailsValidation_NotRetryable()
    {
        var shop = new TestShop();
        var order = new Order(100, "Пусто", "e@example.test", []);
        shop.Register([order]);

        var result = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Failed, result.Outcome);
        Assert.Equal(ProcessingStage.Validation, result.Stage);
        Assert.IsType<OrderValidationException>(result.Error);
        Assert.False(order.IsRetryable);
        Assert.Equal(OrderStatus.Failed, order.Status);
    }

    [Theory]
    [InlineData("XX-404", "client@example.test")]
    [InlineData("KB-01", "без-собаки")]
    public async Task InvalidData_FailsValidation(string sku, string email)
    {
        var shop = new TestShop();
        var order = shop.NewOrder(sku, 1, email: email);

        var result = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingStage.Validation, result.Stage);
        Assert.Equal(OrderStatus.Failed, order.Status);
    }

    [Fact]
    public async Task DeclinedPayment_FailsAndReleasesReservedStock()
    {
        var shop = new TestShop();
        var before = shop.Inventory.GetAvailable("HS-05");
        var order = shop.NewOrder("HS-05", 3, PaymentMethods.DeclinedCard);

        var result = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Failed, result.Outcome);
        Assert.Equal(ProcessingStage.Payment, result.Stage);
        Assert.IsType<PaymentException>(result.Error);
        Assert.False(order.IsRetryable);
        Assert.Null(order.PaymentId);
        Assert.False(order.StockReserved);
        Assert.Equal(before, shop.Inventory.GetAvailable("HS-05")); // компенсация резерва
    }

    [Fact]
    public async Task OutOfStock_Fails_AndDoesNotChangeStock()
    {
        var shop = new TestShop();
        var order = shop.NewOrder("LT-15", 3);

        var result = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingStage.Inventory, result.Stage);
        Assert.IsType<OutOfStockException>(result.Error);
        Assert.Equal(2, shop.Inventory.GetAvailable("LT-15"));
    }

    [Fact]
    public async Task FlakyPayment_IsRetryable_AndSucceedsOnRetry()
    {
        var shop = new TestShop();
        var order = shop.NewOrder("SSD-1T", 1, PaymentMethods.FlakyGateway);

        var first = await shop.Processor.ProcessAsync(order).WithTimeout();
        Assert.Equal(ProcessingOutcome.Failed, first.Outcome);
        Assert.True(order.IsRetryable);

        Assert.True(order.TryResetForRetry(out _));
        var second = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Completed, second.Outcome);
        Assert.Equal(2, order.Attempts);
        Assert.Equal(1, shop.RealPayment.SuccessfulCharges);
    }

    [Fact]
    public async Task NotificationFailure_KeepsPayment_RetryDoesNotChargeTwice()
    {
        var shop = new TestShop();
        var order = shop.NewOrder("KB-01", 1, email: "offline.client@example.test");

        var first = await shop.Processor.ProcessAsync(order).WithTimeout();
        Assert.Equal(ProcessingStage.Notification, first.Stage);
        Assert.IsType<NotificationException>(first.Error);
        Assert.NotNull(order.PaymentId);
        Assert.True(order.StockReserved); // оплаченный резерв не возвращается на склад

        Assert.True(order.TryResetForRetry(out _));
        var second = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Completed, second.Outcome);
        Assert.Equal(1, shop.RealPayment.SuccessfulCharges);
        Assert.Single(shop.RealNotifications.Outbox);
    }

    [Fact]
    public async Task UnexpectedException_IsRethrown_AndOrderIsNotLeftProcessing()
    {
        var shop = new TestShop();
        var order = shop.NewOrder(ProductCatalog.CorruptedSku);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => shop.Processor.ProcessAsync(order)).WithTimeout();

        Assert.Contains(ProductCatalog.CorruptedSku, ex.Message);
        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.False(order.IsRetryable);
        Assert.Equal(0, shop.Statistics.InFlight);
        Assert.Equal(1, shop.Statistics.Failed);
    }

    [Fact]
    public async Task OrderNotInPending_IsSkipped()
    {
        var shop = new TestShop();
        var order = shop.NewOrder();
        await shop.Processor.ProcessAsync(order).WithTimeout();

        var again = await shop.Processor.ProcessAsync(order).WithTimeout();

        Assert.Equal(ProcessingOutcome.Skipped, again.Outcome);
        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.Equal(1, order.Attempts);
    }

    [Fact]
    public async Task ValueTaskCatalog_SecondLookupIsSynchronousCacheHit()
    {
        var catalog = new ProductCatalog(new SimulationOptions { CatalogLookup = TimeSpan.FromMilliseconds(20) });

        var first = catalog.FindAsync("KB-01", CancellationToken.None);
        Assert.False(first.IsCompleted); // промах: настоящая асинхронная операция
        Assert.NotNull(await first);

        var second = catalog.FindAsync("KB-01", CancellationToken.None);
        Assert.True(second.IsCompletedSuccessfully); // попадание: результат доступен сразу
        Assert.Equal("Клавиатура", (await second)!.Name);
        Assert.Equal(1, catalog.CacheHits);
        Assert.Equal(1, catalog.CacheMisses);
    }
}
