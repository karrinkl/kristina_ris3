namespace OrderProcessing.Core.Domain;

/// <summary>Позиция заказа. Неизменяемый record — безопасно читать из любых потоков.</summary>
public sealed record OrderItem(string Sku, string Name, int Quantity, decimal UnitPrice)
{
    public decimal Total => Quantity * UnitPrice;
}
