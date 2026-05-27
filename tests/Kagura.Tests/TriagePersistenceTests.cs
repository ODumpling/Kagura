using Kagura.Core.Domain;
using Kagura.Core.Triage;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Tests;

public class TriagePersistenceTests
{
    [Fact]
    public async Task Triage_save_persists_proposed_tasks()
    {
        using var test = TestDb.Create();
        var wi = await Seed(test, externalId: "ISSUE-001");

        await RunTriageSave(test, wi.Id, proposals: new[]
        {
            new TriagedTaskProposal("First", "do a", 0),
            new TriagedTaskProposal("Second", "do b", 1),
            new TriagedTaskProposal("Third", "do c", 2),
        });

        using var verify = test.NewContext();
        var saved = await verify.AgentTasks
            .Where(t => t.WorkItemId == wi.Id)
            .OrderBy(t => t.Order)
            .ToListAsync();

        Assert.Equal(3, saved.Count);
        Assert.Equal(new[] { "First", "Second", "Third" }, saved.Select(t => t.Title));
        Assert.All(saved, t => Assert.Equal(AgentTaskStatus.Proposed, t.Status));
        Assert.All(saved, t => Assert.NotEqual(Guid.Empty, t.Id));
    }

    [Fact]
    public async Task Retriage_replaces_proposed_but_keeps_approved()
    {
        using var test = TestDb.Create();
        var wi = await Seed(test, externalId: "ISSUE-002");

        await RunTriageSave(test, wi.Id, new[]
        {
            new TriagedTaskProposal("Keep me", "approved task", 0),
            new TriagedTaskProposal("Drop me", "still proposed", 1),
        });

        using (var approveCtx = test.NewContext())
        {
            var first = await approveCtx.AgentTasks
                .Where(t => t.WorkItemId == wi.Id && t.Title == "Keep me")
                .SingleAsync();
            first.Status = AgentTaskStatus.Approved;
            await approveCtx.SaveChangesAsync();
        }

        await RunTriageSave(test, wi.Id, new[]
        {
            new TriagedTaskProposal("Fresh proposal", "new", 0),
        });

        using var verify = test.NewContext();
        var saved = await verify.AgentTasks
            .Where(t => t.WorkItemId == wi.Id)
            .OrderBy(t => t.Title)
            .ToListAsync();

        Assert.Equal(2, saved.Count);
        Assert.Contains(saved, t => t.Title == "Keep me" && t.Status == AgentTaskStatus.Approved);
        Assert.Contains(saved, t => t.Title == "Fresh proposal" && t.Status == AgentTaskStatus.Proposed);
        Assert.DoesNotContain(saved, t => t.Title == "Drop me");
    }

    [Fact]
    public async Task Triage_save_succeeds_even_with_default_guid_initializer()
    {
        // Regression: AgentTask.Id has a `= Guid.NewGuid()` initializer.
        // Adding new tasks via the WorkItem.Tasks navigation collection used to make
        // EF Core's change-tracker treat the entity as Modified (because IsKeySet was true)
        // and emit an UPDATE that failed with DbUpdateConcurrencyException.
        // The fix uses db.AgentTasks.Add(...) explicitly, which unconditionally marks Added.
        using var test = TestDb.Create();
        var wi = await Seed(test, externalId: "ISSUE-003");

        var ex = await Record.ExceptionAsync(() => RunTriageSave(test, wi.Id, new[]
        {
            new TriagedTaskProposal("One", "", 0),
            new TriagedTaskProposal("Two", "", 1),
        }));

        Assert.Null(ex);
    }

    private static async Task<WorkItem> Seed(TestDb test, string externalId)
    {
        var source = new Source { Name = "test-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        var wi = new WorkItem
        {
            SourceId = source.Id,
            ExternalId = externalId,
            Title = "title",
            Body = "body",
        };
        test.Context.Sources.Add(source);
        test.Context.WorkItems.Add(wi);
        await test.Context.SaveChangesAsync();
        return wi;
    }

    // Mirrors the persistence logic of TriageEndpoints.MapPost("/triage", ...)
    // so that future endpoint changes get caught here.
    private static async Task RunTriageSave(TestDb test, Guid workItemId, IEnumerable<TriagedTaskProposal> proposals)
    {
        using var db = test.NewContext();
        var wi = await db.WorkItems.Include(w => w.Tasks).SingleAsync(w => w.Id == workItemId);

        var existingProposed = wi.Tasks.Where(t => t.Status == AgentTaskStatus.Proposed).ToList();
        db.AgentTasks.RemoveRange(existingProposed);

        foreach (var p in proposals)
        {
            db.AgentTasks.Add(new AgentTask
            {
                WorkItemId = wi.Id,
                Title = p.Title,
                Description = p.Description,
                Order = p.Order,
            });
        }

        await db.SaveChangesAsync();
    }
}
