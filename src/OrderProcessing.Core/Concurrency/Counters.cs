namespace OrderProcessing.Core.Concurrency;

/// <summary>Счётчик, к которому одновременно обращаются несколько потоков.</summary>
public interface ICounter
{
    string Name { get; }
    long Value { get; }
    void Increment();
}

/// <summary>
/// Небезопасный счётчик: <c>_value++</c> — это три шага (прочитать, увеличить, записать).
/// Два потока могут прочитать одно и то же значение, и одно увеличение «потеряется».
/// </summary>
public sealed class UnsafeCounter : ICounter
{
    private long _value;
    public string Name => "без синхронизации (value++)";
    public long Value => Volatile.Read(ref _value);
    public void Increment() => _value++;
}

/// <summary>Счётчик под <c>lock</c> с отдельным приватным объектом блокировки.</summary>
public sealed class LockCounter : ICounter
{
    private readonly object _sync = new();
    private long _value;
    public string Name => "lock";

    public long Value
    {
        get
        {
            lock (_sync)
            {
                return _value;
            }
        }
    }

    public void Increment()
    {
        lock (_sync)
        {
            _value++;
        }
    }
}

/// <summary>
/// То же, что <see cref="LockCounter"/>, но явно через <see cref="Monitor"/>.
/// Компилятор разворачивает <c>lock (x) { ... }</c> именно в такой код:
/// <c>Monitor.Enter(x, ref lockTaken)</c> и <c>Monitor.Exit(x)</c> в <c>finally</c>.
/// </summary>
public sealed class MonitorCounter : ICounter
{
    private readonly object _sync = new();
    private long _value;
    public string Name => "Monitor.Enter/Exit";

    public long Value
    {
        get
        {
            var lockTaken = false;
            try
            {
                Monitor.Enter(_sync, ref lockTaken);
                return _value;
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_sync);
                }
            }
        }
    }

    public void Increment()
    {
        var lockTaken = false;
        try
        {
            Monitor.Enter(_sync, ref lockTaken);
            _value++;
        }
        finally
        {
            // Освобождение гарантировано даже при исключении внутри критической секции.
            if (lockTaken)
            {
                Monitor.Exit(_sync);
            }
        }
    }

    /// <summary>Попытка войти с тайм-аутом — возможность Monitor, которой нет у оператора lock.</summary>
    public bool TryIncrement(TimeSpan timeout)
    {
        var lockTaken = false;
        try
        {
            Monitor.TryEnter(_sync, timeout, ref lockTaken);
            if (!lockTaken)
            {
                return false;
            }

            _value++;
            return true;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_sync);
            }
        }
    }

    /// <summary>Выполняет действие, удерживая блокировку (для демонстрации TryEnter из другого потока).</summary>
    public void HoldLock(Action whileHeld)
    {
        ArgumentNullException.ThrowIfNull(whileHeld);
        var lockTaken = false;
        try
        {
            Monitor.Enter(_sync, ref lockTaken);
            whileHeld();
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_sync);
            }
        }
    }
}

/// <summary>Атомарный счётчик: <see cref="Interlocked.Increment(ref long)"/> выполняет чтение-изменение-запись одной инструкцией процессора.</summary>
public sealed class InterlockedCounter : ICounter
{
    private long _value;
    public string Name => "Interlocked.Increment";
    public long Value => Interlocked.Read(ref _value);
    public void Increment() => Interlocked.Increment(ref _value);
}

public static class CounterStress
{
    /// <summary>
    /// Запускает <paramref name="threadCount"/> выделенных потоков <see cref="Thread"/>,
    /// каждый выполняет <paramref name="incrementsPerThread"/> увеличений. Все потоки
    /// дожидаются через Join, поэтому после возврата ни один поток не остаётся работать.
    /// </summary>
    public static long Run(ICounter counter, int threadCount, int incrementsPerThread)
    {
        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                start.Wait(); // все потоки стартуют одновременно — так гонка проявляется чаще
                for (var i = 0; i < incrementsPerThread; i++)
                {
                    counter.Increment();
                }
            })
            {
                IsBackground = true,
                Name = $"counter-{t}",
            };
            threads[t].Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        return counter.Value;
    }
}
