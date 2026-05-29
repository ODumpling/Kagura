using Kagura.Core.Domain;

namespace Kagura.Core.Grill;

/// <summary>
/// Scoped DI ambient context used to pass the Grill Agent's full context (WorkItem +
/// pre-allocated run id) into <see cref="IGrillService.SynthesizeAsync"/> without
/// changing the interface signature.
///
/// The grill kickoff/finalize endpoint populates this before invoking the grill service;
/// the <c>ClaudeCliGrillService</c> reads it to spawn the PTY Agent against the right
/// scratch worktree with a runId-scoped MCP URL.
///
/// When unset (e.g. a per-turn <see cref="IGrillService.RespondAsync"/> call), the grill
/// service must fall back to its legacy <c>claude -p</c> behaviour. This keeps the
/// strings-only interface usable from the conversational turn endpoints that don't
/// participate in the typed MCP submission contract.
/// </summary>
public sealed class GrillAgentContext
{
    public WorkItem? WorkItem { get; set; }
    public Guid RunId { get; set; }

    public bool IsSet => WorkItem is not null && RunId != Guid.Empty;
}
