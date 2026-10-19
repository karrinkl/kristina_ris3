using System.Collections.Concurrent;
using System.Diagnostics;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Analytics;
using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Services;

namespace OrderProcessing.App.Demos;

/// <summary>Пункт 18: потоки, пул потоков, Task.Run/Task.Delay, ValueTask, Parallel.For, PLINQ.</summary>
internal static class AsyncParallelDemos
{
    public static async Task RunAsync(SimulationOptions options)
    {
        Ui.Header("Асинхронность и параллелизм");
        Ui.Info("1 — Thread, ThreadPool и Task");
        Ui.Info("2 — Task.Run (CPU-bound) и Task.Delay / асинхронный ввод-вывод (I/O-bound)");
        Ui.Info("3 — ValueTask<T>: кэшированный результат без лишних аллокаций");
        Ui.Info("4 — Parallel.For и PLINQ на CPU-bound вычислениях (Stopwatch)");
        Ui.Info("5 — async/await сам по себе не означает параллельности");
        Ui.Info("6 — определения: асинхронность, конкурентность, параллелизм");
        Ui.Info("7 — всё подряд");
        Ui.Info("0 — назад");
        var choice = Ui.ReadInt("Вариант", 0, 7, 7);
        if (choice is null or 0)
        {
            return;
        }

        if (choice is 1 or 7)
        {
            await ThreadsAndPoolAsync();
        }

        if (choice is 2 or 7)
        {
            await TaskRunVersusDelayAsync();
        }

        if (choice is 3 or 7)
        {
            await ValueTaskDemoAsync(options);
        }

        if (choice is 4 or 7)
        {
            await ParallelCpuDemoAsync();
        }

        if (choice is 5 or 7)
        {
            await AsyncIsNotParallelAsync();
        }

        if (choice is 6 or 7)
        {
            Concepts();
        }
    }

    private static async Task ThreadsAndPoolAsync()
    {
        Ui.Section("Thread");
        long checksum = 0;
        var worker = new Thread(() =>
        {
            Ui.Info($"  Выделенный поток: Id={Environment.CurrentManagedThreadId}, имя «{Thread.CurrentThread.Name}», из пула: {Thread.CurrentThread.IsThreadPoolThread}");
            var orders = DemoOrderFactory.CreateSuccessfulOrders(50, Sequence());
            checksum = OrderAnalytics.ComputeSequential(orders, 50_000).Sum();
        })
        {
            Name = "order-report-thread",
            IsBackground = true, // фоновый поток не удерживает процесс при выходе
        };
        worker.Start();
        worker.Join(); // дожидаемся завершения — поток не остаётся работать
        Ui.Info($"  Поток завершён (IsAlive={worker.IsAlive}), контрольная сумма отчёта: {checksum}");

        const int count = 200;
        var stopwatch = Stopwatch.StartNew();
        var threads = new Thread[count];
        for (var i = 0; i < count; i++)
        {
            threads[i] = new Thread(() => Thread.SpinWait(1_000));
            threads[i].Start();
        }

        foreach (var t in threads)
        {
            t.Join();
        }

        var threadTime = stopwatch.Elapsed;

        Ui.Section("ThreadPool");
        var usedThreads = new ConcurrentDictionary<int, byte>();
        stopwatch.Restart();
        using (var done = new CountdownEvent(count))
        {
            for (var i = 0; i < count; i++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.SpinWait(1_000);
                    usedThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
                    done.Signal();
                });
            }

