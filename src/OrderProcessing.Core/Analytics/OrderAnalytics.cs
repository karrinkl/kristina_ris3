using OrderProcessing.Core.Domain;

namespace OrderProcessing.Core.Analytics;

/// <summary>
/// CPU-bound вычисления над заказами: «оценка риска мошенничества» — детерминированная
/// хеш-функция с большим числом раундов. Результат для заказа зависит только от его данных,
/// поэтому заказы независимы и их можно считать параллельно.
/// </summary>
public static class OrderAnalytics
{
    public static long ComputeRiskScore(Order order, int rounds)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentOutOfRangeException.ThrowIfNegative(rounds);

        unchecked
        {
            var hash = 1469598103934665603UL ^ (ulong)order.Id;
            foreach (var item in order.Items)
            {
                foreach (var ch in item.Sku)
                {
                    hash = (hash ^ ch) * 1099511628211UL;
                }

                hash = (hash ^ (ulong)item.Quantity) * 1099511628211UL;
                hash = (hash ^ (ulong)decimal.ToInt64(decimal.Round(item.UnitPrice * 100))) * 1099511628211UL;
            }

            for (var i = 0; i < rounds; i++)
            {
                hash ^= hash << 13;
                hash ^= hash >> 7;
                hash ^= hash << 17;
            }

            return (long)(hash % 1_000UL);
        }
    }

    public static long[] ComputeSequential(IReadOnlyList<Order> orders, int rounds)
    {
        var scores = new long[orders.Count];
        for (var i = 0; i < orders.Count; i++)
        {
            scores[i] = ComputeRiskScore(orders[i], rounds);
        }

        return scores;
    }

    /// <summary>
    /// <see cref="Parallel.For(int, int, Action{int})"/>: каждая итерация пишет в свою ячейку массива,
    /// поэтому синхронизация не требуется. Вызывающий поток тоже участвует в работе и
    /// блокируется до завершения всех итераций.
    /// </summary>
    public static long[] ComputeParallelFor(IReadOnlyList<Order> orders, int rounds, int? maxDegreeOfParallelism = null)
    {
        var scores = new long[orders.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism ?? -1 };
        Parallel.For(0, orders.Count, options, i => scores[i] = ComputeRiskScore(orders[i], rounds));
        return scores;
    }

    /// <summary>PLINQ: декларативный запрос, AsOrdered сохраняет исходный порядок результатов.</summary>
    public static long[] ComputePlinq(IReadOnlyList<Order> orders, int rounds) =>
        orders.AsParallel()
            .AsOrdered()
            .Select(order => ComputeRiskScore(order, rounds))
            .ToArray();

    /// <summary>
    /// Агрегация в Parallel.For с локальным состоянием потока: каждый поток копит свою
    /// частичную сумму без синхронизации, а общий итог обновляется один раз на поток
    /// (Interlocked.Add в localFinally). Так синхронизация не тормозит каждую итерацию.
    /// </summary>
    public static long SumRiskScoresParallel(IReadOnlyList<Order> orders, int rounds)
    {
        long total = 0;
        Parallel.For(0, orders.Count,
            localInit: () => 0L,
            body: (i, _, subtotal) => subtotal + ComputeRiskScore(orders[i], rounds),
            localFinally: subtotal => Interlocked.Add(ref total, subtotal));
        return total;
    }

    /// <summary>PLINQ-агрегация: сумма заказов с высоким риском.</summary>
    public static decimal HighRiskRevenuePlinq(IReadOnlyList<Order> orders, int rounds, long threshold) =>
        orders.AsParallel()
            .Where(order => ComputeRiskScore(order, rounds) >= threshold)
            .Sum(order => order.Total);
}
