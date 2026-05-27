using Kagura.Core.Domain;
using Kagura.Core.Sources;
using Kagura.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kagura.Tests;

public class SourceSyncServiceTests
{
    [Fact]
    public async Task Sync_marks_workitems_missing_from_fetched_set_as_closed()
    {
        using var test = TestDb.Create();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        test.Context.Sources.Add(source);

        // ISSUE-1 is still open upstream, ISSUE-2 has been closed (PR merged), ISSUE-3 was already
        // marked closed on a prior sync.
        var stillOpen = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "ISSUE-1",
            Title = "Still open",
            Status = WorkItemStatus.Triaged,
        };
        var nowClosed = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "ISSUE-2",
            Title = "PR merged upstream",
            Status = WorkItemStatus.InProgress,
        };
        var alreadyClosed = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = "ISSUE-3",
            Title = "Old closure",
            Status = WorkItemStatus.Closed,
        };
        test.Context.WorkItems.AddRange(stillOpen, nowClosed, alreadyClosed);
        await test.Context.SaveChangesAsync();

        var provider = new FakeIssueProvider(new[]
        {
            new FetchedIssue("ISSUE-1", "Still open", "", null, null),
        });
        var factory = new FakeIssueProviderFactory(provider);

        using var svcDb = test.NewContext();
        var svc = new SourceSyncService(svcDb, factory, NullLogger<SourceSyncService>.Instance);
        var result = await svc.SyncAsync(source.Id);

        Assert.Equal(1, result.Closed);

        using var verify = test.NewContext();
        var open = await verify.WorkItems.SingleAsync(w => w.ExternalId == "ISSUE-1");
        var closed = await verify.WorkItems.SingleAsync(w => w.ExternalId == "ISSUE-2");
        var old = await verify.WorkItems.SingleAsync(w => w.ExternalId == "ISSUE-3");

        Assert.Equal(WorkItemStatus.Triaged, open.Status);
        Assert.Equal(WorkItemStatus.Closed, closed.Status);
        Assert.Equal(WorkItemStatus.Closed, old.Status);
    }

    private sealed class FakeIssueProvider : IIssueProvider
    {
        private readonly IReadOnlyList<FetchedIssue> _issues;
        public FakeIssueProvider(IReadOnlyList<FetchedIssue> issues) => _issues = issues;
        public SourceType Type => SourceType.Markdown;
        public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
            => Task.FromResult(_issues);
    }

    private sealed class FakeIssueProviderFactory : IIssueProviderFactory
    {
        private readonly IIssueProvider _provider;
        public FakeIssueProviderFactory(IIssueProvider provider) => _provider = provider;
        public IIssueProvider Get(SourceType type) => _provider;
    }
}
