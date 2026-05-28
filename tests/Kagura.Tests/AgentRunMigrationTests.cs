using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Kagura.Tests;

public class AgentRunMigrationTests
{
    [Fact]
    public async Task GeneralizeAgentRunModel_backfills_WorkItemId_and_KindTaskAgent_for_existing_rows()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();

        // Step 1: bring the DB up to the migration immediately before this one,
        // then seed a legacy AgentRun row (no WorkItemId, no Kind yet).
        var optsBuilder = new DbContextOptionsBuilder<KaguraDbContext>().UseSqlite(conn);
        await using (var setup = new KaguraDbContext(optsBuilder.Options, new EphemeralDataProtectionProvider()))
        {
            var migrator = setup.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("AddRalphLoopFields");
        }

        var sourceId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO Sources (Id, Name, Type, LocalRepoPath, ConfigJson, Enabled, CreatedAt)
                VALUES ($sid, 'src-mig', 0, '/tmp/r', '{}', 1, '2026-05-28T00:00:00');

                INSERT INTO WorkItems (Id, SourceId, ExternalId, Title, Body, Status, CreatedAt, UpdatedAt, RalphLoopActive)
                VALUES ($wid, $sid, 'EXT-MIG', 'wi-mig', '', 0, '2026-05-28T00:00:00', '2026-05-28T00:00:00', 0);

                INSERT INTO AgentTasks (Id, WorkItemId, Title, Description, "Order", Status, CreatedAt, UpdatedAt, IncludeInPullRequest, RetryAttempts)
                VALUES ($tid, $wid, 'task-mig', '', 0, 0, '2026-05-28T00:00:00', '2026-05-28T00:00:00', 1, 0);

                INSERT INTO AgentRuns (Id, AgentTaskId, Status, StartedAt, TranscriptLogPath)
                VALUES ($rid, $tid, 2, '2026-05-28T00:00:00', '');
                """;
            cmd.Parameters.AddWithValue("$sid", sourceId);
            cmd.Parameters.AddWithValue("$wid", workItemId);
            cmd.Parameters.AddWithValue("$tid", taskId);
            cmd.Parameters.AddWithValue("$rid", runId);
            await cmd.ExecuteNonQueryAsync();
        }

        // Step 2: apply the new migration on top of the seeded row.
        await using (var ctx = new KaguraDbContext(optsBuilder.Options, new EphemeralDataProtectionProvider()))
        {
            await ctx.Database.MigrateAsync();
        }

        // Step 3: legacy row carries forward with WorkItemId backfilled and Kind=TaskAgent.
        await using (var verify = new KaguraDbContext(optsBuilder.Options, new EphemeralDataProtectionProvider()))
        {
            var run = await verify.AgentRuns.SingleAsync(r => r.Id == runId);
            Assert.Equal(workItemId, run.WorkItemId);
            Assert.Equal(AgentRunKind.TaskAgent, run.Kind);
            Assert.Equal(taskId, run.AgentTaskId);
        }
    }
}
