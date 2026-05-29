using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Services;

public sealed class TriageKickoffResult
{
    public Guid? RunId { get; }
    public string? Error { get; }
    public bool WorkItemNotFound { get; }

    private TriageKickoffResult(Guid? runId, string? error, bool notFound)
    {
        RunId = runId;
        Error = error;
        WorkItemNotFound = notFound;
    }

    public static TriageKickoffResult Accepted(Guid runId) => new(runId, null, false);
    public static TriageKickoffResult NotFound() => new(null, null, true);
    public static TriageKickoffResult Invalid(string error) => new(null, error, false);
}

public interface ITriageKickoffService
{
    Task<TriageKickoffResult> KickoffAsync(Guid workItemId, CancellationToken ct = default);
}

public sealed class TriageKickoffService : ITriageKickoffService
{
    private readonly KaguraDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TriageKickoffService> _log;

    public TriageKickoffService(
        KaguraDbContext db,
        IServiceScopeFactory scopeFactory,
        ILogger<TriageKickoffService> log)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task<TriageKickoffResult> KickoffAsync(Guid workItemId, CancellationToken ct = default)
    {
        var wi = await _db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null) return TriageKickoffResult.NotFound();
        if (wi.Status == WorkItemStatus.Closed)
            return TriageKickoffResult.Invalid("Work item is closed.");

        var run = new AgentRun
        {
            Kind = AgentRunKind.Triage,
            WorkItemId = wi.Id,
            Status = AgentRunStatus.Running,
        };
        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var scopeFactory = _scopeFactory;
        var log = _log;
        _ = Task.Run(() => RunTriageAsync(scopeFactory, log, workItemId, run.Id));

        return TriageKickoffResult.Accepted(run.Id);
    }

    // Fire-and-forget triage. Runs the triage service, persists proposals (or LastTriageError
    // on parse failure), updates the AgentRun row, and emits workItemUpdated on completion.
    private static async Task RunTriageAsync(IServiceScopeFactory scopeFactory, ILogger log, Guid workItemId, Guid runId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var triage = scope.ServiceProvider.GetRequiredService<ITriageService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<IAgentBroadcaster>();
        var agentContext = scope.ServiceProvider.GetRequiredService<Kagura.Core.Triage.TriageAgentContext>();

        var wi = await db.WorkItems
            .Include(w => w.Tasks)
            .Include(w => w.Source)
                .ThenInclude(s => s!.PromptOverrides)
            .FirstOrDefaultAsync(w => w.Id == workItemId);
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (wi is null || run is null) return;

        // Per CONTEXT.md → "Agent result contract": the Triage path runs as a PTY Agent that
        // delivers its proposed tasks via the kagura.submit_triage MCP tool. The scoped
        // TriageAgentContext is how the kickoff hands the WorkItem + runId down to the
        // ClaudeCliTriageService implementation without changing the ITriageService signature.
        agentContext.WorkItem = wi;
        agentContext.RunId = runId;

        try
        {
            var existingTasks = await db.AgentTasks
                .Where(t => t.WorkItemId == wi.Id)
                .OrderBy(t => t.Order)
                .ToListAsync();

            var existing = existingTasks
                .Where(t => t.Status != AgentTaskStatus.Proposed && t.Status != AgentTaskStatus.Cancelled)
                .Select(t => new ExistingTask(t.Title, t.Description))
                .ToList();
            var proposals = await triage.ProposeTasksAsync(wi.Title, wi.Body, wi.Labels, existing);

            var existingProposed = existingTasks.Where(t => t.Status == AgentTaskStatus.Proposed).ToList();
            db.AgentTasks.RemoveRange(existingProposed);
            foreach (var p in proposals)
            {
                db.AgentTasks.Add(new AgentTask
                {
                    WorkItemId = wi.Id,
                    Title = p.Title,
                    Description = p.Description,
                    Order = p.Order,
                });
            }

            wi.LastTriageError = null;
            wi.UpdatedAt = DateTime.UtcNow;
            run.Status = AgentRunStatus.Exited;
            run.EndedAt = DateTime.UtcNow;
        }
        catch (Kagura.Core.Agents.AgentInterruptedException)
        {
            // Per CONTEXT.md "Stop vs Cancel" + issue #70: user-stop on an orchestrated
            // Triage Agent halts Ralph. AgentRunSink has already marked the run Killed and
            // populated RalphLoopHaltReason via the KilledByUser path; we deliberately do
            // NOT set LastTriageError here, so the "Retry Ralph Loop" resume button can
            // re-spawn cleanly without first having to manually clear an error banner.
            log.LogInformation("Triage Agent for work item {WorkItemId} stopped by user", workItemId);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Triage failed for work item {WorkItemId}", workItemId);
            wi.LastTriageError = ex.Message;
            wi.UpdatedAt = DateTime.UtcNow;
            run.Status = AgentRunStatus.Crashed;
            run.EndedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        await broadcaster.WorkItemUpdatedAsync(workItemId);
    }
}
