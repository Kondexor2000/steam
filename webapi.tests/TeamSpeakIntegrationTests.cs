using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.IO.Compression;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServerActivity.Api.Authentication;

namespace ServerActivity.Api.Tests;

public sealed class TeamSpeakIntegrationTests
{
    [Fact]
    public async Task AdministratorCanSyncQuerySessionsAndRegularUsersCannotManageServers()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"teamspeak-test-{Guid.NewGuid():N}");
        using var queryListener = new TcpListener(IPAddress.Loopback, 0);
        queryListener.Start();
        var queryPort = ((IPEndPoint)queryListener.LocalEndpoint).Port;
        var queryServer = ServeOneClientList(queryListener);
        var adminLogin = $"admin-{Guid.NewGuid():N}";
        var userLogin = $"user-{Guid.NewGuid():N}";
        var configuration = new Dictionary<string, string?>
        {
            ["Authentication:DatabasePath"] = Path.Combine(dataDirectory, "users.db"),
            ["LocalPlaytime:DatabasePath"] = Path.Combine(dataDirectory, "playtime.db"),
            ["Security:BackupDirectory"] = Path.Combine(dataDirectory, "backups"),
            ["Security:AdminLogins"] = adminLogin,
            ["Security:BackupKey"] = Convert.ToBase64String(new byte[32]),
        };

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(configuration));
                    builder.ConfigureServices(services =>
                        services.AddDataProtection().PersistKeysToFileSystem(
                            new DirectoryInfo(Path.Combine(dataDirectory, "keys"))));
                });
            using var client = factory.CreateClient();
            var authService = factory.Services.GetRequiredService<AuthService>();
            const string password = "strong-password-123";
            Assert.True(authService.Register(adminLogin, password, "192.0.2.10"));
            Assert.True(authService.Register(userLogin, password, "192.0.2.10"));
            var adminToken = authService.Login(adminLogin, password, "192.0.2.10")!.Token;
            var userToken = authService.Login(userLogin, password, "192.0.2.10")!.Token;

            using var deniedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/teamspeak/servers");
            deniedRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
            deniedRequest.Content = JsonContent.Create(new
            {
                name = "Test",
                host = "127.0.0.1",
                queryPort,
                virtualServerId = 1,
                queryUsername = "query-user",
                queryPassword = "query-password",
            });
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(deniedRequest)).StatusCode);

            using var addRequest = new HttpRequestMessage(HttpMethod.Post, "/api/teamspeak/servers");
            addRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
            addRequest.Content = JsonContent.Create(new
            {
                name = "Główny TeamSpeak",
                host = "127.0.0.1",
                queryPort,
                virtualServerId = 1,
                queryUsername = "query-user",
                queryPassword = "query-password",
            });
            var addResponse = await client.SendAsync(addRequest);
            Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);
            var created = await addResponse.Content.ReadFromJsonAsync<CreatedServer>();
            Assert.NotNull(created);

            using var testRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/teamspeak/servers/{created.Id}/test");
            testRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
            var testResponse = await client.SendAsync(testRequest);
            testResponse.EnsureSuccessStatusCode();
            Assert.Contains("activeClients\":1", await testResponse.Content.ReadAsStringAsync());

            using var syncRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/teamspeak/servers/{created.Id}/sync");
            syncRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
            var syncResponse = await client.SendAsync(syncRequest);
            syncResponse.EnsureSuccessStatusCode();
            var sync = await syncResponse.Content.ReadFromJsonAsync<SyncResult>();
            Assert.Equal(1, sync?.ActiveClients);
            Assert.Equal(1, sync?.StartedSessions);

            using var analysisRequest = new HttpRequestMessage(HttpMethod.Get, "/api/teamspeak/analysis");
            analysisRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
            var analysisResponse = await client.SendAsync(analysisRequest);
            analysisResponse.EnsureSuccessStatusCode();
            var analysis = await analysisResponse.Content.ReadAsStringAsync();
            Assert.Contains("Główny TeamSpeak", analysis);
            Assert.Contains("Alice Smith", analysis);

            var serversRequest = new HttpRequestMessage(HttpMethod.Get, "/api/teamspeak/servers");
            serversRequest.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", userToken);
            var serversResponse = await client.SendAsync(serversRequest);
            var serverList = await serversResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain("query-password", serverList);

            await queryServer;

            using var database = new SqliteConnection(
                $"Data Source={Path.Combine(dataDirectory, "playtime.db")}");
            database.Open();
            using var credentialCommand = database.CreateCommand();
            credentialCommand.CommandText = "SELECT EncryptedPassword FROM TeamSpeakServers WHERE Id = $id;";
            credentialCommand.Parameters.AddWithValue("$id", created.Id);
            var encryptedPassword = (string)credentialCommand.ExecuteScalar()!;
            Assert.NotEqual("query-password", encryptedPassword);

            var backups = factory.Services.GetRequiredService<ISecurityBackupService>();
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(dataDirectory, "keys")));
            backups.CreateEncryptedBackup();
            using var archiveStream = new MemoryStream(backups.GetDecryptedBackup());
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, entry =>
                entry.FullName.StartsWith("data-protection-keys/", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static async Task ServeOneClientList(TcpListener listener)
    {
        for (var connection = 0; connection < 2; connection++)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var greeting = Encoding.UTF8.GetBytes(
                "TS3\n\rWelcome to the TeamSpeak 3 ServerQuery interface.\n\r\n\r");
            await stream.WriteAsync(greeting);

            while (await reader.ReadLineAsync() is { } command)
            {
                if (command.StartsWith("clientlist ", StringComparison.Ordinal))
                {
                    var response = Encoding.UTF8.GetBytes(
                        "clid=1 client_database_id=42 client_type=0 client_nickname=Alice\\sSmith\n\r" +
                        "error id=0 msg=ok\n\r\n\r");
                    await stream.WriteAsync(response);
                    break;
                }

                await stream.WriteAsync(Encoding.UTF8.GetBytes("error id=0 msg=ok\n\r\n\r"));
            }
        }
    }

    private sealed record CreatedServer(long Id);
    private sealed record SyncResult(int ActiveClients, int StartedSessions, int EndedSessions);
}
