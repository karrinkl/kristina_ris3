using System.Collections.Concurrent;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Processing;

public enum OrderEventKind
{
    Started,
    Step,
    Finished,
    Skipped,
}

/// <summary>Событие журнала обработки. Номер потока показывает, где выполнялось продолжение после await.</summary>
public sealed record OrderEvent(
    DateTimeOffset Timestamp, int OrderId, ProcessingStage Stage, OrderEventKind Kind, string Message, int ThreadId)
{
    public override string ToString() =>
        $"{Timestamp:HH:mm:ss.fff} [поток {ThreadId,3}] #{OrderId} {Stage,-12} {Message}";
}

/// <summary>
/// Журнал событий на основе <see cref="ConcurrentQueue{T}"/>: множество рабочих задач
/// добавляют события без внешней блокировки, а читатель получает снимок.
/// Размер ограничен — при переполнении старые события вытесняются.
/// </summary>
public sealed class OrderEventJournal(int capacity = 2_000)
{
    private readonly ConcurrentQueue<OrderEvent> _events = new();

    public int Count => _events.Count;

    public void Add(OrderEvent orderEvent)
    {
        _events.Enqueue(orderEvent);

        // Count и TryDequeue по отдельности потокобезопасны, но вместе не атомарны:
        // журнал может кратковременно превысить ёмкость на несколько элементов — это допустимо.
        while (_events.Count > capacity)
        {
            if (!_events.TryDequeue(out _))
            {
                break;
            }
        }
    }

    public IReadOnlyList<OrderEvent> Snapshot() => _events.ToArray();

    public IReadOnlyList<OrderEvent> Tail(int count)
    {
        var all = _events.ToArray();
        return all.Length <= count ? all : all[^count..];
    }
}
