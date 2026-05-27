using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Merge;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

internal sealed class StubMergeResolver : IMergeConflictResolver
{
    public Task<MergeResolutionResult> ResolveAsync(string worktreePath, string taskTitle, CancellationToken ct = default) =>
        Task.FromResult(new MergeResolutionResult(false, "stub resolver — not invoked in tests"));
}

public class MergePreviewServiceTests : IDisposable
{
    private readonly string _repo;
    private readonly string _worktreesRoot;
    private readonly GitService _git;
    private readonly MergePreviewService _svc;
    private readonly WorkItem _wi;

    public MergePreviewServiceTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "kagura-mpt-" + Guid.NewGuid().ToString("N"));
        _worktreesRoot = Path.Combine(Path.GetTempPath(), "kagura-mpt-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_worktreesRoot);

        _git = new GitService(_worktreesRoot, new StubMergeResolver(), NullLogger<GitService>.Instance);
        _svc = new MergePreviewService(_git, NullLogger<MergePreviewService>.Instance);

        Run("git", "init", "-b", "main", _repo);
        Run("git", "-C", _repo, "config", "user.email", "test@kagura.local");
        Run("git", "-C", _repo, "config", "user.name", "test");
        Run("git", "-C", _repo, "config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(_repo, "README.md"), "base\nline-2\nline-3\n");
        Run("git", "-C", _repo, "add", ".");
        Run("git", "-C", _repo, "commit", "-m", "initial");

        _wi = new WorkItem
        {
            ExternalId = "MPT-1",
            Title = "merge preview test",
        };
    }

    public void Dispose()
    {
        TryDelete(_repo);
        TryDelete(_worktreesRoot);
    }

    [Fact]
    public async Task Empty_input_returns_base_equals_head_and_no_diff()
    {
        var result = await _svc.ComputeAsync(_repo, _wi, Array.Empty<AgentTask>());

        Assert.NotEmpty(result.BaseSha);
        Assert.Equal(result.BaseSha, result.HeadSha);
        Assert.Empty(result.TaskBranchTips);
        Assert.Empty(result.UnifiedDiff);
        Assert.Empty(result.ConflictedFiles);
        Assert.Equal(0, result.Stats.FilesChanged);
        Assert.Equal(0, result.Stats.Additions);
        Assert.Equal(0, result.Stats.Deletions);
    }

    [Fact]
    public async Task Clean_merges_fold_independent_changes_with_stats_and_diff()
    {
        var t1 = MakeTask("Add alpha", order: 0);
        var t2 = MakeTask("Add beta", order: 1);

        CreateTaskBranchWithCommit(t1, "alpha.txt", "alpha\n", "add alpha");
        CreateTaskBranchWithCommit(t2, "beta.txt", "beta one\nbeta two\n", "add beta");

        var result = await _svc.ComputeAsync(_repo, _wi, new[] { t1, t2 });

        Assert.NotEqual(result.BaseSha, result.HeadSha);
        Assert.Empty(result.ConflictedFiles);
        Assert.Equal(2, result.TaskBranchTips.Count);
        Assert.True(result.TaskBranchTips.ContainsKey(t1.Id));
        Assert.True(result.TaskBranchTips.ContainsKey(t2.Id));

        Assert.Contains("alpha.txt", result.UnifiedDiff);
        Assert.Contains("beta.txt", result.UnifiedDiff);
        Assert.Equal(2, result.Stats.FilesChanged);
        Assert.Equal(3, result.Stats.Additions); // 1 line + 2 lines
        Assert.Equal(0, result.Stats.Deletions);
    }

    [Fact]
    public async Task Conflicting_branches_record_conflicted_paths_with_both_task_ids()
    {
        var t1 = MakeTask("Mutate readme A", order: 0);
        var t2 = MakeTask("Mutate readme B", order: 1);

        CreateTaskBranchWithFileReplace(t1, "README.md", "base\nbranch-A-change\nline-3\n", "edit readme A");
        CreateTaskBranchWithFileReplace(t2, "README.md", "base\nbranch-B-change\nline-3\n", "edit readme B");

        var result = await _svc.ComputeAsync(_repo, _wi, new[] { t1, t2 });

        var conflict = Assert.Single(result.ConflictedFiles);
        Assert.Equal("README.md", conflict.Path);
        Assert.Contains(t2.Id, conflict.TaskIds);
        // Diff contains conflict markers that merge-tree writes into the blob.
        Assert.Contains("<<<<<<<", result.UnifiedDiff);
        Assert.Contains(">>>>>>>", result.UnifiedDiff);
    }

    private AgentTask MakeTask(string title, int order) => new()
    {
        WorkItemId = _wi.Id,
        Title = title,
        Order = order,
    };

    private void CreateTaskBranchWithCommit(AgentTask task, string file, string content, string message)
    {
        var branch = _git.TaskBranchName(_wi, task);
        Run("git", "-C", _repo, "checkout", "-b", branch, "main");
        File.WriteAllText(Path.Combine(_repo, file), content);
        Run("git", "-C", _repo, "add", file);
        Run("git", "-C", _repo, "commit", "-m", message);
        Run("git", "-C", _repo, "checkout", "main");
    }

    private void CreateTaskBranchWithFileReplace(AgentTask task, string file, string content, string message)
        => CreateTaskBranchWithCommit(task, file, content, message);

    private static void Run(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(args[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        for (var i = 1; i < args.Length; i++) psi.ArgumentList.Add(args[i]);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"`{string.Join(' ', args)}` exited {p.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}
