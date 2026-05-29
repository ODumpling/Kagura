using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;
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
    /// are resolved through <see cref="IPromptResolver"/>.
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
        // Per ADR 0001 / issue #70: Triage runs only as a PTY Agent. Callers must invoke via
        // TriageKickoffService (which populates the TriageAgentContext); the legacy one-shot
        // claude -p fallback has been removed.
        if (!_context.IsSet)
            throw new InvalidOperationException(
                "ClaudeCliTriageService requires a TriageAgentContext to be populated. " +
                "Invoke ProposeTasksAsync through TriageKickoffService rather than calling it directly.");

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
