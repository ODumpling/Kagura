using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kagura.Cli.Commands;

internal static class StopCommand
{
    public static Command Build()
    {
        var portOption = new Option<int?>(
            aliases: new[] { "--port", "-p" },
            description: "Stop only the Kagura instance bound to this port (default: stop all).");

        var forceOption = new Option<bool>(
            "--force",
            description: "Skip the /api/ping verification and kill every PID file we find.");

        var cmd = new Command("stop", "Stop running Kagura server instance(s).");
        cmd.AddAlias("down");
        cmd.AddOption(portOption);
        cmd.AddOption(forceOption);
        cmd.SetHandler(async (int? port, bool force) =>
            Environment.ExitCode = await RunAsync(port, force), portOption, forceOption);
        return cmd;
    }

    private static async Task<int> RunAsync(int? portFilter, bool force)
    {
        var pidFiles = DiscoverPidFiles();
        if (pidFiles.Count == 0)
        {
            Console.WriteLine("No running Kagura instances found.");
            return 0;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        var stopped = 0;
        var stale = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var path in pidFiles)
        {
            var entry = TryReadPidFile(path);
            if (entry is null)
            {
                TryDeleteFile(path);
                stale++;
                continue;
            }

            var port = ExtractPort(entry.Url);
            if (portFilter is int wanted && port != wanted)
            {
                skipped++;
                continue;
            }

            // Is the process even alive?
            if (!IsProcessAlive(entry.Pid))
            {
                TryDeleteFile(path);
                stale++;
                Console.WriteLine($"  cleaned stale pid file (pid {entry.Pid} not running)");
                continue;
            }

            // Verify via /api/ping unless --force.
            if (!force)
            {
                var matched = await ProbeAsync(http, entry);
                if (!matched)
                {
                    Console.Error.WriteLine($"  pid {entry.Pid} on {entry.Url} did not identify as Kagura — leaving alone (use --force to override)");
                    skipped++;
                    continue;
                }
            }

            if (await KillProcessAsync(entry.Pid, TimeSpan.FromSeconds(5)))
            {
                TryDeleteFile(path);
                stopped++;
                Console.WriteLine($"  stopped pid {entry.Pid} ({entry.Url})");
            }
            else
            {
                failed++;
                Console.Error.WriteLine($"  failed to stop pid {entry.Pid} ({entry.Url})");
            }
        }

        var summary = new List<string>();
        if (stopped > 0) summary.Add($"{stopped} stopped");
        if (stale > 0) summary.Add($"{stale} stale cleaned");
        if (skipped > 0) summary.Add($"{skipped} skipped");
        if (failed > 0) summary.Add($"{failed} failed");
        Console.WriteLine(summary.Count == 0 ? "Nothing to do." : string.Join(", ", summary) + ".");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> DiscoverPidFiles()
    {
        // Look in both known state-dir locations so the command works whether the server
        // wrote to ~/.devflow (legacy) or ~/.kagura (preferred).
        var roots = new[]
        {
            Path.Combine(KaguraPaths.PreferredDirectory, "runtime"),
            Path.Combine(KaguraPaths.LegacyDirectory, "runtime"),
        };
        var results = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                results.AddRange(Directory.EnumerateFiles(root, "*.json"));
            }
            catch { /* best effort */ }
        }
        return results;
    }

    private static PidFileEntry? TryReadPidFile(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PidFileEntry>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    private static int? ExtractPort(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Port;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var _ = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<bool> ProbeAsync(HttpClient http, PidFileEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Url)) return false;
        try
        {
            var pingUrl = entry.Url.TrimEnd('/') + "/api/ping";
            using var resp = await http.GetAsync(pingUrl);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync();
            var ping = JsonSerializer.Deserialize<PingResponse>(body, JsonOpts);
            return ping is { App: "Kagura" } && ping.Pid == entry.Pid;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> KillProcessAsync(int pid, TimeSpan timeout)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(timeout);
            try { await proc.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { return false; }
            return proc.HasExited;
        }
        catch (ArgumentException)
        {
            return true; // already dead
        }
        catch (InvalidOperationException)
        {
            return true; // already exited
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record PidFileEntry(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("startedAt")] string? StartedAt,
        [property: JsonPropertyName("version")] string? Version);

    private sealed record PingResponse(
        [property: JsonPropertyName("app")] string? App,
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("version")] string? Version);
}
