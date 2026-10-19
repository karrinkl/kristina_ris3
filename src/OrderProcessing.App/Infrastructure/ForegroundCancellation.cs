namespace OrderProcessing.App.Infrastructure;

/// <summary>
/// Источник отмены для операции, выполняемой «на переднем плане».
/// Отмена запрашивается клавишей Esc (если консоль интерактивна), сочетанием Ctrl+C
/// или автоматически по тайм-ауту. Слушатель клавиатуры — отдельная асинхронная задача,
/// которая гарантированно останавливается и ожидается в <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class ForegroundCancellation : IAsyncDisposable
{
    private static ForegroundCancellation? _current;

    private readonly CancellationTokenSource _cts;
    private readonly CancellationTokenSource _stopListener = new();
    private readonly Task _keyListener;

    private ForegroundCancellation(TimeSpan? timeout)
    {
        _cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        _keyListener = Console.IsInputRedirected ? Task.CompletedTask : ListenForEscapeAsync(_stopListener.Token);
        Volatile.Write(ref _current, this);
    }

    public CancellationToken Token => _cts.Token;

    public bool IsCancellationRequested => _cts.IsCancellationRequested;

    public static ForegroundCancellation Start(TimeSpan? timeout = null)
    {
        if (!Console.IsInputRedirected)
        {
            Ui.Explain("Нажмите Esc или Ctrl+C, чтобы отменить операцию.");
        }

        return new ForegroundCancellation(timeout);
    }

    /// <summary>Вызывается обработчиком Ctrl+C. Возвращает true, если была активная операция.</summary>
    public static bool TryCancelCurrent()
    {
        var current = Volatile.Read(ref _current);
        if (current is null)
        {
            return false;
        }

        current.Cancel();
        return true;
    }

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Операция уже завершилась и источник освобождён — отменять нечего.
            Ui.Explain("Операция уже завершена.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.CompareExchange(ref _current, null, this);
        _stopListener.Cancel();
        await _keyListener.ConfigureAwait(false); // слушатель не остаётся работать в фоне
        _stopListener.Dispose();
        _cts.Dispose();
    }

    private async Task ListenForEscapeAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                while (Console.KeyAvailable)
                {
                    if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                    {
                        Ui.Warning(">>> Запрошена отмена (Esc)");
                        _cts.Cancel();
                    }
                }

                await Task.Delay(50, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Нормальная остановка слушателя при завершении операции.
            return;
        }
        catch (InvalidOperationException ex)
        {
            // Консоль без клавиатуры (например, перенаправленный ввод) — отмена доступна через Ctrl+C.
            Ui.Explain("Отмена по Esc недоступна: " + ex.Message);
        }
    }
}
