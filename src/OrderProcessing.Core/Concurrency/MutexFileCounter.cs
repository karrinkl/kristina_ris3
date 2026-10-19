using System.Globalization;

namespace OrderProcessing.Core.Concurrency;

/// <summary>
/// Счётчик выданных номеров чеков, хранящийся в локальном файле.
/// Операция «прочитать файл — увеличить — записать» не атомарна, поэтому защищена <see cref="Mutex"/>.
/// <para>
/// В отличие от <c>lock</c>, мьютекс — объект ядра ОС: именованный мьютекс виден другим процессам,
/// поэтому несколько экземпляров приложения могут разделять один файл. Мьютекс принадлежит потоку:
/// освободить его должен тот же поток, что захватил, поэтому внутри критической секции нет <c>await</c>.
/// </para>
/// </summary>
public sealed class MutexFileCounter : IDisposable
{
    private readonly Mutex _mutex;

    /// <param name="filePath">Путь к файлу счётчика.</param>
    /// <param name="mutexName">Имя для межпроцессной синхронизации или <c>null</c> для локального мьютекса.</param>
    public MutexFileCounter(string filePath, string? mutexName)
    {
        FilePath = filePath;
        _mutex = new Mutex(initiallyOwned: false, mutexName, out var createdNew);
        CreatedNew = createdNew;
        IsNamed = mutexName is not null;
    }

    public string FilePath { get; }
    public bool IsNamed { get; }

    /// <summary><c>false</c>, если мьютекс с таким именем уже существовал (например, создан другим процессом).</summary>
    public bool CreatedNew { get; }

    public long NextNumber(TimeSpan timeout)
    {
        var acquired = false;
        try
        {
            try
            {
                acquired = _mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // Предыдущий владелец завершился, не освободив мьютекс. Владение перешло к нам,
                // но данные под защитой могли остаться несогласованными — здесь файл перезаписывается целиком.
                acquired = true;
            }

            if (!acquired)
            {
                throw new TimeoutException($"Не удалось захватить мьютекс за {timeout.TotalMilliseconds:0} мс.");
            }

            var current = File.Exists(FilePath)
                ? long.Parse(File.ReadAllText(FilePath).Trim(), CultureInfo.InvariantCulture)
                : 0;
            var next = current + 1;
            File.WriteAllText(FilePath, next.ToString(CultureInfo.InvariantCulture));
            return next;
        }
        finally
        {
            if (acquired)
            {
                _mutex.ReleaseMutex();
            }
        }
    }

    public long ReadCurrent() =>
        File.Exists(FilePath) ? long.Parse(File.ReadAllText(FilePath).Trim(), CultureInfo.InvariantCulture) : 0;

    public void Dispose() => _mutex.Dispose();
}
