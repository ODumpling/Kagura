using Kagura.Core.Merge;

namespace Kagura.Tests;

public class MergeResolverPromptTests
{
    [Fact]
    public void Prompt_includes_task_title_and_submit_tool_name()
    {
        var prompt = ClaudeCliMergeResolver.RenderPrompt("Migrate MergeResolver to PTY Agent");

        Assert.Contains("Task: Migrate MergeResolver to PTY Agent", prompt);
        Assert.Contains("kagura.submit_merge_resolution", prompt);
    }

    [Fact]
    public void Prompt_instructs_the_agent_to_submit_via_mcp_only()
    {
        var prompt = ClaudeCliMergeResolver.RenderPrompt("any task");

        // The Agent must hand the verdict back via the MCP tool, not stdout / files / extra edits.
        Assert.Contains("Calling the tool is what hands the verdict back to Kagura", prompt);
        Assert.Contains("\"resolved\"", prompt);
        Assert.Contains("\"notes\"", prompt);
    }

    [Fact]
    public void Prompt_forbids_aborting_or_touching_unrelated_files()
    {
        var prompt = ClaudeCliMergeResolver.RenderPrompt("any task");

        Assert.Contains("Do NOT run `git merge --abort`", prompt);
        Assert.Contains("Do NOT touch files that are not currently conflicted", prompt);
    }
}

public class MergeResolverAgentContextTests
{
    [Fact]
    public void Push_sets_slots_and_dispose_clears_them()
    {
        var ctx = new MergeResolverAgentContext();
        var wi = new Kagura.Core.Domain.WorkItem { ExternalId = "EXT", Title = "wi" };
        var runId = Guid.NewGuid();

        Assert.False(ctx.IsSet);

        using (ctx.Push(wi, runId))
        {
            Assert.True(ctx.IsSet);
            Assert.Same(wi, ctx.WorkItem);
            Assert.Equal(runId, ctx.RunId);
        }

        Assert.False(ctx.IsSet);
        Assert.Null(ctx.WorkItem);
        Assert.Equal(Guid.Empty, ctx.RunId);
    }

    [Fact]
    public async Task Push_is_async_flow_scoped_so_nested_async_sees_pushed_values()
    {
        var ctx = new MergeResolverAgentContext();
        var wi = new Kagura.Core.Domain.WorkItem { ExternalId = "EXT", Title = "wi" };
        var runId = Guid.NewGuid();

        bool seenInsideAsync = false;
        async Task PeekAsync()
        {
            await Task.Yield();
            seenInsideAsync = ctx.IsSet && ctx.WorkItem == wi && ctx.RunId == runId;
        }

        using (ctx.Push(wi, runId))
        {
            await PeekAsync();
        }

        Assert.True(seenInsideAsync);
        Assert.False(ctx.IsSet);
    }
}
