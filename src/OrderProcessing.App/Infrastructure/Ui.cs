using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;

namespace OrderProcessing.App.Infrastructure;

/// <summary>Вспомогательные методы консольного ввода-вывода с защитой от ошибочного ввода.</summary>
internal static class Ui
{
    private static readonly object OutputLock = new();

    /// <summary>Ввод закончился (stdin закрыт) — приложение должно завершиться, а не зациклиться.</summary>
    public static bool InputClosed { get; private set; }

    public static void Header(string title)
    {
        WriteColored(ConsoleColor.Cyan, $"{Environment.NewLine}=== {title} ===");
    }

    public static void Section(string title) => WriteColored(ConsoleColor.Yellow, $"{Environment.NewLine}--- {title} ---");

    /// <summary>Пояснение к демонстрации (теория).</summary>
    public static void Explain(string text) => WriteColored(ConsoleColor.DarkCyan, "  ℹ " + text);

    public static void Info(string text) => WriteLine(text);
    public static void Success(string text) => WriteColored(ConsoleColor.Green, text);
    public static void Warning(string text) => WriteColored(ConsoleColor.DarkYellow, text);
    public static void Error(string text) => WriteColored(ConsoleColor.Red, text);

    public static void WriteLine(string text = "")
    {
        lock (OutputLock)
        {
            Console.WriteLine(text);
        }
    }

    /// <summary>
    /// Цвет и текст выводятся под одной блокировкой: иначе параллельные задачи
    /// могли бы перекрасить чужую строку между установкой цвета и выводом.
    /// </summary>
    public static void WriteColored(ConsoleColor color, string text)
    {
        lock (OutputLock)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
        }
    }

    public static string? ReadLine(string prompt)
    {
        lock (OutputLock)
        {
            Console.Write(prompt);
        }

        var line = Console.ReadLine();
        if (line is null)
        {
            InputClosed = true;
            WriteLine();
        }
        else if (Console.IsInputRedirected)
        {
            // При вводе из файла/конвейера эхо отсутствует — печатаем введённое для читаемости протокола.
            WriteLine(line);
        }

        return line?.Trim();
    }

    /// <summary>Читает целое в диапазоне; пустой ввод — значение по умолчанию; null — ввод закрыт.</summary>
    public static int? ReadInt(string prompt, int min, int max, int? defaultValue = null)
    {
        while (true)
        {
            var hint = defaultValue is null ? $" [{min}..{max}]: " : $" [{min}..{max}, по умолчанию {defaultValue}]: ";
            var line = ReadLine(prompt + hint);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0 && defaultValue is not null)
            {
                return defaultValue;
            }

            if (int.TryParse(line, out var value) && value >= min && value <= max)
            {
                return value;
            }

            Error($"Некорректный ввод «{line}». Введите целое число от {min} до {max}.");
        }
    }

    public static bool ReadYesNo(string prompt, bool defaultValue)
    {
        while (true)
        {
            var line = ReadLine($"{prompt} (д/н, по умолчанию {(defaultValue ? "д" : "н")}): ");
            if (line is null || line.Length == 0)
            {
                return defaultValue;
            }

            switch (line.ToLowerInvariant())
            {
                case "д" or "да" or "y" or "yes":
                    return true;
                case "н" or "нет" or "n" or "no":
                    return false;
                default:
                    Error("Ответьте «д» или «н».");
                    break;
            }
        }
    }

    public static void PrintOrders(IReadOnlyCollection<Order> orders, string emptyMessage = "Список пуст.")
    {
        if (orders.Count == 0)
        {
            Warning(emptyMessage);
            return;
        }

        WriteLine($"{"№",4} | {"Клиент",-14} | {"Сумма, ₽",11} | {"Оплата",-13} | {"Статус",-10} | {"Попыт.",6} | {"Время, мс",9} | Состав / ошибка");
        WriteLine(new string('-', 120));
        foreach (var order in orders)
        {
            var duration = order.Duration is { } d ? d.TotalMilliseconds.ToString("0") : "-";
            var details = order.ErrorMessage is { } error
                ? $"[{order.FailedStage}] {error}{(order.IsRetryable ? " (можно повторить)" : string.Empty)}"
                : order.Summary;
            WriteColored(StatusColor(order.Status),
                $"{order.Id,4} | {Truncate(order.Customer, 14),-14} | {order.Total,11:0.00} | {order.PaymentMethod,-13} | {order.Status,-10} | {order.Attempts,6} | {duration,9} | {details}");
        }
    }

    public static void PrintResults(IEnumerable<OrderProcessingResult> results)
    {
        foreach (var result in results.OrderBy(r => r.OrderId))
        {
            var color = result.Outcome switch
            {
                ProcessingOutcome.Completed => ConsoleColor.Green,
                ProcessingOutcome.Failed => ConsoleColor.Red,
                ProcessingOutcome.Cancelled => ConsoleColor.DarkYellow,
                _ => ConsoleColor.Gray,
            };
            WriteColored(color, "  " + result);
        }
    }

    public static void PrintBatch(BatchResult batch)
    {
        PrintResults(batch.Results);
        WriteLine($"Режим: {batch.Mode}; время: {batch.Elapsed.TotalMilliseconds:0} мс; " +
                  $"успешно: {batch.Completed}, ошибки: {batch.Failed}, отменено: {batch.Cancelled}, пропущено: {batch.Skipped}; " +
                  $"макс. одновременно: {batch.MaxObservedConcurrency}");
        if (batch.UnexpectedErrors.Count > 0)
        {
            Error($"Непредвиденных исключений: {batch.UnexpectedErrors.Count}");
            foreach (var error in batch.UnexpectedErrors)
            {
                Error($"  {error.GetType().Name}: {error.Message}");
            }
        }
    }

    public static ConsoleColor StatusColor(OrderStatus status) => status switch
    {
        OrderStatus.Completed => ConsoleColor.Green,
        OrderStatus.Failed => ConsoleColor.Red,
        OrderStatus.Cancelled => ConsoleColor.DarkYellow,
        OrderStatus.Processing => ConsoleColor.Cyan,
        _ => ConsoleColor.Gray,
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
