using System.Diagnostics;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Processing;

/// <summary>Сводный результат пакетной обработки.</summary>
public sealed record BatchResult(
    string Mode,
    IReadOnlyList<OrderProcessingResult> Results,
    IReadOnlyList<Exception> UnexpectedErrors,
    TimeSpan Elapsed,
    int MaxObservedConcurrency)
{
    public int Completed => Results.Count(r => r.Outcome == ProcessingOutcome.Completed);
    public int Failed => Results.Count(r => r.Outcome == ProcessingOutcome.Failed);
    public int Cancelled => Results.Count(r => r.Outcome == ProcessingOutcome.Cancelled);
    public int Skipped => Results.Count(r => r.Outcome == ProcessingOutcome.Skipped);
}

/// <summary>Режимы пакетной обработки: последовательно, параллельно, параллельно с ограничением.</summary>
public sealed class BatchOrderProcessor(OrderProcessor processor)
{
    public async Task<BatchResult> ProcessSequentialAsync(
        IEnumerable<Order> orders, CancellationToken cancellationToken = default, IProgress<OrderEvent>? progress = null)
    {
        processor.Statistics.ResetConcurrencyPeak();
        var stopwatch = Stopwatch.StartNew();
        var results = new List<OrderProcessingResult>();
        var errors = new List<Exception>();

        foreach (var order in orders)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // После отмены новые заказы не запускаются.
                results.Add(processor.CancelPending(order, "пакет отменён до начала обработки заказа", progress));
                continue;
            }

            try
            {
                results.Add(await processor.ProcessAsync(order, cancellationToken, progress).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // Непредвиденная ошибка одного заказа не останавливает пакет, но сохраняется в отчёте.
                errors.Add(ex);
                results.Add(FromUnexpected(order, ex));
            }
        }

        return new BatchResult("последовательно", results, errors, stopwatch.Elapsed,
            processor.Statistics.MaxObservedConcurrency);
    }

    /// <summary>Все заказы запускаются сразу, ожидание — через <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/>.</summary>
    public Task<BatchResult> ProcessParallelAsync(
        IEnumerable<Order> orders, CancellationToken cancellationToken = default, IProgress<OrderEvent>? progress = null) =>
        RunConcurrentAsync("параллельно (без ограничения)", orders.ToArray(),
            order => processor.ProcessAsync(order, cancellationToken, progress));

    /// <summary>Не более <paramref name="maxConcurrency"/> заказов обрабатываются одновременно (SemaphoreSlim).</summary>
    public async Task<BatchResult> ProcessWithLimitAsync(
        IEnumerable<Order> orders, int maxConcurrency, CancellationToken cancellationToken = default,
        IProgress<OrderEvent>? progress = null)
    {
        // using: семафор освобождается после того, как все задачи пакета завершились.
        using var limiter = new ConcurrencyLimiter(maxConcurrency);

        return await RunConcurrentAsync($"параллельно (не более {maxConcurrency})", orders.ToArray(), async order =>
        {
            try
            {
                return await limiter.RunAsync(token => processor.ProcessAsync(order, token, progress), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Отмена во время ожидания слота: заказ не начинал обрабатываться.
                return processor.CancelPending(order, "пакет отменён, пока заказ ожидал свободного слота", progress);
            }
        }).ConfigureAwait(false);
    }

    private async Task<BatchResult> RunConcurrentAsync(
        string mode, IReadOnlyList<Order> orders, Func<Order, Task<OrderProcessingResult>> start)
    {
        processor.Statistics.ResetConcurrencyPeak();
        var stopwatch = Stopwatch.StartNew();

        // Задачи запускаются сразу при вызове асинхронного метода («горячие» задачи).
        var tasks = orders.Select(start).ToArray();
        // Обычный await Task.WhenAll пробросил бы только ПЕРВОЕ исключение, а массив результатов
        // был бы недоступен. SuppressThrowing (.NET 8) просто дожидается завершения всех задач;
        // затем каждая задача разбирается отдельно, и ни один результат или ошибка не теряются.
        await ((Task)Task.WhenAll(tasks)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var results = new List<OrderProcessingResult>(tasks.Length);
        var errors = new List<Exception>();
        for (var i = 0; i < tasks.Length; i++)
        {
            var task = tasks[i];
            switch (task.Status)
            {
                case TaskStatus.RanToCompletion:
                    results.Add(task.Result); // задача уже завершена — Result не блокирует поток
                    break;
                case TaskStatus.Faulted:
                    var error = task.Exception!.InnerException ?? task.Exception;
                    errors.Add(error);
                    results.Add(FromUnexpected(orders[i], error));
                    break;
                default:
                    results.Add(new OrderProcessingResult(orders[i].Id, ProcessingOutcome.Cancelled, orders[i].Status,
                        ProcessingStage.None, "задача отменена", TimeSpan.Zero, orders[i].Attempts, IsRetryable: true));
                    break;
            }
        }

        return new BatchResult(mode, results, errors, stopwatch.Elapsed, processor.Statistics.MaxObservedConcurrency);
    }

    private static OrderProcessingResult FromUnexpected(Order order, Exception error) =>
        new(order.Id, ProcessingOutcome.Failed, order.Status, order.FailedStage,
            order.ErrorMessage ?? error.Message, order.Duration ?? TimeSpan.Zero, order.Attempts, false, error);
}
