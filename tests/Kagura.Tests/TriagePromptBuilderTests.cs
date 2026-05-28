using Kagura.Core.Triage;

namespace Kagura.Tests;

public class TriagePromptBuilderTests
{
    [Fact]
    public void BuildUserPrompt_with_no_existing_tasks_matches_original_format()
    {
        var prompt = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "Fix the thing",
            workItemBody: "It's broken.",
            labels: "bug",
            existingTasks: null);

        var expected =
            """
            Title: Fix the thing

            Labels: bug

            Body:
            It's broken.
            """;

        Assert.Equal(expected, prompt);
    }

    [Fact]
    public void BuildUserPrompt_with_empty_existing_tasks_matches_original_format()
    {
        var promptNull = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "T", workItemBody: "B", labels: null, existingTasks: null);
        var promptEmpty = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "T", workItemBody: "B", labels: null, existingTasks: Array.Empty<ExistingTask>());

        Assert.Equal(promptNull, promptEmpty);
        Assert.DoesNotContain("Existing tasks", promptEmpty);
        Assert.DoesNotContain("duplicate", promptEmpty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildUserPrompt_with_existing_tasks_renders_them_and_dedupe_directive()
    {
        var existing = new[]
        {
            new ExistingTask("Wire markdown sync", "Scan .md files and upsert work items."),
            new ExistingTask("Add triage endpoint", "POST /api/workitems/{id}/triage shells out to claude."),
        };

        var prompt = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "Issue",
            workItemBody: "Body text",
            labels: null,
            existingTasks: existing);

        Assert.Contains("Title: Issue", prompt);
        Assert.Contains("Body:", prompt);
        Assert.Contains("Wire markdown sync", prompt);
        Assert.Contains("Scan .md files and upsert work items.", prompt);
        Assert.Contains("Add triage endpoint", prompt);
        Assert.Contains("POST /api/workitems/{id}/triage shells out to claude.", prompt);

        Assert.Contains("Existing tasks", prompt);
        Assert.Contains("Do NOT propose duplicates", prompt);
    }
}
