using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Services;

public interface IInventoryService
{
    /// <summary>Резервирует все позиции заказа атомарно либо бросает <see cref="OutOfStockException"/>.</summary>
    Task ReserveAsync(Order order, CancellationToken cancellationToken);

    /// <summary>Компенсация: возвращает зарезервированные позиции на склад.</summary>
    void Release(Order order);

    int GetAvailable(string sku);
}

/// <summary>
/// Эмуляция складской службы. Остатки — общее изменяемое состояние, к которому
/// обращаются параллельные задачи обработки, поэтому изменение защищено <c>lock</c>.
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private readonly object _stockLock = new();
    private readonly Dictionary<string, int> _stock;
    private readonly SimulationOptions _options;

    public InventoryService(SimulationOptions options, IReadOnlyDictionary<string, int>? initialStock = null)
    {
        _options = options;
        _stock = new Dictionary<string, int>(initialStock ?? DefaultStock, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, int> DefaultStock { get; } = new Dictionary<string, int>
    {
        ["KB-01"] = 100,
        ["MS-02"] = 100,
        ["MN-27"] = 40,
        ["HS-05"] = 60,
        ["CB-10"] = 500,
        ["SSD-1T"] = 50,
        ["LT-15"] = 2,
        [ProductCatalog.CorruptedSku] = 10,
    };

    public async Task ReserveAsync(Order order, CancellationToken cancellationToken)
    {
        // Ожидание ответа склада — асинхронное, поток не блокируется.
        await SimulationOptions.SimulateLatencyAsync(
            _options.GetDelay(ProcessingStage.Inventory, order.Id), cancellationToken).ConfigureAwait(false);

        // Последняя точка отмены перед изменением общего состояния.
        cancellationToken.ThrowIfCancellationRequested();

        // Проверка всех позиций и списание должны быть одной атомарной операцией:
        // иначе два заказа могут одновременно «увидеть» последний ноутбук.
        // Внутри lock нет await — блокировка удерживается короткое время одним потоком.
        lock (_stockLock)
        {
            foreach (var group in order.Items.GroupBy(i => i.Sku, StringComparer.OrdinalIgnoreCase))
            {
                var requested = group.Sum(i => i.Quantity);
                var available = _stock.GetValueOrDefault(group.Key);
                if (available < requested)
                {
                    throw new OutOfStockException(group.Key, requested, available);
                }
            }

            foreach (var item in order.Items)
            {
                _stock[item.Sku] -= item.Quantity;
            }
        }
    }

    public void Release(Order order)
    {
        lock (_stockLock)
        {
            foreach (var item in order.Items)
            {
                _stock[item.Sku] = _stock.GetValueOrDefault(item.Sku) + item.Quantity;
            }
        }
    }

    public int GetAvailable(string sku)
    {
        lock (_stockLock)
        {
            return _stock.GetValueOrDefault(sku);
        }
    }
}
