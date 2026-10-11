namespace ServerActivity.Api.TeamSpeak;

public sealed record TeamSpeakServer(
    long Id,
    string Name,
    string Host,
    int QueryPort,
    int VirtualServerId,
    string QueryUsername,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    int ActiveClients);

public sealed record TeamSpeakClient(string DatabaseId, string Nickname);

public sealed record CreateTeamSpeakServerRequest(
    string? Name,
    string? Host,
    int QueryPort,
    int VirtualServerId,
    string? QueryUsername,
    string? QueryPassword);

public sealed record TeamSpeakSyncResult(int ActiveClients, int StartedSessions, int EndedSessions);

public sealed class TeamSpeakConnectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
