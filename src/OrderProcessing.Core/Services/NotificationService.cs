using System.Collections.Concurrent;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Services;

public interface INotificationService
{
    /// <summary>Операция без результата — возвращает <see cref="Task"/>, а не <see cref="Task{TResult}"/>.</summary>
    Task SendAsync(Order order, string message, CancellationToken cancellationToken);
}

/// <summary>
/// Эмуляция почтового сервиса. Письма складываются в локальный «исходящий ящик»
/// (<see cref="ConcurrentQueue{T}"/>), реальная отправка не выполняется.
/// Адрес, начинающийся с <c>offline</c>, при первой попытке даёт временную ошибку.
/// </summary>
public sealed class NotificationService(SimulationOptions options) : INotificationService
{
    public const string OfflineMailboxPrefix = "offline";

    private readonly ConcurrentDictionary<int, int> _attempts = new();

    public ConcurrentQueue<string> Outbox { get; } = new();

    public async Task SendAsync(Order order, string message, CancellationToken cancellationToken)
    {
        var attempt = _attempts.AddOrUpdate(order.Id, 1, (_, previous) => previous + 1);

        await SimulationOptions.SimulateLatencyAsync(
            options.GetDelay(ProcessingStage.Notification, order.Id), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (attempt == 1 && order.Email.StartsWith(OfflineMailboxPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotificationException($"Почтовый сервер для {order.Email} временно недоступен");
        }

        Outbox.Enqueue($"{order.Email}: {message}");
    }
}
