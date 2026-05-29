using System.Text.Json.Serialization;

namespace Kagura.Core.Agents.Mcp;

/// <summary>
/// Schema for the <c>kagura.submit_triage</c> MCP tool input. Mirrors the shape the Agent
/// sends — a flat array of proposed tasks. Each task carries title, description, and an
/// integer ordering hint.
/// </summary>
public sealed record TriageSubmission(
    [property: JsonPropertyName("tasks")] IReadOnlyList<TriageTaskItem> Tasks);

public sealed record TriageTaskItem(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("order")] int Order);
