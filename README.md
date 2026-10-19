# ЛР № 3. Асинхронность и многопоточность в C#

**Вариант:** параллельная обработка заказов интернет-магазина (эмуляция).
**Платформа:** .NET 8, C# 12, консольное приложение для Linux и Windows.

Приложение эмулирует прохождение заказов через этапы «проверка → резерв на складе → оплата →
уведомление» без обращения к сети. Внешние службы заменены задержками `Task.Delay`, демонстрационные
заказы хранятся в локальном JSON-файле. На этой модели показаны `async/await`, TPL, отмена операций,
обработка исключений, примитивы синхронизации, потокобезопасные коллекции, `Parallel.For` и PLINQ.

Отчёт: [`reports/lab3/REPORT.md`](reports/lab3/REPORT.md).

## Требования

- .NET 8 SDK (проверено с 8.0.131 на Linux). Файл `global.json` выбирает SDK 8.0.x
  (`rollForward: latestFeature`). Если установлен только более новый SDK, установите .NET 8 SDK
  или удалите `global.json`.
- Интернет нужен **только один раз**, чтобы восстановить NuGet-пакеты тестового проекта
  (`xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`). У самого приложения внешних
  пакетов нет: `System.Threading.Channels` и `System.Text.Json` входят в .NET 8.

### Подготовка к работе без интернета

```bash
dotnet restore Lab3.OrderProcessing.sln   # пакеты попадут в локальный кэш ~/.nuget/packages
```

После этого сборка, тесты и запуск выполняются без сети. Если сборка запускается на другой машине
без доступа к nuget.org, перенесите на неё кэш `~/.nuget/packages` (Windows:
`%UserProfile%\.nuget\packages`) или выполните `dotnet restore` там заранее.

## Структура проекта

```
Lab3.OrderProcessing.sln
global.json, Directory.Build.props        общие настройки: net8.0, C# 12, nullable, предупреждения = ошибки
data/orders.json                          демонстрационные заказы (12 сценариев)
src/OrderProcessing.Core/                 библиотека: модель, службы, обработка, примитивы
  Domain/       Order, OrderItem, OrderStatus, таблица переходов, бизнес-исключения
  Services/     ProductCatalog (ValueTask), InventoryService (lock), PaymentService (Task.FromResult),
                NotificationService (Task), SimulationOptions (эмуляция задержек)
  Processing/   OrderProcessor, BatchOrderProcessor, ConcurrencyLimiter (SemaphoreSlim),
                ProcessingStatistics (Interlocked), OrderRepository (ConcurrentDictionary),
                OrderEventJournal (ConcurrentQueue), ShopEnvironment
  Concurrency/  счётчики lock/Monitor/Interlocked, ConcurrentQueueWorkerPool,
                BlockingCollectionPipeline, ChannelPipeline, MutexFileCounter
  Analytics/    OrderAnalytics — CPU-bound расчёты: последовательно, Parallel.For, PLINQ
  Data/         OrderFileStore (асинхронный JSON), DemoOrderFactory
src/OrderProcessing.App/                  консольное меню и демонстрации (пункты 1–19)
  Infrastructure/  ввод-вывод, отмена по Esc / Ctrl+C
  Demos/           WhenAll/WhenAny, исключения, коллекции, синхронизация, асинхронность vs параллелизм
tests/OrderProcessing.Tests/              77 тестов xUnit
tests/smoke/menu-smoke.txt                сценарий дымового прогона меню (используется в CI)
reports/lab3/                             отчёт, скриншоты, протокол реального запуска
.github/workflows/ci.yml                  сборка и тесты на ubuntu-latest и windows-latest
```

## Как устроена обработка заказа

`OrderProcessor.ProcessAsync(order, token)`:

1. Если отмена уже запрошена — заказ не запускается (`Pending → Cancelled`).
2. `order.TryBeginProcessing()` атомарно переводит `Pending → Processing`. Вторая задача с тем же
   заказом получает результат `Skipped` — одновременная повторная обработка невозможна.
3. **Проверка**: состав, e-mail, наличие товара в каталоге и цена (`ValueTask` с кэшем).
4. **Склад**: резерв всех позиций атомарно под `lock`.
5. **Оплата**: эмулированный шлюз; оплата бонусами возвращает `Task.FromResult`.
6. **Уведомление**: операция без результата (`Task`), письмо кладётся в локальный «исходящий ящик».
7. `Completed`. При ошибке или отмене резерв без оплаты возвращается на склад.

