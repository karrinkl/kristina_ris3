namespace OrderProcessing.Core.Domain;

/// <summary>Статус жизненного цикла заказа.</summary>
public enum OrderStatus
{
    /// <summary>Заказ создан и ожидает обработки.</summary>
    Pending,

    /// <summary>Заказ захвачен одной рабочей задачей и обрабатывается.</summary>
    Processing,

    /// <summary>Все этапы успешно пройдены (конечный статус).</summary>
    Completed,

    /// <summary>Обработка завершилась ошибкой.</summary>
    Failed,

    /// <summary>Обработка отменена через <see cref="System.Threading.CancellationToken"/>.</summary>
    Cancelled,
}

/// <summary>Этап конвейера обработки заказа.</summary>
public enum ProcessingStage
{
    None,
    Validation,
    Inventory,
    Payment,
    Notification,
}
