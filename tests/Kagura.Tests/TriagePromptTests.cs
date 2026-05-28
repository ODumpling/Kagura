using Kagura.Core.Triage;

namespace Kagura.Tests;

public class TriagePromptTests
{
    [Fact]
    public void Prompt_includes_existing_task_titles_and_dedupe_instruction_when_tasks_present()
    {
        var prompt = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "Dedupe for triage",
            workItemBody: "When pressing triage, include existing tasks in the prompt.",
            labels: "bug",
            existingTaskTitles: new[]
            {
                "Load existing tasks for issue",
                "Include existing tasks in triage prompt",
            });

        Assert.Contains("Title: Dedupe for triage", prompt);
        Assert.Contains("Existing tasks already proposed for this issue:", prompt);
        Assert.Contains("- Load existing tasks for issue", prompt);
        Assert.Contains("- Include existing tasks in triage prompt", prompt);
        Assert.Contains("Do not propose duplicates", prompt);
    }

    [Fact]
    public void Prompt_omits_dedupe_section_when_no_existing_tasks()
    {
        var prompt = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "Fresh issue",
            workItemBody: "No tasks yet.",
            labels: null,
            existingTaskTitles: null);

        Assert.Contains("Title: Fresh issue", prompt);
        Assert.Contains("Labels: (none)", prompt);
        Assert.DoesNotContain("Existing tasks already proposed", prompt);
        Assert.DoesNotContain("Do not propose duplicates", prompt);
    }

    [Fact]
    public void Prompt_omits_dedupe_section_when_existing_tasks_list_is_empty()
    {
        var prompt = ClaudeCliTriageService.BuildUserPrompt(
            workItemTitle: "Issue",
            workItemBody: "body",
            labels: "feature",
            existingTaskTitles: Array.Empty<string>());

        Assert.DoesNotContain("Existing tasks already proposed", prompt);
        Assert.DoesNotContain("Do not propose duplicates", prompt);
    }
}
