using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Concurrency;
using OrderProcessing.Core.Processing;

namespace OrderProcessing.App.Demos;

/// <summary>Пункт 17: lock, Monitor, Interlocked, SemaphoreSlim, Mutex.</summary>
internal static class SynchronizationDemos
{
    private const int Threads = 8;
    private const int IncrementsPerThread = 200_000;

    public static async Task RunAsync()
    {
        Ui.Header("Примитивы синхронизации");
        Ui.Info("1 — lock      2 — Monitor      3 — Interlocked      4 — SemaphoreSlim      5 — Mutex      6 — все      0 — назад");
        var choice = Ui.ReadInt("Вариант", 0, 6, 6);
        if (choice is null or 0)
        {
            return;
        }

        if (choice is 1 or 6)
        {
            LockDemo();
        }

        if (choice is 2 or 6)
        {
            MonitorDemo();
        }

        if (choice is 3 or 6)
        {
            InterlockedDemo();
        }

        if (choice is 4 or 6)
        {
            await SemaphoreDemoAsync();
        }

        if (choice is 5 or 6)
        {
            MutexDemo();
        }
    }

    private static void LockDemo()
    {
        Ui.Section("lock");
        var expected = (long)Threads * IncrementsPerThread;
        var unsafeValue = CounterStress.Run(new UnsafeCounter(), Threads, IncrementsPerThread);
        var lockValue = CounterStress.Run(new LockCounter(), Threads, IncrementsPerThread);
        Ui.Info($"  {Threads} потоков × {IncrementsPerThread:N0} увеличений, ожидается {expected:N0}");
        Ui.Warning($"  без синхронизации: {unsafeValue:N0} (потеряно {expected - unsafeValue:N0})");
        Ui.Success($"  lock:              {lockValue:N0}");
        Ui.Explain("lock защищает общее изменяемое состояние; объект блокировки — приватное поле, а не this, тип или строка.");
        Ui.Explain("Внутри lock нельзя использовать await (компилятор запрещает): поток-владелец должен сам освободить блокировку.");
        Ui.Explain("В приложении lock защищает остатки на складе (InventoryService) и состояние заказа (Order).");
    }

    private static void MonitorDemo()
    {
        Ui.Section("Monitor.Enter / Monitor.Exit");
        var counter = new MonitorCounter();
        var value = CounterStress.Run(counter, Threads, IncrementsPerThread);
        Ui.Success($"  Monitor: {value:N0} из {(long)Threads * IncrementsPerThread:N0}");

        // TryEnter с тайм-аутом: второй поток не ждёт бесконечно, пока первый удерживает блокировку.
        using var held = new ManualResetEventSlim();
        var holder = new Thread(() => counter.HoldLock(() =>
        {
            held.Set();
            Thread.Sleep(500); // поток-владелец занят под блокировкой
        }))
        { Name = "holder" };
        holder.Start();
        held.Wait(); // блокировка гарантированно захвачена другим потоком
        var entered = counter.TryIncrement(TimeSpan.FromMilliseconds(100));
        Ui.Info($"  Monitor.TryEnter(100 мс), пока другой поток держит блокировку 500 мс: {(entered ? "вошли" : "не удалось — тайм-аут")}");
        holder.Join();
        Ui.Info($"  После освобождения: TryEnter → {(counter.TryIncrement(TimeSpan.FromMilliseconds(100)) ? "вошли" : "не удалось")}");
        Ui.Explain("lock (x) { … } компилируется в Monitor.Enter(x, ref lockTaken) + try/finally с Monitor.Exit(x).");
        Ui.Explain("Monitor дополнительно даёт TryEnter с тайм-аутом и Wait/Pulse для ожидания условий.");
    }

    private static void InterlockedDemo()
    {
        Ui.Section("Interlocked");
        var value = CounterStress.Run(new InterlockedCounter(), Threads, IncrementsPerThread);
        Ui.Success($"  Interlocked.Increment: {value:N0} из {(long)Threads * IncrementsPerThread:N0}");

        var maximum = 0;
        Parallel.For(0, 10_000, i =>
        {
            var candidate = (i * 7919) % 10_007;
            int observed;
            do
            {
                observed = Volatile.Read(ref maximum);
                if (candidate <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref maximum, candidate, observed) != observed);
        });
        Ui.Info($"  Максимум через цикл CompareExchange: {maximum} (так считается «макс. одновременно» в статистике)");
        Ui.Explain("value++ — это чтение, сложение и запись; другой поток может вклиниться между ними.");
        Ui.Explain("Interlocked выполняет чтение-изменение-запись одной атомарной инструкцией без блокировки — быстро, но только для одной переменной.");
    }

