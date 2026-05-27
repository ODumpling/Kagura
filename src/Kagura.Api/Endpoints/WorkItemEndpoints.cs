using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Endpoints;

public record WorkItemListDto(
    Guid Id,
    Guid SourceId,
    string SourceName,
    string ExternalId,
    string Title,
    WorkItemStatus Status,
    string? Url,
    string? Labels,
    string? BranchName,
    string? PullRequestUrl,
    DateTime UpdatedAt,
    DateTime? TriagedAt,
    int TaskCount);

public record WorkItemDetailDto(
    Guid Id,
    Guid SourceId,
    string SourceName,
    string ExternalId,
    string Title,
    string Body,
    WorkItemStatus Status,
    string? Url,
    string? Labels,
    string? BranchName,
    string? PullRequestUrl,
    DateTime UpdatedAt,
    DateTime? TriagedAt,
    IReadOnlyList<AgentTaskDto> Tasks);

public record AgentTaskDto(
    Guid Id,
    string Title,
    string Description,
    int Order,
    AgentTaskStatus Status,
    string? BranchName,
    string? WorktreePath);

public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/workitems");

        grp.MapGet("", async (KaguraDbContext db, Guid? sourceId, WorkItemStatus? status) =>
        {
            IQueryable<WorkItem> q = db.WorkItems.Include(w => w.Source).Include(w => w.Tasks);
            if (sourceId.HasValue) q = q.Where(w => w.SourceId == sourceId.Value);
            if (status.HasValue) q = q.Where(w => w.Status == status.Value);

            var rows = await q.OrderByDescending(w => w.UpdatedAt).ToListAsync();
            return Results.Ok(rows.Select(w => new WorkItemListDto(
                w.Id, w.SourceId, w.Source.Name, w.ExternalId, w.Title,
                w.Status, w.Url, w.Labels, w.BranchName, w.PullRequestUrl,
                w.UpdatedAt, w.TriagedAt, w.Tasks.Count)));
        });

        grp.MapGet("/{id:guid}", async (Guid id, KaguraDbContext db) =>
        {
            var w = await db.WorkItems
                .Include(x => x.Source)
                .Include(x => x.Tasks.OrderBy(t => t.Order))
                .FirstOrDefaultAsync(x => x.Id == id);
            if (w is null) return Results.NotFound();

            return Results.Ok(new WorkItemDetailDto(
                w.Id, w.SourceId, w.Source.Name, w.ExternalId, w.Title, w.Body,
                w.Status, w.Url, w.Labels, w.BranchName, w.PullRequestUrl,
                w.UpdatedAt, w.TriagedAt,
                w.Tasks.Select(t => new AgentTaskDto(t.Id, t.Title, t.Description, t.Order, t.Status, t.BranchName, t.WorktreePath)).ToList()));
        });

        return app;
    }
}
