using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kagura.Core.Merge;

public class MergeResolverOptions
{
    public string ClaudeBinary { get; set; } = "claude";
    public string? Model { get; set; }
}

public class ClaudeCliMergeResolver : IMergeConflictResolver
{
    /// <summary>
    /// Built-in MergeResolver prompt template. Per ADR 0002 this is the default a Source
    /// resolves at Agent spawn time when it hasn't customised its own MergeResolver prompt;
    /// per-Source overrides are resolved through <see cref="IPromptResolver"/>.
    /// Placeholders: <c>{{TASK_TITLE}}</c>, <c>{{SUBMIT_TOOL}}</c>.
    /// </summary>
    public const string DefaultPromptTemplate =
        """
        You are a git merge-conflict resolver. The working directory is a git worktree
        in the middle of a `git merge` that hit conflicts while integrating the task
        below into its parent work-item branch. Your only job is to resolve those
        conflicts and finalize the merge.

        Task: {{TASK_TITLE}}

        Rules:
        - Use `git status` and `git diff` to understand the conflicts.
        - For each conflicted file, choose the resolution that preserves both branches'
          intent. Prefer combining when both sides contribute meaningful changes.
        - Do NOT touch files that are not currently conflicted.
        - Do NOT run `git merge --abort` or otherwise discard the in-progress merge.
        - When all files are resolved: `git add -A` then `git commit --no-edit` to
          finalize the merge.
        - If you cannot resolve safely (semantic conflict, opposing edits, unclear
          intent), STOP. Do not commit. Do not abort. Leave the merge in-progress for a
          human.

        When you are finished — either because you committed the merge or because you
        decided to abandon — call the MCP tool `{{SUBMIT_TOOL}}` with an argument
        shaped as:

        {
          "resolved": true | false,
          "notes": "1-3 sentences describing what you did or why you gave up"
        }

        Set `resolved` true ONLY after `git commit` finalized the merge; otherwise set
        it false. Calling the tool is what hands the verdict back to Kagura — do not
        print the JSON to stdout, do not create any files documenting the result, and
        do not edit any non-conflicted files. After the tool call succeeds, exit
        cleanly.
        """;

    private readonly MergeResolverAgentContext _context;
    private readonly Lazy<IAgentRunner> _runner;
    private readonly ILogger<ClaudeCliMergeResolver> _log;

    /// <summary>
    /// <see cref="IAgentRunner"/> is wrapped in <see cref="Lazy{T}"/> because the production DI
    /// graph has a cycle on paper: <c>GitService → IMergeConflictResolver → IAgentRunner → GitService</c>.
    /// The cycle is benign in practice — the resolver only touches IAgentRunner inside
    /// <see cref="ResolveAsync"/>, never during construction — but the eager DI graph still
    /// needs an indirection to break it.
    /// </summary>
    public ClaudeCliMergeResolver(
        IOptions<MergeResolverOptions> options,
        MergeResolverAgentContext context,
        Lazy<IAgentRunner> runner,
        ILogger<ClaudeCliMergeResolver> log)
    {
        _ = options; // retained for symmetry with the other Role services / future use
        _context = context;
        _runner = runner;
        _log = log;
    }

    public async Task<MergeResolutionResult> ResolveAsync(
        string worktreePath, string taskTitle, CancellationToken ct = default)
    {
        // Per ADR 0001 / issue #70: MergeResolver runs only as a PTY Agent. The legacy
        // claude -p fallback has been removed; callers must invoke through the kickoff
        // path that populates MergeResolverAgentContext (GitService.MergeTaskBranchAsync
        // does this via IMergeResolverKickoff).
        if (!_context.IsSet)
            throw new InvalidOperationException(
                "ClaudeCliMergeResolver requires a MergeResolverAgentContext to be populated. " +
                "Invoke ResolveAsync through GitService.MergeTaskBranchAsync (which wires the " +
                "IMergeResolverKickoff that pushes the context) rather than calling it directly.");

        var wi = _context.WorkItem!;
        var runId = _context.RunId;

        // The kickoff hook (see MergeResolverKickoffService) renders the prompt and snapshots
        // it onto AgentRun.PromptText inside its own scope before populating the context.
        // The resolver re-renders here only as a defence in depth — if the context didn't
        // include a pre-rendered prompt we still pass something coherent to the Agent.
        var prompt = _context.Prompt ?? RenderPrompt(taskTitle);

        // Per CONTEXT.md → "Agent working directory": MergeResolver runs in the WorkItem's
        // merge worktree — that's where the conflicting state lives. GitService has already
        // ensured the worktree exists and started the merge that hit conflicts; we just
        // hand its path down as the Agent's cwd.
        _log.LogInformation(
            "Spawning MergeResolver Agent for work item {WorkItemId} in merge worktree {Cwd}",
            wi.Id, worktreePath);

        try
        {
            var submission = await _runner.Value.StartAndAwaitResultAsync<MergeResolutionSubmission>(
                runId, wi, Role.MergeResolver, prompt, worktreePath, ct);

            _log.LogInformation(
                "MergeResolver verdict for '{Title}': resolved={Resolved}",
                taskTitle, submission.Resolved);
            return new MergeResolutionResult(submission.Resolved, submission.Notes);
        }
        catch (AgentInterruptedException)
        {
            // User stopped the Agent mid-flight — surface as an unresolved verdict so the
            // upstream merge path leaves the worktree in-conflict for manual handling.
            // Per CONTEXT.md "Stop vs Cancel" the orchestrator will also halt at this point.
            throw;
        }
        catch (AgentSubmissionMissingException ex)
        {
            _log.LogWarning(
                "MergeResolver Agent for '{Title}' exited without submitting (exit code {ExitCode})",
                taskTitle, ex.ExitCode);
            return new MergeResolutionResult(
                Success: false,
                Notes: $"MergeResolver agent exited without submitting (exit code {ex.ExitCode?.ToString() ?? "null"}).");
        }
    }

    public static string RenderPrompt(string taskTitle) =>
        DefaultPromptTemplate
            .Replace("{{TASK_TITLE}}", taskTitle)
            .Replace("{{SUBMIT_TOOL}}", Role.MergeResolver.McpSubmitToolName());
}
