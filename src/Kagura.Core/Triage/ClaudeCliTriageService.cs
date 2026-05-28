using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.ClaudeCli;
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
        var userPrompt = BuildUserPrompt(workItemTitle, workItemBody, labels, existingTaskTitles: null);

        var args = new List<string>
        {
            "-p", userPrompt,
            "--append-system-prompt", SystemPrompt,
            "--output-format", "stream-json",
            "--verbose",
        };
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            args.Add("--model");
            args.Add(_options.Model);
        }

        var result = await ClaudeCliPtyRunner.RunAsync(_options.ClaudeBinary, args, ct: ct);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"claude CLI exited with code {result.ExitCode}. stdout: {result.Stdout.Trim()}");
        }

        var envelopeJson = ClaudeCliPtyRunner.ExtractResultEnvelope(result.Stdout);
        var envelope = JsonSerializer.Deserialize<ClaudeCliResult>(envelopeJson, JsonOpts)
            ?? throw new InvalidOperationException($"Could not parse claude CLI JSON envelope. line: {envelopeJson}");

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

    public static string BuildUserPrompt(
        string workItemTitle,
        string workItemBody,
        string? labels,
        IReadOnlyList<string>? existingTaskTitles)
    {
        var basePrompt =
            $"""
             Title: {workItemTitle}

             Labels: {labels ?? "(none)"}

             Body:
             {workItemBody}
             """;

        if (existingTaskTitles is null || existingTaskTitles.Count == 0)
            return basePrompt;

        var titles = string.Join("\n", existingTaskTitles.Select(t => $"- {t}"));
        return basePrompt + "\n\n" +
            $"""
             Existing tasks already proposed for this issue:
             {titles}

             Do not propose duplicates of the existing tasks above. Only propose tasks that cover work not already represented.
             """;
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
