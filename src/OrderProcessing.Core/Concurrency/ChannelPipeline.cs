using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace OrderProcessing.Core.Concurrency;

/// <summary>
/// Асинхронная схема «производитель — потребители» на <see cref="Channel{T}"/>.
/// Ограниченный канал (<see cref="BoundedChannelFullMode.Wait"/>) создаёт обратное давление
/// (backpressure): <c>WriteAsync</c> асинхронно ждёт свободного места, не блокируя поток.
/// Потребители читают через <c>ReadAllAsync</c>, который завершается после
/// <c>Writer.Complete()</c> и опустошения канала.
/// </summary>
public static class ChannelPipeline
{
    public static async Task<PipelineReport<WorkItemResult<TItem, TResult>>> RunAsync<TItem, TResult>(
        IEnumerable<TItem> source,
        int capacity,
        int consumerCount,
        Func<TItem, int, CancellationToken, Task<TResult>> process,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(consumerCount);
        ArgumentNullException.ThrowIfNull(process);

        var stopwatch = Stopwatch.StartNew();
        var channel = Channel.CreateBounded<TItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = consumerCount == 1,
        });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var results = new ConcurrentQueue<WorkItemResult<TItem, TResult>>();
        var produced = 0;
        var producerWaits = 0;

        async Task ConsumeAsync(int consumerId)
        {
            try
            {
                await foreach (var item in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var result = await process(item, consumerId, token).ConfigureAwait(false);
                    results.Enqueue(new WorkItemResult<TItem, TResult>(item, result, consumerId));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Сбой потребителя: отменяем производителя и остальных, исключение пробрасываем.
                linked.Cancel();
                throw;
            }
        }

        async Task ProduceAsync()
        {
            try
            {
                foreach (var item in source)
                {
                    if (!channel.Writer.TryWrite(item))
                    {
                        producerWaits++;                                         // канал заполнен
                        await channel.Writer.WriteAsync(item, token).ConfigureAwait(false); // backpressure
                    }

                    produced++;
                }
            }
            finally
            {
                // Сигнал «данных больше не будет» — без него ReadAllAsync ждал бы вечно.
                channel.Writer.TryComplete();
            }
        }

        var consumers = Enumerable.Range(1, consumerCount).Select(ConsumeAsync).ToArray();
        var producer = ProduceAsync();

        await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false);
        return new PipelineReport<WorkItemResult<TItem, TResult>>(results.ToArray(), produced, producerWaits, stopwatch.Elapsed);
    }
}
