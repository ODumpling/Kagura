using Kagura.Api.Services;
using Kagura.Core.Agents;
using Kagura.Core.Domain;
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
        // the kickoff service persists the AgentRun and spawns the background task that the
        // Ralph driver also consumes.
        grp.MapPost("/triage", async (
            Guid workItemId,
            ITriageKickoffService kickoff,
            CancellationToken ct) =>
        {
            var result = await kickoff.KickoffAsync(workItemId, ct);
            if (result.WorkItemNotFound) return Results.NotFound();
            if (result.Error is not null) return Results.BadRequest(new { error = result.Error });
            return Results.Accepted(value: new TriageAcceptedDto(result.RunId!.Value));
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
}
