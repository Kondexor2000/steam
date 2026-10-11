using System.Globalization;
using System.Text;

namespace ServerActivity.Api.Analysis;

public sealed class AnalysisValidationException(string message) : Exception(message);

public sealed record SessionRecord(
    string Service,
    string ServerName,
    string Game,
    string Player,
    DateTimeOffset ConnectedAt,
    DateTimeOffset DisconnectedAt);

public sealed record ServerSummary(
    string Service,
    string ServerName,
    string Game,
    int Sessions,
    int UniquePlayers,
    double TotalHours,
    double MeanSessionMinutes,
    double MedianSessionMinutes);

public sealed record PlayerSummary(
    string Service,
    string ServerName,
    string Game,
    string Player,
    int Sessions,
    double TotalHours,
    double MeanSessionMinutes,
    double MedianSessionMinutes);

public sealed record AnalysisResult(
    IReadOnlyList<ServerSummary> Servers,
    IReadOnlyList<PlayerSummary> Players);

public sealed class SessionAnalyzer
{
    private static readonly string[] RequiredColumns =
        ["service", "server_name", "game", "player", "connected_at", "disconnected_at"];

    public AnalysisResult AnalyzeCsv(string csv) => AnalyzeRecords(ParseRecords(csv));

    public IReadOnlyList<SessionRecord> ParseRecords(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count < 2)
        {
            throw new AnalysisValidationException("Plik nie zawiera żadnych sesji.");
        }

        var normalizedHeaders = rows[0]
            .Select(name => name.Trim().TrimStart('\uFEFF'))
            .ToArray();
        if (normalizedHeaders.Distinct(StringComparer.Ordinal).Count() != normalizedHeaders.Length)
        {
            throw new AnalysisValidationException("Nagłówki CSV nie mogą się powtarzać.");
        }

        var headers = normalizedHeaders.Select((name, index) =>
                (Name: name.Trim().TrimStart('\uFEFF'), Index: index))
            .ToDictionary(column => column.Name, column => column.Index, StringComparer.Ordinal);
        var missing = RequiredColumns.Where(column => !headers.ContainsKey(column)).ToArray();
        if (missing.Length > 0)
        {
            throw new AnalysisValidationException(
                $"Brak wymaganych kolumn: {string.Join(", ", missing)}");
        }

        var sessions = new List<SessionRecord>();
        foreach (var row in rows.Skip(1))
        {
            if (row.Count != rows[0].Count)
            {
                throw new AnalysisValidationException("Wiersz CSV ma nieprawidłową liczbę kolumn.");
            }

            var service = Get(row, headers, "service").ToLowerInvariant();
            var server = Get(row, headers, "server_name");
            var game = Get(row, headers, "game");
            var player = Get(row, headers, "player");
            if (service is not ("teamspeak" or "game"))
            {
                throw new AnalysisValidationException(
                    "service musi mieć wartość 'teamspeak' albo 'game'.");
            }

            if (server.Length == 0 || player.Length == 0)
            {
                throw new AnalysisValidationException(
                    "Kolumny server_name i player nie mogą zawierać pustych wartości.");
            }

            if (service == "game" && game.Length == 0)
            {
                throw new AnalysisValidationException("Dla sesji game kolumna game nie może być pusta.");
            }

            if (service == "teamspeak")
            {
                game = string.Empty;
            }

            if (!DateTimeOffset.TryParse(
                    Get(row, headers, "connected_at"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var connectedAt) ||
                !DateTimeOffset.TryParse(
                    Get(row, headers, "disconnected_at"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var disconnectedAt))
            {
                throw new AnalysisValidationException("Nie można odczytać znacznika czasu sesji.");
            }

            if (disconnectedAt <= connectedAt)
            {
                throw new AnalysisValidationException(
                    "Czas zakończenia sesji musi być późniejszy niż czas połączenia.");
            }

            sessions.Add(new SessionRecord(
                service, server, game, player, connectedAt, disconnectedAt));
        }

        if (sessions.Count == 0)
        {
            throw new AnalysisValidationException("Plik nie zawiera żadnych sesji.");
        }

        return sessions;
    }

    public AnalysisResult AnalyzeRecords(IEnumerable<SessionRecord> records)
    {
        var sessions = records
            .Where(session => session.DisconnectedAt > session.ConnectedAt)
            .Select(session => new ParsedSession(
                session.Service,
                session.ServerName,
                session.Game,
                session.Player,
                (session.DisconnectedAt - session.ConnectedAt).TotalMinutes))
            .ToArray();

        return new AnalysisResult(
            sessions.GroupBy(session => (session.Service, session.ServerName, session.Game))
                .Select(group => new ServerSummary(
                    group.Key.Service,
                    group.Key.ServerName,
                    group.Key.Game,
                    group.Count(),
                    group.Select(session => session.Player).Distinct(StringComparer.Ordinal).Count(),
                    group.Sum(session => session.DurationMinutes) / 60,
                    group.Average(session => session.DurationMinutes),
                    Median(group.Select(session => session.DurationMinutes))))
                .OrderBy(summary => summary.Service, StringComparer.Ordinal)
                .ThenBy(summary => summary.ServerName, StringComparer.Ordinal)
                .ThenBy(summary => summary.Game, StringComparer.Ordinal)
                .ToArray(),
            sessions.GroupBy(session =>
                    (session.Service, session.ServerName, session.Game, session.Player))
                .Select(group => new PlayerSummary(
                    group.Key.Service,
                    group.Key.ServerName,
                    group.Key.Game,
                    group.Key.Player,
                    group.Count(),
                    group.Sum(session => session.DurationMinutes) / 60,
                    group.Average(session => session.DurationMinutes),
                    Median(group.Select(session => session.DurationMinutes))))
                .OrderBy(summary => summary.Service, StringComparer.Ordinal)
                .ThenBy(summary => summary.ServerName, StringComparer.Ordinal)
                .ThenBy(summary => summary.Game, StringComparer.Ordinal)
                .ThenBy(summary => summary.Player, StringComparer.Ordinal)
                .ToArray());
    }

    private static string Get(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> headers, string name) =>
        row[headers[name]].Trim();

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < csv.Length; index++)
        {
            var character = csv[index];
            if (quoted)
            {
                if (character == '"' && index + 1 < csv.Length && csv[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else if (character == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(character);
                }
            }
            else if (character == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (character == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n')
                {
                    index++;
                }

                row.Add(field.ToString());
                field.Clear();
                if (row.Any(value => value.Length > 0) || row.Count > 1)
                {
                    rows.Add(row);
                }

                row = [];
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted)
        {
            throw new AnalysisValidationException("Plik CSV zawiera niedomknięty cudzysłów.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private sealed record ParsedSession(
        string Service,
        string ServerName,
        string Game,
        string Player,
        double DurationMinutes);
}
