using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record StartAgentDto(Guid TaskId);

public record AgentRunDto(
    Guid RunId,
    Guid TaskId,
    Guid WorkItemId,
    AgentRunKind Kind,
    string Title,
    string WorkItemExternalId,
    string WorktreePath,
    int ProcessId,
    DateTime StartedAt,
    bool Alive,
    int? ExitCode);

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/agents");

        grp.MapGet("", (IAgentRunner runner) =>
            Results.Ok(runner.Active.Select(ToDto)));

        grp.MapPost("/start/{taskId:guid}", async (
            Guid taskId,
            KaguraDbContext db,
            IAgentRunner runner,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var (session, error) = await StartTaskAsync(db, runner, broadcaster, taskId, ct);
            if (error is not null) return Results.BadRequest(new { error });
            if (session is null) return Results.NotFound();
            return Results.Ok(ToDto(session));
        });

        // Fire-and-forget start every Approved task for a work item. Each task gets its own DI
        // scope so they can run concurrently — the AgentRunner's semaphore caps how many run
        // simultaneously (MaxConcurrentAgents); the rest queue inside StartAsync.
        grp.MapPost("/start-all/{workItemId:guid}", async (
            Guid workItemId,
            KaguraDbContext db,
            IServiceScopeFactory scopeFactory,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var taskIds = await db.AgentTasks
                .Where(t => t.WorkItemId == workItemId && t.Status == AgentTaskStatus.Approved)
                .OrderBy(t => t.Order)
                .Select(t => t.Id)
                .ToListAsync(ct);

            if (taskIds.Count == 0)
                return Results.BadRequest(new { error = "No Approved tasks to start." });

            foreach (var taskId in taskIds)
            {
                _ = Task.Run(async () =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var sdb = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
                    var srunner = scope.ServiceProvider.GetRequiredService<IAgentRunner>();
                    var sbroadcaster = scope.ServiceProvider.GetRequiredService<IAgentBroadcaster>();
                    try
                    {
                        await StartTaskAsync(sdb, srunner, sbroadcaster, taskId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "start-all failed for task {TaskId}", taskId);
                    }
                });
            }

            return Results.Accepted(value: new { queued = taskIds.Count });
        });

        grp.MapPost("/{runId:guid}/stop", async (Guid runId, IAgentRunner runner, KaguraDbContext db, IAgentBroadcaster broadcaster, CancellationToken ct) =>
        {
            await runner.StopAsync(runId);

            var run = await db.AgentRuns
                .Include(r => r.AgentTask)
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is not null)
            {
                run.Status = AgentRunStatus.Killed;
                run.EndedAt = DateTime.UtcNow;
                // Reset task back to Approved so the kanban shows Start again (rerun).
                if (run.AgentTask is not null && run.AgentTask.Status == AgentTaskStatus.Running)
                {
                    run.AgentTask.Status = AgentTaskStatus.Approved;
                    run.AgentTask.UpdatedAt = DateTime.UtcNow;
                }
                await db.SaveChangesAsync(ct);
                if (run.AgentTask is not null)
                    await broadcaster.WorkItemUpdatedAsync(run.AgentTask.WorkItemId);
            }
            return Results.NoContent();
        });

        // Called by the agent itself when it finishes a task. Stops the session, marks the
        // run as Exited, and moves the task to AwaitingReview so it shows up on the review column.
        grp.MapPost("/complete/{taskId:guid}", async (Guid taskId, IAgentRunner runner, KaguraDbContext db, IAgentBroadcaster broadcaster, CancellationToken ct) =>
        {
            var task = await db.AgentTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
            if (task is null) return Results.NotFound();

            var session = runner.Active.FirstOrDefault(s => s.TaskId == taskId);
            if (session is not null)
                await runner.StopAsync(session.RunId);

            var now = DateTime.UtcNow;
            var openRuns = await db.AgentRuns
                .Where(r => r.AgentTaskId == taskId
                            && (r.Status == AgentRunStatus.Running || r.Status == AgentRunStatus.Starting))
                .ToListAsync(ct);
            foreach (var r in openRuns)
            {
                r.Status = AgentRunStatus.Exited;
                r.EndedAt = now;
            }

            task.Status = AgentTaskStatus.AwaitingReview;
            task.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(task.WorkItemId);
            return Results.NoContent();
        });

        // Reset a task that is stuck in Running (no live agent session) back to Approved.
        // Refuses if a session for the task is still alive — caller must Stop it first.
        grp.MapPost("/reset/{taskId:guid}", async (Guid taskId, IAgentRunner runner, KaguraDbContext db, IAgentBroadcaster broadcaster, CancellationToken ct) =>
        {
            var task = await db.AgentTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
            if (task is null) return Results.NotFound();

            if (task.Status != AgentTaskStatus.Running)
                return Results.BadRequest(new { error = $"Task is in status {task.Status}; only Running tasks can be reset." });

            if (runner.Active.Any(s => s.TaskId == taskId))
                return Results.BadRequest(new { error = "Agent is still running. Stop it before resetting." });

            var openRuns = await db.AgentRuns
                .Where(r => r.AgentTaskId == taskId
                            && (r.Status == AgentRunStatus.Running || r.Status == AgentRunStatus.Starting))
                .ToListAsync(ct);
            var now = DateTime.UtcNow;
            foreach (var r in openRuns)
            {
                r.Status = AgentRunStatus.Killed;
                r.EndedAt = now;
            }

            task.Status = AgentTaskStatus.Approved;
            task.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(task.WorkItemId);
            return Results.NoContent();
        });

        return app;
    }

    private static AgentRunDto ToDto(AgentSession s) => new(
        s.RunId, s.TaskId, s.WorkItemId, s.Kind, s.Title, s.WorkItemExternalId,
        s.WorktreePath, s.ProcessId, s.StartedAt, s.Alive, s.ExitCode);

    private static async Task<(AgentSession? session, string? error)> StartTaskAsync(
        KaguraDbContext db,
        IAgentRunner runner,
        IAgentBroadcaster broadcaster,
        Guid taskId,
        CancellationToken ct)
    {
        var task = await db.AgentTasks
            .Include(t => t.WorkItem)
            .ThenInclude(w => w.Source)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return (null, null);

        if (task.Status is not (AgentTaskStatus.Approved or AgentTaskStatus.AwaitingReview))
            return (null, $"Task is in status {task.Status}; must be Approved");

        var session = await runner.StartAsync(task.WorkItem, task, task.WorkItem.Source.LocalRepoPath, ct);

        task.Status = AgentTaskStatus.Running;
        task.BranchName ??= System.IO.Path.GetFileName(session.WorktreePath);
        task.WorktreePath = session.WorktreePath;
        task.UpdatedAt = DateTime.UtcNow;

        db.AgentRuns.Add(new AgentRun
        {
            Id = session.RunId,
            AgentTaskId = task.Id,
            WorkItemId = task.WorkItemId,
            Kind = AgentRunKind.TaskAgent,
            Status = AgentRunStatus.Running,
            ProcessId = session.ProcessId,
            TranscriptLogPath = session.TranscriptLogPath,
        });

        if (task.WorkItem.Status == WorkItemStatus.Triaged)
        {
            task.WorkItem.Status = WorkItemStatus.InProgress;
            task.WorkItem.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await broadcaster.WorkItemUpdatedAsync(task.WorkItemId);
        return (session, null);
    }
}
