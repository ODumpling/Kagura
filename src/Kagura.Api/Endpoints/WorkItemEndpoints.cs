using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Review;
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
    string? WorktreePath,
    bool IncludeInPullRequest,
    string? ReviewNotes = null);

public record UpdateIncludeInPullRequestDto(bool IncludeInPullRequest);

public record AutoReviewItemResultDto(Guid TaskId, string Title, bool AutoMerged, bool Merged, string Reasoning);
public record AutoReviewResultDto(int Reviewed, int AutoMerged, int FlaggedForHuman, IReadOnlyList<AutoReviewItemResultDto> Items);

public record FinishWorkItemResultDto(
    Guid Id,
    WorkItemStatus Status,
    string? BranchName,
    string? PullRequestUrl,
    int Merged,
    int AlreadyMerged,
    string? PullRequestError);

public record PrPreviewResponseDto(
    Guid WorkItemId,
    IReadOnlyList<Guid> IncludedTaskIds,
    string UnifiedDiff,
    IReadOnlyList<ConflictedFileDto> ConflictedFiles,
    string? BaseSha,
    string? HeadSha,
    IReadOnlyDictionary<Guid, string> TaskSnapshots,
    PrPreviewStatsDto Stats);

public record ConflictedFileDto(string Path, IReadOnlyList<Guid> TaskIds);

public record PrPreviewStatsDto(int FilesChanged, int Additions, int Deletions);

public static class WorkItemEndpoints
{
    public static IEndpointRouteBuilder MapWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/workitems");

