using System.Collections.Concurrent;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Processing;

/// <summary>
/// Потокобезопасное хранилище заказов и последних результатов обработки.
/// Отдельные операции <see cref="ConcurrentDictionary{TKey,TValue}"/> атомарны,
/// но последовательность «прочитать — проверить — записать» из нескольких вызовов — нет;
/// поэтому для изменения используется AddOrUpdate/TryAdd, а не TryGetValue + индексатор.
/// </summary>
public sealed class OrderRepository
{
    private readonly ConcurrentDictionary<int, Order> _orders = new();
    private readonly ConcurrentDictionary<int, OrderProcessingResult> _lastResults = new();
    private int _lastId;

    public int Count => _orders.Count;

    public int NextId() => Interlocked.Increment(ref _lastId);

    public bool TryAdd(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (!_orders.TryAdd(order.Id, order))
        {
            return false;
        }

        // Поддерживаем генератор идентификаторов не ниже максимального загруженного Id.
        int current;
        do
        {
            current = Volatile.Read(ref _lastId);
            if (order.Id <= current)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _lastId, order.Id, current) != current);

        return true;
    }

    public bool TryGet(int id, out Order order) => _orders.TryGetValue(id, out order!);

    public IReadOnlyList<Order> GetAll() => _orders.Values.OrderBy(o => o.Id).ToArray();

    public IReadOnlyList<Order> GetByStatus(OrderStatus status) =>
        _orders.Values.Where(o => o.Status == status).OrderBy(o => o.Id).ToArray();

    public IReadOnlyDictionary<OrderStatus, int> CountByStatus()
    {
        var counts = Enum.GetValues<OrderStatus>().ToDictionary(s => s, _ => 0);
        foreach (var order in _orders.Values)
        {
            counts[order.Status]++;
        }

        return counts;
    }

    // Запись через индексатор — одна атомарная операция «добавить или заменить».
    public void SaveResult(OrderProcessingResult result) => _lastResults[result.OrderId] = result;

    public bool TryGetLastResult(int orderId, out OrderProcessingResult result) =>
        _lastResults.TryGetValue(orderId, out result!);

    public void Clear()
    {
        _orders.Clear();
        _lastResults.Clear();
        Interlocked.Exchange(ref _lastId, 0);
    }
}
