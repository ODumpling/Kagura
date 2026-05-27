using System.Globalization;
using Kagura.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Kagura.Core.Git;

public sealed class GitPrPreviewService : IPrPreviewService
{
    private readonly GitService _git;
    private readonly ILogger<GitPrPreviewService> _log;

    public GitPrPreviewService(GitService git, ILogger<GitPrPreviewService> log)
    {
        _git = git;
        _log = log;
    }

    public async Task<PrPreviewResult> ComputePreviewAsync(
        WorkItem workItem,
        IReadOnlyList<AgentTask> includedTasks,
        CancellationToken ct = default)
    {
        var repoPath = workItem.Source.LocalRepoPath;
        var defaultBranch = (await ProcessRunner.RunRequiredAsync(
            "git", new[] { "rev-parse", "--abbrev-ref", "refs/remotes/origin/HEAD" }, repoPath, ct))
            .Stdout.Trim();
        if (defaultBranch.LastIndexOf('/') is var slash and >= 0) defaultBranch = defaultBranch[(slash + 1)..];

        var baseSha = (await ProcessRunner.RunRequiredAsync(
            "git", new[] { "rev-parse", defaultBranch }, repoPath, ct)).Stdout.Trim();

        if (includedTasks.Count == 0)
            return PrPreviewResult.Empty(baseSha);

        var snapshots = new Dictionary<Guid, string>();
        var conflicted = new Dictionary<string, List<Guid>>();
        var currentSha = baseSha;

        foreach (var task in includedTasks.OrderBy(t => t.Order))
        {
            var taskBranch = _git.TaskBranchName(workItem, task);
            var tipRes = await ProcessRunner.RunAsync(
                "git", new[] { "rev-parse", "--verify", taskBranch }, repoPath, ct);
            if (!tipRes.Success)
            {
                _log.LogWarning("Task branch {Branch} missing; skipping in preview", taskBranch);
                continue;
            }
            var tip = tipRes.Stdout.Trim();
            snapshots[task.Id] = tip;

            var merge = await ProcessRunner.RunAsync(
                "git", new[] { "merge-tree", "--write-tree", "--name-only", "-z", currentSha, tip },
                repoPath, ct);

            var lines = merge.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;

            currentSha = lines[0];
            foreach (var conflictedPath in lines.Skip(1))
            {
                if (!conflicted.TryGetValue(conflictedPath, out var list))
                    conflicted[conflictedPath] = list = new List<Guid>();
                list.Add(task.Id);
            }
        }

        var headSha = currentSha;

        var diff = await ProcessRunner.RunAsync(
            "git", new[] { "diff", "--unified=3", baseSha, headSha }, repoPath, ct);
        var stats = await ProcessRunner.RunAsync(
            "git", new[] { "diff", "--shortstat", baseSha, headSha }, repoPath, ct);

        var parsedStats = ParseShortStat(stats.Success ? stats.Stdout : string.Empty);

        return new PrPreviewResult(
            UnifiedDiff: diff.Success ? diff.Stdout : string.Empty,
            ConflictedFiles: conflicted
                .Select(kv => new ConflictedFile(kv.Key, kv.Value))
                .ToList(),
            BaseSha: baseSha,
            HeadSha: headSha,
            TaskSnapshots: snapshots,
            Stats: parsedStats);
    }

    private static PrPreviewDiffStats ParseShortStat(string text)
    {
        int files = 0, add = 0, del = 0;
        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var num = new string(token.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            if (token.Contains("file")) files = n;
            else if (token.Contains("insert")) add = n;
            else if (token.Contains("delet")) del = n;
        }
        return new PrPreviewDiffStats(files, add, del);
    }
}
