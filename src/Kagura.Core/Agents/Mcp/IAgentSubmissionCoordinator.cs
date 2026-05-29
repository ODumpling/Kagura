using System.Text.Json;

namespace Kagura.Core.Agents.Mcp;

/// <summary>
/// Per CONTEXT.md → "Agent result contract" / ADR 0001: the MCP submission tool is both
/// the completion signal and the structured payload. This coordinator owns the side of
/// that contract that lives outside the MCP server itself — it tracks which runIds are
/// currently expecting a submission and routes incoming JSON payloads to the right
/// awaiting Agent.
///
/// One running Agent per runId. Submitting to a stale runId returns false so the MCP
/// server can surface the right error to the caller.
/// </summary>
public interface IAgentSubmissionCoordinator
{
    /// <summary>
    /// Register that <paramref name="runId"/> is awaiting an MCP submission. Returns the
    /// Task to await — completes when <see cref="TrySubmit"/> succeeds with a payload, or
    /// when <see cref="Fail"/> is called to abort.
    /// </summary>
    Task<JsonElement> RegisterAsync(Guid runId, CancellationToken ct = default);

    /// <summary>
    /// Try to deliver a submission payload for <paramref name="runId"/>. Returns false if
    /// no Agent is currently registered for that runId (e.g. stale, never started).
    /// </summary>
    bool TrySubmit(Guid runId, JsonElement payload);

    /// <summary>
    /// Fail the pending submission for <paramref name="runId"/> with the given exception.
    /// Idempotent — returns false if no Agent is currently registered.
    /// </summary>
    bool Fail(Guid runId, Exception ex);

    /// <summary>
    /// Whether any Agent is currently registered as awaiting a submission for this runId.
    /// </summary>
    bool IsActive(Guid runId);
}
