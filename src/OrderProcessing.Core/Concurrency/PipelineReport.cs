namespace OrderProcessing.Core.Concurrency;

/// <summary>Результат работы схемы «производитель — потребители».</summary>
public sealed record PipelineReport<TResult>(
    IReadOnlyList<TResult> Results,
    int Produced,
    int ProducerWaits,
    TimeSpan Elapsed);

/// <summary>Элемент, обработанный конкретной рабочей задачей/потребителем.</summary>
public sealed record WorkItemResult<TItem, TResult>(TItem Item, TResult Result, int WorkerId);
