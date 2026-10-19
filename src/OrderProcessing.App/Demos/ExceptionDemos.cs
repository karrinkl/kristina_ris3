using System.Diagnostics;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Core.Services;

namespace OrderProcessing.App.Demos;

/// <summary>Пункт 15: обработка исключений, AggregateException и Task.WaitAll.</summary>
internal static class ExceptionDemos
{
    public static async Task RunAsync(SimulationOptions options)
    {
        Ui.Header("Обработка исключений");
        var env = new ShopEnvironment(options);
        var scenarios = DemoOrderFactory.CreateScenarioOrders(env.Repository.NextId);
        foreach (var order in scenarios)
        {
            env.Repository.TryAdd(order);
        }

        Order ById(int id) => scenarios.First(o => o.Id == id);

        Ui.Section("1. Отдельные сценарии");
        await RunScenarioAsync(env, "успешная обработка", ById(1), CancellationToken.None);
        await RunScenarioAsync(env, "ошибка проверки заказа", ById(4), CancellationToken.None);
        await RunScenarioAsync(env, "ошибка оплаты", ById(6), CancellationToken.None);
        await RunScenarioAsync(env, "ошибка уведомления", ById(8), CancellationToken.None);
        await RunScenarioAsync(env, "нехватка на складе", ById(9), CancellationToken.None);
        await RunScenarioAsync(env, "непредвиденная ошибка", ById(10), CancellationToken.None);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            await RunScenarioAsync(env, "отмена через 150 мс", ById(3), cts.Token);
        }

        Ui.Section("2. Отмена при прямом вызове службы: OperationCanceledException");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try
            {
                await env.Notifications.SendAsync(ById(11), "тест", cancelled.Token);
            }
            catch (OperationCanceledException ex)
            {
                Ui.Warning($"  Поймано {ex.GetType().Name} (наследник OperationCanceledException), token совпадает: {ex.CancellationToken == cancelled.Token}");
                Ui.Explain("Отмена — не ошибка: её ловят отдельно и не записывают в журнал ошибок.");
            }
        }

        Ui.Section("3. Несколько ошибок при параллельном выполнении (await Task.WhenAll)");
        var failing = StartFailingOperations(env, ById(6), ById(9));
        var all = Task.WhenAll(failing);
        try
        {
            await all;
        }
        catch (Exception first)
        {
            Ui.Warning($"  await выбросил только первое: {first.GetType().Name}: {first.Message}");
            foreach (var inner in all.Exception!.InnerExceptions)
            {
                Ui.Error($"    в all.Exception: {Describe(inner)}");
            }
        }

        Ui.Section("4. Синхронное ожидание Task.WaitAll и AggregateException");
        var tasks = StartFailingOperations(env, ById(6), ById(9)).Append(Task.Delay(100)).ToArray();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Блокирующее ожидание — только для демонстрации: текущий поток простаивает до завершения всех задач.
            Task.WaitAll(tasks);
        }
        catch (AggregateException aggregate)
        {
            Ui.Info($"  Поток {Environment.CurrentManagedThreadId} был заблокирован {stopwatch.ElapsedMilliseconds} мс.");
            var flat = aggregate.Flatten();
            Ui.Warning($"  AggregateException содержит {flat.InnerExceptions.Count} исключения(й):");
            foreach (var inner in flat.InnerExceptions)
            {
                Ui.Error($"    {Describe(inner)}");
            }

            try
            {
                // Handle: бизнес-ошибки считаем обработанными, остальные будут выброшены в новом AggregateException.
                flat.Handle(ex => ex is OrderProcessingException);
            }
            catch (AggregateException unhandled)
            {
                Ui.Error($"  После Handle остались необработанными: {string.Join(", ", unhandled.InnerExceptions.Select(e => e.GetType().Name))}");
            }
        }

        Ui.Explain("WaitAll блокирует поток и упаковывает ВСЕ исключения в AggregateException.");
        Ui.Explain("await Task.WhenAll не блокирует поток и выбрасывает первое исключение; полный список — в свойстве Exception задачи.");
        Ui.Explain("В асинхронном коде следует использовать await Task.WhenAll: WaitAll тратит поток и может привести к взаимоблокировке при наличии SynchronizationContext.");
    }

    private static async Task RunScenarioAsync(ShopEnvironment env, string title, Order order, CancellationToken token)
    {
        try
        {
            var result = await env.Processor.ProcessAsync(order, token);
            var kind = result.Outcome switch
            {
                ProcessingOutcome.Completed => "успех",
                ProcessingOutcome.Failed => $"бизнес-ошибка {result.Error?.GetType().Name}",
                ProcessingOutcome.Cancelled => "отмена (OperationCanceledException перехвачен обработчиком)",
                _ => result.Outcome.ToString(),
            };
            Ui.WriteColored(Ui.StatusColor(order.Status), $"  {title,-24} → {kind}: {result.Message}");
        }
        catch (Exception ex)
        {
            Ui.Error($"  {title,-24} → непредвиденное исключение проброшено вызывающему коду: {ex.GetType().Name}: {ex.Message}; заказ: {order.Status}");
        }
    }

    /// <summary>Три независимые операции, каждая из которых завершается своим исключением.</summary>
    private static Task[] StartFailingOperations(ShopEnvironment env, Order declined, Order outOfStock) =>
    [
        env.Payment.ChargeAsync(declined, CancellationToken.None),
        env.Inventory.ReserveAsync(outOfStock, CancellationToken.None),
        env.Catalog.FindAsync(ProductCatalog.CorruptedSku, CancellationToken.None).AsTask(),
    ];

    private static string Describe(Exception ex) => ex switch
    {
        OrderProcessingException business => $"бизнес-ошибка {ex.GetType().Name} (этап {business.Stage}): {ex.Message}",
        OperationCanceledException => $"отмена {ex.GetType().Name}",
        _ => $"непредвиденная {ex.GetType().Name}: {ex.Message}",
    };
}
