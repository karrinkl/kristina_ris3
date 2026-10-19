using System.Collections.Concurrent;
using System.Threading.Channels;
using OrderProcessing.Core.Concurrency;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Tests.Infrastructure;

namespace OrderProcessing.Tests;

public class ConcurrentCollectionTests
{
    [Fact]
    public void Repository_ConcurrentAdds_HaveUniqueIds()
    {
        var repository = new OrderRepository();

        Parallel.For(0, 1_000, _ =>
        {
            var id = repository.NextId();
            Assert.True(repository.TryAdd(new Order(id, "К", "k@example.test", [new OrderItem("KB-01", "К", 1, 1m)])));
        });

        Assert.Equal(1_000, repository.Count);
        Assert.Equal(Enumerable.Range(1, 1_000), repository.GetAll().Select(o => o.Id));
        Assert.Equal(1_000, repository.CountByStatus()[OrderStatus.Pending]);
    }

    [Fact]
    public void ConcurrentDictionary_AddOrUpdate_IsAtomicPerKey()
    {
        var totals = new ConcurrentDictionary<string, int>();

        Parallel.For(0, 10_000, i => totals.AddOrUpdate($"SKU-{i % 4}", 1, (_, current) => current + 1));

        Assert.Equal(4, totals.Count);
        Assert.All(totals.Values, v => Assert.Equal(2_500, v));
    }

    [Fact]
    public async Task ConcurrentQueueWorkerPool_ProcessesEachItemExactlyOnce()
    {
        var items = Enumerable.Range(1, 200).ToArray();

        var results = await ConcurrentQueueWorkerPool.RunAsync(items, 4, async (item, worker, token) =>
        {
            await Task.Yield();
            return item * 2;
        }).WithTimeout();

        Assert.Equal(items, results.Select(r => r.Item).Order());
        Assert.All(results, r => Assert.Equal(r.Item * 2, r.Result));
        Assert.All(results, r => Assert.InRange(r.WorkerId, 1, 4));
    }

    [Fact]
    public void EventJournal_ConcurrentWriters_KeepCapacity()
    {
        var journal = new OrderEventJournal(capacity: 100);

        Parallel.For(0, 5_000, i =>
            journal.Add(new OrderEvent(DateTimeOffset.Now, i, ProcessingStage.None, OrderEventKind.Step, "e", 0)));

        Assert.InRange(journal.Count, 1, 100);
        Assert.Equal(10, journal.Tail(10).Count);
    }

    [Fact]
    public async Task BlockingCollection_ConsumersFinishAfterCompleteAdding()
    {
        var items = Enumerable.Range(1, 100).ToArray();

        var report = await BlockingCollectionPipeline.RunAsync(items, boundedCapacity: 2, consumerCount: 3,
            (item, consumer) => item + 1000).WithTimeout();

        Assert.Equal(100, report.Produced);
        Assert.Equal(items, report.Results.Select(r => r.Item).Order());
        Assert.All(report.Results, r => Assert.Equal(r.Item + 1000, r.Result));
    }

    [Fact]
    public async Task BlockingCollection_EmptySource_DoesNotHang()
    {
        var report = await BlockingCollectionPipeline.RunAsync(Array.Empty<int>(), 1, 4, (item, _) => item).WithTimeout();

        Assert.Empty(report.Results);
    }

    [Fact]
    public async Task BlockingCollection_ConsumerFailure_PropagatesWithoutHanging()
    {
        var items = Enumerable.Range(1, 1_000);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BlockingCollectionPipeline.RunAsync(items, 1, 2, (item, _) =>
                item == 5 ? throw new InvalidOperationException("сбой потребителя") : item)).WithTimeout();
    }

    [Fact]
    public async Task Channel_ConsumersFinishAfterWriterCompletes()
    {
        var items = Enumerable.Range(1, 100).ToArray();

        var report = await ChannelPipeline.RunAsync(items, capacity: 2, consumerCount: 3, async (item, consumer, token) =>
        {
            await Task.Yield();
            return item * 3;
        }).WithTimeout();

        Assert.Equal(100, report.Produced);
        Assert.Equal(items, report.Results.Select(r => r.Item).Order());
    }

    [Fact]
    public async Task Channel_Cancellation_StopsProducerAndConsumers()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        var pipeline = ChannelPipeline.RunAsync(Enumerable.Range(1, 1_000), 2, 2, async (item, consumer, token) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                cts.Cancel(); // оба потребителя заняты — отменяем
            }

            await gate.Task.WaitAsync(token);
            return item;
        }, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline).WithTimeout();
        Assert.True(pipeline.IsCanceled);
    }

    [Fact]
    public async Task Channel_ConsumerFailure_IsObservedByCaller()
    {
        var pipeline = ChannelPipeline.RunAsync(Enumerable.Range(1, 1_000), 2, 3, (item, consumer, token) =>
            item == 10 ? Task.FromException<int>(new InvalidOperationException("сбой")) : Task.FromResult(item));

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline).WithTimeout();
    }

    [Fact]
    public async Task BoundedChannel_AppliesBackpressure_WhenFull()
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        Assert.True(channel.Writer.TryWrite(1));

        var pendingWrite = channel.Writer.WriteAsync(2);
        Assert.False(pendingWrite.IsCompleted); // производитель ждёт свободного места

        Assert.Equal(1, await channel.Reader.ReadAsync());
        await pendingWrite.AsTask().WithTimeout();
        channel.Writer.Complete();
        Assert.Equal(2, await channel.Reader.ReadAsync());
        await channel.Reader.Completion.WithTimeout();
    }
}
