namespace OrderProcessing.Core.Domain;

/// <summary>
/// Заказ интернет-магазина.
/// <para>
/// Неизменяемые данные (состав, сумма, клиент) задаются при создании. Изменяемое состояние
/// (статус, время, ошибка, признаки выполненных этапов) защищено приватным объектом
/// блокировки <see cref="_gate"/>: проверка текущего статуса и запись нового выполняются
/// атомарно, поэтому две рабочие задачи не могут одновременно захватить один заказ.
/// </para>
/// </summary>
public sealed class Order
{
    // Отдельный приватный объект блокировки: внешний код не может случайно
    // заблокироваться на нём (в отличие от lock(this) или lock("строка")).
    private readonly object _gate = new();

    private OrderStatus _status = OrderStatus.Pending;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;
    private string? _errorMessage;
    private ProcessingStage _failedStage;
    private bool _isRetryable;
    private int _attempts;
    private bool _stockReserved;
    private string? _paymentId;
    private bool _customerNotified;

    public Order(int id, string customer, string email, IEnumerable<OrderItem> items,
        string paymentMethod = PaymentMethods.Card, DateTimeOffset? createdAt = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentNullException.ThrowIfNull(items);

        Id = id;
        Customer = customer ?? string.Empty;
        Email = email ?? string.Empty;
        PaymentMethod = paymentMethod ?? string.Empty;
        Items = items.ToArray();
        Total = Items.Sum(i => i.Total);
        CreatedAt = createdAt ?? DateTimeOffset.Now;
    }

    public int Id { get; }
    public string Customer { get; }
    public string Email { get; }
    public string PaymentMethod { get; }
    public IReadOnlyList<OrderItem> Items { get; }
    public decimal Total { get; }
    public DateTimeOffset CreatedAt { get; }

    public string Summary => Items.Count == 0
        ? "(пустой заказ)"
        : string.Join(", ", Items.Select(i => $"{i.Name} x{i.Quantity}"));

    public OrderStatus Status { get { lock (_gate) return _status; } }
    public DateTimeOffset? StartedAt { get { lock (_gate) return _startedAt; } }
    public DateTimeOffset? FinishedAt { get { lock (_gate) return _finishedAt; } }
    public string? ErrorMessage { get { lock (_gate) return _errorMessage; } }
    public ProcessingStage FailedStage { get { lock (_gate) return _failedStage; } }
    public bool IsRetryable { get { lock (_gate) return _isRetryable; } }
    public int Attempts { get { lock (_gate) return _attempts; } }
    public bool StockReserved { get { lock (_gate) return _stockReserved; } }
    public string? PaymentId { get { lock (_gate) return _paymentId; } }
    public bool CustomerNotified { get { lock (_gate) return _customerNotified; } }

    public TimeSpan? Duration
    {
        get
        {
            lock (_gate)
            {
                return _startedAt is { } s && _finishedAt is { } f ? f - s : null;
            }
        }
    }

    /// <summary>
    /// Атомарно захватывает заказ для обработки (Pending -> Processing).
    /// Возвращает <c>false</c>, если заказ уже обрабатывается или находится в другом статусе.
    /// </summary>
    public bool TryBeginProcessing()
    {
        lock (_gate)
        {
            if (_status != OrderStatus.Pending)
            {
                return false;
            }

            _status = OrderStatus.Processing;
            _attempts++;
            _startedAt = DateTimeOffset.Now;
            _finishedAt = null;
            _errorMessage = null;
            _failedStage = ProcessingStage.None;
            _isRetryable = false;
            return true;
        }
    }

    public void MarkStockReserved(bool reserved)
    {
        lock (_gate)
        {
            EnsureStatus(OrderStatus.Processing);
            _stockReserved = reserved;
        }
    }

    public void MarkPaid(string paymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentId);
        lock (_gate)
        {
            EnsureStatus(OrderStatus.Processing);
            _paymentId = paymentId;
        }
    }

    public void MarkNotified()
    {
        lock (_gate)
        {
            EnsureStatus(OrderStatus.Processing);
            _customerNotified = true;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            TransitionUnsafe(OrderStatus.Completed);
        }
    }

    public void Fail(ProcessingStage stage, string message, bool isRetryable)
    {
        lock (_gate)
        {
            TransitionUnsafe(OrderStatus.Failed);
            _failedStage = stage;
            _errorMessage = message;
            _isRetryable = isRetryable;
        }
    }

    /// <summary>
    /// Отмена во время обработки (Processing -> Cancelled) или до её начала (Pending -> Cancelled).
    /// </summary>
    public void Cancel(ProcessingStage stage, string message)
    {
        lock (_gate)
        {
            TransitionUnsafe(OrderStatus.Cancelled);
            _failedStage = stage;
            _errorMessage = message;
            _isRetryable = true;
        }
    }

    /// <summary>
    /// Пытается отменить заказ, который ещё не начал обрабатываться.
    /// Возвращает <c>false</c>, если заказ уже не в статусе Pending (например, его захватила другая задача).
    /// </summary>
    public bool TryCancelPending(string message)
    {
        lock (_gate)
        {
            if (_status != OrderStatus.Pending)
            {
                return false;
            }

            TransitionUnsafe(OrderStatus.Cancelled);
            _failedStage = ProcessingStage.None;
            _errorMessage = message;
            _isRetryable = true;
            return true;
        }
    }

    /// <summary>
    /// Возвращает неуспешный заказ в очередь (Failed/Cancelled -> Pending), если повтор допустим.
    /// Уже выполненные этапы (резерв, оплата) сохраняются и при повторе не выполняются снова.
    /// </summary>
    public bool TryResetForRetry(out string reason)
    {
        lock (_gate)
        {
            if (_status is not (OrderStatus.Failed or OrderStatus.Cancelled))
            {
                reason = $"статус {_status} не допускает повтора";
                return false;
            }

            if (!_isRetryable)
            {
                reason = "ошибка не является временной: " + _errorMessage;
                return false;
            }

            TransitionUnsafe(OrderStatus.Pending);
            reason = string.Empty;
            return true;
        }
    }

    public override string ToString() => $"Заказ #{Id} ({Customer}, {Total:0.00} ₽, {Status})";

    // Вызывается только под _gate.
    private void TransitionUnsafe(OrderStatus to)
    {
        if (!OrderStatusTransitions.IsAllowed(_status, to))
        {
            throw new InvalidOrderTransitionException(Id, _status, to);
        }

        _status = to;
        if (to is OrderStatus.Completed or OrderStatus.Failed or OrderStatus.Cancelled)
        {
            _finishedAt = DateTimeOffset.Now;
        }
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (_status != expected)
        {
            throw new InvalidOperationException(
                $"Заказ #{Id}: операция допустима только в статусе {expected}, текущий статус {_status}.");
        }
    }
}

/// <summary>Способы оплаты, распознаваемые эмулятором платёжного шлюза.</summary>
public static class PaymentMethods
{
    /// <summary>Обычная карта — оплата проходит.</summary>
    public const string Card = "card";

    /// <summary>Карта, по которой банк отказывает (постоянная бизнес-ошибка).</summary>
    public const string DeclinedCard = "card-declined";

    /// <summary>Шлюз временно недоступен при первой попытке (временная ошибка, повтор поможет).</summary>
    public const string FlakyGateway = "card-flaky";

    /// <summary>Оплата бонусами: результат известен сразу, внешний вызов не нужен (Task.FromResult).</summary>
    public const string Bonus = "bonus";
}
