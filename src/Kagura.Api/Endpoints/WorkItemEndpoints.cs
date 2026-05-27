using Kagura.Core.Domain;
using Kagura.Core.Git;
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

public record FinishWorkItemResultDto(
    Guid Id,
    WorkItemStatus Status,
    string? BranchName,
    string? PullRequestUrl,
    int Merged,
    int AlreadyMerged,
    string? PullRequestError);

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

        grp.MapPost("/{workItemId:guid}/tasks/{taskId:guid}/merge", async (
            Guid workItemId,
            Guid taskId,
            KaguraDbContext db,
            GitService git,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Source)
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
            if (wi is null) return Results.NotFound();

            var task = wi.Tasks.FirstOrDefault(t => t.Id == taskId);
            if (task is null) return Results.NotFound();

            if (task.Status != AgentTaskStatus.AwaitingReview)
                return Results.BadRequest(new { error = $"Task is {task.Status}; only AwaitingReview tasks can be merged." });

            await git.MergeTaskBranchAsync(wi.Source.LocalRepoPath, wi, task, ct);
            if (!string.IsNullOrEmpty(task.WorktreePath))
                await git.RemoveWorktreeAsync(wi.Source.LocalRepoPath, task.WorktreePath, ct);

            var now = DateTime.UtcNow;
            task.Status = AgentTaskStatus.Merged;
            task.UpdatedAt = now;
            wi.BranchName ??= git.WorkItemBranchName(wi);
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new AgentTaskDto(task.Id, task.Title, task.Description, task.Order, task.Status, task.BranchName, task.WorktreePath));
        });

        grp.MapPost("/{id:guid}/finish", async (
            Guid id,
            KaguraDbContext db,
            GitService git,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Source)
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();

            if (wi.Tasks.Any(t => t.Status == AgentTaskStatus.Running))
                return Results.BadRequest(new { error = "Stop all running agents before finishing this work item." });

            var toMerge = wi.Tasks.Where(t => t.Status == AgentTaskStatus.AwaitingReview).ToList();
            var alreadyMerged = wi.Tasks.Count(t => t.Status == AgentTaskStatus.Merged);
            var repoPath = wi.Source.LocalRepoPath;
            var now = DateTime.UtcNow;
            var merged = 0;

            foreach (var task in toMerge)
            {
                try
                {
                    await git.MergeTaskBranchAsync(repoPath, wi, task, ct);
                    if (!string.IsNullOrEmpty(task.WorktreePath))
                        await git.RemoveWorktreeAsync(repoPath, task.WorktreePath, ct);
                    task.Status = AgentTaskStatus.Merged;
                    task.UpdatedAt = now;
                    merged++;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Failed to merge task {TaskId} for work item {WorkItemId}", task.Id, wi.Id);
                    await db.SaveChangesAsync(ct);
                    return Results.BadRequest(new { error = $"Merge failed for task '{task.Title}': {ex.Message}" });
                }
            }

            var totalMerged = alreadyMerged + merged;
            if (totalMerged == 0)
                return Results.BadRequest(new { error = "Nothing to ship. Move at least one task to Review or Merged first." });

            wi.BranchName ??= git.WorkItemBranchName(wi);
            if (wi.Status != WorkItemStatus.PullRequested)
                wi.Status = WorkItemStatus.Merged;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            string? prError = null;
            try
            {
                var prUrl = await git.OpenPullRequestAsync(repoPath, wi, ct);
                if (prUrl is not null)
                {
                    wi.PullRequestUrl = prUrl;
                    wi.Status = WorkItemStatus.PullRequested;
                    wi.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    prError = "Could not open PR (push or `gh pr create` failed — check API logs).";
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "OpenPullRequestAsync threw for work item {WorkItemId}", wi.Id);
                prError = ex.Message;
            }

            return Results.Ok(new FinishWorkItemResultDto(
                wi.Id, wi.Status, wi.BranchName, wi.PullRequestUrl,
                merged, alreadyMerged, prError));
        });

        return app;
    }
}
