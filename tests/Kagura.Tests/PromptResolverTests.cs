using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Triage;

namespace Kagura.Tests;

public class PromptResolverTests
{
    [Fact]
    public void Resolve_returns_builtin_default_when_no_override_row()
    {
        var src = new Source { Name = "s", LocalRepoPath = "/tmp/r" };
        var resolver = new PromptResolver();

        foreach (var role in new[] { Role.Triage, Role.Task, Role.AutoReview, Role.Grill, Role.MergeResolver })
        {
            var resolved = resolver.Resolve(src, role);
            Assert.Equal(RolePromptDefaults.For(role), resolved);
            // Sanity: the default itself is non-empty so a UI rendering it shows something.
            Assert.False(string.IsNullOrWhiteSpace(resolved));
        }
    }

    [Fact]
    public void Resolve_returns_override_text_when_row_present()
    {
        var src = new Source { Name = "s", LocalRepoPath = "/tmp/r" };
        src.PromptOverrides.Add(new SourcePromptOverride
        {
            SourceId = src.Id,
            Role = Role.Triage,
            PromptText = "CUSTOM TRIAGE — {{TITLE}}",
        });
        var resolver = new PromptResolver();

        Assert.Equal("CUSTOM TRIAGE — {{TITLE}}", resolver.Resolve(src, Role.Triage));
        // Roles without an override still fall back to the built-in default — no spillover.
        Assert.Equal(RolePromptDefaults.For(Role.Task), resolver.Resolve(src, Role.Task));
    }

    [Fact]
    public void RenderPrompt_interpolates_placeholders_from_the_passed_template()
    {
        // ADR 0002: AgentRun.PromptText is the resolved + interpolated prompt. Verify that
        // when a Source customises the Triage template, RenderPrompt swaps placeholders on
        // the override text — not on the built-in default.
        const string custom = "X: {{TITLE}} / {{LABELS}} / submit via {{SUBMIT_TOOL}}";

        var rendered = ClaudeCliTriageService.RenderPrompt(
            template: custom,
            workItemTitle: "Add prompt overrides",
            workItemBody: "body",
            labels: "feature",
            existingTasks: null);

        Assert.Equal("X: Add prompt overrides / feature / submit via kagura.submit_triage", rendered);
    }
}
