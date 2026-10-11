using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ServerActivity.Api.Authentication;

namespace ServerActivity.Api.Tests;

public sealed class ApiIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dataDirectory;

    public ApiIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), $"server-activity-api-test-{Guid.NewGuid():N}");
        var configuration = new Dictionary<string, string?>
        {
            ["Authentication:DatabasePath"] = Path.Combine(_dataDirectory, "users.db"),
            ["LocalPlaytime:DatabasePath"] = Path.Combine(_dataDirectory, "playtime.db"),
            ["Security:BackupDirectory"] = Path.Combine(_dataDirectory, "backups"),
        };
        _factory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(configuration)));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task UserCanRegisterLoginAndAnalyzeSessions()
    {
        var login = $"player-{Guid.NewGuid():N}";
        var registration = await _client.PostAsJsonAsync(
            "/api/auth/register", new { login, password = "strong-password-123" });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

        var loginResponse = await _client.PostAsJsonAsync(
            "/api/auth/login", new { login, password = "strong-password-123" });
        loginResponse.EnsureSuccessStatusCode();
        var auth = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(auth?.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/analysis/csv");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(
            "service,server_name,game,player,connected_at,disconnected_at\n" +
            "game,Survival,Minecraft,Ala,2026-09-01T18:00:00Z,2026-09-01T20:00:00Z",
            Encoding.UTF8,
            "text/csv"), "file", "sessions.csv");
        request.Content = form;

        var analysis = await _client.SendAsync(request);

        analysis.EnsureSuccessStatusCode();
        var json = await analysis.Content.ReadAsStringAsync();
        Assert.Contains("\"sessions\":1", json);
        Assert.Contains("\"totalHours\":2", json);
    }

    [Fact]
    public async Task AnalysisRequiresAuthenticationAndRejectsBadPassword()
    {
        var anonymous = await _client.PostAsync("/api/analysis/csv", new MultipartFormDataContent());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var login = $"player-{Guid.NewGuid():N}";
        await _client.PostAsJsonAsync(
            "/api/auth/register", new { login, password = "strong-password-123" });
        var invalidLogin = await _client.PostAsJsonAsync(
            "/api/auth/login", new { login, password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, invalidLogin.StatusCode);
    }

    [Fact]
    public async Task LoginFromANewIpCreatesAnEncryptedBackupOnlyDownloadableByConfiguredAdministrators()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"server-activity-test-{Guid.NewGuid():N}");
        var adminLogin = $"admin-{Guid.NewGuid():N}";
        var userLogin = $"user-{Guid.NewGuid():N}";
        var configuration = new Dictionary<string, string?>
        {
            ["Authentication:DatabasePath"] = Path.Combine(dataDirectory, "users.db"),
            ["LocalPlaytime:DatabasePath"] = Path.Combine(dataDirectory, "playtime.db"),
            ["Security:BackupDirectory"] = Path.Combine(dataDirectory, "backups"),
            ["Security:BackupKey"] = Convert.ToBase64String(new byte[32]),
            ["Security:AdminLogins"] = adminLogin,
        };

        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                    builder.ConfigureAppConfiguration((_, config) =>
                        config.AddInMemoryCollection(configuration)));
            using var client = factory.CreateClient();
            var authService = factory.Services.GetRequiredService<AuthService>();
            const string password = "strong-password-123";
            const string originalIp = "192.0.2.10";

            Assert.True(authService.Register(adminLogin, password, originalIp));
            Assert.True(authService.Register(userLogin, password, originalIp));

            var adminLoginResult = authService.Login(adminLogin, password, "192.0.2.11");
            Assert.NotNull(adminLoginResult);
            Assert.True(adminLoginResult.IsAdministrator);
            Assert.Null(adminLoginResult.SecurityWarning);

            var history = authService.GetLoginHistory(adminLogin);
            Assert.Equal("created", history[0].BackupStatus);
            Assert.True(history[0].IsNewIp);
            Assert.Equal("192.0.2.11", history[0].IpAddress);
            Assert.True(File.Exists(Path.Combine(dataDirectory, "backups", "backup.enc")));

            var userLoginResult = authService.Login(userLogin, password, originalIp);
            Assert.NotNull(userLoginResult);
            Assert.False(userLoginResult.IsAdministrator);
            Assert.Null(userLoginResult.SecurityWarning);

            using var userBackupRequest = new HttpRequestMessage(HttpMethod.Get, "/api/security/backup");
            userBackupRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", userLoginResult.Token);
            var userBackup = await client.SendAsync(userBackupRequest);
            Assert.Equal(HttpStatusCode.Forbidden, userBackup.StatusCode);

            using var adminBackupRequest = new HttpRequestMessage(HttpMethod.Get, "/api/security/backup");
            adminBackupRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", adminLoginResult.Token);
            var adminBackup = await client.SendAsync(adminBackupRequest);
            Assert.Equal(HttpStatusCode.OK, adminBackup.StatusCode);

            using var archiveStream = new MemoryStream(await adminBackup.Content.ReadAsByteArrayAsync());
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            Assert.Contains(archive.Entries, entry => entry.Name == "users.db");
            Assert.Contains(archive.Entries, entry => entry.Name == "playtime.db");

            using var historyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/security/login-history");
            historyRequest.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", adminLoginResult.Token);
            var historyResponse = await client.SendAsync(historyRequest);
            historyResponse.EnsureSuccessStatusCode();
            var historyJson = await historyResponse.Content.ReadAsStringAsync();
            Assert.Contains("\"isNewIp\":true", historyJson);
            Assert.Contains("\"backupStatus\":\"created\"", historyJson);
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

    [Fact]
    public async Task LocalPlaytimeTracksAndStopsAProcessForTheAuthenticatedUser()
    {
        var token = await RegisterAndLogin($"local-{Guid.NewGuid():N}");
        using var currentProcess = Process.GetCurrentProcess();
        var processName = currentProcess.ProcessName;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-playtime/tracked-games");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            gameName = "Example Game",
            processName,
        });

        var create = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreatedTrackedGameResponse>();
        Assert.NotNull(created);

        using var addFirstUserRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/local-playtime/tracked-games/{created.Id}/users");
        addFirstUserRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        addFirstUserRequest.Content = JsonContent.Create(new { userName = "Ala" });
        var addFirstUser = await _client.SendAsync(addFirstUserRequest);
        Assert.Equal(HttpStatusCode.Created, addFirstUser.StatusCode);
        var firstUser = await addFirstUser.Content.ReadFromJsonAsync<CreatedTrackedGameUserResponse>();
        Assert.NotNull(firstUser);

        using var addSecondUserRequest = new HttpRequestMessage(
            HttpMethod.Post, $"/api/local-playtime/tracked-games/{created.Id}/users");
        addSecondUserRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        addSecondUserRequest.Content = JsonContent.Create(new { userName = "Bartek" });
        var addSecondUser = await _client.SendAsync(addSecondUserRequest);
        Assert.Equal(HttpStatusCode.Created, addSecondUser.StatusCode);
        var secondUser = await addSecondUser.Content.ReadFromJsonAsync<CreatedTrackedGameUserResponse>();
        Assert.NotNull(secondUser);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/local-playtime/tracked-games");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var games = await _client.SendAsync(listRequest);
        games.EnsureSuccessStatusCode();
        var trackedGames = await games.Content.ReadFromJsonAsync<TrackedGameResponse[]>();
        var game = Assert.Single(trackedGames!);
        Assert.Equal("Example Game", game.GameName);
        Assert.True(game.IsTracking);
        Assert.Equal("Ala", Assert.Single(game.Users, user => user.IsActive).UserName);

        await Task.Delay(TimeSpan.FromSeconds(6));
        using var runningListRequest = new HttpRequestMessage(
            HttpMethod.Get, "/api/local-playtime/tracked-games");
        runningListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var runningGames = await _client.SendAsync(runningListRequest);
        var runningItems = await runningGames.Content.ReadFromJsonAsync<TrackedGameResponse[]>();
        Assert.NotNull(runningItems);
        var running = Assert.Single(runningItems);
        Assert.True(running.IsRunning);
        Assert.True(running.TotalSeconds > 0);
        Assert.True(Assert.Single(running.Users, user => user.Id == firstUser.Id).TotalSeconds > 0);
        Assert.Equal(0, Assert.Single(running.Users, user => user.Id == secondUser.Id).TotalSeconds);

        using var changeActiveUserRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/api/local-playtime/tracked-games/{created.Id}/users/{secondUser.Id}/active");
        changeActiveUserRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var changeActiveUser = await _client.SendAsync(changeActiveUserRequest);
        Assert.Equal(HttpStatusCode.NoContent, changeActiveUser.StatusCode);
        await Task.Delay(TimeSpan.FromSeconds(6));

        using var perUserListRequest = new HttpRequestMessage(
            HttpMethod.Get, "/api/local-playtime/tracked-games");
        perUserListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var perUserResponse = await _client.SendAsync(perUserListRequest);
        var perUserGames = await perUserResponse.Content.ReadFromJsonAsync<TrackedGameResponse[]>();
        Assert.NotNull(perUserGames);
        var perUserGame = Assert.Single(perUserGames);
        Assert.True(Assert.Single(perUserGame.Users, user => user.Id == secondUser.Id).TotalSeconds > 0);

        var otherToken = await RegisterAndLogin($"other-{Guid.NewGuid():N}");
        using var unauthorizedStopRequest = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/local-playtime/tracked-games/{created.Id}");
        unauthorizedStopRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        var unauthorizedStop = await _client.SendAsync(unauthorizedStopRequest);
        Assert.Equal(HttpStatusCode.NotFound, unauthorizedStop.StatusCode);

        using var stopRequest = new HttpRequestMessage(
            HttpMethod.Delete, $"/api/local-playtime/tracked-games/{created.Id}");
        stopRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var stop = await _client.SendAsync(stopRequest);
        Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

        using var stoppedListRequest = new HttpRequestMessage(
            HttpMethod.Get, "/api/local-playtime/tracked-games");
        stoppedListRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var stoppedGames = await _client.SendAsync(stoppedListRequest);
        var stoppedItems = await stoppedGames.Content.ReadFromJsonAsync<TrackedGameResponse[]>();
        Assert.NotNull(stoppedItems);
        var stopped = Assert.Single(stoppedItems);
        Assert.False(stopped.IsTracking);
    }

    [Fact]
    public async Task LocalPlaytimeEndpointsRequireAuthentication()
    {
        var response = await _client.GetAsync("/api/local-playtime/processes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> RegisterAndLogin(string login)
    {
        await _client.PostAsJsonAsync(
            "/api/auth/register", new { login, password = "strong-password-123" });
        var response = await _client.PostAsJsonAsync(
            "/api/auth/login", new { login, password = "strong-password-123" });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return auth!.Token;
    }

    private sealed record LoginResponse(string Token, int ExpiresInSeconds);
    private sealed record CreatedTrackedGameResponse(long Id);
    private sealed record CreatedTrackedGameUserResponse(long Id);
    private sealed record TrackedGameResponse(
        long Id,
        string GameName,
        string ProcessName,
        double TotalSeconds,
        bool IsRunning,
        bool IsTracking,
        TrackedGameUserResponse[] Users);
    private sealed record TrackedGameUserResponse(
        long Id,
        string UserName,
        double TotalSeconds,
        bool IsActive);
}
