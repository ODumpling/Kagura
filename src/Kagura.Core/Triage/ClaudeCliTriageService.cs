using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kagura.Core.Triage;

public class TriageOptions
{
    public string ClaudeBinary { get; set; } = "claude";
    public string? Model { get; set; }
}

public class ClaudeCliTriageService : ITriageService
{
    private const string SystemPrompt =
        """
        You are a triage assistant for a developer workflow tool. You receive a software issue (title, body, labels)
        and propose a list of small, independently executable tasks that together complete the issue.

        Rules:
        - Each task should be small enough to be completed by one autonomous coding agent in a single session.
        - Prefer 1–5 tasks. Fewer if the issue is small.
        - Tasks should be as parallelizable as possible. If they MUST run in order, that's fine — set Order accordingly.
        - Titles are imperative, under 80 characters ("Add ...", "Refactor ...", "Wire ...").
        - Descriptions are 1-3 sentences explaining scope, files likely involved, and acceptance criteria.

        Respond with ONLY a JSON array, no prose, no markdown fences. Schema:
        [
          {"title": "string", "description": "string", "order": integer}
        ]
        """;

    private readonly TriageOptions _options;
    private readonly ILogger<ClaudeCliTriageService> _log;

    public ClaudeCliTriageService(IOptions<TriageOptions> options, ILogger<ClaudeCliTriageService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
        string workItemTitle, string workItemBody, string? labels, CancellationToken ct = default)
    {
        var userPrompt =
            $"""
             Title: {workItemTitle}

             Labels: {labels ?? "(none)"}

             Body:
             {workItemBody}
             """;

        var psi = new ProcessStartInfo
        {
            FileName = _options.ClaudeBinary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(userPrompt);
        psi.ArgumentList.Add("--append-system-prompt");
        psi.ArgumentList.Add(SystemPrompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_options.Model);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var _ = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"claude CLI exited with code {process.ExitCode}. stderr: {stderr.ToString().Trim()}");
        }

        var envelope = JsonSerializer.Deserialize<ClaudeCliResult>(stdout.ToString(), JsonOpts)
            ?? throw new InvalidOperationException($"Could not parse claude CLI JSON envelope. stdout: {stdout}");

        if (envelope.IsError || string.IsNullOrWhiteSpace(envelope.Result))
        {
            throw new InvalidOperationException(
                $"claude CLI returned error envelope. subtype={envelope.Subtype} result={envelope.Result}");
        }

        var json = ExtractJsonArray(envelope.Result);
        var arr = JsonSerializer.Deserialize<List<TriagedTaskProposal>>(json, JsonOpts)
                  ?? throw new InvalidOperationException("Could not parse triage response as JSON array");

        _log.LogInformation("Triage proposed {Count} tasks", arr.Count);
        return arr;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"Triage response did not contain a JSON array. Got: {text}");
        return text[start..(end + 1)];
    }

    private sealed record ClaudeCliResult(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("subtype")] string? Subtype,
        [property: JsonPropertyName("is_error")] bool IsError);
}
