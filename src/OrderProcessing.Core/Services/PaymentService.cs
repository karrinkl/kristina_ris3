using System.Collections.Concurrent;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Services;

public sealed record PaymentReceipt(string PaymentId, int OrderId, decimal Amount, string Method);

public interface IPaymentService
{
    Task<PaymentReceipt> ChargeAsync(Order order, CancellationToken cancellationToken);
}

/// <summary>
/// Эмуляция платёжного шлюза. Правила эмуляции задаются способом оплаты заказа
/// (см. <see cref="PaymentMethods"/>).
/// </summary>
public sealed class PaymentService(SimulationOptions options) : IPaymentService
{
    private readonly ConcurrentDictionary<int, int> _gatewayAttempts = new();
    private long _charges;

    /// <summary>Количество успешных списаний (для проверки отсутствия двойной оплаты).</summary>
    public long SuccessfulCharges => Interlocked.Read(ref _charges);

    /// <summary>
    /// Метод намеренно не помечен <c>async</c>: для оплаты бонусами ответ известен сразу,
    /// и мы возвращаем уже завершённую задачу через <see cref="Task.FromResult{TResult}"/>
    /// без конечного автомата и без обращения к шлюзу.
    /// </summary>
    public Task<PaymentReceipt> ChargeAsync(Order order, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<PaymentReceipt>(cancellationToken);
        }

        if (string.Equals(order.PaymentMethod, PaymentMethods.Bonus, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _charges);
            return Task.FromResult(new PaymentReceipt($"BONUS-{order.Id:D5}", order.Id, order.Total, order.PaymentMethod));
        }

        return ChargeViaGatewayAsync(order, cancellationToken);
    }

    private async Task<PaymentReceipt> ChargeViaGatewayAsync(Order order, CancellationToken cancellationToken)
    {
        // AddOrUpdate атомарно увеличивает счётчик попыток конкретного заказа.
        var attempt = _gatewayAttempts.AddOrUpdate(order.Id, 1, (_, previous) => previous + 1);

        await SimulationOptions.SimulateLatencyAsync(
            options.GetDelay(ProcessingStage.Payment, order.Id), cancellationToken).ConfigureAwait(false);

        // Деньги ещё не списаны — отмена здесь безопасна.
        cancellationToken.ThrowIfCancellationRequested();

        switch (order.PaymentMethod.ToLowerInvariant())
        {
            case PaymentMethods.DeclinedCard:
                throw new PaymentException("Банк отклонил платёж: недостаточно средств", isRetryable: false);
            case PaymentMethods.FlakyGateway when attempt == 1:
                throw new PaymentException("Платёжный шлюз не ответил (тайм-аут), можно повторить", isRetryable: true);
        }

        Interlocked.Increment(ref _charges);
        return new PaymentReceipt($"PAY-{order.Id:D5}-{attempt}", order.Id, order.Total, order.PaymentMethod);
    }
}
