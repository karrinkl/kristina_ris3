using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Services;

namespace OrderProcessing.Core.Data;

/// <summary>Встроенные демонстрационные заказы (используются, если файл data/orders.json недоступен, и в тестах).</summary>
public static class DemoOrderFactory
{
    private static readonly Product[] ValidProducts =
        ProductCatalog.DefaultProducts.Where(p => p.Sku is not (ProductCatalog.CorruptedSku or "LT-15")).ToArray();

    private static OrderItem Item(string sku, int quantity)
    {
        var product = ProductCatalog.DefaultProducts.First(p => p.Sku == sku);
        return new OrderItem(product.Sku, product.Name, quantity, product.Price);
    }

    /// <summary>Набор, покрывающий все сценарии: успех, ошибки проверки/оплаты/склада/уведомления, непредвиденная ошибка.</summary>
    public static IReadOnlyList<Order> CreateScenarioOrders(Func<int> nextId) =>
    [
        new(nextId(), "Иванов И.", "ivanov@example.test", [Item("KB-01", 1), Item("MS-02", 1)]),
        new(nextId(), "Петрова А.", "petrova@example.test", [Item("CB-10", 2)], PaymentMethods.Bonus),
        new(nextId(), "Сидоров П.", "sidorov@example.test", [Item("MN-27", 1)]),
        new(nextId(), "Пустой заказ", "empty@example.test", []),
        new(nextId(), "Кузнецова Е.", "kuznetsova@example.test", [new OrderItem("XX-404", "Несуществующий товар", 1, 100m)]),
        new(nextId(), "Смирнов Д.", "smirnov@example.test", [Item("HS-05", 1)], PaymentMethods.DeclinedCard),
        new(nextId(), "Попова О.", "popova@example.test", [Item("SSD-1T", 1)], PaymentMethods.FlakyGateway),
        new(nextId(), "Волков С.", "offline.volkov@example.test", [Item("KB-01", 2)]),
        new(nextId(), "Лебедева М.", "lebedeva@example.test", [Item("LT-15", 3)]),
        new(nextId(), "Козлов Н.", "kozlov@example.test", [Item(ProductCatalog.CorruptedSku, 1)]),
        new(nextId(), "Новикова Т.", "novikova@example.test", [Item("SSD-1T", 1), Item("CB-10", 1)]),
        new(nextId(), "Морозов А.", "morozov@example.test", [Item("HS-05", 1), Item("MS-02", 2)]),
    ];

    /// <summary>Независимые заказы, которые гарантированно проходят все этапы (для сравнения режимов).</summary>
    public static IReadOnlyList<Order> CreateSuccessfulOrders(int count, Func<int> nextId)
    {
        var orders = new List<Order>(count);
        for (var i = 0; i < count; i++)
        {
            var id = nextId();
            var first = ValidProducts[i % ValidProducts.Length];
            var second = ValidProducts[(i + 2) % ValidProducts.Length];
            orders.Add(new Order(id, $"Покупатель {id}", $"buyer{id}@example.test",
                [new OrderItem(first.Sku, first.Name, 1 + i % 2, first.Price), new OrderItem(second.Sku, second.Name, 1, second.Price)]));
        }

        return orders;
    }

    /// <summary>Копия заказа с новым идентификатором в исходном статусе Pending.</summary>
    public static Order CloneAsNew(Order source, int newId) =>
        new(newId, source.Customer, source.Email, source.Items, source.PaymentMethod);
}
