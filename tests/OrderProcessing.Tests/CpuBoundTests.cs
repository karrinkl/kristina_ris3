using OrderProcessing.Core.Analytics;
using OrderProcessing.Core.Data;

namespace OrderProcessing.Tests;

public class CpuBoundTests
{
    private static readonly IReadOnlyList<Core.Domain.Order> Orders = CreateOrders();

    private static IReadOnlyList<Core.Domain.Order> CreateOrders()
    {
        var id = 0;
        return DemoOrderFactory.CreateSuccessfulOrders(300, () => ++id);
    }

    [Fact]
    public void ParallelFor_AndPlinq_MatchSequentialResult()
    {
        var sequential = OrderAnalytics.ComputeSequential(Orders, 2_000);

        Assert.Equal(sequential, OrderAnalytics.ComputeParallelFor(Orders, 2_000));
        Assert.Equal(sequential, OrderAnalytics.ComputeParallelFor(Orders, 2_000, maxDegreeOfParallelism: 2));
        Assert.Equal(sequential, OrderAnalytics.ComputePlinq(Orders, 2_000));
    }

    [Fact]
    public void ParallelAggregation_WithLocalState_MatchesSequentialSum()
    {
        var expected = OrderAnalytics.ComputeSequential(Orders, 1_000).Sum();

        Assert.Equal(expected, OrderAnalytics.SumRiskScoresParallel(Orders, 1_000));
    }

    [Fact]
    public void PlinqFilterAndSum_MatchesLinq()
    {
        var expected = Orders.Where(o => OrderAnalytics.ComputeRiskScore(o, 500) >= 500).Sum(o => o.Total);

        Assert.Equal(expected, OrderAnalytics.HighRiskRevenuePlinq(Orders, 500, 500));
    }

    [Fact]
    public void RiskScore_IsDeterministicAndInRange()
    {
        var first = OrderAnalytics.ComputeRiskScore(Orders[0], 10_000);

        Assert.Equal(first, OrderAnalytics.ComputeRiskScore(Orders[0], 10_000));
        Assert.All(OrderAnalytics.ComputeSequential(Orders, 100), s => Assert.InRange(s, 0, 999));
    }
}
