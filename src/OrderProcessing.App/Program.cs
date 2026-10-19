using System.Text;
using OrderProcessing.App;
using OrderProcessing.App.Infrastructure;
using OrderProcessing.Core.Processing;

try
{
    // Кириллица в консоли Windows; на Linux UTF-8 используется по умолчанию.
    Console.OutputEncoding = Encoding.UTF8;
}
catch (IOException ex)
{
    Console.Error.WriteLine($"Не удалось установить кодировку UTF-8: {ex.Message}");
}

// Ctrl+C отменяет текущую операцию, а не завершает процесс аварийно.
Console.CancelKeyPress += (_, e) =>
{
    if (ForegroundCancellation.TryCancelCurrent())
    {
        e.Cancel = true;
        Ui.Warning(">>> Запрошена отмена текущей операции (Ctrl+C)");
    }
};

var shop = new ShopEnvironment();
await new MainMenu(shop).RunAsync();
return 0;
