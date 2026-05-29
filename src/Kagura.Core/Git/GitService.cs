using System.Text.RegularExpressions;
using Kagura.Core.Domain;
using Kagura.Core.Merge;
using Microsoft.Extensions.Logging;

namespace Kagura.Core.Git;

public enum MergeOutcome { AlreadyMerged, Merged, MergedByAgent }

public record MergeResult(MergeOutcome Outcome, string? Notes = null);

/// <summary>
/// Hook invoked by <see cref="GitService.MergeTaskBranchAsync"/> immediately before the
/// <see cref="IMergeConflictResolver"/> is called on a conflicted merge. The
/// <see cref="Kagura.Api"/> implementation creates an AgentRun row, populates the
/// ambient <see cref="MergeResolverAgentContext"/>, and returns an <see cref="IDisposable"/>
/// that clears the ambient on dispose so a later merge invocation on the same async
/// flow doesn't accidentally reuse stale state. Tests can pass <c>null</c> (or a no-op
/// implementation) and the resolver will fall back to its legacy path.
/// </summary>
public interface IMergeResolverKickoff
{
    Task<IAsyncDisposable> BeginAsync(WorkItem wi, AgentTask task, CancellationToken ct);
}

public partial class GitService
{
    private readonly string _worktreesRoot;
    private readonly string _scratchRoot;
    private readonly IMergeConflictResolver _resolver;
    private readonly IMergeResolverKickoff? _mergeKickoff;
    private readonly ILogger<GitService> _log;

    public GitService(string worktreesRoot, IMergeConflictResolver resolver, ILogger<GitService> log)
        : this(worktreesRoot, scratchRoot: "~/.devflow/scratch", resolver, log, mergeKickoff: null)
    {
    }

    public GitService(string worktreesRoot, string scratchRoot, IMergeConflictResolver resolver, ILogger<GitService> log)
        : this(worktreesRoot, scratchRoot, resolver, log, mergeKickoff: null)
    {
    }

    public GitService(
        string worktreesRoot,
        string scratchRoot,
        IMergeConflictResolver resolver,
        ILogger<GitService> log,
        IMergeResolverKickoff? mergeKickoff)
    {
        _worktreesRoot = ResolveHome(worktreesRoot);
        _scratchRoot = ResolveHome(scratchRoot);
        _resolver = resolver;
        _log = log;
        _mergeKickoff = mergeKickoff;
        Directory.CreateDirectory(_worktreesRoot);
        Directory.CreateDirectory(_scratchRoot);
    }

    [GeneratedRegex("[^a-zA-Z0-9]+")]
    private static partial Regex SlugRegex();

    public static string Slug(string text, int maxLen = 40)
    {
        var s = SlugRegex().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return s.Length <= maxLen ? s : s[..maxLen].TrimEnd('-');
    }

    public string WorkItemBranchName(WorkItem wi) =>
        $"devflow/{Slug(wi.ExternalId)}-{Slug(wi.Title, 30)}";

    // Use `--` (not `/`) so task branches are siblings of the work-item branch.
    // Git forbids both `foo` and `foo/bar` existing as refs.
    public string TaskBranchName(WorkItem wi, AgentTask task) =>
        $"{WorkItemBranchName(wi)}--{task.Order:D2}-{Slug(task.Title, 30)}";

    public string TaskWorktreePath(WorkItem wi, AgentTask task) =>
        Path.Combine(_worktreesRoot, Slug(wi.ExternalId), $"{task.Order:D2}-{Slug(task.Title, 30)}");

    // Underscore prefix keeps this from colliding with any task slug (Slug() strips `_`).
    public string WorkItemMergeWorktreePath(WorkItem wi) =>
        Path.Combine(_worktreesRoot, Slug(wi.ExternalId), "_merge");

    /// <summary>
    /// Path to the long-lived scratch worktree for a Source. Per CONTEXT.md → "Agent working
    /// directory": one per-Source worktree at <c>~/.devflow/scratch/&lt;source&gt;/</c> on a
    /// detached HEAD at the default branch. Used by Triage and Grill Agents so they don't
    /// touch the user's working copy.
    /// </summary>
    public string ScratchWorktreePath(Source source) =>
        Path.Combine(_scratchRoot, Slug(source.Name));

