using Kagura.Core.Domain;
using Kagura.Core.Triage;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record TriageResultDto(Guid WorkItemId, int TaskCount, IReadOnlyList<AgentTaskDto> Tasks);

public record UpdateTaskDto(string Title, string Description, int Order);

public static class TriageEndpoints
{
    public static IEndpointRouteBuilder MapTriageEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/workitems/{workItemId:guid}");

        grp.MapPost("/triage", async (Guid workItemId, KaguraDbContext db, ITriageService triage, CancellationToken ct) =>
        {
            var wi = await db.WorkItems.Include(w => w.Tasks).FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            var existingProposed = wi.Tasks.Where(t => t.Status == AgentTaskStatus.Proposed).ToList();
            db.AgentTasks.RemoveRange(existingProposed);

            var proposals = await triage.ProposeTasksAsync(wi.Title, wi.Body, wi.Labels, ct);
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

            await db.SaveChangesAsync(ct);

            var dtoList = await db.AgentTasks
                .Where(t => t.WorkItemId == wi.Id)
                .OrderBy(t => t.Order)
                .Select(t => new AgentTaskDto(t.Id, t.Title, t.Description, t.Order, t.Status, t.BranchName, t.WorktreePath))
                .ToListAsync(ct);
            return Results.Ok(new TriageResultDto(wi.Id, dtoList.Count, dtoList));
        });

        grp.MapPost("/triage/approve", async (Guid workItemId, KaguraDbContext db, CancellationToken ct) =>
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
            return Results.Ok(new { wi.Id, wi.Status, wi.TriagedAt });
        });

        grp.MapPut("/tasks/{taskId:guid}", async (Guid workItemId, Guid taskId, UpdateTaskDto dto, KaguraDbContext db) =>
        {
            var t = await db.AgentTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.WorkItemId == workItemId);
            if (t is null) return Results.NotFound();
            t.Title = dto.Title;
            t.Description = dto.Description;
            t.Order = dto.Order;
            t.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new AgentTaskDto(t.Id, t.Title, t.Description, t.Order, t.Status, t.BranchName, t.WorktreePath));
        });

        grp.MapDelete("/tasks/{taskId:guid}", async (Guid workItemId, Guid taskId, KaguraDbContext db) =>
        {
            var t = await db.AgentTasks.FirstOrDefaultAsync(x => x.Id == taskId && x.WorkItemId == workItemId);
            if (t is null) return Results.NotFound();
            db.AgentTasks.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        return app;
    }
}
