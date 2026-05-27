using Kagura.Core.Domain;
using Microsoft.Extensions.Logging;

namespace Kagura.Core.Git;

public record MergePreviewConflict(string Path, IReadOnlyList<Guid> TaskIds);

public record MergePreviewStats(int FilesChanged, int Additions, int Deletions);

public record MergePreviewResult(
    string BaseSha,
    string HeadSha,
    IReadOnlyDictionary<Guid, string> TaskBranchTips,
    string UnifiedDiff,
    IReadOnlyList<MergePreviewConflict> ConflictedFiles,
    MergePreviewStats Stats);

public class MergePreviewService
{
    private readonly GitService _git;
    private readonly ILogger<MergePreviewService> _log;

    public MergePreviewService(GitService git, ILogger<MergePreviewService> log)
    {
        _git = git;
        _log = log;
    }

    public async Task<MergePreviewResult> ComputeAsync(
        string repoPath,
        WorkItem workItem,
        IReadOnlyList<AgentTask> includedTasks,
        CancellationToken ct = default)
    {
        var defaultBranch = await GitService.GetDefaultBranchPublicAsync(repoPath, ct);
        var baseSha = (await ProcessRunner.RunRequiredAsync("git",
            new[] { "rev-parse", "--verify", defaultBranch }, repoPath, ct)).Stdout.Trim();

        var taskTips = new Dictionary<Guid, string>();
        var conflicts = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var headSha = baseSha;

        foreach (var task in includedTasks)
        {
            var branch = _git.TaskBranchName(workItem, task);
            var tip = (await ProcessRunner.RunRequiredAsync("git",
                new[] { "rev-parse", "--verify", branch }, repoPath, ct)).Stdout.Trim();
            taskTips[task.Id] = tip;

            var merge = await ProcessRunner.RunAsync("git",
                new[] { "merge-tree", "--write-tree", "-z", "--name-only", headSha, tip },
                repoPath, ct);
            if (merge.ExitCode > 1)
                throw new InvalidOperationException(
                    $"git merge-tree failed for task {task.Id}: {merge.Stderr}");

            var (treeSha, conflictedPaths) = ParseMergeTreeOutput(merge.Stdout);
            foreach (var path in conflictedPaths)
            {
                if (!conflicts.TryGetValue(path, out var list))
                {
                    list = new List<Guid>();
                    conflicts[path] = list;
                }
                if (!list.Contains(task.Id)) list.Add(task.Id);
            }

            var commitMsg = $"preview-merge: {task.Title}";
            var commit = await ProcessRunner.RunRequiredAsync("git",
                new[]
                {
                    "-c", "user.name=kagura-preview",
                    "-c", "user.email=preview@kagura.local",
                    "commit-tree", treeSha, "-p", headSha, "-p", tip, "-m", commitMsg
                }, repoPath, ct);
            headSha = commit.Stdout.Trim();
        }

        string diff = "";
        var stats = new MergePreviewStats(0, 0, 0);
        if (includedTasks.Count > 0 && headSha != baseSha)
        {
            diff = (await ProcessRunner.RunRequiredAsync("git",
                new[] { "diff", baseSha, headSha }, repoPath, ct)).Stdout;
            var numstat = (await ProcessRunner.RunRequiredAsync("git",
                new[] { "diff", "--numstat", baseSha, headSha }, repoPath, ct)).Stdout;
            stats = ParseNumstat(numstat);
        }

        var conflictList = conflicts
            .Select(kv => new MergePreviewConflict(kv.Key, kv.Value))
            .ToList();

        return new MergePreviewResult(baseSha, headSha, taskTips, diff, conflictList, stats);
    }

    private static (string TreeSha, IReadOnlyList<string> ConflictedPaths) ParseMergeTreeOutput(string stdout)
    {
        // Format with `-z --name-only`:
        //   <tree-oid>\0 <filename>\0 ... \0 <messages...>
        // Conflicted file names section terminates at the first empty NUL token.
        var parts = stdout.Split('\0');
        var treeSha = parts.Length > 0 ? parts[0] : "";
        var paths = new List<string>();
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) break;
            if (!paths.Contains(parts[i])) paths.Add(parts[i]);
        }
        return (treeSha, paths);
    }

    private static MergePreviewStats ParseNumstat(string stdout)
    {
        var filesChanged = 0;
        var additions = 0;
        var deletions = 0;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var cols = line.Split('\t');
            if (cols.Length < 3) continue;
            filesChanged++;
            if (int.TryParse(cols[0], out var add)) additions += add;
            if (int.TryParse(cols[1], out var del)) deletions += del;
        }
        return new MergePreviewStats(filesChanged, additions, deletions);
    }
}
