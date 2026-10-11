using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using ServerActivity.Api.Analysis;

namespace ServerActivity.Api.TeamSpeak;

public sealed class TeamSpeakService
{
    private readonly string _connectionString;
    private readonly TeamSpeakQueryClient _queryClient;
    private readonly IDataProtector _passwordProtector;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _syncLocks = new();

    public TeamSpeakService(
        IWebHostEnvironment environment,
        IConfiguration configuration,
        TeamSpeakQueryClient queryClient,
        IDataProtectionProvider dataProtectionProvider)
    {
        _queryClient = queryClient;
        _passwordProtector = dataProtectionProvider.CreateProtector("ServerActivity.TeamSpeak.QueryPassword.v1");
        var path = configuration["LocalPlaytime:DatabasePath"] ??
            Path.Combine(environment.ContentRootPath, "data", "playtime.db");
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString();
        InitializeDatabase();
    }

    public IReadOnlyList<TeamSpeakServer> GetServers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server.Id, server.Name, server.Host, server.QueryPort, server.VirtualServerId,
                server.QueryUsername, server.LastSyncAt, server.LastError,
                (SELECT COUNT(*) FROM Sessions AS session
                 WHERE session.TeamSpeakServerId = server.Id AND session.DisconnectedAt IS NULL)
            FROM TeamSpeakServers AS server
            ORDER BY server.Name COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        var servers = new List<TeamSpeakServer>();
        while (reader.Read())
        {
            servers.Add(ReadServer(reader));
        }

