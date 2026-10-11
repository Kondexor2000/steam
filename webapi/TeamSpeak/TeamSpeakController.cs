using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServerActivity.Api.Analysis;

namespace ServerActivity.Api.TeamSpeak;

[ApiController]
[Authorize]
[Route("api/teamspeak")]
public sealed class TeamSpeakController(
    TeamSpeakService service,
    SessionAnalyzer analyzer) : ControllerBase
{
    [HttpGet("servers")]
    public ActionResult<IReadOnlyList<TeamSpeakServer>> GetServers() =>
        Ok(service.GetServers());

    [HttpPost("servers")]
    [Authorize(Roles = "Administrator")]
    public IActionResult AddServer(CreateTeamSpeakServerRequest request)
    {
        var name = request.Name?.Trim();
        var host = request.Host?.Trim();
        var username = request.QueryUsername?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.Any(char.IsControl))
        {
            return BadRequest(new { error = "Nazwa serwera musi mieć od 1 do 100 znaków." });
        }

        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return BadRequest(new { error = "Podaj prawidłowy adres IP lub nazwę hosta serwera." });
        }

        if (request.QueryPort is < 1 or > 65535 || request.VirtualServerId < 1)
        {
            return BadRequest(new { error = "Port Query musi być z zakresu 1–65535, a ID serwera wirtualnego musi być dodatnie." });
        }

        if (string.IsNullOrWhiteSpace(username) || username.Length > 128 ||
            username.Any(char.IsControl) || string.IsNullOrEmpty(request.QueryPassword) ||
            request.QueryPassword.Length > 256 || request.QueryPassword.Any(char.IsControl))
        {
            return BadRequest(new { error = "Podaj poprawny login i hasło ServerQuery." });
        }

        var id = service.AddServer(request with { Name = name, Host = host, QueryUsername = username });
        return Created($"/api/teamspeak/servers/{id}", new { id });
    }

    [HttpDelete("servers/{id:long}")]
    [Authorize(Roles = "Administrator")]
    public IActionResult RemoveServer(long id) =>
        service.RemoveServer(id)
            ? NoContent()
            : NotFound(new { error = "Nie znaleziono serwera TeamSpeak." });

    [HttpPost("servers/{id:long}/test")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> TestConnection(long id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { activeClients = await service.TestConnectionAsync(id, cancellationToken) });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (TeamSpeakConnectionException exception)
        {
            return StatusCode((int)HttpStatusCode.BadGateway, new { error = exception.Message });
        }
    }

    [HttpPost("servers/{id:long}/sync")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Synchronize(long id, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await service.SynchronizeAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { error = exception.Message });
        }
        catch (TeamSpeakConnectionException exception)
        {
            return StatusCode((int)HttpStatusCode.BadGateway, new { error = exception.Message });
        }
    }

    [HttpGet("analysis")]
    public ActionResult<AnalysisResult> GetAnalysis() =>
        Ok(service.GetAnalysis(analyzer));
}
