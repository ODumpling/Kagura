using System.Text.Json.Serialization;

namespace Kagura.Core.Agents.Mcp;

/// <summary>
/// Schema for the <c>kagura.submit_review</c> MCP tool input. The AutoReview Agent calls this
/// when it has decided whether the task's diff is safe to auto-merge. The shape mirrors the
/// existing <see cref="Kagura.Core.Review.ReviewVerdict"/> contract so the typed-result
/// surface seen by Ralph Loop is unchanged.
/// </summary>
public sealed record ReviewSubmission(
    [property: JsonPropertyName("autoMerge")] bool AutoMerge,
    [property: JsonPropertyName("reasoning")] string Reasoning);