Допустимые переходы статусов: `Pending → Processing | Cancelled`,
`Processing → Completed | Failed | Cancelled`, `Failed → Pending` (только временная ошибка),
`Cancelled → Pending`. `Completed` — конечный статус, поэтому заказ не может быть одновременно
успешным и отменённым. Повторная обработка не повторяет выполненные этапы: деньги не списываются дважды.

### Сценарии демонстрационных данных (`data/orders.json`)

| № | Сценарий | Итог первой обработки |
|---|----------|-----------------------|
| 1, 3, 11, 12 | обычные заказы | Completed |
| 2 | оплата бонусами (`bonus`) | Completed, без обращения к шлюзу |
| 4 | пустой заказ | Failed, Validation |
| 5 | товара нет в каталоге | Failed, Validation |
| 6 | `card-declined` — отказ банка | Failed, Payment, повтор невозможен |
| 7 | `card-flaky` — тайм-аут шлюза | Failed, Payment, повтор успешен |
| 8 | адрес `offline.*` — почта недоступна | Failed, Notification, повтор отправляет только письмо |
| 9 | ноутбуков 3, на складе 2 | Failed, Inventory |
| 10 | `BROKEN-01` — повреждённая карточка | непредвиденное `InvalidOperationException`, Failed |

## Меню

| Пункт | Что показывает |
|-------|----------------|
| 1, 8, 9, 10 | список заказов и выборки по статусам |
| 2 | загрузка `data/orders.json` (асинхронный файловый ввод-вывод) или генерация N успешных заказов |
| 3 | обработка одного заказа с журналом этапов и номерами потоков |
| 4 | все ожидающие заказы параллельно, `Task.WhenAll` |
| 5 | параллельно с ограничением `SemaphoreSlim` |
| 6 | последовательно / параллельно / с ограничением на одинаковом наборе, `Stopwatch` |
| 7 | фоновая обработка и её отмена; отмена по тайм-ауту `CancelAfter` |
| 11 | повтор временных ошибок и отменённых заказов |
| 12 | статистика: фактические статусы, счётчики `Interlocked`, ошибки по этапам, журнал событий |
| 13, 14 | `Task.WhenAll`, `Task.WhenAny` (первая успешная, порядок завершения, тайм-аут) |
| 15 | бизнес-ошибки, отмена, непредвиденные исключения, `AggregateException`, `Task.WaitAll` |
| 16 | `ConcurrentDictionary`, `ConcurrentQueue`, `BlockingCollection`, `Channel<T>` |
| 17 | `lock`, `Monitor`, `Interlocked`, `SemaphoreSlim`, `Mutex` |
| 18 | `Thread`, `ThreadPool`, `Task.Run` и `Task.Delay`, `ValueTask`, `Parallel.For`, PLINQ |
| 19 | выход: фоновая обработка отменяется и дожидается завершения |

Ошибочный ввод (текст, число вне диапазона) отклоняется с подсказкой. Закрытие потока ввода (EOF)
корректно завершает приложение.

### Отмена

- Во время операций пунктов 3–6 и 11 нажмите **Esc** или **Ctrl+C** — операция отменяется через
  `CancellationTokenSource`, приложение продолжает работать.
- Пункт 7 → 1 запускает обработку в фоне; можно вернуться в меню, посмотреть заказы в статусе
  `Processing` и отменить обработку через пункт 7 → 2.
- Пункт 7 → 3 отменяет пакет по тайм-ауту (`CancelAfter`).

Токен передаётся по всей цепочке вызовов, задержки используют `Task.Delay(…, token)`, на границах
этапов вызывается `ThrowIfCancellationRequested`. Завершённые заказы сохраняют статус `Completed`,
прерванные получают `Cancelled`, ожидавшие слота семафора не запускаются.

### Ошибки

Бизнес-ошибки (`OrderValidationException`, `OutOfStockException`, `PaymentException`,
`NotificationException`) переводят заказ в `Failed` с указанием этапа и признака «можно повторить».
`OperationCanceledException` при запрошенной отмене даёт `Cancelled`. Непредвиденное исключение
фиксируется в заказе (он не остаётся в `Processing`) и **пробрасывается** вызывающему коду; пакетная
обработка собирает такие исключения в `BatchResult.UnexpectedErrors`, не теряя результатов
остальных заказов.

## Синхронизация и коллекции

