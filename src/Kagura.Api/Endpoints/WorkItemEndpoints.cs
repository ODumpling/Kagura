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
    int TaskCount,
    bool RalphLoopActive,
    string? RalphLoopHaltReason);

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
    bool RalphLoopActive,
    string? RalphLoopHaltReason,
    string? LastTriageError,
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
    string? ReviewNotes = null,
    int RetryAttempts = 0,
    string? LastFailureReason = null);

public record UpdateIncludeInPullRequestDto(bool IncludeInPullRequest);

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
                w.UpdatedAt, w.TriagedAt, w.Tasks.Count, w.RalphLoopActive, w.RalphLoopHaltReason)));
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
                w.UpdatedAt, w.TriagedAt, w.RalphLoopActive, w.RalphLoopHaltReason,
                w.LastTriageError,
                w.Tasks.Select(t => new AgentTaskDto(t.Id, t.Title, t.Description, t.Order, t.Status, t.BranchName, t.WorktreePath, t.IncludeInPullRequest, t.ReviewNotes, t.RetryAttempts, t.LastFailureReason)).ToList()));
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

        // Fire-and-forget auto-review. Validates the queue, persists a Running AgentRun,
        // returns 202 with the runId, then a background task asks the review service for
        // each AwaitingReview task and applies its verdict — auto-merging or flagging
        // with ReviewNotes — before emitting workItemUpdated on completion.
        grp.MapPost("/{id:guid}/auto-review", async (
            Guid id,
            KaguraDbContext db,
            IServiceScopeFactory scopeFactory,
            ILogger<Program> log,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();

            var hasQueue = wi.Tasks.Any(t => t.Status == AgentTaskStatus.AwaitingReview);
            if (!hasQueue)
                return Results.BadRequest(new { error = "No tasks in AwaitingReview." });

            var run = new AgentRun
            {
                Kind = AgentRunKind.AutoReview,
                WorkItemId = wi.Id,
                Status = AgentRunStatus.Running,
            };
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync(ct);

            _ = Task.Run(() => RunAutoReviewAsync(scopeFactory, log, id, run.Id));

            return Results.Accepted(value: new { runId = run.Id });
        });

        // Start (or re-start after halt) the Ralph Loop on this work item.
        // Resets any Failed tasks to Approved with fresh retry budget, then flips the flag.
        grp.MapPost("/{id:guid}/ralph-loop", async (
            Guid id,
            KaguraDbContext db,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems
                .Include(w => w.Tasks)
                .FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();

            if (wi.Status is WorkItemStatus.Closed or WorkItemStatus.PullRequested)
                return Results.BadRequest(new { error = $"Work item is {wi.Status}; nothing for Ralph to do." });

            if (wi.Tasks.Count == 0)
                return Results.BadRequest(new { error = "No tasks on this work item." });

            if (wi.Tasks.All(t => t.Status == AgentTaskStatus.Merged))
                return Results.BadRequest(new { error = "All tasks already merged." });

            var now = DateTime.UtcNow;
            foreach (var t in wi.Tasks.Where(t => t.Status == AgentTaskStatus.Failed))
            {
                t.Status = AgentTaskStatus.Approved;
                t.RetryAttempts = 0;
                t.LastFailureReason = null;
                t.UpdatedAt = now;
            }

            wi.RalphLoopActive = true;
            wi.RalphLoopHaltReason = null;
            wi.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.Accepted();
        });

        // Cancel the loop. Does not kill in-flight agents — they finish their current attempt;
        // the loop just stops advancing further stages.
        grp.MapPost("/{id:guid}/ralph-loop/cancel", async (
            Guid id,
            KaguraDbContext db,
            IAgentBroadcaster broadcaster,
            CancellationToken ct) =>
        {
            var wi = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (wi is null) return Results.NotFound();

            if (!wi.RalphLoopActive) return Results.NoContent();

            wi.RalphLoopActive = false;
            wi.RalphLoopHaltReason = "Cancelled by user.";
            wi.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await broadcaster.WorkItemUpdatedAsync(wi.Id);

            return Results.NoContent();
        });

        return app;
    }

    private static async Task RunAutoReviewAsync(IServiceScopeFactory scopeFactory, ILogger log, Guid workItemId, Guid runId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var git = scope.ServiceProvider.GetRequiredService<GitService>();
        var reviewer = scope.ServiceProvider.GetRequiredService<IReviewService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<IAgentBroadcaster>();

        var wi = await db.WorkItems
            .Include(w => w.Source)
            .Include(w => w.Tasks)
            .FirstOrDefaultAsync(w => w.Id == workItemId);
        var run = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (wi is null || run is null) return;

        try
        {
            var queue = wi.Tasks
                .Where(t => t.Status == AgentTaskStatus.AwaitingReview)
                .OrderBy(t => t.Order)
                .ToList();

            var repoPath = wi.Source.LocalRepoPath;
            var now = DateTime.UtcNow;

            foreach (var task in queue)
            {
                string reasoning;
                bool autoMerge;
                try
                {
                    var diff = await git.DiffTaskAgainstWorkItemAsync(repoPath, wi, task);
                    var verdict = await reviewer.ReviewAsync(task.Title, task.Description, diff);
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
                    continue;
                }

                try
                {
                    var mergeResult = await git.MergeTaskBranchAsync(repoPath, wi, task);
                    if (!string.IsNullOrEmpty(task.WorktreePath))
                        await git.RemoveWorktreeAsync(repoPath, task.WorktreePath);
                    task.Status = AgentTaskStatus.Merged;
                    task.ReviewNotes = mergeResult.Outcome == MergeOutcome.MergedByAgent
                        ? $"{reasoning}\n\nConflicts resolved by AI: {mergeResult.Notes}"
                        : reasoning;
                    task.UpdatedAt = now;
                    wi.BranchName ??= git.WorkItemBranchName(wi);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Auto-merge failed for task {TaskId} after positive review", task.Id);
                    task.ReviewNotes = $"Reviewer approved auto-merge but git merge failed: {ex.Message}";
                    task.UpdatedAt = now;
                }
            }

            wi.UpdatedAt = now;
            run.Status = AgentRunStatus.Exited;
            run.EndedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Auto-review failed for work item {WorkItemId}", workItemId);
            run.Status = AgentRunStatus.Crashed;
            run.EndedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
        await broadcaster.WorkItemUpdatedAsync(workItemId);
    }
}
