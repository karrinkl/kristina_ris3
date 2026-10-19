using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Processing;
using OrderProcessing.Core.Services;

namespace OrderProcessing.Tests.Infrastructure;

/// <summary>Магазин с нулевыми задержками и подменяемыми службами для детерминированных тестов.</summary>
internal sealed class TestShop
{
    private int _nextId;

    public TestShop(IPaymentService? payment = null, INotificationService? notifications = null)
    {
        Options = SimulationOptions.Instant;
        Catalog = new ProductCatalog(Options);
        Inventory = new InventoryService(Options);
        RealPayment = new PaymentService(Options);
        RealNotifications = new NotificationService(Options);
        Payment = payment ?? RealPayment;
        Notifications = notifications ?? RealNotifications;
        Statistics = new ProcessingStatistics();
        Journal = new OrderEventJournal();
        Repository = new OrderRepository();
        Processor = new OrderProcessor(Catalog, Inventory, Payment, Notifications, Statistics, Journal, Options, Repository);
        Batch = new BatchOrderProcessor(Processor);
    }

    public SimulationOptions Options { get; }
    public ProductCatalog Catalog { get; }
    public InventoryService Inventory { get; }
    public PaymentService RealPayment { get; }
    public NotificationService RealNotifications { get; }
    public IPaymentService Payment { get; }
    public INotificationService Notifications { get; }
    public ProcessingStatistics Statistics { get; }
    public OrderEventJournal Journal { get; }
    public OrderRepository Repository { get; }
    public OrderProcessor Processor { get; }
    public BatchOrderProcessor Batch { get; }

    public Order NewOrder(string sku = "KB-01", int quantity = 1, string paymentMethod = PaymentMethods.Card, string? email = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var product = ProductCatalog.DefaultProducts.FirstOrDefault(p => p.Sku == sku) ?? new Product(sku, "Неизвестный", 100m);
        var order = new Order(id, $"Клиент {id}", email ?? $"client{id}@example.test",
            [new OrderItem(product.Sku, product.Name, quantity, product.Price)], paymentMethod);
        Repository.TryAdd(order);
        return order;
    }

    public IReadOnlyList<Order> NewOrders(int count) => Enumerable.Range(0, count).Select(_ => NewOrder()).ToArray();

    public void Register(IEnumerable<Order> orders)
    {
        foreach (var order in orders)
        {
            Repository.TryAdd(order);
            _nextId = Math.Max(_nextId, order.Id);
        }
    }
}
