using OrderProcessing.Core.Data;
using OrderProcessing.Core.Domain;

namespace OrderProcessing.Tests;

public class OrderFileStoreTests
{
    [Fact]
    public async Task DemoDataFile_IsValid_AndCoversScenarios()
    {
        var dtos = await OrderFileStore.LoadAsync(OrderFileStore.DefaultPath);

        Assert.Equal(12, dtos.Count);
        Assert.Contains(dtos, d => d.PaymentMethod == PaymentMethods.DeclinedCard);
        Assert.Contains(dtos, d => d.Items.Count == 0);
        var orders = dtos.Select((d, i) => OrderFileStore.ToOrder(d, i + 1)).ToArray();
        Assert.All(orders, o => Assert.Equal(OrderStatus.Pending, o.Status));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lab3-orders-{Guid.NewGuid():N}", "orders.json");
        var id = 0;
        var orders = DemoOrderFactory.CreateScenarioOrders(() => ++id);
        try
        {
            await OrderFileStore.SaveAsync(path, orders);
            var loaded = await OrderFileStore.LoadAsync(path);

            Assert.Equal(orders.Select(o => (o.Id, o.Customer, o.Total)),
                loaded.Select(d => OrderFileStore.ToOrder(d, d.Id)).Select(o => (o.Id, o.Customer, o.Total)));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFile_ThrowsIoException()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json");

        await Assert.ThrowsAnyAsync<IOException>(() => OrderFileStore.LoadAsync(path));
    }
}