    private static async Task SemaphoreDemoAsync()
    {
        Ui.Section("SemaphoreSlim: не более 3 операций одновременно");
        using var limiter = new ConcurrencyLimiter(3);
        using var cts = new CancellationTokenSource();
        var current = 0;
        var peak = 0;

        async Task<int> OperationAsync(int n, CancellationToken token)
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            try
            {
                await Task.Delay(100, token);
                if (n == 4)
                {
                    throw new InvalidOperationException("сбой операции 4 (эмуляция)");
                }

                if (n == 7)
                {
                    cts.Cancel(); // операция 7 отменяет оставшиеся
                    token.ThrowIfCancellationRequested();
                }

                return n;
            }
            finally
            {
                Interlocked.Decrement(ref current);
            }
        }

        var tasks = Enumerable.Range(1, 12)
            .Select(n => limiter.RunAsync(token => OperationAsync(n, token), cts.Token))
            .ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            Ui.Info($"  WhenAll завершился исключением {ex.GetType().Name} (ожидаемо: одна операция падает, затем пакет отменяется)");
        }

        Ui.Info($"  Успешно: {tasks.Count(t => t.IsCompletedSuccessfully)}, с ошибкой: {tasks.Count(t => t.IsFaulted)}, отменено: {tasks.Count(t => t.IsCanceled)}");
        Ui.Success($"  Пиковое число одновременных операций: {peak} (лимит 3)");
        Ui.Success($"  Свободных слотов после ошибок и отмены: {limiter.AvailableSlots} из 3 — утечки разрешений нет");
        Ui.Explain("WaitAsync ждёт асинхронно; Release вызывается в finally. При отмене во время ожидания разрешение не выдаётся и не освобождается.");
    }

    private static void MutexDemo()
    {
        Ui.Section("Mutex: общий файл-счётчик номеров чеков");
        var path = Path.Combine(Path.GetTempPath(), $"order-lab3-receipts-{Environment.ProcessId}.txt");
        const string mutexName = "OrderProcessingLab3.ReceiptCounter";
        MutexFileCounter counter;
        try
        {
            counter = new MutexFileCounter(path, mutexName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException or PlatformNotSupportedException)
        {
            Ui.Warning($"  Именованный мьютекс недоступен ({ex.GetType().Name}: {ex.Message}); используется локальный.");
            counter = new MutexFileCounter(path, mutexName: null);
        }

        using (counter)
        {
            Ui.Info($"  Мьютекс «{mutexName}»: {(counter.IsNamed ? "именованный" : "безымянный")}, создан этим процессом: {counter.CreatedNew}");
            const int threadCount = 4;
            const int perThread = 25;
            var threads = Enumerable.Range(1, threadCount).Select(t => new Thread(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    counter.NextNumber(TimeSpan.FromSeconds(5));
                }
            })
            { Name = $"receipt-{t}" }).ToArray();

            foreach (var thread in threads)
            {
                thread.Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }

            Ui.Success($"  {threadCount} потока × {perThread} номеров → в файле {counter.ReadCurrent()} (ожидается {threadCount * perThread})");
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Ui.Warning($"  Не удалось удалить временный файл {path}: {ex.Message}");
        }

        Ui.Explain("Mutex — объект ядра ОС: медленнее lock, но именованный мьютекс синхронизирует разные процессы.");
        Ui.Explain("Мьютекс принадлежит потоку: ReleaseMutex вызывает тот же поток, поэтому между WaitOne и ReleaseMutex нет await.");
        Ui.Explain("Именование различается: на Windows доступны префиксы Global\\ и Local\\; на Linux/macOS .NET реализует");
        Ui.Explain("именованные мьютексы через разделяемые файлы во временном каталоге. Используется имя без префикса (область сеанса).");
    }

    private static void InterlockedMax(ref int target, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, observed) != observed);
    }
}
