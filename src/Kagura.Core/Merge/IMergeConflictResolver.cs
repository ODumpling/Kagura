namespace Kagura.Core.Merge;

public record MergeResolutionResult(bool Success, string Notes);

public interface IMergeConflictResolver
{
    Task<MergeResolutionResult> ResolveAsync(
        string worktreePath,
        string taskTitle,
        CancellationToken ct = default);
}
