using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using ServerActivity.Api.Analysis;
using ServerActivity.Api.Authentication;
using ServerActivity.Api.LocalPlaytime;
using ServerActivity.Api.TeamSpeak;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication("Bearer")
    .AddScheme<AuthenticationSchemeOptions, TokenAuthenticationHandler>("Bearer", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<ISecurityBackupService, SecurityBackupService>();
builder.Services.AddSingleton<IPasswordHasher<Account>, PasswordHasher<Account>>();
builder.Services.AddSingleton<SessionAnalyzer>();
var playtimeDatabasePath = builder.Configuration["LocalPlaytime:DatabasePath"] ??
    Path.Combine(builder.Environment.ContentRootPath, "data", "playtime.db");
var dataProtectionKeysPath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(playtimeDatabasePath))!,
    "keys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
builder.Services.AddSingleton<TeamSpeakQueryClient>();
builder.Services.AddSingleton<TeamSpeakService>();
builder.Services.AddHostedService<TeamSpeakSyncService>();
builder.Services.AddSingleton<LocalPlaytimeService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<LocalPlaytimeService>());
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(builder.Configuration["Frontend:Origin"] ?? "http://localhost:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program { }
