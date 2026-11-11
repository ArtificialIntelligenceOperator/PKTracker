using Microsoft.Extensions.Hosting;
using PKTracker.Data;

namespace PKTracker.Mcp;

/// <summary>
/// Режим «закрывать PKTracker, пока открыт клиент GG» относится и к MCP-серверу: это отдельный процесс трекера,
/// и рядом с клиентом GG он оставаться не должен. В остальных режимах сервер продолжает работать (инструменты
/// анализа при запущенном клиенте рума и так не отвечают — <see cref="TrackerContext.AnalysisHostAsync"/>).
/// </summary>
internal sealed class GgCoexistenceWatch(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Настройки перечитываются каждый раз: режим меняют в приложении, пока сервер уже работает.
                if (TrackerSettings.Load().WhenGgRunning == GgCoexistence.Exit && RoomClientProbe.ForbiddenCoexistence() is { } client)
                {
                    Console.Error.WriteLine($"[pktracker] Запущен клиент GG ({client}): по настройке «Пока открыт клиент GG — закрывать PKTracker» MCP-сервер завершается.");
                    lifetime.StopApplication();
                    return;
                }
            }
            // settings.json в этот момент может переписывать приложение. Исключение фоновой службы остановило бы
            // весь сервер, поэтому сбой чтения — просто пропуск до следующей проверки.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
