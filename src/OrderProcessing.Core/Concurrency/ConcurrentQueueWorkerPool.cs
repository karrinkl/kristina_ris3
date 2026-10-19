using System.Collections.Concurrent;

namespace OrderProcessing.Core.Concurrency;

/// <summary>
/// Пул рабочих задач, разбирающих общую <see cref="ConcurrentQueue{T}"/>.
/// Все элементы известны заранее: очередь заполняется до старта работников,
/// каждый работник вызывает <see cref="ConcurrentQueue{T}.TryDequeue"/> до опустошения.
/// TryDequeue атомарен — один элемент получит ровно один работник.
/// Ожидать появления новых элементов ConcurrentQueue не умеет — для этого нужны
/// <see cref="BlockingCollection{T}"/> или <see cref="System.Threading.Channels.Channel{T}"/>.
/// </summary>
public static class ConcurrentQueueWorkerPool
{
    public static async Task<IReadOnlyList<WorkItemResult<TItem, TResult>>> RunAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        int workerCount,
        Func<TItem, int, CancellationToken, Task<TResult>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);
        ArgumentNullException.ThrowIfNull(handler);

        var queue = new ConcurrentQueue<TItem>(items);
        var results = new ConcurrentQueue<WorkItemResult<TItem, TResult>>();

        async Task WorkerAsync(int workerId)
        {
            // Yield гарантирует, что работник продолжит асинхронно и не «съест» всю очередь
            // синхронно до старта остальных, если обработчик завершается без ожидания.
            await Task.Yield();
            while (queue.TryDequeue(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await handler(item, workerId, cancellationToken).ConfigureAwait(false);
                results.Enqueue(new WorkItemResult<TItem, TResult>(item, result, workerId));
            }
        }

        var workers = Enumerable.Range(1, workerCount).Select(WorkerAsync).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return results.ToArray();
    }
}
