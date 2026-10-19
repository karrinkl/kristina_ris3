using System.Collections.Concurrent;

namespace OrderProcessing.Core.Services;

public sealed record Product(string Sku, string Name, decimal Price);

public interface IProductCatalog
{
    /// <summary>
    /// Поиск товара. Возвращает <see cref="ValueTask{TResult}"/>: при попадании в кэш результат
    /// доступен синхронно и выделение объекта Task не требуется.
    /// Возвращённое значение можно ожидать только один раз.
    /// </summary>
    ValueTask<Product?> FindAsync(string sku, CancellationToken cancellationToken);
}

/// <summary>
/// Эмуляция удалённого каталога товаров с локальным кэшем.
/// Пример осмысленного применения <see cref="ValueTask{TResult}"/>: большинство обращений
/// попадает в кэш и завершается синхронно, а «сетевое» обращение происходит только при промахе.
/// </summary>
public sealed class ProductCatalog : IProductCatalog
{
    /// <summary>SKU с «повреждёнными» данными: обращение к нему вызывает непредвиденное исключение.</summary>
    public const string CorruptedSku = "BROKEN-01";

    private readonly IReadOnlyDictionary<string, Product> _remoteSource;
    private readonly ConcurrentDictionary<string, Product> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _lookupDelay;
    private long _cacheHits;
    private long _cacheMisses;

    public ProductCatalog(SimulationOptions options, IEnumerable<Product>? products = null)
    {
        _lookupDelay = options.CatalogLookup;
        _remoteSource = (products ?? DefaultProducts)
            .ToDictionary(p => p.Sku, StringComparer.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<Product> DefaultProducts { get; } =
    [
        new("KB-01", "Клавиатура", 3_500m),
        new("MS-02", "Мышь", 1_200m),
        new("MN-27", "Монитор 27\"", 18_990m),
        new("HS-05", "Гарнитура", 4_500m),
        new("CB-10", "Кабель USB-C", 490m),
        new("SSD-1T", "SSD 1 ТБ", 7_990m),
        new("LT-15", "Ноутбук 15\"", 65_990m),
        new(CorruptedSku, "Товар с повреждённой карточкой", 1m),
    ];

    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);

    public ValueTask<Product?> FindAsync(string sku, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<Product?>(cancellationToken);
        }

        // Быстрый путь: значение уже есть — завершаем синхронно, без аллокации Task.
        if (_cache.TryGetValue(sku, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return ValueTask.FromResult<Product?>(cached);
        }

        // Медленный путь: оборачиваем настоящую асинхронную операцию.
        return new ValueTask<Product?>(LoadFromRemoteAsync(sku, cancellationToken));
    }

    private async Task<Product?> LoadFromRemoteAsync(string sku, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _cacheMisses);
        await SimulationOptions.SimulateLatencyAsync(_lookupDelay, cancellationToken).ConfigureAwait(false);

        if (string.Equals(sku, CorruptedSku, StringComparison.OrdinalIgnoreCase))
        {
            // Непредвиденная ошибка инфраструктуры — не бизнес-правило.
            throw new InvalidOperationException($"Каталог вернул повреждённую карточку товара {sku}.");
        }

        if (!_remoteSource.TryGetValue(sku, out var product))
        {
            return null;
        }

        // Два параллельных промаха по одному SKU могут оба сходить в «сеть» — это допустимо:
        // TryAdd атомарен, в кэше останется одно значение.
        _cache.TryAdd(sku, product);
        return product;
    }
}
