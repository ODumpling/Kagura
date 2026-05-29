using System.Text.Json.Serialization;

namespace Kagura.Core.Agents.Mcp;

/// <summary>
/// Schema for the <c>kagura.submit_grill</c> MCP tool input. The Grill Agent grills the user
/// through a PTY conversation and, when ready, submits the rewritten work-item body via this
/// tool. The submitted markdown becomes the new <c>WorkItem.Body</c> exactly as the legacy
/// <c>SynthesizeAsync</c> output did.
/// </summary>
public sealed record GrillSubmission(
    [property: JsonPropertyName("body")] string Body);