        return servers;
    }

    public long AddServer(CreateTeamSpeakServerRequest request)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TeamSpeakServers (
                Name, Host, QueryPort, VirtualServerId, QueryUsername, EncryptedPassword, CreatedAt)
            VALUES ($name, $host, $port, $virtualServerId, $username, $password, $createdAt)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$name", request.Name!.Trim());
        command.Parameters.AddWithValue("$host", request.Host!.Trim());
        command.Parameters.AddWithValue("$port", request.QueryPort);
        command.Parameters.AddWithValue("$virtualServerId", request.VirtualServerId);
        command.Parameters.AddWithValue("$username", request.QueryUsername!.Trim());
        command.Parameters.AddWithValue("$password", _passwordProtector.Protect(request.QueryPassword!));
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        return (long)command.ExecuteScalar()!;
    }

    public bool RemoveServer(long id)
    {
        var syncLock = _syncLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        syncLock.Wait();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TeamSpeakServers WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteNonQuery() > 0;
        }
        finally
        {
            syncLock.Release();
        }
    }

    public async Task<int> TestConnectionAsync(long id, CancellationToken cancellationToken)
    {
        var server = GetServerConnection(id);
        try
        {
            var clients = await _queryClient.GetClientsAsync(
                server.Host,
                server.QueryPort,
                server.VirtualServerId,
                server.QueryUsername,
                server.QueryPassword,
                cancellationToken);
            SetLastError(id, null);
            return clients.Count;
        }
        catch (TeamSpeakConnectionException exception)
        {
            SetLastError(id, exception.Message);
            throw;
        }
    }

    public async Task<TeamSpeakSyncResult> SynchronizeAsync(long id, CancellationToken cancellationToken)
    {
        var syncLock = _syncLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        await syncLock.WaitAsync(cancellationToken);
        try
        {
            var server = GetServerConnection(id);
            IReadOnlyList<TeamSpeakClient> clients;
            try
            {
                clients = await _queryClient.GetClientsAsync(
                    server.Host,
                    server.QueryPort,
                    server.VirtualServerId,
                    server.QueryUsername,
                    server.QueryPassword,
                    cancellationToken);
            }
            catch (TeamSpeakConnectionException exception)
            {
                SetLastError(id, exception.Message);
                throw;
            }

            var result = UpdateSessions(id, server.Name, clients, DateTimeOffset.UtcNow);
            SetLastError(id, null);
            return result;
        }
        finally
        {
            syncLock.Release();
        }
    }

    public AnalysisResult GetAnalysis(SessionAnalyzer analyzer) =>
        analyzer.AnalyzeRecords(GetSessions());

    public IReadOnlyList<SessionRecord> GetSessions()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Service, ServerName, Game, Player, ConnectedAt, DisconnectedAt
            FROM Sessions
            ORDER BY ConnectedAt;
            """;
        using var reader = command.ExecuteReader();
        var now = DateTimeOffset.UtcNow;
        var sessions = new List<SessionRecord>();
        while (reader.Read())
        {
            var connectedAt = DateTimeOffset.Parse(reader.GetString(4));
            var disconnectedAt = reader.IsDBNull(5) ? now : DateTimeOffset.Parse(reader.GetString(5));
            if (disconnectedAt > connectedAt)
            {
                sessions.Add(new SessionRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    connectedAt,
                    disconnectedAt));
            }
        }

        return sessions;
    }

    public int ImportCsvSessions(IReadOnlyList<SessionRecord> sessions, string csv)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var imported = 0;
        var importHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Sessions (
                    SourceKey, Service, ServerName, Game, Player, ConnectedAt, DisconnectedAt)
                VALUES ($sourceKey, $service, $serverName, $game, $player, $connectedAt, $disconnectedAt)
                ON CONFLICT(SourceKey) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$sourceKey", $"{importHash}:{index}");
            command.Parameters.AddWithValue("$service", session.Service);
            command.Parameters.AddWithValue("$serverName", session.ServerName);
            command.Parameters.AddWithValue("$game", session.Game);
            command.Parameters.AddWithValue("$player", session.Player);
            command.Parameters.AddWithValue("$connectedAt", session.ConnectedAt.ToUniversalTime().ToString("O"));
            command.Parameters.AddWithValue("$disconnectedAt", session.DisconnectedAt.ToUniversalTime().ToString("O"));
            imported += command.ExecuteNonQuery();
        }

        transaction.Commit();
        return imported;
    }

    private TeamSpeakSyncResult UpdateSessions(
        long serverId,
        string serverName,
        IReadOnlyList<TeamSpeakClient> clients,
        DateTimeOffset now)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var openClients = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ClientDatabaseId, Player FROM Sessions
                WHERE TeamSpeakServerId = $serverId AND DisconnectedAt IS NULL;
                """;
            command.Parameters.AddWithValue("$serverId", serverId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                openClients.Add(reader.GetString(0), reader.GetString(1));
            }
        }

        var currentClientIds = clients.Select(client => client.DatabaseId)
            .ToHashSet(StringComparer.Ordinal);
        var ended = 0;
        foreach (var databaseId in openClients.Keys.Where(id => !currentClientIds.Contains(id)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE Sessions SET DisconnectedAt = $endedAt
                WHERE TeamSpeakServerId = $serverId AND ClientDatabaseId = $clientId
                    AND DisconnectedAt IS NULL;
                """;
            command.Parameters.AddWithValue("$endedAt", now.ToString("O"));
            command.Parameters.AddWithValue("$serverId", serverId);
            command.Parameters.AddWithValue("$clientId", databaseId);
            ended += command.ExecuteNonQuery();
        }

        var started = 0;
        foreach (var client in clients.Where(client => !openClients.ContainsKey(client.DatabaseId)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Sessions (
                    SourceKey, TeamSpeakServerId, ClientDatabaseId, Service, ServerName, Game,
                    Player, ConnectedAt)
                VALUES (NULL, $serverId, $clientId, 'teamspeak', $serverName, '', $player, $startedAt);
                """;
            command.Parameters.AddWithValue("$serverId", serverId);
            command.Parameters.AddWithValue("$clientId", client.DatabaseId);
            command.Parameters.AddWithValue("$serverName", serverName);
            command.Parameters.AddWithValue("$player", client.Nickname);
            command.Parameters.AddWithValue("$startedAt", now.ToString("O"));
            started += command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE TeamSpeakServers SET LastSyncAt = $now WHERE Id = $id;";
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$id", serverId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new TeamSpeakSyncResult(clients.Count, started, ended);
    }

    private (string Name, string Host, int QueryPort, int VirtualServerId, string QueryUsername, string QueryPassword)
        GetServerConnection(long id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Name, Host, QueryPort, VirtualServerId, QueryUsername, EncryptedPassword
            FROM TeamSpeakServers WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new KeyNotFoundException("Nie znaleziono skonfigurowanego serwera TeamSpeak.");
        }

        string password;
        try
        {
            password = _passwordProtector.Unprotect(reader.GetString(5));
        }
        catch (CryptographicException exception)
        {
            throw new TeamSpeakConnectionException(
                "Nie można odszyfrować hasła ServerQuery. Sprawdź, czy przywrócono katalog kluczy Data Protection.",
                exception);
        }

        return (
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetString(4),
            password);
    }

    private void SetLastError(long id, string? error)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE TeamSpeakServers SET LastError = $error WHERE Id = $id;";
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static TeamSpeakServer ReadServer(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8));

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TeamSpeakServers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Host TEXT NOT NULL,
                QueryPort INTEGER NOT NULL,
                VirtualServerId INTEGER NOT NULL,
                QueryUsername TEXT NOT NULL,
                EncryptedPassword TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                LastSyncAt TEXT NULL,
                LastError TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceKey TEXT NULL UNIQUE,
                TeamSpeakServerId INTEGER NULL,
                ClientDatabaseId TEXT NULL,
                Service TEXT NOT NULL,
                ServerName TEXT NOT NULL,
                Game TEXT NOT NULL,
                Player TEXT NOT NULL,
                ConnectedAt TEXT NOT NULL,
                DisconnectedAt TEXT NULL,
                FOREIGN KEY (TeamSpeakServerId) REFERENCES TeamSpeakServers(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Sessions_OpenTeamSpeakClient
                ON Sessions (TeamSpeakServerId, ClientDatabaseId)
                WHERE TeamSpeakServerId IS NOT NULL AND DisconnectedAt IS NULL;
            CREATE INDEX IF NOT EXISTS IX_Sessions_ConnectedAt ON Sessions (ConnectedAt);
            """;
        command.ExecuteNonQuery();
        using var closeOpenSessions = connection.CreateCommand();
        closeOpenSessions.CommandText = """
            UPDATE Sessions
            SET DisconnectedAt = (
                SELECT LastSyncAt FROM TeamSpeakServers
                WHERE TeamSpeakServers.Id = Sessions.TeamSpeakServerId
            )
            WHERE TeamSpeakServerId IS NOT NULL
                AND DisconnectedAt IS NULL
                AND EXISTS (
                    SELECT 1 FROM TeamSpeakServers
                    WHERE TeamSpeakServers.Id = Sessions.TeamSpeakServerId
                        AND TeamSpeakServers.LastSyncAt IS NOT NULL
                );
            """;
        closeOpenSessions.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
