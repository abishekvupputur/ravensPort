using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using RavensPort.Core.Admin;

namespace RavensPort.Cli;

/// <summary>Exit codes, shared by every command so scripts can tell the cases apart.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Refused = 1;
    public const int Usage = 2;
    public const int NotRunning = 3;
    public const int VaultUnavailable = 4;
    public const int StartFailed = 5;
}

/// <summary>
/// Talks to the running RavensPort over its admin socket, and turns every reply into what a person
/// at a terminal needs: the message, the read-only warning when there is one, and an exit code.
/// </summary>
internal sealed class AdminClient(string? socketPath, bool json, TextWriter output, TextWriter error) : IDisposable
{
    private readonly HttpClient _http = AdminChannel.CreateClient(socketPath);

    public string SocketPath { get; } = socketPath ?? AdminChannel.SocketPath();

    public bool Json => json;

    public TextWriter Out => output;

    /// <summary>
    /// Runs a command body, translating "nothing is listening" into the one sentence that says what
    /// to do about it, rather than a socket exception.
    /// </summary>
    public async Task<int> RunAsync(Func<Task<int>> body)
    {
        try
        {
            return await body();
        }
        catch (HttpRequestException ex) when (IsNotListening(ex))
        {
            error.WriteLine($"No running RavensPort answered at {SocketPath}.");
            error.WriteLine("Start one with `ravensport-cli serve`, or open the RavensPort desktop app.");
            return ExitCodes.NotRunning;
        }
    }

    private static bool IsNotListening(HttpRequestException ex) =>
        ex.InnerException is SocketException or FileNotFoundException or DirectoryNotFoundException
        || ex.HttpRequestError == HttpRequestError.ConnectionError;

    /// <summary>GETs a value, printing the server's message instead when it refuses.</summary>
    /// <param name="print">False for lookups a command makes on the way to something else.</param>
    public async Task<(T? Value, int Code)> GetAsync<T>(string path, bool print = true)
    {
        using var response = await _http.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode) return (default, Fail(response.StatusCode, body));
        if (json && print) output.WriteLine(Pretty(body));

        return (JsonSerializer.Deserialize<T>(body, AdminChannel.JsonOptions), ExitCodes.Ok);
    }

    public Task<int> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

    public Task<int> PatchAsync(string path, object body) => SendAsync(HttpMethod.Patch, path, body);

    public Task<int> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

    /// <summary>Sends a change and prints the reply.</summary>
    public async Task<int> SendAsync(HttpMethod method, string path, object? body)
    {
        var (reply, code) = await SendForAsync<AdminMessage>(method, path, body);
        if (reply is not null && !json) PrintMessage(reply);
        return code;
    }

    /// <summary>Sends a change and returns the typed reply, for the few that carry more than a message.</summary>
    public async Task<(T? Value, int Code)> SendForAsync<T>(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body ?? new { }, options: AdminChannel.JsonOptions),
        };

        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode) return (default, Fail(response.StatusCode, text));
        if (json) output.WriteLine(Pretty(text));

        return (JsonSerializer.Deserialize<T>(text, AdminChannel.JsonOptions), ExitCodes.Ok);
    }

    /// <summary>
    /// Posts and copies the streamed text reply line by line as it arrives — sign-in, where the
    /// person has to act on a line before the request can finish. The last line says how it went.
    /// </summary>
    public async Task<int> StreamAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            return Fail(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        var last = "";

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            output.WriteLine(line);
            if (line.Length > 0) last = line;
        }

        return last.StartsWith("DONE:", StringComparison.Ordinal) ? ExitCodes.Ok : ExitCodes.Refused;
    }

    public void PrintMessage(AdminMessage message)
    {
        output.WriteLine(message.Message);
        if (message.Warning is { } warning) error.WriteLine($"warning: {warning}");
    }

    private int Fail(HttpStatusCode status, string body)
    {
        string message;
        try
        {
            message = JsonSerializer.Deserialize<AdminMessage>(body, AdminChannel.JsonOptions)?.Message ?? body;
        }
        catch (JsonException)
        {
            message = body.Length > 0 ? body : status.ToString();
        }

        error.WriteLine($"error: {message}");
        return ExitCodes.Refused;
    }

    private static string Pretty(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public void Dispose() => _http.Dispose();
}