| Механизм | Где используется |
|----------|------------------|
| `lock` | состояние заказа (`Order`), остатки склада (`InventoryService`), консольный вывод |
| `Monitor` | `MonitorCounter`: `Enter/Exit` в `finally`, `TryEnter` с тайм-аутом |
| `Interlocked` | `ProcessingStatistics`, кэш-счётчики каталога, генератор номеров заказов, максимум через `CompareExchange` |
| `SemaphoreSlim` | `ConcurrencyLimiter` — ограничение числа одновременно обрабатываемых заказов |
| `Mutex` | `MutexFileCounter` — файл-счётчик номеров чеков, именованный мьютекс (пункт 17) |
| `ConcurrentDictionary` | `OrderRepository` (заказы и результаты по номеру), попытки оплаты, статистика по этапам |
| `ConcurrentQueue` | журнал событий, исходящий ящик писем, `ConcurrentQueueWorkerPool` |
| `BlockingCollection` | `BlockingCollectionPipeline` — ограниченный производитель-потребитель на выделенных потоках |
| `Channel<T>` | `ChannelPipeline` — ограниченный асинхронный канал с backpressure и отменой |

## Автономный режим

Приложение и тесты не выполняют сетевых запросов: задержки внешних служб моделирует
`Task.Delay`, данные читаются из `data/orders.json`, временные файлы (пункт 17, тесты) создаются в
`Path.GetTempPath()` и удаляются. `HttpListener` не используется: эмуляция через `Task.Delay`
проще и не требует портов и прав. Приложение запускалось в сетевом пространстве имён без
интерфейсов, кроме `lo` (`unshare -n`), и отработало полностью.

Пути строятся через `Path.Combine` и `AppContext.BaseDirectory`, поэтому зависимостей от
разделителей Linux нет. Файл `data/orders.json` копируется в каталог сборки
(`bin/<Configuration>/net8.0/data/orders.json`).

## Сборка, тесты и запуск

### Linux (Bash)

```bash
dotnet --info
dotnet restore Lab3.OrderProcessing.sln
dotnet build Lab3.OrderProcessing.sln
dotnet test Lab3.OrderProcessing.sln
dotnet run --project src/OrderProcessing.App/OrderProcessing.App.csproj
```

### Windows (PowerShell)

```powershell
dotnet --info
dotnet restore Lab3.OrderProcessing.sln
dotnet build Lab3.OrderProcessing.sln
dotnet test Lab3.OrderProcessing.sln
dotnet run --project src\OrderProcessing.App\OrderProcessing.App.csproj
```

Приложение само переключает вывод консоли на UTF-8. Если кириллица в старом окне `cmd.exe`
отображается неверно, используйте Windows Terminal или выполните `chcp 65001`.

### Неинтерактивный прогон меню

```bash
dotnet run --project src/OrderProcessing.App/OrderProcessing.App.csproj < tests/smoke/menu-smoke.txt
```

```powershell
Get-Content tests\smoke\menu-smoke.txt | dotnet run --project src\OrderProcessing.App\OrderProcessing.App.csproj
```

## Что проверено

- Linux (Ubuntu 24.04, .NET SDK 8.0.131): `restore`, `build` (Debug и Release, без предупреждений),
  `dotnet test` — 77 из 77 тестов; 40 повторных прогонов тестов подряд и 5 прогонов на одном ядре
  (`taskset -c 0`) без падений; ручной прогон всех пунктов меню; запуск без сети.
- Windows локально **не запускалась**. Сборку, тесты и дымовой прогон меню на `windows-latest`
  выполняет workflow GitHub Actions (`.github/workflows/ci.yml`); его результат смотрите на вкладке
  Actions репозитория.

## Известные ограничения

- Время в пунктах 6 и 18 зависит от машины, нагрузки и планировщика; ускорение не гарантируется.
- Строка «без синхронизации» в пункте 17 и столбец «чтение+запись» в пункте 16 демонстрируют
  гонку; величина потерь от запуска к запуску разная и теоретически может быть нулевой.
- Ctrl+C вне выполняющейся операции завершает процесс стандартным образом; для корректного
  выхода с отменой фоновой обработки используйте пункт 19.
- Именованный мьютекс в пункте 17 использует имя без префикса `Global\`, т. е. область сеанса
  пользователя. Межпроцессная синхронизация проверялась только в пределах одного процесса и потоков.
- Состояние магазина хранится в памяти и сбрасывается при перезапуске.
