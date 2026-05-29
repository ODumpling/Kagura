using Kagura.Core.Domain;

namespace Kagura.Core.Triage;

/// <summary>
/// Scoped DI ambient context used to pass the Triage Agent's full context (WorkItem +
/// pre-allocated run id) into <see cref="ITriageService.ProposeTasksAsync"/> without
/// changing the interface signature.
///
/// The kickoff service populates this before invoking the triage service; the
/// <c>ClaudeCliTriageService</c> reads it to spawn the PTY Agent against the right
/// scratch worktree with a runId-scoped MCP URL.
///
/// When unset (e.g. when called from a non-Agent path), the triage service must fall back
/// to its legacy <c>claude -p</c> behaviour. This keeps the strings-only interface usable
/// from places that don't have a WorkItem at hand.
/// </summary>
public sealed class TriageAgentContext
{
    public WorkItem? WorkItem { get; set; }
    public Guid RunId { get; set; }

    public bool IsSet => WorkItem is not null && RunId != Guid.Empty;
}
