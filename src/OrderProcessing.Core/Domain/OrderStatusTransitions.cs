namespace OrderProcessing.Core.Domain;

/// <summary>
/// Таблица допустимых переходов между статусами заказа.
/// <code>
/// Pending    -> Processing | Cancelled (пакет отменён до начала обработки заказа)
/// Processing -> Completed | Failed | Cancelled
/// Failed     -> Pending   (повторная обработка, только если ошибка временная)
/// Cancelled  -> Pending   (повторная обработка)
/// Completed  -> (конечный статус, переходов нет)
/// </code>
/// </summary>
public static class OrderStatusTransitions
{
    private static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> Allowed =
        new Dictionary<OrderStatus, OrderStatus[]>
        {
            [OrderStatus.Pending] = [OrderStatus.Processing, OrderStatus.Cancelled],
            [OrderStatus.Processing] = [OrderStatus.Completed, OrderStatus.Failed, OrderStatus.Cancelled],
            [OrderStatus.Failed] = [OrderStatus.Pending],
            [OrderStatus.Cancelled] = [OrderStatus.Pending],
            [OrderStatus.Completed] = [],
        };

    public static bool IsAllowed(OrderStatus from, OrderStatus to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static bool IsTerminal(OrderStatus status) => status == OrderStatus.Completed;
}
