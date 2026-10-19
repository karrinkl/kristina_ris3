using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Data;

/// <summary>Формат файла демонстрационных заказов.</summary>
public sealed class OrderFileModel
{
    public List<OrderDto> Orders { get; set; } = [];
}

public sealed class OrderDto
{
    public int Id { get; set; }
    public string Customer { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PaymentMethod { get; set; } = PaymentMethods.Card;
    public string? Scenario { get; set; }
    public List<OrderItemDto> Items { get; set; } = [];
}

public sealed class OrderItemDto
{
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>
/// Загрузка и сохранение заказов в локальный JSON-файл. Используется настоящий асинхронный
/// файловый ввод-вывод (FileStream с useAsync: true + JsonSerializer.*Async), поэтому
/// оборачивать операции в Task.Run не нужно: поток не занят, пока ОС читает файл.
/// </summary>
public static class OrderFileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Путь к демонстрационному файлу рядом с исполняемым файлом (без жёстко заданных разделителей).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "data", "orders.json");

    public static async Task<IReadOnlyList<OrderDto>> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        var model = await JsonSerializer.DeserializeAsync<OrderFileModel>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        return model?.Orders ?? [];
    }

    public static async Task SaveAsync(string path, IEnumerable<Order> orders, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var model = new OrderFileModel { Orders = orders.Select(ToDto).ToList() };
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, model, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Создаёт заказ из DTO; идентификатор выдаёт вызывающий код, чтобы не было конфликтов.</summary>
    public static Order ToOrder(OrderDto dto, int id) =>
        new(id, dto.Customer, dto.Email,
            dto.Items.Select(i => new OrderItem(i.Sku, i.Name, i.Quantity, i.UnitPrice)),
            string.IsNullOrWhiteSpace(dto.PaymentMethod) ? PaymentMethods.Card : dto.PaymentMethod);

    public static OrderDto ToDto(Order order) => new()
    {
        Id = order.Id,
        Customer = order.Customer,
        Email = order.Email,
        PaymentMethod = order.PaymentMethod,
        Items = order.Items.Select(i => new OrderItemDto
        {
            Sku = i.Sku,
            Name = i.Name,
            Quantity = i.Quantity,
            UnitPrice = i.UnitPrice,
        }).ToList(),
    };
}
