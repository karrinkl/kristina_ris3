using OrderProcessing.Core.Domain;

namespace OrderProcessing.Tests;

public class OrderStatusTransitionTests
{
    private static Order NewOrder() =>
        new(1, "Тест", "test@example.test", [new OrderItem("KB-01", "Клавиатура", 1, 3_500m)]);

    [Theory]
    [InlineData(OrderStatus.Pending, OrderStatus.Processing, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Pending, OrderStatus.Completed, false)]
    [InlineData(OrderStatus.Processing, OrderStatus.Completed, true)]
    [InlineData(OrderStatus.Processing, OrderStatus.Failed, true)]
    [InlineData(OrderStatus.Processing, OrderStatus.Cancelled, true)]
    [InlineData(OrderStatus.Processing, OrderStatus.Pending, false)]
    [InlineData(OrderStatus.Completed, OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Completed, OrderStatus.Failed, false)]
    [InlineData(OrderStatus.Completed, OrderStatus.Pending, false)]
    [InlineData(OrderStatus.Failed, OrderStatus.Pending, true)]
    [InlineData(OrderStatus.Failed, OrderStatus.Completed, false)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Pending, true)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Completed, false)]
    public void TransitionTable_MatchesSpecification(OrderStatus from, OrderStatus to, bool allowed) =>
        Assert.Equal(allowed, OrderStatusTransitions.IsAllowed(from, to));

    [Fact]
    public void NewOrder_IsPending_AndTotalIsSumOfItems()
    {
        var order = new Order(5, "Тест", "t@example.test",
            [new OrderItem("KB-01", "Клавиатура", 2, 3_500m), new OrderItem("MS-02", "Мышь", 1, 1_200m)]);

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(8_200m, order.Total);
        Assert.Equal(0, order.Attempts);
    }

    [Fact]
    public void TryBeginProcessing_SecondCall_ReturnsFalse()
    {
        var order = NewOrder();

        Assert.True(order.TryBeginProcessing());
        Assert.False(order.TryBeginProcessing());
        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Equal(1, order.Attempts);
    }

    [Fact]
    public void CompletedOrder_CannotBeCancelledOrFailed()
    {
        var order = NewOrder();
        order.TryBeginProcessing();
        order.Complete();

        Assert.Throws<InvalidOrderTransitionException>(() => order.Cancel(ProcessingStage.Payment, "поздно"));
        Assert.Throws<InvalidOrderTransitionException>(() => order.Fail(ProcessingStage.Payment, "поздно", false));
        Assert.False(order.TryCancelPending("поздно"));
        Assert.False(order.TryResetForRetry(out _));
        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.NotNull(order.FinishedAt);
    }

    [Fact]
    public void PendingOrder_CannotBeCompletedWithoutProcessing()
    {
        var order = NewOrder();
        Assert.Throws<InvalidOrderTransitionException>(order.Complete);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void NonRetryableFailure_IsNotResetForRetry()
    {
        var order = NewOrder();
        order.TryBeginProcessing();
        order.Fail(ProcessingStage.Payment, "отказ банка", isRetryable: false);

        Assert.False(order.TryResetForRetry(out var reason));
        Assert.Contains("отказ банка", reason);
        Assert.Equal(OrderStatus.Failed, order.Status);
    }

    [Fact]
    public void RetryableFailure_AndCancellation_ReturnToPending()
    {
        var failed = NewOrder();
        failed.TryBeginProcessing();
        failed.Fail(ProcessingStage.Notification, "сервер недоступен", isRetryable: true);
        Assert.True(failed.TryResetForRetry(out _));
        Assert.Equal(OrderStatus.Pending, failed.Status);

        var cancelled = NewOrder();
        cancelled.TryBeginProcessing();
        cancelled.Cancel(ProcessingStage.Inventory, "отмена");
        Assert.True(cancelled.TryResetForRetry(out _));
        Assert.Equal(OrderStatus.Pending, cancelled.Status);
        Assert.True(cancelled.TryBeginProcessing());
        Assert.Equal(2, cancelled.Attempts);
    }

    [Fact]
    public void StageMarks_RequireProcessingStatus()
    {
        var order = NewOrder();
        Assert.Throws<InvalidOperationException>(() => order.MarkPaid("PAY-1"));
        order.TryBeginProcessing();
        order.MarkPaid("PAY-1");
        Assert.Equal("PAY-1", order.PaymentId);
    }
}
