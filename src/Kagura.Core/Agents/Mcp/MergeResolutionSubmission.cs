using System.Text.Json.Serialization;

namespace Kagura.Core.Agents.Mcp;

/// <summary>
/// Schema for the <c>kagura.submit_merge_resolution</c> MCP tool input. The MergeResolver
/// Agent calls this when it's finished — either because it successfully resolved the
/// conflicts and finalized the merge, or because it decided the conflicts were too
/// ambiguous to resolve safely and left the worktree mid-merge for human attention.
/// </summary>
public sealed record MergeResolutionSubmission(
    [property: JsonPropertyName("resolved")] bool Resolved,
    [property: JsonPropertyName("notes")] string Notes);