        grp.MapGet("", async (KaguraDbContext db, Guid? sourceId, WorkItemStatus? status, bool? includeClosed) =>
        {
            IQueryable<WorkItem> q = db.WorkItems.Include(w => w.Source).Include(w => w.Tasks);
            if (sourceId.HasValue) q = q.Where(w => w.SourceId == sourceId.Value);
            if (status.HasValue)
                q = q.Where(w => w.Status == status.Value);
            else if (includeClosed != true)
                q = q.Where(w => w.Status != WorkItemStatus.Closed);

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
                w.Tasks.Select(t => new AgentTaskDto(t.Id, t.Title, t.Description, t.Order, t.Status, t.BranchName, t.WorktreePath, t.IncludeInPullRequest, t.ReviewNotes)).ToList()));
        });

        grp.MapGet("/{id:guid}/pr-preview", async (
            Guid id,
            KaguraDbContext db,
            IPrPreviewService previewService,
            [Microsoft.AspNetCore.Mvc.FromQuery] Guid[]? taskIds,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Source)
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();

            var requested = (taskIds ?? Array.Empty<Guid>()).ToHashSet();
            var included = wi.Tasks
                .Where(t => requested.Contains(t.Id) && t.Status == AgentTaskStatus.AwaitingReview)
                .OrderBy(t => t.Order)
                .ToList();

            PrPreviewResult result;
            if (included.Count == 0)
            {
                result = PrPreviewResult.Empty();
            }
            else
            {
                result = await previewService.ComputePreviewAsync(wi, included, ct);
            }

            return Results.Ok(new PrPreviewResponseDto(
                WorkItemId: wi.Id,
                IncludedTaskIds: included.Select(t => t.Id).ToList(),
                UnifiedDiff: result.UnifiedDiff,
                ConflictedFiles: result.ConflictedFiles
                    .Select(c => new ConflictedFileDto(c.Path, c.TaskIds))
                    .ToList(),
                BaseSha: result.BaseSha,
                HeadSha: result.HeadSha,
                TaskSnapshots: result.TaskSnapshots,
                Stats: new PrPreviewStatsDto(
                    result.Stats.FilesChanged,
                    result.Stats.Additions,
                    result.Stats.Deletions)));
        });

        grp.MapPost("/{workItemId:guid}/tasks/{taskId:guid}/merge", async (
            Guid workItemId,
            Guid taskId,
            KaguraDbContext db,
            GitService git,
            IAgentBroadcaster broadcaster,
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

            var result = await git.MergeTaskBranchAsync(wi.Source.LocalRepoPath, wi, task, ct);
            if (!string.IsNullOrEmpty(task.WorktreePath))
                await git.RemoveWorktreeAsync(wi.Source.LocalRepoPath, task.WorktreePath, ct);

            var now = DateTime.UtcNow;
            task.Status = AgentTaskStatus.Merged;
            if (result.Outcome == MergeOutcome.MergedByAgent)
                task.ReviewNotes = $"Conflicts resolved by AI: {result.Notes}";
            task.UpdatedAt = now;
            wi.BranchName ??= git.WorkItemBranchName(wi);
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.Ok(new AgentTaskDto(task.Id, task.Title, task.Description, task.Order, task.Status, task.BranchName, task.WorktreePath, task.IncludeInPullRequest, task.ReviewNotes));
        });

        grp.MapMethods("/{workItemId:guid}/tasks/{taskId:guid}/include", new[] { "PATCH" }, async (
            Guid workItemId,
            Guid taskId,
            UpdateIncludeInPullRequestDto body,
            KaguraDbContext db,
            CancellationToken ct) =>
        {
            var task = await db.AgentTasks
                .FirstOrDefaultAsync(t => t.Id == taskId && t.WorkItemId == workItemId, ct);
            if (task is null) return Results.NotFound();

            task.IncludeInPullRequest = body.IncludeInPullRequest;
            task.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            return Results.Ok(new AgentTaskDto(task.Id, task.Title, task.Description, task.Order, task.Status, task.BranchName, task.WorktreePath, task.IncludeInPullRequest, task.ReviewNotes));
        });

        grp.MapPost("/{id:guid}/finish", async (
            Guid id,
            KaguraDbContext db,
            GitService git,
            IAgentBroadcaster broadcaster,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Source)
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();
            if (wi.Status == WorkItemStatus.Closed)
                return Results.BadRequest(new { error = "Work item is closed." });

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
                    var mergeResult = await git.MergeTaskBranchAsync(repoPath, wi, task, ct);
                    if (!string.IsNullOrEmpty(task.WorktreePath))
                        await git.RemoveWorktreeAsync(repoPath, task.WorktreePath, ct);
                    task.Status = AgentTaskStatus.Merged;
                    if (mergeResult.Outcome == MergeOutcome.MergedByAgent)
                        task.ReviewNotes = $"Conflicts resolved by AI: {mergeResult.Notes}";
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

            await git.RemoveWorkItemMergeWorktreeAsync(repoPath, wi, ct);

            await broadcaster.WorkItemUpdatedAsync(wi.Id);
            return Results.Ok(new FinishWorkItemResultDto(
                wi.Id, wi.Status, wi.BranchName, wi.PullRequestUrl,
                merged, alreadyMerged, prError));
        });

        // For every AwaitingReview task on this work item, ask the review service whether
        // the diff is safe to auto-merge. Tasks marked auto-mergeable are merged; the rest
        // stay in AwaitingReview with ReviewNotes explaining why a human should look.
        grp.MapPost("/{id:guid}/auto-review", async (
            Guid id,
            KaguraDbContext db,
            GitService git,
            IReviewService reviewer,
            IAgentBroadcaster broadcaster,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Source)
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();

            var queue = wi.Tasks
                .Where(t => t.Status == AgentTaskStatus.AwaitingReview)
                .OrderBy(t => t.Order)
                .ToList();
            if (queue.Count == 0)
                return Results.BadRequest(new { error = "No tasks in AwaitingReview." });

            var repoPath = wi.Source.LocalRepoPath;
            var items = new List<AutoReviewItemResultDto>();
            var autoMergedCount = 0;
            var flaggedCount = 0;
            var now = DateTime.UtcNow;

            foreach (var task in queue)
            {
                string reasoning;
                bool autoMerge;
                try
                {
                    var diff = await git.DiffTaskAgainstWorkItemAsync(repoPath, wi, task, ct);
                    var verdict = await reviewer.ReviewAsync(task.Title, task.Description, diff, ct);
                    autoMerge = verdict.AutoMerge;
                    reasoning = verdict.Reasoning;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Auto-review threw for task {TaskId}", task.Id);
                    autoMerge = false;
                    reasoning = $"Review failed: {ex.Message}";
                }

                if (!autoMerge)
                {
                    task.ReviewNotes = reasoning;
                    task.UpdatedAt = now;
                    flaggedCount++;
                    items.Add(new AutoReviewItemResultDto(task.Id, task.Title, false, false, reasoning));
                    continue;
                }

                try
                {
                    var mergeResult = await git.MergeTaskBranchAsync(repoPath, wi, task, ct);
                    if (!string.IsNullOrEmpty(task.WorktreePath))
                        await git.RemoveWorktreeAsync(repoPath, task.WorktreePath, ct);
                    task.Status = AgentTaskStatus.Merged;
                    task.ReviewNotes = mergeResult.Outcome == MergeOutcome.MergedByAgent
                        ? $"{reasoning}\n\nConflicts resolved by AI: {mergeResult.Notes}"
                        : reasoning;
                    task.UpdatedAt = now;
                    wi.BranchName ??= git.WorkItemBranchName(wi);
                    autoMergedCount++;
                    items.Add(new AutoReviewItemResultDto(task.Id, task.Title, true, true, reasoning));
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Auto-merge failed for task {TaskId} after positive review", task.Id);
                    task.ReviewNotes = $"Reviewer approved auto-merge but git merge failed: {ex.Message}";
                    task.UpdatedAt = now;
                    flaggedCount++;
                    items.Add(new AutoReviewItemResultDto(task.Id, task.Title, true, false, task.ReviewNotes));
                }
            }

            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.Ok(new AutoReviewResultDto(queue.Count, autoMergedCount, flaggedCount, items));
        });

        return app;
    }
}
