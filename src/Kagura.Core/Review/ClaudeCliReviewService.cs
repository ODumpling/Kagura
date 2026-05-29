using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;
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
    /// Built-in AutoReview prompt template. Per ADR 0002 this is the default a Source resolves
    /// at Agent spawn time when it hasn't customised its own AutoReview prompt; per-Source
    /// overrides are wired in through <see cref="IPromptResolver"/>.
    /// Placeholders: <c>{{TASK_TITLE}}</c>, <c>{{TASK_DESCRIPTION}}</c>, <c>{{DIFF}}</c>,
    /// <c>{{DIFF_BANNER}}</c>, <c>{{SUBMIT_TOOL}}</c>.
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

        # Task

        Title: {{TASK_TITLE}}

        Description:
        {{TASK_DESCRIPTION}}

        # Diff{{DIFF_BANNER}}

        ```diff
        {{DIFF}}
        ```

        # Delivering the result

        When you have decided, call the MCP tool `{{SUBMIT_TOOL}}` with an argument shaped as:

        {
          "autoMerge": true | false,
          "reasoning": "1-3 sentences explaining the decision"
        }

        Calling the tool is what hands the verdict back to Kagura — do not print the JSON to
        stdout, do not create any files documenting the result, and do not edit the working
        tree. After the tool call succeeds, exit cleanly.
        """;

    private readonly ReviewOptions _options;
    private readonly AutoReviewAgentContext _context;
    private readonly Lazy<IAgentRunner> _runner;
    private readonly ILogger<ClaudeCliReviewService> _log;

    /// <summary>
    /// <see cref="IAgentRunner"/> is wrapped in <see cref="Lazy{T}"/> to mirror the
    /// MergeResolver pattern — keeps the resolver out of the DI construction graph while
    /// still resolving at first use. AutoReview itself does not currently sit on a cycle,
    /// but using Lazy here keeps the four orchestrated-Role services structurally identical
    /// and protects against future cycles introduced by review-prompt coordination.
    /// </summary>
    public ClaudeCliReviewService(
        IOptions<ReviewOptions> options,
        AutoReviewAgentContext context,
        Lazy<IAgentRunner> runner,
        ILogger<ClaudeCliReviewService> log)
    {
        _options = options.Value;
        _context = context;
        _runner = runner;
        _log = log;
    }

    public async Task<ReviewVerdict> ReviewAsync(
        Guid runId, string taskTitle, string taskDescription, string diff, CancellationToken ct = default)
    {
        _ = runId;
        // Per CONTEXT.md → "Service interfaces" + ADR 0001: AutoReview is one of the
        // orchestrated Roles, so it must be invoked inside an Agent kickoff scope that
        // pushed the AutoReviewAgentContext for this work item / run id. Failing fast
        // here makes the (now-removed) legacy claude -p fallback impossible to hit by
        // accident — callers either wire the kickoff or they don't get a review.
        if (!_context.IsSet)
            throw new InvalidOperationException(
                "ClaudeCliReviewService requires an AutoReviewAgentContext to be populated. " +
                "Invoke ReviewAsync through AutoReviewKickoffService (which pushes the context) " +
                "rather than calling it directly.");

        // Empty diffs are a degenerate input we short-circuit before spawning an Agent — there
        // is nothing for the reviewer to evaluate and no point burning a PTY round-trip on it.
        if (string.IsNullOrWhiteSpace(diff))
            return new ReviewVerdict(false, "Diff was empty — nothing to review.");

        var wi = _context.WorkItem!;
        var contextRunId = _context.RunId;

        // The kickoff hook (see AutoReviewKickoffService) renders the prompt and snapshots
        // it onto AgentRun.PromptText inside its own scope before populating the context.
        // The service re-renders here only as a defence in depth — if the context didn't
        // include a pre-rendered prompt we still pass something coherent to the Agent.
        var prompt = _context.Prompt
            ?? RenderPrompt(DefaultPromptTemplate, taskTitle, taskDescription, diff, _options.MaxDiffBytes);

        // Per CONTEXT.md → "Agent working directory": AutoReview runs in the WorkItem's merge
        // worktree — the merged diff already lives there, and that's the natural place for the
        // reviewer to poke around if the user attaches. The kickoff (AutoReviewKickoffService)
        // ensures the merge worktree exists and passes its path through the context.
        var cwd = _context.MergeWorktreePath
            ?? throw new InvalidOperationException(
                "AutoReviewAgentContext.MergeWorktreePath was not populated. " +
                "AutoReviewKickoffService must ensure the WorkItem's merge worktree exists " +
                "and pass its path on Push() before invoking ReviewAsync.");

        _log.LogInformation(
            "Spawning AutoReview Agent for work item {WorkItemId} task '{TaskTitle}' in {Cwd}",
            wi.Id, taskTitle, cwd);

        try
        {
            var submission = await _runner.Value.StartAndAwaitResultAsync<ReviewSubmission>(
                contextRunId, wi, Role.AutoReview, prompt, cwd, ct);

            _log.LogInformation(
                "AutoReview verdict for '{Title}': autoMerge={AutoMerge}", taskTitle, submission.AutoMerge);
            return new ReviewVerdict(submission.AutoMerge, submission.Reasoning);
        }
        catch (AgentSubmissionMissingException ex)
        {
            // Exited without submitting — surface as a non-auto-merge verdict so the upstream
            // pipeline flags the task for human review rather than silently failing the run.
            _log.LogWarning(
                "AutoReview Agent for '{Title}' exited without submitting (exit code {ExitCode})",
                taskTitle, ex.ExitCode);
            return new ReviewVerdict(
                AutoMerge: false,
                Reasoning: $"AutoReview agent exited without submitting (exit code {ex.ExitCode?.ToString() ?? "null"}).");
        }
    }

    /// <summary>
    /// Interpolate an AutoReview prompt template with the task title / description / diff.
    /// Pure function — takes the resolved template as input so callers control whether they
    /// got it from <see cref="IPromptResolver"/> (the runtime path) or from a literal for
    /// tests. Truncation is applied here, matching the legacy reviewer's MaxDiffBytes cap so
    /// the LLM never receives an unbounded payload.
    /// </summary>
    public static string RenderPrompt(
        string template,
        string taskTitle,
        string taskDescription,
        string diff,
        int maxDiffBytes)
    {
        var truncated = diff.Length > maxDiffBytes;
        var diffForLlm = truncated ? diff[..maxDiffBytes] : diff;
        var banner = truncated ? $" (truncated to {maxDiffBytes} bytes of {diff.Length})" : string.Empty;

        return template
            .Replace("{{TASK_TITLE}}", taskTitle)
            .Replace("{{TASK_DESCRIPTION}}", taskDescription)
            .Replace("{{DIFF_BANNER}}", banner)
            .Replace("{{DIFF}}", diffForLlm)
            .Replace("{{SUBMIT_TOOL}}", Role.AutoReview.McpSubmitToolName());
    }

    /// <summary>
    /// Convenience overload that interpolates the built-in default template. Useful for tests
    /// that want to assert on the default output without going through the resolver.
    /// </summary>
    public static string RenderPrompt(string taskTitle, string taskDescription, string diff, int maxDiffBytes = 50_000)
        => RenderPrompt(DefaultPromptTemplate, taskTitle, taskDescription, diff, maxDiffBytes);
}
