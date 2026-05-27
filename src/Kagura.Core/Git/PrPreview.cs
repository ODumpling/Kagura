using Kagura.Core.Domain;

namespace Kagura.Core.Git;

public record ConflictedFile(string Path, IReadOnlyList<Guid> TaskIds);

public record PrPreviewDiffStats(int FilesChanged, int Additions, int Deletions);

public record PrPreviewResult(
    string UnifiedDiff,
    IReadOnlyList<ConflictedFile> ConflictedFiles,
    string? BaseSha,
    string? HeadSha,
    IReadOnlyDictionary<Guid, string> TaskSnapshots,
    PrPreviewDiffStats Stats)
{
    public static PrPreviewResult Empty(string? baseSha = null) => new(
        UnifiedDiff: string.Empty,
        ConflictedFiles: Array.Empty<ConflictedFile>(),
        BaseSha: baseSha,
        HeadSha: baseSha,
        TaskSnapshots: new Dictionary<Guid, string>(),
        Stats: new PrPreviewDiffStats(0, 0, 0));
}

public interface IPrPreviewService
{
    Task<PrPreviewResult> ComputePreviewAsync(
        WorkItem workItem,
        IReadOnlyList<AgentTask> includedTasks,
        CancellationToken ct = default);
}
