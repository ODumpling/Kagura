using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Triage;

namespace Kagura.Tests;

/// <summary>
/// Acceptance test for issue #69: when a Source has a Triage override row, the prompt text
/// passed to the Agent (and snapshotted onto <c>AgentRun.PromptText</c>) is the override —
/// post-interpolation — not the built-in default. The override-vs-default decision lives in
/// <see cref="PromptResolver"/>; the interpolation lives in
/// <see cref="ClaudeCliTriageService.RenderPrompt(string, string, string, string?, IReadOnlyList{ExistingTask}?)"/>.
/// </summary>
public class TriagePromptOverrideTests
{
    [Fact]
    public void Override_template_is_used_and_placeholders_are_interpolated()
    {
        var src = new Source { Name = "s", LocalRepoPath = "/tmp/r" };
        src.PromptOverrides.Add(new SourcePromptOverride
        {
            SourceId = src.Id,
            Role = Role.Triage,
            PromptText = "BEGIN\nTitle={{TITLE}}\nLabels={{LABELS}}\nBody={{BODY}}\nSubmit={{SUBMIT_TOOL}}\nEND",
        });

        var resolver = new PromptResolver();
        var template = resolver.Resolve(src, Role.Triage);

        var rendered = ClaudeCliTriageService.RenderPrompt(
            template,
            workItemTitle: "Wire prompt overrides",
            workItemBody: "Triage should pick up the source-level override.",
            labels: "feature,backend",
            existingTasks: null);

        Assert.StartsWith("BEGIN", rendered);
        Assert.Contains("Title=Wire prompt overrides", rendered);
        Assert.Contains("Labels=feature,backend", rendered);
        Assert.Contains("Body=Triage should pick up the source-level override.", rendered);
        // The submit-tool placeholder is interpolated so the override prompt still tells
        // Claude which MCP tool to call.
        Assert.Contains("Submit=kagura.submit_triage", rendered);
        // The built-in default's instructions do NOT bleed in.
        Assert.DoesNotContain("triage assistant for a developer workflow tool", rendered);
    }

    [Fact]
    public void No_override_falls_back_to_builtin_default()
    {
        var src = new Source { Name = "s", LocalRepoPath = "/tmp/r" };
        var resolver = new PromptResolver();

        var template = resolver.Resolve(src, Role.Triage);
        Assert.Equal(ClaudeCliTriageService.DefaultPromptTemplate, template);
    }
}
