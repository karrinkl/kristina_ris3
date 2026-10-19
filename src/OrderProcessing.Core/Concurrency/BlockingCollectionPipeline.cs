using System.Collections.Concurrent;
using System.Diagnostics;

namespace OrderProcessing.Core.Concurrency;

/// <summary>
/// Классическая синхронная схема «производитель — потребители» на <see cref="BlockingCollection{T}"/>.
/// <list type="bullet">
/// <item>Ограниченная ёмкость: <c>Add</c> блокирует поток производителя, пока очередь заполнена.</item>
/// <item>Потребители читают <c>GetConsumingEnumerable</c>, который блокирует поток при пустой очереди.</item>
/// <item><c>CompleteAdding</c> в <c>finally</c> гарантирует, что потребители выйдут из цикла,
/// даже если производитель завершился исключением или отменой.</item>
/// </list>
/// Потоки при этом действительно блокируются, поэтому потребители запускаются как
/// <see cref="TaskCreationOptions.LongRunning"/> (выделенные потоки), а не занимают пул.
/// </summary>
public static class BlockingCollectionPipeline
{
    public static async Task<PipelineReport<WorkItemResult<TItem, TResult>>> RunAsync<TItem, TResult>(
        IEnumerable<TItem> source,
        int boundedCapacity,
        int consumerCount,
        Func<TItem, int, TResult> process,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boundedCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consumerCount);
        ArgumentNullException.ThrowIfNull(process);

        var stopwatch = Stopwatch.StartNew();
        using var collection = new BlockingCollection<TItem>(boundedCapacity);
        // Связанный токен: сбой потребителя отменяет остальных и разблокирует производителя.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var results = new ConcurrentQueue<WorkItemResult<TItem, TResult>>();
        var produced = 0;
        var producerWaits = 0;

        var consumers = Enumerable.Range(1, consumerCount).Select(id => Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var item in collection.GetConsumingEnumerable(token))
                {
                    results.Enqueue(new WorkItemResult<TItem, TResult>(item, process(item, id), id));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                linked.Cancel();
                throw;
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var producer = Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (var item in source)
                {
                    if (!collection.TryAdd(item))
                    {
                        producerWaits++;               // очередь заполнена — производитель будет ждать
                        collection.Add(item, token);   // блокирующее ожидание свободного места
                    }

                    produced++;
                }
            }
            finally
            {
                collection.CompleteAdding();
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        // Ожидаем всех: исключения и отмена наблюдаются вызывающим кодом.
        await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false);
        return new PipelineReport<WorkItemResult<TItem, TResult>>(results.ToArray(), produced, producerWaits, stopwatch.Elapsed);
    }
}
