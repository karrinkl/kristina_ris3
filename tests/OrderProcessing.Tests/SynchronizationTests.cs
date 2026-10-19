using OrderProcessing.Core.Concurrency;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;

namespace OrderProcessing.Tests;

public class SynchronizationTests
{
    private const int Threads = 8;
    private const int Increments = 50_000;

    public static TheoryData<ICounter> SafeCounters => new() { new LockCounter(), new MonitorCounter(), new InterlockedCounter() };

    [Theory]
    [MemberData(nameof(SafeCounters))]
    public void SafeCounters_DoNotLoseIncrements(ICounter counter) =>
        Assert.Equal((long)Threads * Increments, CounterStress.Run(counter, Threads, Increments));

    [Fact]
    public void MonitorTryEnter_TimesOut_WhileAnotherThreadHoldsLock()
    {
        var counter = new MonitorCounter();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Поток удерживает блокировку счётчика до сигнала теста.
        var holder = new Thread(() => counter.HoldLock(() =>
        {
            held.Set();
            release.Wait();
        }));
        holder.Start();
        held.Wait();

        Assert.False(counter.TryIncrement(TimeSpan.FromMilliseconds(20)));

        release.Set();
        holder.Join();
        Assert.True(counter.TryIncrement(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public void ProcessingStatistics_CountersAreThreadSafe()
    {
        var stats = new ProcessingStatistics();
        const int n = 10_000;

        Parallel.For(0, n, i =>
        {
            stats.OnStarted();
            var outcome = (i % 3) switch
            {
                0 => ProcessingOutcome.Completed,
                1 => ProcessingOutcome.Failed,
                _ => ProcessingOutcome.Cancelled,
            };
            stats.OnFinished(new OrderProcessingResult(i, outcome, OrderStatus.Completed,
                outcome == ProcessingOutcome.Failed ? ProcessingStage.Payment : ProcessingStage.None,
                "", TimeSpan.FromTicks(10), 1));
        });

        Assert.Equal(n, stats.Started);
        Assert.Equal(3_334, stats.Completed);
        Assert.Equal(3_333, stats.Failed);
        Assert.Equal(3_333, stats.Cancelled);
        Assert.Equal(0, stats.InFlight);
        Assert.Equal(3_333, stats.FailuresByStage[ProcessingStage.Payment]);
        Assert.Equal(TimeSpan.FromTicks(10L * n), stats.TotalProcessingTime);
        Assert.InRange(stats.MaxObservedConcurrency, 1, n);
    }

    [Fact]
    public void Mutex_ProtectsFileReadModifyWrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lab3-mutex-test-{Guid.NewGuid():N}.txt");
        try
        {
            using var counter = new MutexFileCounter(path, mutexName: null);
            var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    counter.NextNumber(TimeSpan.FromSeconds(10));
                }
            })).ToArray();

            foreach (var t in threads)
            {
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            Assert.Equal(200, counter.ReadCurrent());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
