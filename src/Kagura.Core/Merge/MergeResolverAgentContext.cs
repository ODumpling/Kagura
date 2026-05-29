using Kagura.Core.Domain;

namespace Kagura.Core.Merge;

/// <summary>
/// Ambient context used to hand the MergeResolver Agent's full context (WorkItem +
/// pre-allocated run id) into <see cref="IMergeConflictResolver.ResolveAsync"/> without
/// changing that interface's signature.
///
/// Unlike the Triage variant (scoped DI) merge resolution flows through the singleton
/// <see cref="Kagura.Core.Git.GitService"/>, so a scoped service wouldn't be visible to
/// the resolver. Backing the slots with <see cref="AsyncLocal{T}"/> lets the caller
/// (GitService.MergeTaskBranchAsync, which already has the WorkItem in hand) set the
/// context immediately before invoking the resolver — and the resolver reads it on the
/// same async flow regardless of DI lifetime.
///
/// When unset (e.g. tests using a stub resolver, or callers that haven't created an
/// AgentRun row), the resolver falls back to its legacy <c>claude -p</c> behaviour so
/// the strings-only <see cref="IMergeConflictResolver"/> interface remains usable from
/// places that don't have a WorkItem at hand.
/// </summary>
public sealed class MergeResolverAgentContext
{
    private readonly AsyncLocal<WorkItem?> _workItem = new();
    private readonly AsyncLocal<Guid> _runId = new();
    private readonly AsyncLocal<string?> _prompt = new();

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
    /// <c>AgentRun.PromptText</c>) and then sets it here so the singleton resolver can
    /// re-use it without needing its own DbContext.
    /// </summary>
    public string? Prompt
    {
        get => _prompt.Value;
        set => _prompt.Value = value;
    }

    public bool IsSet => WorkItem is not null && RunId != Guid.Empty;

    /// <summary>
    /// Convenience helper that scopes the context to a single resolver invocation. The
    /// caller writes WorkItem + RunId into the AsyncLocal slots for the lifetime of the
    /// returned <see cref="IDisposable"/>; on dispose the slots are cleared so a later
    /// merge invocation on the same async flow doesn't accidentally re-use stale state.
    /// </summary>
    public IDisposable Push(WorkItem wi, Guid runId, string? prompt = null)
    {
        var priorWi = _workItem.Value;
        var priorRunId = _runId.Value;
        var priorPrompt = _prompt.Value;
        _workItem.Value = wi;
        _runId.Value = runId;
        _prompt.Value = prompt;
        return new Scope(this, priorWi, priorRunId, priorPrompt);
    }

    private sealed class Scope : IDisposable
    {
        private readonly MergeResolverAgentContext _owner;
        private readonly WorkItem? _priorWi;
        private readonly Guid _priorRunId;
        private readonly string? _priorPrompt;

        public Scope(MergeResolverAgentContext owner, WorkItem? priorWi, Guid priorRunId, string? priorPrompt)
        {
            _owner = owner;
            _priorWi = priorWi;
            _priorRunId = priorRunId;
            _priorPrompt = priorPrompt;
        }

        public void Dispose()
        {
            _owner._workItem.Value = _priorWi;
            _owner._runId.Value = _priorRunId;
            _owner._prompt.Value = _priorPrompt;
        }
    }
}
