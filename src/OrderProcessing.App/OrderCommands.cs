using System.Text.Json;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;

namespace OrderProcessing.App;

/// <summary>Команды меню 1–12: работа с заказами основного магазина.</summary>
internal sealed class OrderCommands(ShopEnvironment shop)
{
    private BackgroundJob? _background;

    public bool HasRunningBackgroundJob => _background is { Task.IsCompleted: false };

    // 1
    public void ShowOrders()
    {
        Ui.Header("Список заказов");
        Ui.PrintOrders(shop.Repository.GetAll(), "Заказов нет. Создайте демонстрационные заказы (пункт 2).");
    }

    // 2
    public async Task CreateDemoOrdersAsync()
    {
        Ui.Header("Создание демонстрационных заказов");
        Ui.Info("1 — загрузить набор сценариев из файла data/orders.json");
        Ui.Info("2 — сгенерировать N заказов, которые гарантированно обрабатываются успешно");
        var choice = Ui.ReadInt("Вариант", 1, 2, 1);
        if (choice is null)
        {
            return;
        }

        IReadOnlyList<Order> orders;
        if (choice == 1)
        {
            var path = OrderFileStore.DefaultPath;
            try
            {
                // Настоящий асинхронный файловый ввод-вывод: Task.Run здесь не нужен.
                var dtos = await OrderFileStore.LoadAsync(path);
                orders = dtos.Select(dto => OrderFileStore.ToOrder(dto, shop.Repository.NextId())).ToArray();
                Ui.Explain($"Файл прочитан асинхронно: {path}. Номера заказов назначаются заново, чтобы не было конфликтов.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Ui.Warning($"Не удалось прочитать {path}: {ex.Message}");
                Ui.Warning("Используется встроенный набор сценариев.");
                orders = DemoOrderFactory.CreateScenarioOrders(shop.Repository.NextId);
            }
        }
        else
        {
            var count = Ui.ReadInt("Количество заказов", 1, 100, 10);
            if (count is null)
            {
                return;
            }

            orders = DemoOrderFactory.CreateSuccessfulOrders(count.Value, shop.Repository.NextId);
        }

        var added = orders.Count(shop.Repository.TryAdd);
        Ui.Success($"Добавлено заказов: {added}. Всего в магазине: {shop.Repository.Count}.");
        Ui.PrintOrders(orders.ToArray());
    }

    // 3
    public async Task ProcessSingleAsync()
    {
        Ui.Header("Асинхронная обработка одного заказа");
        var pending = shop.Repository.GetByStatus(OrderStatus.Pending);
        if (pending.Count == 0)
        {
            Ui.Warning("Нет заказов в статусе Pending. Создайте заказы (пункт 2) или повторите неуспешные (пункт 11).");
            return;
        }

        Ui.PrintOrders(pending);
        var id = Ui.ReadInt("Номер заказа", 1, int.MaxValue, pending[0].Id);
        if (id is null)
        {
            return;
        }

        if (!shop.Repository.TryGet(id.Value, out var order))
        {
            Ui.Error($"Заказ #{id} не найден.");
            return;
        }

        Ui.Info($"Поток меню перед запуском: {Environment.CurrentManagedThreadId}");
        await using var cancellation = ForegroundCancellation.Start();
        try
        {
            var result = await shop.Processor.ProcessAsync(order, cancellation.Token, new ConsoleProgress(verbose: true));
            Ui.PrintResults([result]);
        }
        catch (Exception ex)
        {
            // Непредвиденная ошибка пробрасывается обработчиком — показываем её, а не скрываем.
            Ui.Error($"Непредвиденное исключение {ex.GetType().Name}: {ex.Message}. Статус заказа: {order.Status}.");
        }

        Ui.Info($"Поток после await: {Environment.CurrentManagedThreadId}");
        Ui.Explain("Каждый этап ожидает Task.Delay — во время ожидания ни один поток не занят.");
        Ui.Explain("Продолжение после await выполняется на свободном потоке пула, поэтому номера потоков в журнале могут меняться.");
    }

    // 4
    public async Task ProcessParallelAsync()
    {
        Ui.Header("Параллельная обработка всех ожидающих заказов (Task.WhenAll)");
        var pending = RequirePending();
        if (pending is null)
        {
            return;
        }

        await using var cancellation = ForegroundCancellation.Start();
        var batch = await shop.Batch.ProcessParallelAsync(pending, cancellation.Token, new ConsoleProgress(verbose: false));
        Ui.PrintBatch(batch);
        Ui.Explain("Все задачи запущены сразу; await Task.WhenAll ждёт их завершения, не блокируя поток.");
        Ui.Explain("Если какая-то задача завершилась исключением, результаты остальных всё равно собираются по каждой задаче отдельно.");
    }

    // 5
    public async Task ProcessWithLimitAsync()
    {
        Ui.Header("Параллельная обработка с ограничением (SemaphoreSlim)");
        var pending = RequirePending();
        if (pending is null)
        {
            return;
        }

        var limit = Ui.ReadInt("Максимум одновременно обрабатываемых заказов", 1, 20, 3);
        if (limit is null)
        {
            return;
        }

        await using var cancellation = ForegroundCancellation.Start();
        var batch = await shop.Batch.ProcessWithLimitAsync(pending, limit.Value, cancellation.Token, new ConsoleProgress(verbose: false));
        Ui.PrintBatch(batch);
        Ui.Explain($"SemaphoreSlim({limit}) пропускает не более {limit} заказов одновременно; остальные асинхронно ждут WaitAsync.");
        Ui.Explain("Release вызывается в finally, поэтому ошибки и отмена не «съедают» разрешения семафора.");
    }

    // 6
    public async Task CompareSequentialAndParallelAsync()
    {
        Ui.Header("Последовательная и параллельная обработка одинакового набора заказов");
        var count = Ui.ReadInt("Количество независимых заказов", 2, 50, 8);
        if (count is null)
        {
            return;
        }

        Ui.Explain("Для каждого режима создаётся отдельный изолированный магазин и копия одного и того же набора заказов.");
        await using var cancellation = ForegroundCancellation.Start();

        var template = DemoOrderFactory.CreateSuccessfulOrders(count.Value, CreateIdSequence(1000));
        var runs = new List<BatchResult>();
        foreach (var mode in new[] { "seq", "par", "lim" })
        {
            var env = new ShopEnvironment(shop.Options);
            var copy = template.Select(o => DemoOrderFactory.CloneAsNew(o, o.Id)).ToArray();
            runs.Add(mode switch
            {
                "seq" => await env.Batch.ProcessSequentialAsync(copy, cancellation.Token),
                "par" => await env.Batch.ProcessParallelAsync(copy, cancellation.Token),
                _ => await env.Batch.ProcessWithLimitAsync(copy, 3, cancellation.Token),
            });
        }

        Ui.WriteLine($"{"Режим",-32} | {"Время, мс",9} | {"Успешно",7} | {"Отменено",8} | {"Макс. одновр.",13}");
        Ui.WriteLine(new string('-', 82));
        foreach (var run in runs)
        {
            Ui.WriteLine($"{run.Mode,-32} | {run.Elapsed.TotalMilliseconds,9:0} | {run.Completed,7} | {run.Cancelled,8} | {run.MaxObservedConcurrency,13}");
        }

        var sequential = runs[0].Elapsed.TotalMilliseconds;
        foreach (var run in runs.Skip(1).Where(r => r.Elapsed.TotalMilliseconds > 0))
        {
            Ui.Info($"Отношение времени «последовательно / {run.Mode}» в этом запуске: {sequential / run.Elapsed.TotalMilliseconds:0.00}");
        }

        Ui.Explain("Этапы обработки — это ожидание (I/O-bound, Task.Delay), а не вычисления, поэтому одновременное ожидание");
        Ui.Explain("многих заказов сокращает общее время. Цифры зависят от машины, нагрузки и планировщика и не гарантируются.");
        Ui.Explain("Для CPU-bound вычислений выигрыш ограничен числом ядер — см. пункт 18.");
    }

    // 7
    public async Task CancellationMenuAsync()
    {
        Ui.Header("Отмена выполняющейся обработки");
        Ui.Info("1 — запустить фоновую обработку ожидающих заказов (затем можно вернуться в меню)");
        Ui.Info("2 — отменить фоновую обработку");
        Ui.Info("3 — отмена по тайм-ауту (CancellationTokenSource.CancelAfter) на изолированном наборе");
        Ui.Info("0 — назад");
        switch (Ui.ReadInt("Вариант", 0, 3, 0))
        {
            case 1:
                StartBackgroundJob();
                break;
            case 2:
                await CancelBackgroundJobAsync();
                break;
            case 3:
                await TimeoutCancellationDemoAsync();
                break;
        }

        Ui.Explain("Отмена в .NET кооперативная: токен лишь сообщает о запросе отмены. Код сам проверяет его");
        Ui.Explain("(ThrowIfCancellationRequested, Task.Delay(…, token)) и корректно завершает работу. Поток не «убивается».");
    }

    // 8, 9, 10
    public void ShowByStatus(OrderStatus status, string title)
    {
        Ui.Header(title);
        Ui.PrintOrders(shop.Repository.GetByStatus(status), $"Заказов в статусе {status} нет.");
    }

    // 11
    public async Task RetryAsync()
    {
        Ui.Header("Повторная обработка неуспешных заказов");
        var retry = new List<Order>();
        foreach (var order in shop.Repository.GetAll().Where(o => o.Status is OrderStatus.Failed or OrderStatus.Cancelled))
        {
            if (order.TryResetForRetry(out var reason))
            {
                retry.Add(order);
            }
            else
            {
                Ui.Warning($"  #{order.Id} не повторяется: {reason}");
            }
        }

        if (retry.Count == 0)
        {
            Ui.Warning("Нет заказов, подходящих для повтора (временные ошибки и отменённые заказы).");
            return;
        }

        Ui.Info($"Возвращено в очередь: {string.Join(", ", retry.Select(o => "#" + o.Id))}");
        await using var cancellation = ForegroundCancellation.Start();
        var batch = await shop.Batch.ProcessWithLimitAsync(retry, 3, cancellation.Token, new ConsoleProgress(verbose: true));
        Ui.PrintBatch(batch);
        Ui.Explain("Выполненные ранее этапы (резерв, оплата) не повторяются — деньги не списываются дважды.");
    }

    // 12
    public void ShowStatistics()
    {
        Ui.Header("Статистика обработки");
        var counts = shop.Repository.CountByStatus();
        Ui.Section("Фактические статусы заказов");
        foreach (var (status, count) in counts)
        {
            Ui.WriteColored(Ui.StatusColor(status), $"  {status,-10}: {count}");
        }

        Ui.Info($"  Всего заказов: {shop.Repository.Count}");

        var stats = shop.Statistics;
        Ui.Section("Счётчики попыток (Interlocked)");
        Ui.Info($"  Запущено обработок: {stats.Started}; успешно: {stats.Completed}; с ошибкой: {stats.Failed}; " +
                $"отменено: {stats.Cancelled}; пропущено (повторный захват): {stats.Skipped}");
        Ui.Info($"  Сейчас в обработке: {stats.InFlight}; макс. одновременно в последнем пакете: {stats.MaxObservedConcurrency}");
        var finished = stats.Completed + stats.Failed + stats.Cancelled;
        if (finished > 0)
        {
            Ui.Info($"  Суммарное время обработки: {stats.TotalProcessingTime.TotalMilliseconds:0} мс");
        }

        foreach (var (stage, count) in stats.FailuresByStage)
        {
            Ui.Info($"  Ошибок на этапе {stage}: {count}");
        }

        Ui.Section("Службы");
        Ui.Info($"  Каталог (ValueTask): попаданий в кэш {shop.Catalog.CacheHits}, обращений к «удалённому» каталогу {shop.Catalog.CacheMisses}");
        Ui.Info($"  Успешных списаний: {shop.Payment.SuccessfulCharges}; отправленных писем: {shop.Notifications.Outbox.Count}");

        var completedByStatus = counts[OrderStatus.Completed];
        var consistent = completedByStatus == stats.Completed;
        var message = $"  Согласованность: Completed по статусам = {completedByStatus}, по счётчику = {stats.Completed}";
        if (consistent)
        {
            Ui.Success(message + " — совпадает");
        }
        else
        {
            Ui.Warning(message + " — расхождение (идёт обработка?)");
        }

        Ui.Section("Последние события журнала (ConcurrentQueue)");
        foreach (var e in shop.Journal.Tail(10))
        {
            Ui.Info("  " + e);
        }
    }

    /// <summary>Вызывается в каждом цикле меню: сообщает о завершении фоновой обработки и освобождает ресурсы.</summary>
    public async Task ReportCompletedBackgroundJobAsync()
    {
        if (_background is { Task.IsCompleted: true } job)
        {
            _background = null;
            Ui.Section("Фоновая обработка завершена");
            await ObserveAsync(job);
        }
    }

    /// <summary>Завершение приложения: отменяем и дожидаемся фоновой обработки, чтобы не оставлять задач.</summary>
    public async Task ShutdownAsync()
    {
        if (_background is { } job)
        {
            _background = null;
            if (!job.Task.IsCompleted)
            {
                Ui.Warning("Отмена фоновой обработки перед выходом...");
                job.Cancellation.Cancel();
            }

            await ObserveAsync(job);
        }
    }

    private void StartBackgroundJob()
    {
        if (HasRunningBackgroundJob)
        {
            Ui.Warning("Фоновая обработка уже выполняется.");
            return;
        }

        var pending = RequirePending();
        if (pending is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        // Задача не ожидается здесь, но сохраняется и обязательно наблюдается позже (ReportCompleted/Cancel/Shutdown).
        var task = shop.Batch.ProcessWithLimitAsync(pending, 2, cts.Token);
        _background = new BackgroundJob(task, cts);
        Ui.Success($"Фоновая обработка запущена: {pending.Count} заказов, не более 2 одновременно.");
        Ui.Explain("Вернитесь в меню: пункт 1 покажет заказы в статусе Processing, пункт 7 → 2 отменит обработку.");
    }

    private async Task CancelBackgroundJobAsync()
    {
        if (_background is not { } job)
        {
            Ui.Warning("Нет выполняющейся фоновой обработки. Запустите её (пункт 7 → 1).");
            return;
        }

        _background = null;
        if (job.Task.IsCompleted)
        {
            Ui.Warning("Фоновая обработка уже завершилась — отменять нечего.");
        }
        else
        {
            Ui.Warning("Запрошена отмена фоновой обработки...");
            job.Cancellation.Cancel();
        }

        await ObserveAsync(job);
        Ui.Explain("Завершённые заказы сохранили статус Completed; прерванные получили Cancelled; ожидавшие слота не запускались.");
    }

    private static async Task ObserveAsync(BackgroundJob job)
    {
        try
        {
            Ui.PrintBatch(await job.Task);
        }
        catch (Exception ex)
        {
            Ui.Error($"Фоновая обработка завершилась исключением {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            job.Cancellation.Dispose();
        }
    }

    private async Task TimeoutCancellationDemoAsync()
    {
        var timeout = Ui.ReadInt("Тайм-аут, мс", 50, 5000, 700);
        if (timeout is null)
        {
            return;
        }

        var env = new ShopEnvironment(shop.Options);
        var orders = DemoOrderFactory.CreateSuccessfulOrders(10, env.Repository.NextId);
        orders.ToList().ForEach(o => env.Repository.TryAdd(o));

        // using гарантирует Dispose источника (и его внутреннего таймера) после завершения работы.
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeout.Value));
        var batch = await env.Batch.ProcessWithLimitAsync(orders, 4, cts.Token, new ConsoleProgress(verbose: false));
        Ui.PrintBatch(batch);
        Ui.PrintOrders(env.Repository.GetAll());
        Ui.Explain("CancelAfter запускает таймер; по его срабатыванию Task.Delay(…, token) прерывает ожидание,");
        Ui.Explain("обработчик ловит OperationCanceledException и переводит заказ в Cancelled. Резерв без оплаты возвращается на склад.");
    }

    private IReadOnlyList<Order>? RequirePending()
    {
        var pending = shop.Repository.GetByStatus(OrderStatus.Pending);
        if (pending.Count > 0)
        {
            return pending;
        }

        Ui.Warning("Нет заказов в статусе Pending. Создайте заказы (пункт 2) или повторите неуспешные (пункт 11).");
        return null;
    }

    private static Func<int> CreateIdSequence(int start)
    {
        var next = start;
        return () => Interlocked.Increment(ref next);
    }

    private sealed record BackgroundJob(Task<BatchResult> Task, CancellationTokenSource Cancellation);
}
