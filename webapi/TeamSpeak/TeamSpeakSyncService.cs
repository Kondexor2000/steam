namespace ServerActivity.Api.TeamSpeak;

public sealed class TeamSpeakSyncService(
    TeamSpeakService service,
    ILogger<TeamSpeakSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SyncInterval, stoppingToken);
                foreach (var server in service.GetServers())
                {
                    try
                    {
                        await service.SynchronizeAsync(server.Id, stoppingToken);
                    }
                    catch (Exception exception) when (
                        exception is TeamSpeakConnectionException or KeyNotFoundException)
                    {
                        logger.LogWarning(exception, "Synchronizacja serwera TeamSpeak {ServerId} nie powiodła się.", server.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Nieoczekiwany błąd cyklicznej synchronizacji TeamSpeak.");
            }
        }
    }
}
