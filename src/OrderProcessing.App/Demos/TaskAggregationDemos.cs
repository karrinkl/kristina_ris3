using System.Diagnostics;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Core.Services;

namespace OrderProcessing.App.Demos;

/// <summary>Пункты 13 и 14: Task.WhenAll и Task.WhenAny.</summary>
internal static class TaskAggregationDemos
{
    // 13
    public static async Task WhenAllAsync(SimulationOptions options)
    {
        Ui.Header("Task.WhenAll — ожидание всех задач");
        var env = new ShopEnvironment(options);
        var orders = DemoOrderFactory.CreateScenarioOrders(env.Repository.NextId);
        foreach (var order in orders)
        {
            env.Repository.TryAdd(order);
        }

        Ui.Info($"Запускаем {orders.Count} задач обработки (изолированный набор сценариев). Задачи стартуют сразу при вызове ProcessAsync.");
        var stopwatch = Stopwatch.StartNew();
        Task<OrderProcessingResult>[] tasks = orders.Select(o => env.Processor.ProcessAsync(o)).ToArray();
        Task<OrderProcessingResult[]> all = Task.WhenAll(tasks);

        try
        {
            OrderProcessingResult[] results = await all;
            Ui.Success($"Все {results.Length} задач завершились без исключений.");
        }
        catch (Exception ex)
        {
            Ui.Warning($"await Task.WhenAll выбросил одно (первое) исключение: {ex.GetType().Name}: {ex.Message}");
            Ui.Info($"Статус агрегирующей задачи: {all.Status}; всего исключений в all.Exception: {all.Exception?.InnerExceptions.Count ?? 0}");
            Ui.Explain("Массив результатов при этом не возвращается, но каждая исходная задача уже завершена — разбираем их по одной.");
        }

        stopwatch.Stop();

        Ui.Section("Результаты по каждой задаче");
        int completed = 0, businessFailed = 0, unexpected = 0, cancelled = 0;
        for (var i = 0; i < tasks.Length; i++)
        {
            var task = tasks[i];
            if (task.IsCompletedSuccessfully)
            {
                var result = task.Result; // задача завершена, Result не блокирует
                switch (result.Outcome)
                {
                    case ProcessingOutcome.Completed:
                        completed++;
                        break;
                    case ProcessingOutcome.Failed:
                        businessFailed++;
                        break;
                    case ProcessingOutcome.Cancelled:
                        cancelled++;
                        break;
                }

                Ui.PrintResults([result]);
            }
            else if (task.IsFaulted)
            {
                unexpected++;
                var error = task.Exception!.InnerException!;
                Ui.Error($"  #{orders[i].Id}: задача Faulted — {error.GetType().Name}: {error.Message} (статус заказа {orders[i].Status})");
            }
            else
            {
                cancelled++;
                Ui.Warning($"  #{orders[i].Id}: задача Canceled");
            }
        }

        Ui.Info($"Итог: успешно {completed}, бизнес-ошибки {businessFailed}, непредвиденные исключения {unexpected}, отменено {cancelled}.");
        var longest = tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result.Elapsed).DefaultIfEmpty().Max();
        Ui.Info($"Общее время: {stopwatch.ElapsedMilliseconds} мс; самая долгая успешная задача: {longest.TotalMilliseconds:0} мс.");
        Ui.Explain("При одновременном ожидании общее время близко к времени самой долгой задачи, а не к сумме.");
    }

    private sealed record DeliveryQuote(string Carrier, decimal Price);

    private static async Task<DeliveryQuote> RequestQuoteAsync(
        string carrier, int delayMs, decimal price, bool fails, CancellationToken cancellationToken)
    {
        await Task.Delay(delayMs, cancellationToken);
        if (fails)
        {
            throw new InvalidOperationException($"служба «{carrier}» ответила ошибкой 503 (эмуляция)");
        }

        return new DeliveryQuote(carrier, price);
    }

    // 14
    public static async Task WhenAnyAsync()
    {
        Ui.Header("Task.WhenAny — первая завершившаяся задача");
        Ui.Info("Запрашиваем стоимость доставки у четырёх эмулированных служб с разной задержкой:");
        Ui.Info("  Экспресс — 150 мс (ошибка), Курьер+ — 300 мс, Почта — 600 мс, Грузовичок — 900 мс.");

        Ui.Section("1. Первая успешная котировка, остальные запросы отменяются");
        using (var cts = new CancellationTokenSource())
        {
            var tasks = StartQuotes(cts.Token);
            DeliveryQuote? winner = null;
            var pending = tasks.Keys.ToList();
            while (pending.Count > 0)
            {
                // WhenAny никогда не выбрасывает исключение задачи: он возвращает саму завершившуюся задачу.
                var finished = await Task.WhenAny(pending);
                pending.Remove(finished);
                var carrier = tasks[finished];
                try
                {
                    var quote = await finished; // здесь исключение задачи будет выброшено
                    if (winner is null)
                    {
                        winner = quote;
                        Ui.Success($"  Первая успешная: {quote.Carrier}, {quote.Price:0} ₽. Отменяем остальные запросы.");
                        cts.Cancel();
                    }
                }
                catch (OperationCanceledException)
                {
                    Ui.Warning($"  {carrier}: запрос отменён (задача Canceled)");
                }
                catch (InvalidOperationException ex)
                {
                    Ui.Error($"  {carrier}: первая завершившаяся задача — с ошибкой: {ex.Message}. Ждём следующую.");
                }
            }

            Ui.Explain("Цикл продолжается до опустошения списка: все задачи, включая отменённые, наблюдены — «потерянных» исключений нет.");
        }

        Ui.Section("2. Все котировки в порядке завершения, выбор самой дешёвой");
        var allTasks = StartQuotes(CancellationToken.None);
        var remaining = allTasks.Keys.ToList();
        DeliveryQuote? cheapest = null;
        var stopwatch = Stopwatch.StartNew();
        while (remaining.Count > 0)
        {
            var finished = await Task.WhenAny(remaining);
            remaining.Remove(finished);
            if (finished.IsCompletedSuccessfully)
            {
                var quote = finished.Result;
                Ui.Info($"  {stopwatch.ElapsedMilliseconds,4} мс: {quote.Carrier} — {quote.Price:0} ₽");
                if (cheapest is null || quote.Price < cheapest.Price)
                {
                    cheapest = quote;
                }
            }
            else
            {
                Ui.Error($"  {stopwatch.ElapsedMilliseconds,4} мс: {allTasks[finished]} — {finished.Exception?.InnerException?.Message ?? "отменено"}");
            }
        }

        Ui.Success($"  Самая дешёвая доставка: {cheapest?.Carrier} ({cheapest?.Price:0} ₽)");

        Ui.Section("3. Тайм-аут через WhenAny(задача, Task.Delay)");
        using (var cts = new CancellationTokenSource())
        {
            var slow = RequestQuoteAsync("Грузовичок", 900, 350m, fails: false, cts.Token);
            var timeout = Task.Delay(250, cts.Token);
            var first = await Task.WhenAny(slow, timeout);
            if (first == timeout)
            {
                Ui.Warning("  За 250 мс ответа нет — отменяем медленный запрос.");
                cts.Cancel();
                try
                {
                    await slow;
                }
                catch (OperationCanceledException)
                {
                    Ui.Info($"  Медленный запрос завершён со статусом {slow.Status} — задача наблюдена.");
                }
            }
            else
            {
                Ui.Success($"  Ответ получен до тайм-аута: {(await slow).Price:0} ₽");
                cts.Cancel(); // останавливаем таймер тайм-аута
            }
        }

        Ui.Explain("Начиная с .NET 6 то же можно записать короче: await task.WaitAsync(TimeSpan, token).");
    }

    private static Dictionary<Task<DeliveryQuote>, string> StartQuotes(CancellationToken token) => new()
    {
        [RequestQuoteAsync("Экспресс", 150, 390m, fails: true, token)] = "Экспресс",
        [RequestQuoteAsync("Курьер+", 300, 450m, fails: false, token)] = "Курьер+",
        [RequestQuoteAsync("Почта", 600, 290m, fails: false, token)] = "Почта",
        [RequestQuoteAsync("Грузовичок", 900, 350m, fails: false, token)] = "Грузовичок",
    };
}
