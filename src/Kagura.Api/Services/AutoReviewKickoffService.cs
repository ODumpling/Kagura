using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Interactive;
using Kagura.Core.Review;
using Kagura.Data;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Api.Services;

public sealed class AutoReviewKickoffResult
{
    public Guid? RunId { get; }
    public string? Error { get; }
    public bool WorkItemNotFound { get; }

    private AutoReviewKickoffResult(Guid? runId, string? error, bool notFound)
    {
        RunId = runId;
        Error = error;
        WorkItemNotFound = notFound;
    }

    public static AutoReviewKickoffResult Accepted(Guid runId) => new(runId, null, false);
    public static AutoReviewKickoffResult NotFound() => new(null, null, true);
    public static AutoReviewKickoffResult Invalid(string error) => new(null, error, false);
}

public interface IAutoReviewKickoffService
{
    Task<AutoReviewKickoffResult> KickoffAsync(Guid workItemId, CancellationToken ct = default);
}

public sealed class AutoReviewKickoffService : IAutoReviewKickoffService
{
    private readonly KaguraDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoReviewKickoffService> _log;

    public AutoReviewKickoffService(
        KaguraDbContext db,
        IServiceScopeFactory scopeFactory,
        ILogger<AutoReviewKickoffService> log)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task<AutoReviewKickoffResult> KickoffAsync(Guid workItemId, CancellationToken ct = default)
    {
        var wi = await _db.WorkItems
            .Include(w => w.Tasks)
            .FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (wi is null) return AutoReviewKickoffResult.NotFound();

        var hasQueue = wi.Tasks.Any(t => t.Status == AgentTaskStatus.AwaitingReview);
        if (!hasQueue) return AutoReviewKickoffResult.Invalid("No tasks in AwaitingReview.");

        var run = new AgentRun
        {
            Kind = AgentRunKind.AutoReview,
            WorkItemId = wi.Id,
            Status = AgentRunStatus.Running,
        };
        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        var scopeFactory = _scopeFactory;
        var log = _log;
        _ = Task.Run(() => RunAutoReviewAsync(scopeFactory, log, workItemId, run.Id));

        return AutoReviewKickoffResult.Accepted(run.Id);
    }

    // Fire-and-forget auto-review. Asks the review service for each AwaitingReview task and
    // applies its verdict — auto-merging or flagging with ReviewNotes — before emitting
    // workItemUpdated on completion.
    private static async Task RunAutoReviewAsync(IServiceScopeFactory scopeFactory, ILogger log, Guid workItemId, Guid runId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var git = scope.ServiceProvider.GetRequiredService<GitService>();
        var reviewer = scope.ServiceProvider.GetRequiredService<IReviewService>();
        var broadcaster = scope.ServiceProvider.GetRequiredService<IAgentBroadcaster>();
        var prompts = scope.ServiceProvider.GetRequiredService<IInteractivePromptService>();

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
                    var verdict = await reviewer.ReviewAsync(runId, task.Title, task.Description, diff);
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
                    // The LLM said don't auto-merge. Give the user a chance to override — the
                    // pipeline blocks on AskAsync until POST /api/agents/{runId}/prompts/{id}/respond
                    // arrives, then resumes. If the user picks "merge", we fall through to the
                    // merge path with the reasoning preserved as context.
                    string choice;
                    try
                    {
                        choice = await prompts.AskAsync(
                            runId,
                            $"Reviewer flagged '{task.Title}': {reasoning}. Override and merge anyway?",
                            new[] { "merge", "skip" });
                    }
                    catch (OperationCanceledException)
                    {
                        choice = "skip";
                    }

                    if (!string.Equals(choice, "merge", StringComparison.OrdinalIgnoreCase))
                    {
                        task.ReviewNotes = reasoning;
                        task.UpdatedAt = now;
                        continue;
                    }

                    reasoning = $"User overrode reviewer ({reasoning}).";
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
