namespace OrderProcessing.Core.Processing;

/// <summary>
/// Ограничитель количества одновременно выполняемых операций на основе <see cref="SemaphoreSlim"/>.
/// Разрешение освобождается в <c>finally</c>, поэтому исключение или отмена внутри операции
/// не приводят к «утечке» слотов. Если отмена произошла во время ожидания слота,
/// разрешение не было получено и освобождать его не нужно.
/// </summary>
public sealed class ConcurrencyLimiter : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public ConcurrencyLimiter(int maxConcurrency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        MaxConcurrency = maxConcurrency;
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public int MaxConcurrency { get; }

    /// <summary>Количество свободных слотов в данный момент.</summary>
    public int AvailableSlots => _semaphore.CurrentCount;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        // Асинхронное ожидание: поток не блокируется, пока слотов нет.
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Слот мог освободиться одновременно с отменой (его отдали этой задаче раньше,
            // чем сработала регистрация токена) — новую операцию после отмены не начинаем.
            cancellationToken.ThrowIfCancellationRequested();
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
