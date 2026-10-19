using OrderProcessing.Core.Services;

namespace OrderProcessing.Core.Processing;

/// <summary>
/// Набор взаимосвязанных эмулированных служб и обработчиков. Основное приложение использует
/// один экземпляр; демонстрации и сравнения режимов создают изолированные экземпляры,
/// чтобы не изменять остатки и статистику основного магазина.
/// </summary>
public sealed class ShopEnvironment
{
    public ShopEnvironment(SimulationOptions? options = null)
    {
        Options = options ?? SimulationOptions.Default;
        Catalog = new ProductCatalog(Options);
        Inventory = new InventoryService(Options);
        Payment = new PaymentService(Options);
        Notifications = new NotificationService(Options);
        Statistics = new ProcessingStatistics();
        Journal = new OrderEventJournal();
        Repository = new OrderRepository();
        Processor = new OrderProcessor(Catalog, Inventory, Payment, Notifications, Statistics, Journal, Options, Repository);
        Batch = new BatchOrderProcessor(Processor);
    }

    public SimulationOptions Options { get; }
    public ProductCatalog Catalog { get; }
    public InventoryService Inventory { get; }
    public PaymentService Payment { get; }
    public NotificationService Notifications { get; }
    public ProcessingStatistics Statistics { get; }
    public OrderEventJournal Journal { get; }
    public OrderRepository Repository { get; }
    public OrderProcessor Processor { get; }
    public BatchOrderProcessor Batch { get; }
}
