using System.Collections.Concurrent;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Analytics;
using OrderProcessing.Core.Concurrency;
using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Core.Services;

namespace OrderProcessing.App.Demos;

/// <summary>Пункт 16: потокобезопасные коллекции — каждая в своём характерном сценарии.</summary>
internal static class CollectionDemos
{
    public static async Task RunAsync(SimulationOptions options)
    {
        Ui.Header("Потокобезопасные коллекции");
        Ui.Info("1 — ConcurrentDictionary (подсчёт продаж и результаты по номеру заказа)");
        Ui.Info("2 — ConcurrentQueue (общая очередь заказов для нескольких работников)");
        Ui.Info("3 — BlockingCollection (ограниченный производитель-потребитель на потоках)");
        Ui.Info("4 — Channel<T> (асинхронная очередь с backpressure и отменой)");
        Ui.Info("5 — все демонстрации подряд и сравнение");
        Ui.Info("0 — назад");
        var choice = Ui.ReadInt("Вариант", 0, 5, 5);
        if (choice is null or 0)
        {
            return;
        }

        if (choice is 1 or 5)
        {
            await ConcurrentDictionaryAsync(options);
        }

        if (choice is 2 or 5)
        {
            await ConcurrentQueueAsync(options);
        }

        if (choice is 3 or 5)
        {
            await BlockingCollectionAsync();
        }

        if (choice is 4 or 5)
        {
            await ChannelAsync(options);
        }

        if (choice == 5)
        {
            Ui.Section("Сравнение");
            Ui.Explain("ConcurrentDictionary — потокобезопасный доступ по ключу; атомарны отдельные методы (TryAdd, AddOrUpdate, GetOrAdd).");
            Ui.Explain("ConcurrentQueue — неблокирующая FIFO-очередь: TryDequeue сразу возвращает false, если пусто; ждать элементов не умеет.");
            Ui.Explain("BlockingCollection — обёртка над очередью с блокирующими Add/Take, ограничением ёмкости и CompleteAdding; блокирует потоки.");
            Ui.Explain("Channel<T> — асинхронный аналог: WriteAsync/ReadAllAsync ожидают без блокировки потоков, есть Complete и backpressure.");
        }
    }

    private static async Task ConcurrentDictionaryAsync(SimulationOptions options)
    {
        Ui.Section("ConcurrentDictionary<TKey, TValue>");
        var orders = DemoOrderFactory.CreateSuccessfulOrders(400, CreateIds());
        var items = orders.SelectMany(o => o.Items).ToArray();

        // Правильно: AddOrUpdate атомарно для одного ключа.
        var safe = new ConcurrentDictionary<string, int>();
        // Неправильно: каждая операция потокобезопасна, но последовательность «прочитать → вычислить → записать» — нет.
        var racy = new ConcurrentDictionary<string, int>();

        Parallel.ForEach(items, item =>
        {
            safe.AddOrUpdate(item.Sku, item.Quantity, (_, current) => current + item.Quantity);

            racy.TryAdd(item.Sku, 0);
            racy[item.Sku] = racy[item.Sku] + item.Quantity; // гонка: между чтением и записью значение может измениться
        });

        var expected = items.GroupBy(i => i.Sku).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        Ui.WriteLine($"  {"SKU",-8} | {"ожидается",9} | {"AddOrUpdate",11} | {"чтение+запись",13}");
        foreach (var (sku, value) in expected.OrderBy(p => p.Key))
        {
            Ui.WriteLine($"  {sku,-8} | {value,9} | {safe[sku],11} | {racy[sku],13}{(racy[sku] != value ? "  ← потерянные обновления" : string.Empty)}");
        }

        Ui.Explain("AddOrUpdate может вызвать делегат обновления повторно при конфликте, поэтому делегат должен быть без побочных эффектов.");
        Ui.Explain("Потеря обновлений во втором столбце зависит от планировщика и может не проявиться в отдельном запуске.");

        Ui.Info("  Результаты обработки по номеру заказа (OrderRepository на ConcurrentDictionary):");
        var env = new ShopEnvironment(options);
        var batch = DemoOrderFactory.CreateSuccessfulOrders(5, env.Repository.NextId);
        await env.Batch.ProcessParallelAsync(batch);
        foreach (var order in batch)
        {
            if (env.Repository.TryGetLastResult(order.Id, out var result))
            {
                Ui.Info($"    [{order.Id}] → {result.Outcome}, {result.Elapsed.TotalMilliseconds:0} мс");
            }
        }
    }

