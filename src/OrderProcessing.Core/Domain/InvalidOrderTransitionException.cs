namespace OrderProcessing.Core.Domain;

/// <summary>Попытка выполнить недопустимый переход статуса (ошибка программы, а не бизнес-ошибка).</summary>
public sealed class InvalidOrderTransitionException(int orderId, OrderStatus from, OrderStatus to)
    : InvalidOperationException($"Заказ #{orderId}: переход {from} -> {to} недопустим.")
{
    public int OrderId { get; } = orderId;
    public OrderStatus From { get; } = from;
    public OrderStatus To { get; } = to;
}
