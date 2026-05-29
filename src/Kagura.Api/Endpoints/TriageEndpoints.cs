using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record TriageAcceptedDto(Guid RunId);

public record UpdateTaskDto(string Title, string Description, int Order);

public record UpdateTaskStatusDto(AgentTaskStatus Status);

public static class TriageEndpoints
{
    public static IEndpointRouteBuilder MapTriageEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/workitems/{workItemId:guid}");

        // Fire-and-forget triage. Returns 202 with the new AgentRun's runId immediately;
        // the background task runs the triage service, persists proposals (or LastTriageError
        // on parse failure), updates the AgentRun row, and emits workItemUpdated on completion.
        grp.MapPost("/triage", async (
            Guid workItemId,
            KaguraDbContext db,
            IServiceScopeFactory scopeFactory,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();
            if (wi.Status == WorkItemStatus.Closed)
                return Results.BadRequest(new { error = "Work item is closed." });

            var run = new AgentRun
            {
                Kind = AgentRunKind.Triage,
                WorkItemId = wi.Id,
                Status = AgentRunStatus.Running,
            };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(ct);

            _ = Task.Run(() => RunTriageAsync(scopeFactory, log, workItemId, run.Id));

            return Results.Accepted(value: new TriageAcceptedDto(run.Id));
        });

        grp.MapPost("/triage/approve", async (Guid workItemId, KaguraDbContext db, IAgentBroadcaster broadcaster, CancellationToken ct) =>
        {
            var wi = await db.WorkItems.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            foreach (var t in wi.Tasks.Where(t => t.Status == AgentTaskStatus.Proposed))
            {
                t.Status = AgentTaskStatus.Approved;
                t.UpdatedAt = DateTime.UtcNow;
            }
            wi.Status = WorkItemStatus.Triaged;
            wi.TriagedAt = DateTime.UtcNow;
            wi.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);
            return Results.Ok(new { wi.Id, wi.Status, wi.TriagedAt });
        });

        grp.MapPost("/tasks/{taskId:guid}/approve", async (Guid workItemId, Guid taskId, KaguraDbContext db, IAgentBroadcaster broadcaster, CancellationToken ct) =>
        {
            var wi = await db.WorkItems.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            var task = wi.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null) return Results.NotFound();
            if (task.Status != AgentTaskStatus.Proposed)
                return Results.BadRequest(new { error = $"Task is {task.Status}, only Proposed tasks can be approved." });

            var now = DateTime.UtcNow;
            task.Status = AgentTaskStatus.Approved;
            task.UpdatedAt = now;

            if (wi.Status == WorkItemStatus.New)
            {
                wi.Status = WorkItemStatus.Triaged;
                wi.TriagedAt = now;
            }
            wi.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);
            return Results.Ok(new AgentTaskDto(task.Id, task.Title, task.Description, task.Order, task.Status, task.BranchName, task.WorktreePath, task.IncludeInPullRequest, task.ReviewNotes));
        });

        grp.MapPut("/tasks/{taskId:guid}", async (Guid workItemId, Guid taskId, UpdateTaskDto dto, KaguraDbContext db, IAgentBroadcaster broadcaster) =>
        {
            var t = await db.AgentTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.WorkItemId == workItemId);
            if (t is null) return Results.NotFound();
            t.Title = dto.Title;
            t.Description = dto.Description;
            t.Order = dto.Order;
            t.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await broadcaster.WorkItemUpdatedAsync(workItemId);
            return Results.Ok(new AgentTaskDto(t.Id, t.Title, t.Description, t.Order, t.Status, t.BranchName, t.WorktreePath, t.IncludeInPullRequest, t.ReviewNotes));
        });

        grp.MapPatch("/tasks/{taskId:guid}/status", async (Guid workItemId, Guid taskId, UpdateTaskStatusDto dto, KaguraDbContext db, IAgentBroadcaster broadcaster, CancellationToken ct) =>
        {
            var wi = await db.WorkItems.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            var task = wi.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null) return Results.NotFound();

            if (task.Status == AgentTaskStatus.Running && dto.Status != AgentTaskStatus.Running)
                return Results.BadRequest(new { error = "Stop the agent before changing status of a Running task." });
            if (dto.Status == AgentTaskStatus.Running)
                return Results.BadRequest(new { error = "Running status is set by starting an agent, not via drag-and-drop." });

            var now = DateTime.UtcNow;
            task.Status = dto.Status;
            task.UpdatedAt = now;

            if (dto.Status != AgentTaskStatus.Proposed && wi.Status == WorkItemStatus.New)
            {
                wi.Status = WorkItemStatus.Triaged;
                wi.TriagedAt = now;
            }
            wi.UpdatedAt = now;

            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);
            return Results.Ok(new AgentTaskDto(task.Id, task.Title, task.Description, task.Order, task.Status, task.BranchName, task.WorktreePath, task.IncludeInPullRequest, task.ReviewNotes));
        });

        grp.MapDelete("/tasks/{taskId:guid}", async (Guid workItemId, Guid taskId, KaguraDbContext db, IAgentBroadcaster broadcaster) =>
        {
            var t = await db.AgentTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.WorkItemId == workItemId);
            if (t is null) return Results.NotFound();
            db.AgentTasks.Remove(t);
            await db.SaveChangesAsync();
            await broadcaster.WorkItemUpdatedAsync(workItemId);
            return Results.NoContent();
        });

        return app;
    }

    private static async Task RunTriageAsync(IServiceScopeFactory scopeFactory, ILogger log, Guid workItemId, Guid runId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var triage = scope.ServiceProvider.GetRequiredService<ITriageService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<IAgentBroadcaster>();

        var wi = await db.WorkItems.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == workItemId);
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (wi is null || run is null) return;

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
