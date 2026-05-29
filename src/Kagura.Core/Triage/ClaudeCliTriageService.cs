using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;
using Kagura.Core.ClaudeCli;
using Kagura.Core.Git;
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
    /// <summary>
    /// Built-in Triage prompt template. Per ADR 0002 this is the default a Source resolves at
    /// Agent spawn time when it hasn't customised its own Triage prompt; per-Source overrides
    /// are issue #69 and intentionally out of scope here.
    /// Placeholders: <c>{{TITLE}}</c>, <c>{{LABELS}}</c>, <c>{{BODY}}</c>,
    /// <c>{{EXISTING_TASKS}}</c>, <c>{{SUBMIT_TOOL}}</c>.
    /// </summary>
    public const string DefaultPromptTemplate =
        """
        You are a triage assistant for a developer workflow tool. You receive a software issue
        (title, body, labels) and propose a list of small, independently executable tasks that
        together complete the issue.

        Rules:
        - Each task should be small enough to be completed by one autonomous coding agent in a single session.
        - Prefer 1–5 tasks. Fewer if the issue is small.
        - Tasks should be as parallelizable as possible. If they MUST run in order, that's fine — set Order accordingly.
        - Titles are imperative, under 80 characters ("Add ...", "Refactor ...", "Wire ...").
        - Descriptions are 1-3 sentences explaining scope, files likely involved, and acceptance criteria.

        The work item:

        Title: {{TITLE}}

        Labels: {{LABELS}}

        Body:
        {{BODY}}
        {{EXISTING_TASKS}}

        When you are ready to deliver the proposed tasks, call the MCP tool `{{SUBMIT_TOOL}}` with
        an argument shaped as:

        {
          "tasks": [
            { "title": "...", "description": "...", "order": 0 },
            ...
          ]
        }

        Calling the tool is what hands the result back to Kagura — do not print the JSON to stdout,
        do not create any files, do not edit the working tree. After the tool call succeeds,
        exit cleanly.
        """;

    // Legacy one-shot SystemPrompt used when no TriageAgentContext is supplied (fallback path).
    private const string LegacySystemPrompt =
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
    private readonly TriageAgentContext _context;
    private readonly IAgentRunner _runner;
    private readonly GitService _git;
    private readonly IPromptSnapshotSink _promptSink;
    private readonly IPromptResolver _promptResolver;
    private readonly ILogger<ClaudeCliTriageService> _log;

    public ClaudeCliTriageService(
        IOptions<TriageOptions> options,
        TriageAgentContext context,
        IAgentRunner runner,
        GitService git,
        IPromptSnapshotSink promptSink,
        IPromptResolver promptResolver,
        ILogger<ClaudeCliTriageService> log)
    {
        _options = options.Value;
        _context = context;
        _runner = runner;
        _git = git;
        _promptSink = promptSink;
        _promptResolver = promptResolver;
        _log = log;
    }

    public async Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
        string workItemTitle, string workItemBody, string? labels,
        IReadOnlyList<ExistingTask>? existingTasks = null,
        CancellationToken ct = default)
    {
        // Per ADR 0001: when invoked inside the Agent kickoff path, spawn a PTY Triage Agent
        // and block on its MCP submission. Without a context we fall back to the legacy
        // one-shot `claude -p` invocation so the strings-only interface still works.
        if (_context.IsSet)
            return await ProposeViaAgentAsync(workItemTitle, workItemBody, labels, existingTasks, ct);

        return await ProposeViaLegacyCliAsync(workItemTitle, workItemBody, labels, existingTasks, ct);
    }

    private async Task<IReadOnlyList<TriagedTaskProposal>> ProposeViaAgentAsync(
        string workItemTitle, string workItemBody, string? labels,
        IReadOnlyList<ExistingTask>? existingTasks,
        CancellationToken ct)
    {
        var wi = _context.WorkItem!;
        var runId = _context.RunId;

        // Per CONTEXT.md → "Agent working directory": Triage runs in the Source's scratch
        // worktree on detached HEAD at the default branch. Refresh on every spawn.
        var cwd = await _git.EnsureScratchWorktreeAsync(wi.Source, ct);

        // Per ADR 0002: resolve the prompt template lazily — the per-Source override wins
        // if present, otherwise the built-in default. Caller must have eager-loaded the
        // Source's PromptOverrides collection (TriageKickoffService does).
        var template = _promptResolver.Resolve(wi.Source, Role.Triage);
        var prompt = RenderPrompt(template, workItemTitle, workItemBody, labels, existingTasks);

        // Snapshot the resolved prompt onto AgentRun.PromptText BEFORE spawning so the audit
        // trail is correct even if the PTY crashes immediately. (ADR 0002.)
        await _promptSink.SaveAsync(runId, prompt, ct);

        _log.LogInformation(
            "Spawning Triage Agent for work item {WorkItemId} in scratch worktree {Cwd}",
            wi.Id, cwd);

        var submission = await _runner.StartAndAwaitResultAsync<TriageSubmission>(
            runId, wi, Role.Triage, prompt, cwd, ct);

        return submission.Tasks
            .Select(t => new TriagedTaskProposal(t.Title, t.Description, t.Order))
            .ToList();
    }

    /// <summary>
    /// Interpolate a Triage prompt template with the work item's title/body/labels and the
    /// optional existing-tasks dedupe block. Pure function — takes the resolved template as
    /// input so callers control whether they got it from <see cref="IPromptResolver"/> (the
    /// runtime path) or from a literal for tests.
    /// </summary>
    public static string RenderPrompt(
        string template,
        string workItemTitle, string workItemBody, string? labels,
        IReadOnlyList<ExistingTask>? existingTasks)
    {
        var existingBlock = (existingTasks is null || existingTasks.Count == 0)
            ? string.Empty
            : "\n\nExisting tasks already proposed or in flight for this work item:\n" +
              string.Join("\n", existingTasks.Select((t, i) => $"{i + 1}. {t.Title}\n   {t.Description}")) +
              "\n\nDo NOT propose duplicates or near-duplicates of the existing tasks above. Only suggest new tasks that cover work not already represented.";

        return template
            .Replace("{{TITLE}}", workItemTitle)
            .Replace("{{LABELS}}", labels ?? "(none)")
            .Replace("{{BODY}}", workItemBody)
            .Replace("{{EXISTING_TASKS}}", existingBlock)
            .Replace("{{SUBMIT_TOOL}}", Role.Triage.McpSubmitToolName());
    }

    /// <summary>
    /// Convenience overload that interpolates the built-in Triage default template. Useful
    /// for tests that want to assert on the default output without going through the
    /// resolver.
    /// </summary>
    public static string RenderPrompt(
        string workItemTitle, string workItemBody, string? labels,
        IReadOnlyList<ExistingTask>? existingTasks)
        => RenderPrompt(DefaultPromptTemplate, workItemTitle, workItemBody, labels, existingTasks);

    // ---------------- Legacy one-shot fallback ----------------

    private async Task<IReadOnlyList<TriagedTaskProposal>> ProposeViaLegacyCliAsync(
        string workItemTitle, string workItemBody, string? labels,
        IReadOnlyList<ExistingTask>? existingTasks,
        CancellationToken ct)
    {
        var userPrompt = BuildUserPrompt(workItemTitle, workItemBody, labels, existingTasks);

        var args = new List<string>
        {
            "-p", userPrompt,
            "--append-system-prompt", LegacySystemPrompt,
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

        _log.LogInformation("Triage (legacy path) proposed {Count} tasks", arr.Count);
        return arr;
    }

    public static string BuildUserPrompt(
        string workItemTitle,
        string workItemBody,
        string? labels,
        IReadOnlyList<ExistingTask>? existingTasks)
    {
        var basePrompt =
            $"""
             Title: {workItemTitle}

             Labels: {labels ?? "(none)"}

             Body:
             {workItemBody}
             """;

        if (existingTasks is null || existingTasks.Count == 0)
            return basePrompt;

        var rendered = string.Join(
            "\n",
            existingTasks.Select((t, i) => $"{i + 1}. {t.Title}\n   {t.Description}"));

        return basePrompt +
            $"""


             Existing tasks already proposed or in flight for this work item:
             {rendered}

             Do NOT propose duplicates or near-duplicates of the existing tasks above. Only suggest new tasks that cover work not already represented.
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

/// <summary>
/// Persists the resolved prompt onto the matching <c>AgentRun</c> row at spawn time.
/// Per ADR 0002: every AgentRun snapshots the resolved prompt so the audit trail of past
/// runs is unaffected by later prompt edits. The interface lives in Core so
/// <c>ClaudeCliTriageService</c> can call it without a hard Data dependency.
/// </summary>
public interface IPromptSnapshotSink
{
    Task SaveAsync(Guid runId, string promptText, CancellationToken ct);
}
