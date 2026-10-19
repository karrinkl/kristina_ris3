using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Services;

/// <summary>
/// Параметры эмуляции «сетевых» задержек внешних служб. Вместо реальных HTTP-запросов
/// используется <see cref="Task.Delay(TimeSpan, CancellationToken)"/>, поэтому программа
/// работает полностью автономно. Задержка детерминирована (зависит от номера заказа),
/// чтобы последовательный и параллельный прогоны сравнивались на одинаковой нагрузке.
/// </summary>
public sealed record SimulationOptions
{
    public TimeSpan Validation { get; init; } = TimeSpan.FromMilliseconds(40);
    public TimeSpan CatalogLookup { get; init; } = TimeSpan.FromMilliseconds(60);
    public TimeSpan Inventory { get; init; } = TimeSpan.FromMilliseconds(120);
    public TimeSpan Payment { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan Notification { get; init; } = TimeSpan.FromMilliseconds(80);

    /// <summary>Максимальная детерминированная добавка к задержке, мс.</summary>
    public int JitterMilliseconds { get; init; } = 60;

    public static SimulationOptions Default { get; } = new();

    /// <summary>Нулевые задержки — для модульных тестов.</summary>
    public static SimulationOptions Instant { get; } = new()
    {
        Validation = TimeSpan.Zero,
        CatalogLookup = TimeSpan.Zero,
        Inventory = TimeSpan.Zero,
        Payment = TimeSpan.Zero,
        Notification = TimeSpan.Zero,
        JitterMilliseconds = 0,
    };

    public TimeSpan GetDelay(ProcessingStage stage, int key)
    {
        var baseDelay = stage switch
        {
            ProcessingStage.Validation => Validation,
            ProcessingStage.Inventory => Inventory,
            ProcessingStage.Payment => Payment,
            ProcessingStage.Notification => Notification,
            _ => TimeSpan.Zero,
        };

        if (baseDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var jitter = JitterMilliseconds <= 0 ? 0 : Math.Abs(key * 37 + (int)stage * 11) % (JitterMilliseconds + 1);
        return baseDelay + TimeSpan.FromMilliseconds(jitter);
    }

    /// <summary>
    /// Асинхронное ожидание «ответа внешней службы». Поток на время ожидания не блокируется:
    /// Task.Delay регистрирует таймер и возвращает незавершённую задачу.
    /// Перегрузка с токеном завершает ожидание сразу при отмене (TaskCanceledException).
    /// </summary>
    public static Task SimulateLatencyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    }
}
