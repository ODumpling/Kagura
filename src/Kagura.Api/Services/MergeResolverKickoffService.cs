using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Merge;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Services;

/// <summary>
/// Per issue #66: bridges the singleton <see cref="GitService"/> into the scoped DI world
/// long enough to spawn a MergeResolver PTY Agent. <see cref="ResolveAsync"/> creates the
/// <see cref="AgentRun"/> row, pushes its id and the WorkItem onto the ambient
/// <see cref="MergeResolverAgentContext"/>, invokes the resolver, and pops the ambient
/// on the way out — all inside a single async frame so the AsyncLocal context is actually
/// visible to the resolver call (AsyncLocal mutations only flow downstream, not back to
/// the caller of an awaited async method).
///
/// The AgentRun row's terminal status (Exited / Killed / Crashed) is filled in by
/// <c>AgentRunSink.RecordExitAsync</c> on PTY exit — so we deliberately do NOT update it
/// here on the way out; doing so would race with the sink and clobber the right status.
/// </summary>
public sealed class MergeResolverKickoffService : IMergeResolverKickoff
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MergeResolverAgentContext _context;
    private readonly IMergeConflictResolver _resolver;
    private readonly ILogger<MergeResolverKickoffService> _log;

    public MergeResolverKickoffService(
        IServiceScopeFactory scopeFactory,
        MergeResolverAgentContext context,
        IMergeConflictResolver resolver,
        ILogger<MergeResolverKickoffService> log)
    {
        _scopeFactory = scopeFactory;
        _context = context;
        _resolver = resolver;
        _log = log;
    }

    public async Task<MergeResolutionResult> ResolveAsync(
        WorkItem wi, AgentTask task, string worktreePath, CancellationToken ct)
    {
        var (runId, prompt) = await AllocateRunAsync(wi, task, ct);

        // Push the ambient context in THIS async frame so the resolver — which runs inside
        // the same frame via the await below — actually sees it. The context is popped on
        // dispose at the end of this method, regardless of whether the resolver throws.
        using var _ = _context.Push(wi, runId, prompt);
        return await _resolver.ResolveAsync(worktreePath, task.Title, ct);
    }

    private async Task<(Guid RunId, string Prompt)> AllocateRunAsync(
        WorkItem wi, AgentTask task, CancellationToken ct)
    {
        // Allocate the AgentRun row and snapshot the resolved prompt in our own scope so the
        // DbContext / scoped IPromptSnapshotSink are short-lived. The PTY agent then opens
        // fresh scopes as needed (for AgentRunSink on exit).
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var promptSink = scope.ServiceProvider.GetRequiredService<IPromptSnapshotSink>();

        var run = new AgentRun
        {
            Kind = AgentRunKind.MergeResolver,
            WorkItemId = wi.Id,
            AgentTaskId = task.Id,
            Status = AgentRunStatus.Running,
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var prompt = ClaudeCliMergeResolver.RenderPrompt(task.Title);

        // ADR 0002 — snapshot the resolved prompt before the PTY spawns so the audit trail
        // survives even if the Agent crashes immediately. The snapshot path uses the scoped
        // DbContext we already have in scope here.
        await promptSink.SaveAsync(run.Id, prompt, ct);

        _log.LogInformation(
            "Allocated MergeResolver AgentRun {RunId} for work item {WorkItemId} / task {TaskId}",
            run.Id, wi.Id, task.Id);

        return (run.Id, prompt);
    }
}
