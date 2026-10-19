using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Processing;

/// <summary>Итог одной попытки обработки заказа.</summary>
public enum ProcessingOutcome
{
    Completed,
    Failed,
    Cancelled,

    /// <summary>Заказ не был обработан: его уже обрабатывает другая задача или статус не Pending.</summary>
    Skipped,
}

/// <summary>Неизменяемый результат обработки заказа (record — значение, безопасное для передачи между потоками).</summary>
public sealed record OrderProcessingResult(
    int OrderId,
    ProcessingOutcome Outcome,
    OrderStatus FinalStatus,
    ProcessingStage Stage,
    string Message,
    TimeSpan Elapsed,
    int Attempt,
    bool IsRetryable = false,
    Exception? Error = null)
{
    public bool IsSuccess => Outcome == ProcessingOutcome.Completed;

    public override string ToString() =>
        $"#{OrderId}: {Outcome} [{FinalStatus}] {(Stage == ProcessingStage.None ? "" : $"этап {Stage}, ")}{Message} ({Elapsed.TotalMilliseconds:0} мс)";
}
