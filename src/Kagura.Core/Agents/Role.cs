namespace Kagura.Core.Agents;

/// <summary>
/// The kind of work an Agent is doing. Per CONTEXT.md → "Role":
/// each role has its own configurable prompt template and its own contract
/// for how the Agent signals completion / returns structured results.
///
/// The role is fixed at session start and never changes.
/// </summary>
public enum Role
{
    Triage = 0,
    Task = 1,
    AutoReview = 2,
    Grill = 3,
    MergeResolver = 4,
}

public static class RoleExtensions
{
    /// <summary>
    /// Map a Role to the underlying AgentRunKind persisted on AgentRun.
    /// They're parallel enums; this keeps the two domains decoupled.
    /// </summary>
    public static Kagura.Core.Domain.AgentRunKind ToAgentRunKind(this Role role) => role switch
    {
        Role.Triage => Kagura.Core.Domain.AgentRunKind.Triage,
        Role.Task => Kagura.Core.Domain.AgentRunKind.TaskAgent,
        Role.AutoReview => Kagura.Core.Domain.AgentRunKind.AutoReview,
        Role.Grill => Kagura.Core.Domain.AgentRunKind.Grill,
        Role.MergeResolver => Kagura.Core.Domain.AgentRunKind.MergeResolver,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Role"),
    };

    /// <summary>
    /// MCP submission tool name for a Role. Each Role has exactly one submission tool;
    /// calling it both signals completion and delivers the typed payload.
    /// </summary>
    public static string McpSubmitToolName(this Role role) => role switch
    {
        Role.Triage => "kagura.submit_triage",
        Role.Task => "kagura.submit_task",
        Role.AutoReview => "kagura.submit_review",
        Role.Grill => "kagura.submit_grill",
        Role.MergeResolver => "kagura.submit_merge_resolution",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Role"),
    };
}
