using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DevDriveReclaim.Providers.Models;

/// <summary>What a model manager says about itself while it runs, and its command line. Swapped for a fake in tests.</summary>
public interface ILocalModelRuntime
{
    /// <summary>
    /// The names Ollama has loaded (<c>GET /api/ps</c>): empty when its server isn't running, null when it's running and
    /// didn't answer.
    /// </summary>
    IReadOnlyList<string>? OllamaLoaded(Uri host);

    /// <summary>
    /// The models Ollama serves (<c>GET /api/tags</c>), or null when its server is running and didn't answer.
    /// </summary>
    OllamaServed? OllamaListed(Uri host);

    /// <summary>
    /// The model ids Foundry Local has loaded: empty when its service isn't running, null when it's running and didn't answer.
    /// Never starts the service.
    /// </summary>
    IReadOnlyList<string>? FoundryLoaded();

    /// <summary>Foundry Local's cache folder (<c>foundry cache location</c>), or null when Foundry Local isn't installed.</summary>
    string? FoundryCacheLocation();

    /// <summary>The full path of a tool's command line (<c>ollama</c>, <c>foundry</c>), or null when it isn't installed.</summary>
    string? FindTool(string tool);

    /// <summary>Runs a command line and returns its exit code and output (both streams).</summary>
    /// <param name="environment">Variables to set for it, over this process's.</param>
    (int ExitCode, string Output) Run(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>What an Ollama server serves.</summary>
/// <param name="Running">False when nothing answers at its address.</param>
/// <param name="Digests">Each name it lists, with its digest: the SHA-256 of the manifest file it read the name from.</param>
public sealed record OllamaServed(bool Running, IReadOnlyDictionary<string, string> Digests)
{
    public static OllamaServed NotRunning { get; } = new(false, new Dictionary<string, string>());
}

/// <summary>Ollama's server address, read the way Ollama reads <c>OLLAMA_HOST</c> (envconfig.Host).</summary>
public static class OllamaApi
{
    /// <summary>The address a client connects to for <paramref name="setting"/>: http://127.0.0.1:11434 when it's unset.</summary>
    public static Uri HostFrom(string? setting)
    {
        string value = (setting ?? string.Empty).Trim();
        string scheme = "http";
        string port = "11434";
        int split = value.IndexOf("://", StringComparison.Ordinal);
        if (split >= 0)
        {
            scheme = value[..split].ToLowerInvariant();
            value = value[(split + 3)..];
            port = scheme == "https" ? "443" : "80";
        }

        value = value.Split('/')[0];
        string host = "127.0.0.1";
        if (value.StartsWith('[') && value.IndexOf(']') is var end and > 0)
        {
            host = value[1..end];
            if (end + 1 < value.Length && value[end + 1] == ':')
            {
                port = value[(end + 2)..];
            }
        }
        else if (value.Length > 0)
        {
            // One colon is host:port; more is a bare IPv6 address.
            int colon = value.IndexOf(':');
            if (colon >= 0 && colon == value.LastIndexOf(':'))
            {
                host = value[..colon];
                port = value[(colon + 1)..];
            }
            else
            {
                host = value;
            }
        }

        // A server listening everywhere is reached on this PC's loopback.
        host = host.Trim('[', ']') switch { "" or "0.0.0.0" => "127.0.0.1", "::" => "::1", var h => h };
        string authority = host.Contains(':') ? $"[{host}]" : host;
        return Uri.TryCreate($"{scheme}://{authority}:{port}/", UriKind.Absolute, out Uri? uri) ? uri : new Uri("http://127.0.0.1:11434/");
    }
}

/// <summary>The real runtime: Ollama's local API, Foundry Local's daemon file and CLI, and processes.</summary>
public sealed class LocalModelRuntime : ILocalModelRuntime
{
    public static LocalModelRuntime Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public IReadOnlyList<string>? OllamaLoaded(Uri host)
    {
        try
        {
            using JsonDocument? json = OllamaGet(host, "api/ps");
            return json is null ? null : Names(json.RootElement, "models", "name");
        }
        catch (HttpRequestException exception) when (exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return [];
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public OllamaServed? OllamaListed(Uri host)
    {
        try
        {
            using JsonDocument? json = OllamaGet(host, "api/tags");
            if (json is null)
            {
                return null;
            }

            var digests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("models", out JsonElement models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement model in models.EnumerateArray())
                {
                    if (model.ValueKind == JsonValueKind.Object &&
                        model.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String &&
                        name.GetString() is { } text)
                    {
                        digests[text] = model.TryGetProperty("digest", out JsonElement digest) && digest.ValueKind == JsonValueKind.String
                            ? digest.GetString() ?? string.Empty
                            : string.Empty;
                    }
                }
            }

            return new OllamaServed(true, digests);
        }
        catch (HttpRequestException exception) when (exception.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return OllamaServed.NotRunning;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A GET from Ollama's API, or null when it answered with an error.</summary>
    private static JsonDocument? OllamaGet(Uri host, string endpoint)
    {
        using HttpResponseMessage response = Http.GetAsync(new Uri(host, endpoint)).GetAwaiter().GetResult();
        return response.IsSuccessStatusCode ? JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult()) : null;
    }

    public IReadOnlyList<string>? FoundryLoaded()
    {
        if (!FoundryRunning() || FindTool(LocalModelTools.FoundryLocal) is not { } foundry)
        {
            return [];
        }

        (int exit, string output) = Run(foundry, ["cache", "list", "-o", "json"], TimeSpan.FromSeconds(60), CancellationToken.None);
        if (exit != 0)
        {
            return null;
        }

        try
        {
            int start = output.IndexOf('{');
            using JsonDocument json = JsonDocument.Parse(start >= 0 ? output[start..] : output);
            if (!json.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return [.. models.EnumerateArray()
                .Where(m => m.TryGetProperty("loaded", out JsonElement loaded) && loaded.ValueKind == JsonValueKind.True)
                .Select(m => m.TryGetProperty("id", out JsonElement id) ? id.GetString() : null)
                .OfType<string>()];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Foundry Local's daemon is running: <c>~\.foundry\daemon.json</c> names a live <c>foundrylocald</c>.</summary>
    private static bool FoundryRunning()
    {
        try
        {
            string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "daemon.json");
            if (!File.Exists(file))
            {
                return false;
            }

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
            if (!json.RootElement.TryGetProperty("pid", out JsonElement pid) || !pid.TryGetInt32(out int id))
            {
                return false;
            }

            using Process process = Process.GetProcessById(id);
            return !process.HasExited && process.ProcessName.StartsWith("foundrylocald", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public string? FoundryCacheLocation()
    {
        if (FindTool(LocalModelTools.FoundryLocal) is not { } foundry)
        {
            return null;
        }

        (int exit, string output) = Run(foundry, ["cache", "location"], TimeSpan.FromSeconds(30), CancellationToken.None);
        if (exit != 0)
        {
            return null;
        }

        // "note: C:\Users\me\.foundry\cache\models": the one rooted path in what it prints.
        foreach (string line in output.Split('\n'))
        {
            int drive = line.IndexOf(":\\", StringComparison.Ordinal);
            if (drive > 0 && char.IsAsciiLetter(line[drive - 1]))
            {
                return line[(drive - 1)..].Trim();
            }
        }

        return null;
    }

    public string? FindTool(string tool)
    {
        string exe = tool switch
        {
            LocalModelTools.Ollama => "ollama.exe",
            LocalModelTools.FoundryLocal => "foundry.exe",
            _ => tool + ".exe",
        };

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IEnumerable<string> known = tool switch
        {
            LocalModelTools.Ollama => [Path.Combine(local, "Programs", "Ollama", exe)],
            LocalModelTools.FoundryLocal => [Path.Combine(local, "Microsoft", "WindowsApps", exe)],
            _ => [],
        };

        // The machine's and the user's Path too, so a tool installed after this app started is found.
        IEnumerable<string> onPath = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(t => (Environment.GetEnvironmentVariable("PATH", t) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(folder => Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim('"')), exe));

        return onPath.Concat(known).FirstOrDefault(File.Exists);
    }

    public (int ExitCode, string Output) Run(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} didn't start");
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            Task reading = Task.WhenAll(Pump(process.StandardOutput, stdout), Pump(process.StandardError, stderr));
            using CancellationTokenSource timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            try
            {
                process.WaitForExitAsync(timer.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return (-1, cancellationToken.IsCancellationRequested ? "stopped" : $"didn't finish in {timeout.TotalSeconds:N0} seconds");
            }

            // A program the command started (Ollama's app, a service) inherits its output and can keep it open long after the
            // command exits, so the output is what arrived by then.
            reading.Wait(OutputGrace);
            string text;
            lock (stdout)
            {
                lock (stderr)
                {
                    text = (stdout.ToString() + stderr).Trim();
                }
            }

            return (process.ExitCode, text);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, exception.Message);
        }
    }

    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    private static async Task Pump(StreamReader reader, StringBuilder into)
    {
        char[] buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                lock (into)
                {
                    into.Append(buffer, 0, read);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The process was disposed while a program it started still held the pipe.
        }
    }

    private static IReadOnlyList<string> Names(JsonElement root, string array, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(array, out JsonElement items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray()
                .Select(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty(property, out JsonElement n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null)
                .OfType<string>()]
            : [];
}
