using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Core.Review;

namespace Kagura.Tests;

/// <summary>
/// Acceptance tests for issue #70 Part A — AutoReview PTY migration. The AutoReview Agent's
/// prompt is built by <see cref="ClaudeCliReviewService.RenderPrompt(string, string, string, string, int)"/>;
/// the default template tells Claude to deliver its verdict via the MCP submission tool.
/// </summary>
public class AutoReviewPromptTests
{
    [Fact]
    public void Default_prompt_includes_task_context_and_submit_tool_name()
    {
        var prompt = ClaudeCliReviewService.RenderPrompt(
            taskTitle: "Wire AutoReview to PTY",
            taskDescription: "Spawn AutoReview as a PTY Agent that submits via kagura.submit_review.",
            diff: "diff --git a/foo b/foo\n+bar\n",
            maxDiffBytes: 50_000);

        Assert.Contains("Title: Wire AutoReview to PTY", prompt);
        Assert.Contains("Spawn AutoReview as a PTY Agent", prompt);
        Assert.Contains("kagura.submit_review", prompt);
    }

    [Fact]
    public void Default_prompt_instructs_the_agent_to_submit_via_mcp_only()
    {
        var prompt = ClaudeCliReviewService.RenderPrompt(
            taskTitle: "t", taskDescription: "d", diff: "x", maxDiffBytes: 1_000);

        Assert.Contains("Calling the tool is what hands the verdict back to Kagura", prompt);
        Assert.Contains("\"autoMerge\"", prompt);
        Assert.Contains("\"reasoning\"", prompt);
    }

    [Fact]
    public void Truncates_oversized_diffs_with_banner()
    {
        var bigDiff = new string('x', 200);
        var prompt = ClaudeCliReviewService.RenderPrompt(
            taskTitle: "t", taskDescription: "d", diff: bigDiff, maxDiffBytes: 50);

        // The banner names both the cap and the original size so the LLM knows it's seeing
        // a truncated payload.
        Assert.Contains("truncated to 50 bytes of 200", prompt);
        // The post-banner diff body is 50 chars long, not 200.
        var diffBlockStart = prompt.IndexOf("```diff", StringComparison.Ordinal);
        var diffBlockEnd = prompt.IndexOf("```", diffBlockStart + "```diff".Length, StringComparison.Ordinal);
        var diffBody = prompt.Substring(diffBlockStart, diffBlockEnd - diffBlockStart);
        Assert.Equal(50, diffBody.Count(c => c == 'x'));
    }

    [Fact]
    public void Per_source_override_template_wins_over_built_in_default()
    {
        var src = new Source { Name = "s", LocalRepoPath = "/tmp/r" };
        src.PromptOverrides.Add(new SourcePromptOverride
        {
            SourceId = src.Id,
            Role = Role.AutoReview,
            PromptText = "OVERRIDE: task={{TASK_TITLE}} submit={{SUBMIT_TOOL}} diff={{DIFF}}",
        });

        var resolver = new PromptResolver();
        var template = resolver.Resolve(src, Role.AutoReview);

        var rendered = ClaudeCliReviewService.RenderPrompt(
            template, taskTitle: "Custom review", taskDescription: "irrelevant", diff: "DIFFY", maxDiffBytes: 100);

        Assert.StartsWith("OVERRIDE: task=Custom review submit=kagura.submit_review", rendered);
        Assert.Contains("diff=DIFFY", rendered);
        // The built-in default's instructions do NOT bleed in.
        Assert.DoesNotContain("automated code reviewer", rendered);
    }
}

/// <summary>
/// Behavioural tests for <see cref="AutoReviewAgentContext"/> — mirrors
/// MergeResolverAgentContextTests in shape. AsyncLocal scoping is the load-bearing
/// behaviour because IReviewService is invoked from a background Task inside the kickoff.
/// </summary>
public class AutoReviewAgentContextTests
{
    [Fact]
    public void Push_sets_slots_and_dispose_clears_them()
    {
        var ctx = new AutoReviewAgentContext();
        var wi = new WorkItem { ExternalId = "EXT", Title = "wi" };
        var runId = Guid.NewGuid();

        Assert.False(ctx.IsSet);

        using (ctx.Push(wi, runId, prompt: "p", mergeWorktreePath: "/tmp/m"))
        {
            Assert.True(ctx.IsSet);
            Assert.Same(wi, ctx.WorkItem);
            Assert.Equal(runId, ctx.RunId);
            Assert.Equal("p", ctx.Prompt);
            Assert.Equal("/tmp/m", ctx.MergeWorktreePath);
        }

        Assert.False(ctx.IsSet);
        Assert.Null(ctx.WorkItem);
        Assert.Equal(Guid.Empty, ctx.RunId);
        Assert.Null(ctx.Prompt);
        Assert.Null(ctx.MergeWorktreePath);
    }

    [Fact]
    public async Task Push_is_async_flow_scoped_so_nested_async_sees_pushed_values()
    {
        var ctx = new AutoReviewAgentContext();
        var wi = new WorkItem { ExternalId = "EXT", Title = "wi" };
        var runId = Guid.NewGuid();

        bool seenInsideAsync = false;
        async Task PeekAsync()
        {
            await Task.Yield();
            seenInsideAsync = ctx.IsSet && ctx.WorkItem == wi && ctx.RunId == runId
                && ctx.MergeWorktreePath == "/tmp/m";
        }

        using (ctx.Push(wi, runId, mergeWorktreePath: "/tmp/m"))
        {
            await PeekAsync();
        }

        Assert.True(seenInsideAsync);
        Assert.False(ctx.IsSet);
    }

}
