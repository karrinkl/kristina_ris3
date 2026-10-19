using OrderProcessing.App.Demos;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;

namespace OrderProcessing.App;

/// <summary>Интерактивное меню. Все команды выполняются асинхронно; ошибки команды не завершают приложение.</summary>
internal sealed class MainMenu(ShopEnvironment shop)
{
    private readonly OrderCommands _orders = new(shop);

    private static readonly string[] Items =
    [
        "Показать список заказов",
        "Создать демонстрационные заказы",
        "Обработать один заказ асинхронно",
        "Обработать несколько заказов параллельно",
        "Обработать заказы с ограничением параллелизма (SemaphoreSlim)",
        "Сравнить последовательную и параллельную обработку",
        "Отменить выполняющуюся обработку (фоновый пакет / тайм-аут)",
        "Показать успешные заказы",
        "Показать заказы с ошибками",
        "Показать отменённые заказы",
        "Повторить обработку подходящих неуспешных заказов",
        "Показать статистику обработки",
        "Демонстрация Task.WhenAll",
        "Демонстрация Task.WhenAny",
        "Демонстрация обработки исключений (AggregateException, Task.WaitAll)",
        "Демонстрация потокобезопасных коллекций",
        "Демонстрация примитивов синхронизации",
        "Сравнить асинхронность и параллелизм (Thread, ThreadPool, ValueTask, Parallel, PLINQ)",
        "Завершить приложение",
    ];

    public async Task RunAsync()
    {
        while (true)
        {
            await _orders.ReportCompletedBackgroundJobAsync();
            PrintMenu();

            var choice = Ui.ReadInt("Выберите пункт", 1, Items.Length);
            if (choice is null || choice == Items.Length)
            {
                break;
            }

            try
            {
                await ExecuteAsync(choice.Value);
            }
            catch (Exception ex)
            {
                // Последний рубеж: показываем ошибку целиком и продолжаем работу меню.
                Ui.Error($"Команда завершилась непредвиденной ошибкой: {ex}");
            }

            if (Ui.InputClosed)
            {
                break;
            }
        }

        await _orders.ShutdownAsync();
        Ui.Success("Работа завершена. Все запущенные задачи завершены или отменены.");
    }

    private void PrintMenu()
    {
        Ui.Header("Обработка заказов интернет-магазина — ЛР №3");
        var counts = shop.Repository.CountByStatus();
        Ui.Info($"Заказов: {shop.Repository.Count} (Pending {counts[OrderStatus.Pending]}, Processing {counts[OrderStatus.Processing]}, " +
                $"Completed {counts[OrderStatus.Completed]}, Failed {counts[OrderStatus.Failed]}, Cancelled {counts[OrderStatus.Cancelled]})" +
                (_orders.HasRunningBackgroundJob ? " — идёт фоновая обработка" : string.Empty));
        for (var i = 0; i < Items.Length; i++)
        {
            Ui.Info($"{i + 1,2}. {Items[i]}");
        }
    }

    private Task ExecuteAsync(int choice)
    {
        switch (choice)
        {
            case 1: _orders.ShowOrders(); return Task.CompletedTask;
            case 2: return _orders.CreateDemoOrdersAsync();
            case 3: return _orders.ProcessSingleAsync();
            case 4: return _orders.ProcessParallelAsync();
            case 5: return _orders.ProcessWithLimitAsync();
            case 6: return _orders.CompareSequentialAndParallelAsync();
            case 7: return _orders.CancellationMenuAsync();
            case 8: _orders.ShowByStatus(OrderStatus.Completed, "Успешно обработанные заказы"); return Task.CompletedTask;
            case 9: _orders.ShowByStatus(OrderStatus.Failed, "Заказы с ошибками"); return Task.CompletedTask;
            case 10: _orders.ShowByStatus(OrderStatus.Cancelled, "Отменённые заказы"); return Task.CompletedTask;
            case 11: return _orders.RetryAsync();
            case 12: _orders.ShowStatistics(); return Task.CompletedTask;
            case 13: return TaskAggregationDemos.WhenAllAsync(shop.Options);
            case 14: return TaskAggregationDemos.WhenAnyAsync();
            case 15: return ExceptionDemos.RunAsync(shop.Options);
            case 16: return CollectionDemos.RunAsync(shop.Options);
            case 17: return SynchronizationDemos.RunAsync();
            case 18: return AsyncParallelDemos.RunAsync(shop.Options);
            default:
                Ui.Error("Неизвестный пункт меню.");
                return Task.CompletedTask;
        }
    }
}
