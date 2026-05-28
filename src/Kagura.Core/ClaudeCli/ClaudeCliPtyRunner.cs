using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Porta.Pty;

namespace Kagura.Core.ClaudeCli;

public sealed record ClaudeCliPtyResult(int ExitCode, string Stdout);

// Shared PTY-based runner for short-lived `claude -p` invocations (triage, auto-review).
// Spawns claude through a pseudo-terminal so the live stream-json output can be observed,
// while preserving a final-JSON parse contract on process exit.
//
// Does NOT acquire AgentRunner's MaxConcurrentAgents semaphore — these calls are short
// and cheap and must not block task-agent slots.
public static class ClaudeCliPtyRunner
{
    public static async Task<ClaudeCliPtyResult> RunAsync(
        string binary,
        IReadOnlyList<string> args,
        string? cwd = null,
        Action<byte[]>? onData = null,
        CancellationToken ct = default)
    {
        var options = new PtyOptions
        {
            Name = "xterm-256color",
            Cols = 120,
            Rows = 32,
            Cwd = cwd ?? Environment.CurrentDirectory,
            App = binary,
            CommandLine = args.ToArray(),
        };

        var pty = await PtyProvider.SpawnAsync(options, ct);
        try
        {
            var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            pty.ProcessExited += (_, e) => exitTcs.TrySetResult(e.ExitCode);

            await using var reg = ct.Register(() =>
            {
                try { pty.Dispose(); } catch { }
                exitTcs.TrySetCanceled();
            });

            var output = new MemoryStream();
            var readLoop = Task.Run(async () =>
            {
                var buf = new byte[4096];
                try
                {
                    while (true)
                    {
                        var n = await pty.ReaderStream.ReadAsync(buf.AsMemory(), ct);
                        if (n == 0) break;
                        output.Write(buf, 0, n);
                        if (onData is not null)
                        {
                            var chunk = new byte[n];
                            Array.Copy(buf, chunk, n);
                            onData(chunk);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { /* pty closed after exit — swallow */ }
            });

            var exit = await exitTcs.Task;

            // Give the read loop a brief window to drain any remaining buffered output
            // before the pty handle is disposed.
            try { await readLoop.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None); } catch { }

            return new ClaudeCliPtyResult(exit, Encoding.UTF8.GetString(output.ToArray()));
        }
        finally
        {
            try { pty.Dispose(); } catch { }
        }
    }

    // Stream-json emits one JSON object per line. The final meaningful event has
    // {"type":"result", ...} and matches the same envelope shape used by `--output-format json`.
    // Returns the raw JSON line so callers can deserialize with their existing envelope type.
    public static string ExtractResultEnvelope(string streamJsonOutput)
    {
        string? lastResult = null;
        foreach (var raw in streamJsonOutput.Split('\n'))
        {
            var line = StripAnsi(raw).Trim().TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "result")
                {
                    lastResult = line;
                }
            }
            catch (JsonException) { /* keep scanning */ }
        }

        if (lastResult is null)
        {
            throw new InvalidOperationException(
                $"Could not find stream-json result envelope. Raw output: {streamJsonOutput}");
        }
        return lastResult;
    }

    private static readonly Regex AnsiRegex =
        new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static string StripAnsi(string s) => AnsiRegex.Replace(s, "");
}
