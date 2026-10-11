using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace ServerActivity.Api.TeamSpeak;

public sealed class TeamSpeakQueryClient
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(8);

    public async Task<IReadOnlyList<TeamSpeakClient>> GetClientsAsync(
        string host,
        int port,
        int virtualServerId,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectionTimeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                NewLine = "\n"
            };

            await ReadGreetingAsync(reader, timeout.Token);
            await ExecuteAsync(
                reader,
                writer,
                $"login client_login_name={Escape(username)} client_login_password={Escape(password)}",
                timeout.Token);
            await ExecuteAsync(
                reader,
                writer,
                $"use sid={virtualServerId.ToString(CultureInfo.InvariantCulture)}",
                timeout.Token);
            var rows = await ExecuteAsync(reader, writer, "clientlist -uid", timeout.Token);

            return rows
                .Select(ParseRow)
                .Where(row => row.GetValueOrDefault("client_type") == "0")
                .Where(row => row.TryGetValue("client_database_id", out var id) && !string.IsNullOrWhiteSpace(id))
                .Select(row => new TeamSpeakClient(
                    row["client_database_id"],
                    row.GetValueOrDefault("client_nickname") ?? "Nieznany użytkownik"))
                .ToArray();
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TeamSpeakConnectionException("Przekroczono limit czasu połączenia z TeamSpeak ServerQuery.", exception);
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or InvalidOperationException or FormatException)
        {
            throw new TeamSpeakConnectionException(
                "Nie udało się połączyć z TeamSpeak ServerQuery. Sprawdź adres, port, dane logowania i uprawnienia.",
                exception);
        }
    }

    private static async Task ReadGreetingAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var receivedGreeting = false;
        while (await ReadLineAsync(reader, cancellationToken) is { } line)
        {
            if (line.StartsWith("Welcome to the TeamSpeak", StringComparison.Ordinal))
            {
                receivedGreeting = true;
            }

            if (line.Length == 0 && receivedGreeting)
            {
                return;
            }
        }

        throw new TeamSpeakConnectionException("Serwer nie zwrócił prawidłowego powitania ServerQuery.");
    }

    private static async Task<IReadOnlyList<string>> ExecuteAsync(
        StreamReader reader,
        StreamWriter writer,
        string command,
        CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(command);
        await writer.FlushAsync(cancellationToken);

        var rows = new List<string>();
        while (await ReadLineAsync(reader, cancellationToken) is { } line)
        {
            if (!line.StartsWith("error ", StringComparison.Ordinal))
            {
                if (line.Length > 0)
                {
                    rows.Add(line);
                }

                continue;
            }

            var error = ParseRow(line);
            if (!error.TryGetValue("id", out var errorId) || errorId != "0")
            {
                var message = error.GetValueOrDefault("msg") ?? "błąd ServerQuery";
                throw new TeamSpeakConnectionException($"TeamSpeak ServerQuery odrzucił polecenie: {message}");
            }

            return rows;
        }

        throw new TeamSpeakConnectionException("Serwer zakończył połączenie przed odpowiedzią ServerQuery.");
    }

    private static async Task<string?> ReadLineAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);
        return line?.Trim('\r');
    }

    private static Dictionary<string, string> ParseRow(string row)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in row.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 1)
            {
                continue;
            }

            result[field[..separator]] = Unescape(field[(separator + 1)..]);
        }

        return result;
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("/", "\\/", StringComparison.Ordinal)
            .Replace(" ", "\\s", StringComparison.Ordinal)
            .Replace("|", "\\p", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

    private static string Unescape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                result.Append(value[index]);
                continue;
            }

            result.Append(value[++index] switch
            {
                's' => ' ',
                'p' => '|',
                '/' => '/',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                'f' => '\f',
                var character => character
            });
        }

        return result.ToString();
    }
}
