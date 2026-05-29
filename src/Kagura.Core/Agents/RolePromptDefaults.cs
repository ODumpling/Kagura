using Kagura.Core.Grill;
using Kagura.Core.Merge;
using Kagura.Core.Review;
using Kagura.Core.Triage;

namespace Kagura.Core.Agents;

/// <summary>
/// Central registry of built-in prompt defaults for each <see cref="Role"/>. Per ADR 0002
/// these are resolved lazily by <see cref="PromptResolver"/> at Agent spawn time when a
/// Source has not customised its own prompt for that Role — they are intentionally NOT
/// copied into the override table when a Source is created, so improving a built-in default
/// flows through to every uncustomised Source automatically.
///
/// Roles that have not been migrated to the PTY-Agent path yet still surface their templates
/// here so the Source's "Prompts" tab can render them with a "Using default" badge.
/// </summary>
public static class RolePromptDefaults
{
    public static string For(Role role) => role switch
    {
        Role.Triage => ClaudeCliTriageService.DefaultPromptTemplate,
        Role.Task => AgentRunnerOptions.DefaultPromptTemplate,
        Role.AutoReview => ClaudeCliReviewService.DefaultPromptTemplate,
        Role.Grill => ClaudeCliGrillService.DefaultPromptTemplate,
        Role.MergeResolver => ClaudeCliMergeResolver.DefaultPromptTemplate,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown Role"),
    };
}
