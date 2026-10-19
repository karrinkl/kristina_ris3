using OrderProcessing.Core.Processing;

namespace OrderProcessing.App.Infrastructure;

/// <summary>
/// Синхронная реализация <see cref="IProgress{T}"/>: событие печатается сразу в потоке,
/// где оно возникло. Стандартный <see cref="Progress{T}"/> в консольном приложении
/// (без SynchronizationContext) отправляет обработчик в пул потоков, и строки могут
/// выводиться не по порядку.
/// </summary>
internal sealed class ConsoleProgress(bool verbose) : IProgress<OrderEvent>
{
    public void Report(OrderEvent value)
    {
        if (!verbose && value.Kind == OrderEventKind.Step)
        {
            return;
        }

        var color = value.Kind switch
        {
            OrderEventKind.Started => ConsoleColor.Cyan,
            OrderEventKind.Finished => ConsoleColor.White,
            OrderEventKind.Skipped => ConsoleColor.DarkYellow,
            _ => ConsoleColor.Gray,
        };
        Ui.WriteColored(color, "  " + value);
    }
}
