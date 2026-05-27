using Kagura.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Tests;

public class WorkItemClosedAtTests
{
    [Fact]
    public async Task ClosedAt_is_null_for_open_work_item()
    {
        using var test = TestDb.Create();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "ISSUE-OPEN",
            Title = "open",
        };
        test.Context.Sources.Add(source);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        using var verify = test.NewContext();
        var loaded = await verify.WorkItems.SingleAsync(w => w.Id == wi.Id);
        Assert.Null(loaded.ClosedAt);
    }

    [Fact]
    public async Task MarkClosed_sets_status_and_closedat()
    {
        using var test = TestDb.Create();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "ISSUE-CLOSED",
            Title = "closing",
        };
        test.Context.Sources.Add(source);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        var before = DateTime.UtcNow;
        wi.MarkClosed();
        var after = DateTime.UtcNow;
        await test.Context.SaveChangesAsync();

        using var verify = test.NewContext();
        var loaded = await verify.WorkItems.SingleAsync(w => w.Id == wi.Id);
        Assert.Equal(WorkItemStatus.Closed, loaded.Status);
        Assert.NotNull(loaded.ClosedAt);
        Assert.InRange(loaded.ClosedAt!.Value, before, after);
    }

    [Fact]
    public async Task MarkClosed_is_idempotent_and_does_not_overwrite_existing_timestamp()
    {
        using var test = TestDb.Create();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "ISSUE-IDEMPOTENT",
            Title = "already closed",
        };
        test.Context.Sources.Add(source);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();

        wi.MarkClosed();
        var firstStamp = wi.ClosedAt;
        await Task.Delay(10);
        wi.MarkClosed();

        Assert.Equal(firstStamp, wi.ClosedAt);
    }
}