    private static async Task ConcurrentQueueAsync(SimulationOptions options)
    {
        Ui.Section("ConcurrentQueue<T>");
        var env = new ShopEnvironment(options);
        var orders = DemoOrderFactory.CreateSuccessfulOrders(10, env.Repository.NextId);
        foreach (var order in orders)
        {
            env.Repository.TryAdd(order);
        }

        var results = await ConcurrentQueueWorkerPool.RunAsync(orders, workerCount: 3,
            (order, workerId, token) => env.Processor.ProcessAsync(order, token));

        foreach (var group in results.GroupBy(r => r.WorkerId).OrderBy(g => g.Key))
        {
            Ui.Info($"  Работник {group.Key}: заказы {string.Join(", ", group.Select(r => "#" + r.Item.Id))}");
        }

        Ui.Info($"  Обработано {results.Count} из {orders.Count}; дубликатов: {results.Count - results.Select(r => r.Item.Id).Distinct().Count()}");
        Ui.Explain("TryDequeue атомарен: каждый заказ достался ровно одному работнику. Журнал событий обработки также построен на ConcurrentQueue.");
        Ui.Info($"  Событий в журнале изолированного магазина: {env.Journal.Count}");
    }

    private static async Task BlockingCollectionAsync()
    {
        Ui.Section("BlockingCollection<T>: производитель → [ёмкость 3] → 3 потребителя");
        var orders = DemoOrderFactory.CreateSuccessfulOrders(15, CreateIds());
        var report = await BlockingCollectionPipeline.RunAsync(orders, boundedCapacity: 3, consumerCount: 3,
            (order, consumerId) =>
            {
                Thread.Sleep(40); // синхронная «упаковка» заказа: поток потребителя действительно занят
                return OrderAnalytics.ComputeRiskScore(order, 20_000);
            });

        foreach (var group in report.Results.GroupBy(r => r.WorkerId).OrderBy(g => g.Key))
        {
            Ui.Info($"  Потребитель {group.Key}: {group.Count()} заказов");
        }

        Ui.Info($"  Произведено {report.Produced}, обработано {report.Results.Count}, производитель ждал места {report.ProducerWaits} раз, {report.Elapsed.TotalMilliseconds:0} мс");
        Ui.Explain("После CompleteAdding потребители выходят из GetConsumingEnumerable — зависания на пустой очереди нет.");
        Ui.Explain("Add и перечисление блокируют потоки, поэтому потребители запущены как LongRunning (выделенные потоки).");
    }

    private static async Task ChannelAsync(SimulationOptions options)
    {
        Ui.Section("Channel<T>: производитель → [ёмкость 2] → 3 асинхронных потребителя");
        var env = new ShopEnvironment(options);
        var orders = DemoOrderFactory.CreateSuccessfulOrders(12, env.Repository.NextId);
        foreach (var order in orders)
        {
            env.Repository.TryAdd(order);
        }

        var report = await ChannelPipeline.RunAsync(orders, capacity: 2, consumerCount: 3,
            (order, consumerId, token) => env.Processor.ProcessAsync(order, token));
        foreach (var group in report.Results.GroupBy(r => r.WorkerId).OrderBy(g => g.Key))
        {
            Ui.Info($"  Потребитель {group.Key}: {string.Join(", ", group.Select(r => $"#{r.Item.Id}={r.Result.Outcome}"))}");
        }

        Ui.Info($"  Записано {report.Produced}, обработано {report.Results.Count}, WriteAsync ждал места {report.ProducerWaits} раз (backpressure), {report.Elapsed.TotalMilliseconds:0} мс");

        Ui.Info("  Повтор с отменой через 400 мс:");
        var env2 = new ShopEnvironment(options);
        var orders2 = DemoOrderFactory.CreateSuccessfulOrders(12, env2.Repository.NextId);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        try
        {
            await ChannelPipeline.RunAsync(orders2, capacity: 2, consumerCount: 3,
                (order, consumerId, token) => env2.Processor.ProcessAsync(order, token), cts.Token);
            Ui.Success("  Канал обработан до срабатывания тайм-аута.");
        }
        catch (OperationCanceledException)
        {
            var statuses = orders2.GroupBy(o => o.Status).Select(g => $"{g.Key}: {g.Count()}");
            Ui.Warning($"  Конвейер отменён, все задачи завершены. Статусы: {string.Join(", ", statuses)}");
        }

        Ui.Explain("Writer.TryComplete в finally сообщает потребителям об окончании данных; ReadAllAsync завершается, когда канал пуст.");
        Ui.Explain("Заказы, не попавшие в канал до отмены, остались Pending — новые операции после отмены не запускались.");
    }

    private static Func<int> CreateIds()
    {
        var id = 0;
        return () => Interlocked.Increment(ref id);
    }
}
