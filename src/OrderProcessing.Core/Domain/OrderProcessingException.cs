namespace OrderProcessing.Core.Domain;

/// <summary>
/// Базовый класс ожидаемых бизнес-ошибок обработки заказа. Такие ошибки переводят заказ
/// в статус <see cref="OrderStatus.Failed"/> и не пробрасываются вызывающему коду.
/// Все прочие исключения считаются непредвиденными.
/// </summary>
public abstract class OrderProcessingException(ProcessingStage stage, string message, bool isRetryable)
    : Exception(message)
{
    public ProcessingStage Stage { get; } = stage;

    /// <summary>Временная ли ошибка: имеет ли смысл повторять обработку.</summary>
    public bool IsRetryable { get; } = isRetryable;
}

public sealed class OrderValidationException(string message)
    : OrderProcessingException(ProcessingStage.Validation, message, isRetryable: false);

public sealed class OutOfStockException(string sku, int requested, int available)
    : OrderProcessingException(ProcessingStage.Inventory,
        $"Недостаточно товара {sku}: запрошено {requested}, доступно {available}", isRetryable: false)
{
    public string Sku { get; } = sku;
}

public sealed class PaymentException(string message, bool isRetryable)
    : OrderProcessingException(ProcessingStage.Payment, message, isRetryable);

public sealed class NotificationException(string message)
    : OrderProcessingException(ProcessingStage.Notification, message, isRetryable: true);
