using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Merge;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Services;

/// <summary>
/// Per issue #66: bridges the singleton <see cref="GitService"/> into the scoped DI world
/// long enough to spawn a MergeResolver PTY Agent. When <see cref="BeginAsync"/> is invoked
/// at the moment a merge hits conflicts, this service creates an <see cref="AgentRun"/>
/// row for the work item, pushes its <see cref="AgentRun.Id"/> and the WorkItem onto the
/// ambient <see cref="MergeResolverAgentContext"/>, and returns a disposer that pops the
/// ambient when the resolver call completes (success or failure).
///
/// The AgentRun row's terminal status (Exited / Killed / Crashed) is filled in by
/// <c>AgentRunSink.RecordExitAsync</c> on PTY exit — so we deliberately do NOT update it
/// here on the way out; doing so would race with the sink and clobber the right status.
/// </summary>
public sealed class MergeResolverKickoffService : IMergeResolverKickoff
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MergeResolverAgentContext _context;
    private readonly ILogger<MergeResolverKickoffService> _log;

    public MergeResolverKickoffService(
        IServiceScopeFactory scopeFactory,
        MergeResolverAgentContext context,
        ILogger<MergeResolverKickoffService> log)
    {
        _scopeFactory = scopeFactory;
        _context = context;
        _log = log;
    }

    public async Task<IAsyncDisposable> BeginAsync(WorkItem wi, AgentTask task, CancellationToken ct)
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

        var pop = _context.Push(wi, run.Id, prompt);
        return new Disposer(pop);
    }

    private sealed class Disposer : IAsyncDisposable
    {
        private readonly IDisposable _pop;
        public Disposer(IDisposable pop) => _pop = pop;
        public ValueTask DisposeAsync()
        {
            _pop.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
