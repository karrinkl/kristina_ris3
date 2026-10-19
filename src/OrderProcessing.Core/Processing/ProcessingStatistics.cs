using System.Collections.Concurrent;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Processing;

/// <summary>
/// Счётчики попыток обработки. Обновляются из многих задач одновременно, поэтому
/// используются атомарные операции <see cref="Interlocked"/> и <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Итоговые количества заказов по статусам берутся из фактических статусов заказов
/// (<see cref="OrderRepository.CountByStatus"/>), а здесь хранятся счётчики событий.
/// </summary>
public sealed class ProcessingStatistics
{
    private readonly ConcurrentDictionary<ProcessingStage, int> _failuresByStage = new();
    private long _started;
    private long _completed;
    private long _failed;
    private long _cancelled;
    private long _skipped;
    private long _totalProcessingTicks;
    private int _inFlight;
    private int _maxInFlight;

    public long Started => Interlocked.Read(ref _started);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failed => Interlocked.Read(ref _failed);
    public long Cancelled => Interlocked.Read(ref _cancelled);
    public long Skipped => Interlocked.Read(ref _skipped);
    public int InFlight => Volatile.Read(ref _inFlight);
    public int MaxObservedConcurrency => Volatile.Read(ref _maxInFlight);
    public TimeSpan TotalProcessingTime => TimeSpan.FromTicks(Interlocked.Read(ref _totalProcessingTicks));

    public IReadOnlyDictionary<ProcessingStage, int> FailuresByStage =>
        new SortedDictionary<ProcessingStage, int>(_failuresByStage);

    public void OnStarted()
    {
        Interlocked.Increment(ref _started);
        var current = Interlocked.Increment(ref _inFlight);

        // Атомарное обновление максимума: цикл CompareExchange повторяется,
        // если другой поток успел изменить значение между чтением и записью.
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxInFlight);
            if (current <= observed)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _maxInFlight, current, observed) != observed);
    }

    public void OnFinished(OrderProcessingResult result)
    {
        Interlocked.Decrement(ref _inFlight);
        Interlocked.Add(ref _totalProcessingTicks, result.Elapsed.Ticks);

        switch (result.Outcome)
        {
            case ProcessingOutcome.Completed:
                Interlocked.Increment(ref _completed);
                break;
            case ProcessingOutcome.Failed:
                Interlocked.Increment(ref _failed);
                _failuresByStage.AddOrUpdate(result.Stage, 1, (_, n) => n + 1);
                break;
            case ProcessingOutcome.Cancelled:
                Interlocked.Increment(ref _cancelled);
                break;
        }
    }

    /// <summary>Заказ отменён до начала обработки (не захватывался задачей).</summary>
    public void OnCancelledBeforeStart() => Interlocked.Increment(ref _cancelled);

    public void OnSkipped() => Interlocked.Increment(ref _skipped);

    public void ResetConcurrencyPeak() => Interlocked.Exchange(ref _maxInFlight, Volatile.Read(ref _inFlight));
}