            done.Wait(); // ждём все рабочие элементы, прежде чем освободить CountdownEvent
        }

        var poolTime = stopwatch.Elapsed;
        ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
        ThreadPool.GetAvailableThreads(out var availableWorkers, out _);
        Ui.Info($"  {count} новых Thread: {threadTime.TotalMilliseconds:0.0} мс; {count} элементов ThreadPool: {poolTime.TotalMilliseconds:0.0} мс");
        Ui.Info($"  Элементы пула выполнили {usedThreads.Count} разных потоков (потоки переиспользуются)");
        Ui.Info($"  Пул: потоков сейчас {ThreadPool.ThreadCount}, минимум рабочих {minWorkers}, I/O {minIo}, доступно рабочих {availableWorkers}; ядер: {Environment.ProcessorCount}");

        Ui.Section("Task");
        var runThread = await Task.Run(() => (Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
        Ui.Info($"  Task.Run выполнился на потоке {runThread.Item1}, из пула: {runThread.Item2}");
        var delays = Enumerable.Range(0, 1_000).Select(_ => Task.Delay(300)).ToArray();
        Ui.Info($"  Запущено 1000 Task.Delay(300); потоков в пуле: {ThreadPool.ThreadCount}");
        await Task.WhenAll(delays);
        Ui.Info($"  Все 1000 ожиданий завершены; потоков в пуле: {ThreadPool.ThreadCount}");
        Ui.Explain("Создание Thread дорого (стек ~1 МБ, системный вызов); пул повторно использует готовые потоки.");
        Ui.Explain("Task — это обещание результата, а не поток: Task.Run ставит работу в пул, а Task.Delay вообще не занимает поток.");
        Ui.Explain("Планировщик сам выбирает поток для задачи, поэтому Task не гарантирует выполнение на отдельном физическом потоке.");
    }

    private static async Task TaskRunVersusDelayAsync()
    {
        Ui.Section("Task.Run для CPU-bound");
        var orders = DemoOrderFactory.CreateSuccessfulOrders(120, Sequence());
        var caller = Environment.CurrentManagedThreadId;
        var stopwatch = Stopwatch.StartNew();
        var (scores, worker) = await Task.Run(() => (OrderAnalytics.ComputeSequential(orders, 100_000), Environment.CurrentManagedThreadId));
        Ui.Info($"  Расчёт {orders.Count} оценок риска занял {stopwatch.ElapsedMilliseconds} мс на потоке {worker} (вызывающий поток {caller}); сумма {scores.Sum()}");
        Ui.Explain("Task.Run переносит вычисления в пул, чтобы не занимать вызывающий поток (в GUI — поток интерфейса, здесь — цикл меню).");

        Ui.Section("Task.Delay и асинхронный ввод-вывод — без Task.Run");
        stopwatch.Restart();
        await Task.Delay(200);
        Ui.Info($"  await Task.Delay(200): {stopwatch.ElapsedMilliseconds} мс; в это время ни один поток не ждал — сработал таймер");

        var path = OrderFileStore.DefaultPath;
        if (File.Exists(path))
        {
            stopwatch.Restart();
            var dtos = await OrderFileStore.LoadAsync(path);
            Ui.Info($"  Асинхронное чтение data/orders.json: {dtos.Count} заказов за {stopwatch.ElapsedMilliseconds} мс");
        }

        Ui.Explain("Оборачивать уже асинхронный ввод-вывод в Task.Run не нужно: это лишь займёт поток пула, который будет ждать.");
        Ui.Explain("Task.Delay — асинхронная пауза; Thread.Sleep блокирует поток и в асинхронном коде не используется.");
    }

    private static async Task ValueTaskDemoAsync(SimulationOptions options)
    {
        Ui.Section("ValueTask<T>");
        var catalog = new ProductCatalog(options);
        foreach (var sku in new[] { "KB-01", "MS-02", "KB-01", "KB-01", "MS-02" })
        {
            var stopwatch = Stopwatch.StartNew();
            ValueTask<Product?> pending = catalog.FindAsync(sku, CancellationToken.None);
            var synchronous = pending.IsCompletedSuccessfully; // проверка состояния допустима до единственного await
            var product = await pending;                        // ValueTask ожидается ровно один раз
            Ui.Info($"  {sku}: {product?.Name,-12} завершён синхронно: {synchronous,-5} {stopwatch.Elapsed.TotalMilliseconds,7:0.0} мс");
        }

        Ui.Info($"  Попаданий в кэш: {catalog.CacheHits}, обращений к «удалённому» каталогу: {catalog.CacheMisses}");

        // Если результат нужен нескольким потребителям или для WhenAll, ValueTask преобразуют в Task.
        var many = await Task.WhenAll(new[] { "SSD-1T", "HS-05" }.Select(sku => catalog.FindAsync(sku, CancellationToken.None).AsTask()));
        Ui.Info($"  Через AsTask() + WhenAll: {string.Join(", ", many.Select(p => p?.Name))}");
        Ui.Explain("ValueTask полезен, когда результат часто доступен сразу: на быстром пути не создаётся объект Task.");
        Ui.Explain("Ограничения: ожидать только один раз, не ждать одновременно из нескольких мест, не читать Result до завершения;");
        Ui.Explain("для повторного использования — AsTask(). Это не универсальная замена Task<T>: структура больше и легко ошибиться.");
    }

    private static async Task ParallelCpuDemoAsync()
    {
        Ui.Section("CPU-bound: последовательно, Parallel.For, PLINQ");
        var orders = DemoOrderFactory.CreateSuccessfulOrders(400, Sequence());
        const int rounds = 400_000;

        // Тяжёлые вычисления выполняются в пуле (Task.Run), а не в потоке цикла меню.
        var report = await Task.Run(() =>
        {
            OrderAnalytics.ComputeSequential(orders, 1_000); // прогрев JIT, чтобы не учитывать компиляцию
            var sw = Stopwatch.StartNew();
            var sequential = OrderAnalytics.ComputeSequential(orders, rounds);
            var tSeq = sw.Elapsed;
            sw.Restart();
            var parallelFor = OrderAnalytics.ComputeParallelFor(orders, rounds);
            var tFor = sw.Elapsed;
            sw.Restart();
            var plinq = OrderAnalytics.ComputePlinq(orders, rounds);
            var tPlinq = sw.Elapsed;
            sw.Restart();
            var aggregated = OrderAnalytics.SumRiskScoresParallel(orders, rounds);
            var tAgg = sw.Elapsed;

            // Небольшая работа: накладные расходы на распараллеливание сопоставимы с самой работой.
            var tiny = orders.Take(8).ToArray();
            sw.Restart();
            OrderAnalytics.ComputeSequential(tiny, 10);
            var tTinySeq = sw.Elapsed;
            sw.Restart();
            OrderAnalytics.ComputeParallelFor(tiny, 10);
            var tTinyPar = sw.Elapsed;

            return (sequential, parallelFor, plinq, aggregated, tSeq, tFor, tPlinq, tAgg, tTinySeq, tTinyPar);
        });

        var same = report.sequential.SequenceEqual(report.parallelFor) && report.sequential.SequenceEqual(report.plinq)
                   && report.sequential.Sum() == report.aggregated;
        Ui.Info($"  {orders.Count} заказов × {rounds:N0} раундов, ядер: {Environment.ProcessorCount}");
        Ui.Info($"  Последовательно:            {report.tSeq.TotalMilliseconds,8:0.0} мс");
        Ui.Info($"  Parallel.For:               {report.tFor.TotalMilliseconds,8:0.0} мс (x{Ratio(report.tSeq, report.tFor):0.00})");
        Ui.Info($"  PLINQ AsParallel/AsOrdered: {report.tPlinq.TotalMilliseconds,8:0.0} мс (x{Ratio(report.tSeq, report.tPlinq):0.00})");
        Ui.Info($"  Parallel.For + localFinally: {report.tAgg.TotalMilliseconds,7:0.0} мс (сумма {report.aggregated})");
        Ui.Info($"  Маленькая задача (8 заказов × 10 раундов): последовательно {report.tTinySeq.TotalMilliseconds:0.000} мс, Parallel.For {report.tTinyPar.TotalMilliseconds:0.000} мс");
        if (same)
        {
            Ui.Success("  Результаты всех способов совпадают.");
        }
        else
        {
            Ui.Error("  Результаты различаются — ошибка в параллельном коде!");
        }

        Ui.Explain("Parallel.For — для индексируемых независимых итераций; PLINQ — для декларативных запросов (Where/Select/Sum).");
        Ui.Explain("Ускорение ограничено числом ядер и долей последовательной работы; на маленьких объёмах его съедают накладные расходы.");
        Ui.Explain("Синхронизация на каждой итерации свела бы выигрыш на нет, поэтому сумма копится локально и сливается один раз на поток.");
    }

    private static async Task AsyncIsNotParallelAsync()
    {
        Ui.Section("Последовательные await против одновременного ожидания");
        var stopwatch = Stopwatch.StartNew();
        await Task.Delay(200);
        await Task.Delay(200);
        await Task.Delay(200);
        Ui.Info($"  Три await подряд: {stopwatch.ElapsedMilliseconds} мс — асинхронно, но последовательно");

        stopwatch.Restart();
        await Task.WhenAll(Task.Delay(200), Task.Delay(200), Task.Delay(200));
        Ui.Info($"  Три задачи + WhenAll: {stopwatch.ElapsedMilliseconds} мс — конкурентное ожидание");

        var caller = Environment.CurrentManagedThreadId;
        var inside = await CpuWorkMarkedAsync();
        Ui.Info($"  async-метод без await внутри выполнился синхронно на вызывающем потоке: {inside == caller} (поток {inside})");
        Ui.Explain("Ключевое слово async не создаёт потоков: метод выполняется синхронно до первого незавершённого await.");
        Ui.Explain("Параллелизм появляется, только если несколько операций запущены до ожидания (WhenAll) или работа передана в пул (Task.Run, Parallel).");
    }

#pragma warning disable CS1998 // намеренно: async-метод без await, чтобы показать синхронное выполнение
    private static async Task<int> CpuWorkMarkedAsync()
    {
        OrderAnalytics.ComputeSequential(DemoOrderFactory.CreateSuccessfulOrders(10, Sequence()), 10_000);
        return Environment.CurrentManagedThreadId;
    }
#pragma warning restore CS1998

    private static void Concepts()
    {
        Ui.Section("Определения");
        Ui.Info("  Асинхронность — не блокировать поток во время ожидания (I/O, таймер): поток возвращается в пул, продолжение выполнится позже.");
        Ui.Info("  Конкурентность — несколько операций находятся «в работе» в один промежуток времени (могут чередоваться даже на одном ядре).");
        Ui.Info("  Параллелизм — несколько операций буквально выполняются одновременно на разных ядрах.");
        Ui.Info("  I/O-bound (ожидание службы) → async/await, Task.WhenAll, SemaphoreSlim для ограничения нагрузки.");
        Ui.Info("  CPU-bound (вычисления) → Parallel.For / PLINQ / Task.Run, число ядер — естественный предел ускорения.");
    }

    private static double Ratio(TimeSpan baseline, TimeSpan value) =>
        value.TotalMilliseconds <= 0 ? 0 : baseline.TotalMilliseconds / value.TotalMilliseconds;

    private static Func<int> Sequence()
    {
        var id = 0;
        return () => Interlocked.Increment(ref id);
    }
}