    /// <summary>
    /// Ensure the per-Source scratch worktree exists and is fresh. Creates it with
    /// <c>git worktree add --detach</c> from <c>origin/&lt;default&gt;</c> on first call,
    /// then refreshes via <c>git fetch &amp;&amp; git reset --hard origin/&lt;default&gt;</c>
    /// on every subsequent call so the snapshot is current at Agent spawn.
    /// Returns the worktree path.
    /// </summary>
    public async Task<string> EnsureScratchWorktreeAsync(Source source, CancellationToken ct = default)
    {
        var repoPath = source.LocalRepoPath;
        var worktreePath = ScratchWorktreePath(source);
        var defaultBranch = await GetDefaultBranchAsync(repoPath, ct);

        // git fetch is best-effort — if the user is offline, we still want the worktree to be
        // usable from whatever commits are already in the local repo.
        var fetch = await ProcessRunner.RunAsync("git", new[] { "fetch", "origin" }, repoPath, ct);
        if (!fetch.Success)
            _log.LogWarning("git fetch failed in scratch worktree setup for {Source}: {Stderr}", source.Name, fetch.Stderr);

        var resetTarget = $"origin/{defaultBranch}";
        // If origin/<default> doesn't resolve (e.g. no remote, or unfetched), fall back to the
        // local default branch.
        var hasOrigin = (await ProcessRunner.RunAsync("git",
            new[] { "rev-parse", "--verify", resetTarget }, repoPath, ct)).Success;
        if (!hasOrigin) resetTarget = defaultBranch;

        if (!Directory.Exists(worktreePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
            await ProcessRunner.RunRequiredAsync("git",
                new[] { "worktree", "add", "--detach", worktreePath, resetTarget }, repoPath, ct);
            _log.LogInformation("Created scratch worktree {Path} on {Ref} for {Source}",
                worktreePath, resetTarget, source.Name);
            return worktreePath;
        }

        // Refresh path: reset the existing worktree to match origin/<default>.
        await ProcessRunner.RunRequiredAsync("git",
            new[] { "reset", "--hard", resetTarget }, worktreePath, ct);
        _log.LogDebug("Refreshed scratch worktree {Path} to {Ref}", worktreePath, resetTarget);
        return worktreePath;
    }

    /// <summary>
    /// Remove a Source's scratch worktree. Called when a Source is deleted so we don't leak
    /// disk space. Safe to call when the directory doesn't exist.
    /// </summary>
    public Task RemoveScratchWorktreeAsync(Source source, CancellationToken ct = default) =>
        RemoveWorktreeAsync(source.LocalRepoPath, ScratchWorktreePath(source), ct);

    public async Task<string> EnsureWorkItemBranchAsync(string repoPath, WorkItem wi, CancellationToken ct = default)
    {
        var branch = WorkItemBranchName(wi);
        var defaultBranch = await GetDefaultBranchAsync(repoPath, ct);

        var existing = await ProcessRunner.RunAsync("git", new[] { "rev-parse", "--verify", branch }, repoPath, ct);
        if (existing.Success)
        {
            _log.LogInformation("Work item branch {Branch} already exists", branch);
            return branch;
        }

        await ProcessRunner.RunRequiredAsync("git", new[] { "branch", branch, defaultBranch }, repoPath, ct);
        _log.LogInformation("Created work item branch {Branch} from {Base}", branch, defaultBranch);
        return branch;
    }

    public async Task<string> CreateTaskWorktreeAsync(string repoPath, WorkItem wi, AgentTask task, CancellationToken ct = default)
    {
        var workItemBranch = await EnsureWorkItemBranchAsync(repoPath, wi, ct);
        var taskBranch = TaskBranchName(wi, task);
        var worktreePath = TaskWorktreePath(wi, task);

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        if (Directory.Exists(worktreePath))
        {
            _log.LogInformation("Worktree {Path} already exists, reusing", worktreePath);
            return worktreePath;
        }

        var taskBranchExists = (await ProcessRunner.RunAsync("git",
            new[] { "rev-parse", "--verify", taskBranch }, repoPath, ct)).Success;

        var args = taskBranchExists
            ? new[] { "worktree", "add", worktreePath, taskBranch }
            : new[] { "worktree", "add", "-b", taskBranch, worktreePath, workItemBranch };

        await ProcessRunner.RunRequiredAsync("git", args, repoPath, ct);
        _log.LogInformation("Created worktree {Path} on branch {Branch}", worktreePath, taskBranch);
        return worktreePath;
    }

    public async Task<MergeResult> MergeTaskBranchAsync(string repoPath, WorkItem wi, AgentTask task, CancellationToken ct = default)
    {
        var taskBranch = TaskBranchName(wi, task);
        var workItemBranch = WorkItemBranchName(wi);

        // `git diff --quiet base...head` exits 0 when head introduces no changes relative to the merge-base.
        var diff = await ProcessRunner.RunAsync("git",
            new[] { "diff", "--quiet", $"{workItemBranch}...{taskBranch}" }, repoPath, ct);
        if (diff.Success)
        {
            _log.LogInformation("Task branch {TaskBranch} has no diff against {WorkItemBranch}; treating as already merged",
                taskBranch, workItemBranch);
            return new MergeResult(MergeOutcome.AlreadyMerged);
        }

        var mergePath = await EnsureWorkItemMergeWorktreeAsync(repoPath, wi, ct);

        var merge = await ProcessRunner.RunAsync("git",
            new[] { "merge", "--no-ff", "-m", $"Merge task: {task.Title}", taskBranch }, mergePath, ct);
        if (merge.Success)
        {
            _log.LogInformation("Merged {TaskBranch} into {WorkItemBranch} via {Path}",
                taskBranch, workItemBranch, mergePath);
            return new MergeResult(MergeOutcome.Merged);
        }

        // Conflict vs other failure: `git ls-files --unmerged` lists rows only when files are mid-conflict.
        var unmerged = await ProcessRunner.RunAsync("git",
            new[] { "ls-files", "--unmerged" }, mergePath, ct);
        if (!unmerged.Success || string.IsNullOrWhiteSpace(unmerged.Stdout))
            throw new InvalidOperationException(
                $"git merge failed (no conflicts detected). stderr: {merge.Stderr.Trim()}");

        _log.LogWarning("Merge of {TaskBranch} into {WorkItemBranch} hit conflicts; invoking resolver",
            taskBranch, workItemBranch);

        // Per ADR 0001 and issue #66: when a kickoff hook is wired in (production path), the
        // resolver runs as a PTY MergeResolver Agent in this very merge worktree. The hook
        // creates the AgentRun row and populates the ambient MergeResolverAgentContext for
        // the duration of the ResolveAsync call. In test paths (and any caller that didn't
        // wire the hook in) the resolver falls back to its legacy one-shot CLI behaviour —
        // both keep ResolveAsync's signature intact.
        MergeResolutionResult resolution;
        if (_mergeKickoff is not null)
        {
            await using var _ = await _mergeKickoff.BeginAsync(wi, task, ct);
            resolution = await _resolver.ResolveAsync(mergePath, task.Title, ct);
        }
        else
        {
            resolution = await _resolver.ResolveAsync(mergePath, task.Title, ct);
        }

        if (resolution.Success && await IsMergeFinalizedAsync(mergePath, ct))
        {
            _log.LogInformation("Resolver finalized merge of {TaskBranch}", taskBranch);
            return new MergeResult(MergeOutcome.MergedByAgent, resolution.Notes);
        }

        throw new InvalidOperationException(
            $"Merge of '{task.Title}' hit conflicts and the resolver could not complete it. " +
            $"Worktree {mergePath} left in-conflict for manual resolution. Resolver notes: {resolution.Notes}");
    }

    private static async Task<bool> IsMergeFinalizedAsync(string mergePath, CancellationToken ct)
    {
        var mergeHead = await ProcessRunner.RunAsync("git",
            new[] { "rev-parse", "--verify", "MERGE_HEAD" }, mergePath, ct);
        if (mergeHead.Success) return false; // still mid-merge
        var unmerged = await ProcessRunner.RunAsync("git",
            new[] { "ls-files", "--unmerged" }, mergePath, ct);
        return unmerged.Success && string.IsNullOrWhiteSpace(unmerged.Stdout);
    }

    public Task RemoveWorkItemMergeWorktreeAsync(string repoPath, WorkItem wi, CancellationToken ct = default) =>
        RemoveWorktreeAsync(repoPath, WorkItemMergeWorktreePath(wi), ct);

    private async Task<string> EnsureWorkItemMergeWorktreeAsync(string repoPath, WorkItem wi, CancellationToken ct)
    {
        var branch = await EnsureWorkItemBranchAsync(repoPath, wi, ct);
        var path = WorkItemMergeWorktreePath(wi);

        if (Directory.Exists(path)) return path;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ProcessRunner.RunRequiredAsync("git", new[] { "worktree", "add", path, branch }, repoPath, ct);
        _log.LogInformation("Created merge worktree {Path} on branch {Branch}", path, branch);
        return path;
    }

    public async Task<string> DiffTaskAgainstWorkItemAsync(string repoPath, WorkItem wi, AgentTask task, CancellationToken ct = default)
    {
        var workItemBranch = WorkItemBranchName(wi);
        var taskBranch = TaskBranchName(wi, task);
        var r = await ProcessRunner.RunAsync("git",
            new[] { "diff", $"{workItemBranch}...{taskBranch}" }, repoPath, ct);
        return r.Success ? r.Stdout : "";
    }

    public async Task RemoveWorktreeAsync(string repoPath, string worktreePath, CancellationToken ct = default)
    {
        if (!Directory.Exists(worktreePath)) return;
        var r = await ProcessRunner.RunAsync("git", new[] { "worktree", "remove", "--force", worktreePath }, repoPath, ct);
        if (!r.Success) _log.LogWarning("Failed to remove worktree {Path}: {Stderr}", worktreePath, r.Stderr);
    }

    // Clean-slate reset for the Ralph-loop retry path: drop the task's worktree and
    // delete the task branch so the next CreateTaskWorktreeAsync starts fresh from the work-item branch.
    public async Task ResetTaskBranchAsync(string repoPath, WorkItem wi, AgentTask task, CancellationToken ct = default)
    {
        var worktreePath = TaskWorktreePath(wi, task);
        if (Directory.Exists(worktreePath))
            await RemoveWorktreeAsync(repoPath, worktreePath, ct);

        var taskBranch = TaskBranchName(wi, task);
        var del = await ProcessRunner.RunAsync("git", new[] { "branch", "-D", taskBranch }, repoPath, ct);
        if (!del.Success)
            _log.LogWarning("Failed to delete task branch {Branch}: {Stderr}", taskBranch, del.Stderr);
    }

    public async Task<string?> OpenPullRequestAsync(string repoPath, WorkItem wi, CancellationToken ct = default)
    {
        var branch = WorkItemBranchName(wi);
        var push = await ProcessRunner.RunAsync("git", new[] { "push", "-u", "origin", branch }, repoPath, ct);
        if (!push.Success)
        {
            _log.LogWarning("Push failed for {Branch}: {Stderr}", branch, push.Stderr);
            return null;
        }

        var body = $"Issue: {wi.ExternalId}\n\n{wi.Body}\n\n closes #{wi.ExternalId}";
        var pr = await ProcessRunner.RunAsync("gh",
            new[] { "pr", "create", "--head", branch, "--title", wi.Title, "--body", body }, repoPath, ct);
        if (pr.Success) return pr.Stdout.Trim();

        _log.LogWarning("gh pr create failed: {Stderr}", pr.Stderr);
        return null;
    }

    public static async Task<string> GetDefaultBranchPublicAsync(string repoPath, CancellationToken ct = default)
        => await GetDefaultBranchAsync(repoPath, ct);

    private static async Task<string> GetDefaultBranchAsync(string repoPath, CancellationToken ct)
    {
        var remote = await ProcessRunner.RunAsync("git", new[] { "symbolic-ref", "refs/remotes/origin/HEAD" }, repoPath, ct);
        if (remote.Success)
        {
            var line = remote.Stdout.Trim();
            var idx = line.LastIndexOf('/');
            if (idx >= 0) return line[(idx + 1)..];
        }
        foreach (var candidate in new[] { "main", "master", "trunk" })
        {
            var r = await ProcessRunner.RunAsync("git", new[] { "rev-parse", "--verify", candidate }, repoPath, ct);
            if (r.Success) return candidate;
        }
        throw new InvalidOperationException($"Could not determine default branch in {repoPath}");
    }

    private static string ResolveHome(string path) =>
        path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
}
