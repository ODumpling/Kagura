using System.Text.RegularExpressions;
using Kagura.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Kagura.Core.Git;

public partial class GitService
{
    private readonly string _worktreesRoot;
    private readonly ILogger<GitService> _log;

    public GitService(string worktreesRoot, ILogger<GitService> log)
    {
        _worktreesRoot = ResolveHome(worktreesRoot);
        _log = log;
        Directory.CreateDirectory(_worktreesRoot);
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

    public async Task MergeTaskBranchAsync(string repoPath, WorkItem wi, AgentTask task, CancellationToken ct = default)
    {
        var workItemBranch = WorkItemBranchName(wi);
        var taskBranch = TaskBranchName(wi, task);

        var originalRef = (await ProcessRunner.RunRequiredAsync("git",
            new[] { "rev-parse", "--abbrev-ref", "HEAD" }, repoPath, ct)).Stdout.Trim();

        try
        {
            await ProcessRunner.RunRequiredAsync("git", new[] { "checkout", workItemBranch }, repoPath, ct);
            await ProcessRunner.RunRequiredAsync("git",
                new[] { "merge", "--no-ff", "-m", $"Merge task: {task.Title}", taskBranch }, repoPath, ct);
            _log.LogInformation("Merged {TaskBranch} into {WorkItemBranch}", taskBranch, workItemBranch);
        }
        finally
        {
            await ProcessRunner.RunAsync("git", new[] { "checkout", originalRef }, repoPath, ct);
        }
    }

    public async Task RemoveWorktreeAsync(string repoPath, string worktreePath, CancellationToken ct = default)
    {
        if (!Directory.Exists(worktreePath)) return;
        var r = await ProcessRunner.RunAsync("git", new[] { "worktree", "remove", "--force", worktreePath }, repoPath, ct);
        if (!r.Success) _log.LogWarning("Failed to remove worktree {Path}: {Stderr}", worktreePath, r.Stderr);
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

        var body = $"Issue: {wi.ExternalId}\n\n{wi.Body}";
        var pr = await ProcessRunner.RunAsync("gh",
            new[] { "pr", "create", "--head", branch, "--title", wi.Title, "--body", body }, repoPath, ct);
        if (pr.Success) return pr.Stdout.Trim();

        _log.LogWarning("gh pr create failed: {Stderr}", pr.Stderr);
        return null;
    }

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
