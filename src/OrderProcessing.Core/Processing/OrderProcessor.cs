using System.Diagnostics;
using OrderProcessing.Core.Domain;
using OrderProcessing.Core.Services;

namespace OrderProcessing.Core.Processing;

/// <summary>
/// Асинхронный конвейер обработки одного заказа:
/// проверка → резерв на складе → оплата → уведомление → завершение.
/// <para>
/// Ошибки разделены на три группы:
/// <list type="bullet">
/// <item><see cref="OperationCanceledException"/> при запрошенной отмене — заказ получает статус Cancelled;</item>
/// <item><see cref="OrderProcessingException"/> (бизнес-ошибки) — статус Failed, исключение не пробрасывается;</item>
/// <item>прочие (непредвиденные) — статус Failed, затем исключение пробрасывается вызывающему коду,
/// чтобы ошибка не была замаскирована.</item>
/// </list>
/// </para>
/// </summary>
public sealed class OrderProcessor(
    IProductCatalog catalog,
    IInventoryService inventory,
    IPaymentService payment,
    INotificationService notifications,
    ProcessingStatistics statistics,
    OrderEventJournal journal,
    SimulationOptions options,
    OrderRepository? repository = null)
{
    public ProcessingStatistics Statistics => statistics;
    public OrderEventJournal Journal => journal;

    public async Task<OrderProcessingResult> ProcessAsync(
        Order order, CancellationToken cancellationToken = default, IProgress<OrderEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(order);

        // Отмена уже запрошена — новую операцию не начинаем.
        if (cancellationToken.IsCancellationRequested)
        {
            return CancelPending(order, "обработка отменена до начала", progress);
        }

        // Атомарный захват заказа: вторая задача с тем же заказом получит Skipped.
        if (!order.TryBeginProcessing())
        {
            statistics.OnSkipped();
            var skipped = new OrderProcessingResult(order.Id, ProcessingOutcome.Skipped, order.Status,
                ProcessingStage.None, $"пропущен: заказ уже в статусе {order.Status}", TimeSpan.Zero, order.Attempts);
            Report(order, ProcessingStage.None, OrderEventKind.Skipped, skipped.Message, progress);
            return skipped;
        }

        statistics.OnStarted();
        var stopwatch = Stopwatch.StartNew();
        var stage = ProcessingStage.Validation;
        Report(order, stage, OrderEventKind.Started, $"захвачен для обработки (попытка {order.Attempts})", progress);

        try
        {
            await ValidateAsync(order, cancellationToken).ConfigureAwait(false);
            Report(order, stage, OrderEventKind.Step, "проверка пройдена", progress);

            stage = ProcessingStage.Inventory;
            if (order.StockReserved)
            {
                Report(order, stage, OrderEventKind.Step, "резерв выполнен в прошлой попытке — пропуск", progress);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                await inventory.ReserveAsync(order, cancellationToken).ConfigureAwait(false);
                order.MarkStockReserved(true);
                Report(order, stage, OrderEventKind.Step, "товар зарезервирован", progress);
            }

            stage = ProcessingStage.Payment;
            if (order.PaymentId is { } existingPayment)
            {
                // Идемпотентность: повторная обработка не списывает деньги второй раз.
                Report(order, stage, OrderEventKind.Step, $"оплата уже проведена ({existingPayment}) — пропуск", progress);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                var receipt = await payment.ChargeAsync(order, cancellationToken).ConfigureAwait(false);
                order.MarkPaid(receipt.PaymentId);
                Report(order, stage, OrderEventKind.Step, $"оплачено {receipt.Amount:0.00} ₽, платёж {receipt.PaymentId}", progress);
            }

            stage = ProcessingStage.Notification;
            cancellationToken.ThrowIfCancellationRequested();
            await notifications.SendAsync(order, $"Заказ #{order.Id} на сумму {order.Total:0.00} ₽ оплачен и передан в доставку",
                cancellationToken).ConfigureAwait(false);
            order.MarkNotified();
            Report(order, stage, OrderEventKind.Step, "уведомление отправлено", progress);

            order.Complete();
            return Finish(order, new OrderProcessingResult(order.Id, ProcessingOutcome.Completed, order.Status,
                ProcessingStage.None, "успешно обработан", stopwatch.Elapsed, order.Attempts), progress);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseUnpaidReservation(order);
            var message = order.PaymentId is null
                ? $"обработка отменена на этапе {stage}"
                : $"обработка отменена на этапе {stage}; оплата сохранена, при повторе будет выполнено только уведомление";
            order.Cancel(stage, message);
            return Finish(order, new OrderProcessingResult(order.Id, ProcessingOutcome.Cancelled, order.Status,
                stage, message, stopwatch.Elapsed, order.Attempts, IsRetryable: true), progress);
        }
        catch (OrderProcessingException ex)
        {
            ReleaseUnpaidReservation(order);
            order.Fail(ex.Stage, ex.Message, ex.IsRetryable);
            return Finish(order, new OrderProcessingResult(order.Id, ProcessingOutcome.Failed, order.Status,
                ex.Stage, ex.Message, stopwatch.Elapsed, order.Attempts, ex.IsRetryable, ex), progress);
        }
        catch (Exception ex)
        {
            // Непредвиденная ошибка: фиксируем согласованное состояние заказа
            // (он не должен навсегда остаться в Processing) и пробрасываем исключение дальше.
            ReleaseUnpaidReservation(order);
            var message = $"непредвиденная ошибка: {ex.GetType().Name}: {ex.Message}";
            order.Fail(stage, message, isRetryable: false);
            Finish(order, new OrderProcessingResult(order.Id, ProcessingOutcome.Failed, order.Status,
                stage, message, stopwatch.Elapsed, order.Attempts, false, ex), progress);
            throw;
        }
    }

    /// <summary>
    /// Отменяет заказ, обработка которого ещё не начиналась (например, задача ждала слота семафора).
    /// Если заказ уже захвачен другой задачей, его статус не меняется.
    /// </summary>
    public OrderProcessingResult CancelPending(Order order, string reason, IProgress<OrderEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.TryCancelPending(reason))
        {
            statistics.OnCancelledBeforeStart();
            var cancelled = new OrderProcessingResult(order.Id, ProcessingOutcome.Cancelled, order.Status,
                ProcessingStage.None, reason, TimeSpan.Zero, order.Attempts, IsRetryable: true);
            repository?.SaveResult(cancelled);
            Report(order, ProcessingStage.None, OrderEventKind.Finished, reason, progress);
            return cancelled;
        }

        statistics.OnSkipped();
        return new OrderProcessingResult(order.Id, ProcessingOutcome.Skipped, order.Status, ProcessingStage.None,
            $"отмена не применена: заказ в статусе {order.Status}", TimeSpan.Zero, order.Attempts);
    }

    private async Task ValidateAsync(Order order, CancellationToken cancellationToken)
    {
        await SimulationOptions.SimulateLatencyAsync(
            options.GetDelay(ProcessingStage.Validation, order.Id), cancellationToken).ConfigureAwait(false);

        if (order.Items.Count == 0)
        {
            throw new OrderValidationException("заказ не содержит товаров");
        }

        if (string.IsNullOrWhiteSpace(order.Email) || !order.Email.Contains('@'))
        {
            throw new OrderValidationException($"некорректный адрес электронной почты: '{order.Email}'");
        }

        foreach (var item in order.Items)
        {
            if (item.Quantity <= 0)
            {
                throw new OrderValidationException($"некорректное количество товара {item.Sku}: {item.Quantity}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // ValueTask ожидается ровно один раз и сразу — так его и следует использовать.
            var product = await catalog.FindAsync(item.Sku, cancellationToken).ConfigureAwait(false);
            if (product is null)
            {
                throw new OrderValidationException($"товар {item.Sku} не найден в каталоге");
            }

            if (product.Price != item.UnitPrice)
            {
                throw new OrderValidationException(
                    $"цена товара {item.Sku} изменилась: в заказе {item.UnitPrice:0.00}, в каталоге {product.Price:0.00}");
            }
        }
    }

    /// <summary>Компенсация: если деньги не списаны, резерв возвращается на склад.</summary>
    private void ReleaseUnpaidReservation(Order order)
    {
        if (order.StockReserved && order.PaymentId is null)
        {
            inventory.Release(order);
            order.MarkStockReserved(false);
        }
    }

    private OrderProcessingResult Finish(Order order, OrderProcessingResult result, IProgress<OrderEvent>? progress)
    {
        repository?.SaveResult(result);
        statistics.OnFinished(result);
        Report(order, result.Stage, OrderEventKind.Finished, $"итог: {result.Outcome} — {result.Message}", progress);
        return result;
    }

    private void Report(Order order, ProcessingStage stage, OrderEventKind kind, string message, IProgress<OrderEvent>? progress)
    {
        var orderEvent = new OrderEvent(DateTimeOffset.Now, order.Id, stage, kind, message, Environment.CurrentManagedThreadId);
        journal.Add(orderEvent);
        progress?.Report(orderEvent);
    }
}
