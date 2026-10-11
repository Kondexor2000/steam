using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServerActivity.Api.TeamSpeak;

namespace ServerActivity.Api.Analysis;

[ApiController]
[Authorize]
[Route("api/analysis")]
public sealed class AnalysisController(
    SessionAnalyzer analyzer,
    TeamSpeakService teamSpeakService) : ControllerBase
{
    [HttpGet]
    public ActionResult<AnalysisResult> GetAnalysis() =>
        Ok(teamSpeakService.GetAnalysis(analyzer));

    [HttpPost("csv")]
    [RequestSizeLimit(5_000_000)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> AnalyzeCsv(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "Wybierz niepusty plik CSV." });
        }

        if (file.Length > 5_000_000)
        {
            return BadRequest(new { error = "Plik CSV nie może przekraczać 5 MB." });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var csv = await reader.ReadToEndAsync(cancellationToken);
        try
        {
            var importedSessions = analyzer.ParseRecords(csv);
            teamSpeakService.ImportCsvSessions(importedSessions, csv);
            return Ok(teamSpeakService.GetAnalysis(analyzer));
        }
        catch (AnalysisValidationException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }
}
