using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.ClaudeCli;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kagura.Core.Review;

public class ReviewOptions
{
    public string ClaudeBinary { get; set; } = "claude";
    public string? Model { get; set; }
    public int MaxDiffBytes { get; set; } = 50_000;
}

public class ClaudeCliReviewService : IReviewService
{
    /// <summary>
    /// Built-in AutoReview prompt. Surfaced as the lazy default in
    /// <see cref="Kagura.Core.Agents.RolePromptDefaults"/> so the Source's "Prompts" tab can
    /// render it even though AutoReview hasn't been migrated to the PTY-Agent path yet
    /// (that's #67's job). When AutoReview is migrated this constant becomes the template
    /// resolved by <c>PromptResolver</c> at Agent spawn time.
    /// </summary>
    public const string DefaultPromptTemplate =
        """
        You are an automated code reviewer for a developer workflow tool. You receive a task
        description and a git diff of an autonomous agent's changes. Decide whether the diff
        is safe to merge automatically or whether it needs human review.

        Mark autoMerge=true when ALL of these hold:
        - The diff implements what the task description says, with nothing unrelated.
        - The changes are reasonably small / focused.
        - No obvious security, data-loss, or destructive risks.
        - No suspicious patterns (broad permission grants, disabled tests, eval/exec, secrets).

        Mark autoMerge=false when ANY of these hold:
        - The diff is empty, off-topic, or doesn't match the task.
        - The change is large, sprawling, or touches sensitive areas (auth, payments, migrations, infra).
        - The intent is unclear or the test coverage looks insufficient.
        - Anything that would make a thoughtful human reviewer pause.

        Respond with ONLY a JSON object, no prose, no markdown fences:
        {"autoMerge": true|false, "reasoning": "1-3 sentences explaining the decision"}
        """;

    private const string SystemPrompt = DefaultPromptTemplate;

    private readonly ReviewOptions _options;
    private readonly ILogger<ClaudeCliReviewService> _log;

    public ClaudeCliReviewService(
        IOptions<ReviewOptions> options,
        ILogger<ClaudeCliReviewService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<ReviewVerdict> ReviewAsync(
        Guid runId, string taskTitle, string taskDescription, string diff, CancellationToken ct = default)
    {
        _ = runId;
        if (string.IsNullOrWhiteSpace(diff))
            return new ReviewVerdict(false, "Diff was empty — nothing to review.");

        var truncated = diff.Length > _options.MaxDiffBytes;
        var diffForLlm = truncated ? diff[.._options.MaxDiffBytes] : diff;

        var userPrompt =
            $"""
             Task title: {taskTitle}

             Task description:
             {taskDescription}

             Diff{(truncated ? $" (truncated to {_options.MaxDiffBytes} bytes of {diff.Length})" : "")}:
             ```diff
             {diffForLlm}
             ```
             """;

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
            throw new InvalidOperationException(
                $"claude CLI exited with code {result.ExitCode}. stdout: {result.Stdout.Trim()}");

        var envelopeJson = ClaudeCliPtyRunner.ExtractResultEnvelope(result.Stdout);
        var envelope = JsonSerializer.Deserialize<ClaudeCliResult>(envelopeJson, JsonOpts)
            ?? throw new InvalidOperationException($"Could not parse claude CLI JSON envelope. line: {envelopeJson}");

        if (envelope.IsError || string.IsNullOrWhiteSpace(envelope.Result))
            throw new InvalidOperationException(
                $"claude CLI returned error envelope. subtype={envelope.Subtype} result={envelope.Result}");

        var json = ExtractJsonObject(envelope.Result);
        var verdict = JsonSerializer.Deserialize<ReviewVerdict>(json, JsonOpts)
                      ?? throw new InvalidOperationException("Could not parse review verdict JSON");

        _log.LogInformation("Review verdict for '{Title}': autoMerge={AutoMerge}", taskTitle, verdict.AutoMerge);
        return verdict;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"Review response did not contain a JSON object. Got: {text}");
        return text[start..(end + 1)];
    }

    private sealed record ClaudeCliResult(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("subtype")] string? Subtype,
        [property: JsonPropertyName("is_error")] bool IsError);
}
