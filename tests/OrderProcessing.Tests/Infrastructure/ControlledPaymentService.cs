using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Services;

namespace OrderProcessing.Tests.Infrastructure;

/// <summary>
/// Управляемая тестовая подмена платёжной службы: оплата «зависает» на шлюзе до вызова
/// <see cref="ReleaseAll"/> или до отмены. Ведёт счётчики одновременных вызовов и сигналит о каждом входе —
/// тесты ждут сигналов, а не фиксированных задержек.
/// </summary>
internal sealed class ControlledPaymentService(Func<Order, bool>? shouldBlock = null) : IPaymentService
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _entered = new(0);
    private readonly Func<Order, bool> _shouldBlock = shouldBlock ?? (_ => true);
    private int _current;
    private int _max;
    private int _charges;

    public int MaxConcurrent => Volatile.Read(ref _max);
    public int Current => Volatile.Read(ref _current);
    public int Charges => Volatile.Read(ref _charges);

    public void ReleaseAll() => _gate.TrySetResult();

    /// <summary>Ждёт, пока в оплату войдут ещё <paramref name="count"/> вызовов.</summary>
    public async Task WaitForEntriesAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await _entered.WaitAsync().WaitAsync(TestTimeouts.Default);
        }
    }

    public async Task<PaymentReceipt> ChargeAsync(Order order, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _current);
        int observed;
        do
        {
            observed = Volatile.Read(ref _max);
            if (now <= observed)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _max, now, observed) != observed);

        try
        {
            _entered.Release();
            if (_shouldBlock(order))
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            Interlocked.Increment(ref _charges);
            return new PaymentReceipt($"TEST-{order.Id}", order.Id, order.Total, order.PaymentMethod);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}

internal static class TestTimeouts
{
    /// <summary>Защитный тайм-аут: тест падает, а не зависает, если задача не завершилась.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(15);

    public static Task<T> WithTimeout<T>(this Task<T> task) => task.WaitAsync(Default);

    public static Task WithTimeout(this Task task) => task.WaitAsync(Default);
}
