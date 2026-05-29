using Kagura.Core.Domain;

namespace Kagura.Core.Review;

/// <summary>
/// Ambient context used to hand the AutoReview Agent's full context (WorkItem +
/// pre-allocated run id + resolved prompt) into <see cref="IReviewService.ReviewAsync"/>
/// without changing that interface's signature.
///
/// Mirrors <see cref="Kagura.Core.Merge.MergeResolverAgentContext"/>: AsyncLocal slots
/// scoped by <see cref="Push"/>/Dispose so the orchestrator (AutoReviewKickoffService)
/// can populate them immediately before calling <c>ReviewAsync</c> for a given task
/// and clear them on exit. AsyncLocal is used here (rather than scoped DI like
/// <see cref="Kagura.Core.Triage.TriageAgentContext"/>) because AutoReviewKickoffService
/// loops over multiple AwaitingReview tasks within one scope — each loop iteration
/// pushes its own per-task RunId / Prompt and pops it on Dispose so a later iteration
/// can't see stale state.
/// </summary>
public sealed class AutoReviewAgentContext
{
    private readonly AsyncLocal<WorkItem?> _workItem = new();
    private readonly AsyncLocal<Guid> _runId = new();
    private readonly AsyncLocal<string?> _prompt = new();
    private readonly AsyncLocal<string?> _mergeWorktreePath = new();

    public WorkItem? WorkItem
    {
        get => _workItem.Value;
        set => _workItem.Value = value;
    }

    public Guid RunId
    {
        get => _runId.Value;
        set => _runId.Value = value;
    }

    /// <summary>
    /// Resolved prompt text the Agent will be launched with. The kickoff hook renders this
    /// inside its scope (so the scoped <c>IPromptSnapshotSink</c> can persist it onto
    /// <c>AgentRun.PromptText</c>) and then sets it here so the resolver can re-use it
    /// without needing its own DbContext.
    /// </summary>
    public string? Prompt
    {
        get => _prompt.Value;
        set => _prompt.Value = value;
    }

    /// <summary>
    /// Path of the WorkItem's merge worktree, computed by the kickoff (which has GitService
    /// in scope) and handed to the review service for use as the Agent's cwd. Per
    /// CONTEXT.md → "Agent working directory": AutoReview runs in the WorkItem's merge
    /// worktree where the merged diff already lives.
    /// </summary>
    public string? MergeWorktreePath
    {
        get => _mergeWorktreePath.Value;
        set => _mergeWorktreePath.Value = value;
    }

    public bool IsSet => WorkItem is not null && RunId != Guid.Empty;

    /// <summary>
    /// Convenience helper that scopes the context to a single ReviewAsync invocation.
    /// On dispose the slots are cleared so a later review on the same async flow doesn't
    /// accidentally re-use stale state.
    /// </summary>
    public IDisposable Push(WorkItem wi, Guid runId, string? prompt = null, string? mergeWorktreePath = null)
    {
        var priorWi = _workItem.Value;
        var priorRunId = _runId.Value;
        var priorPrompt = _prompt.Value;
        var priorMergeWorktreePath = _mergeWorktreePath.Value;
        _workItem.Value = wi;
        _runId.Value = runId;
        _prompt.Value = prompt;
        _mergeWorktreePath.Value = mergeWorktreePath;
        return new Scope(this, priorWi, priorRunId, priorPrompt, priorMergeWorktreePath);
    }

    private sealed class Scope : IDisposable
    {
        private readonly AutoReviewAgentContext _owner;
        private readonly WorkItem? _priorWi;
        private readonly Guid _priorRunId;
        private readonly string? _priorPrompt;
        private readonly string? _priorMergeWorktreePath;

        public Scope(AutoReviewAgentContext owner, WorkItem? priorWi, Guid priorRunId, string? priorPrompt, string? priorMergeWorktreePath)
        {
            _owner = owner;
            _priorWi = priorWi;
            _priorRunId = priorRunId;
            _priorPrompt = priorPrompt;
            _priorMergeWorktreePath = priorMergeWorktreePath;
        }

        public void Dispose()
        {
            _owner._workItem.Value = _priorWi;
            _owner._runId.Value = _priorRunId;
            _owner._prompt.Value = _priorPrompt;
            _owner._mergeWorktreePath.Value = _priorMergeWorktreePath;
        }
    }
}
