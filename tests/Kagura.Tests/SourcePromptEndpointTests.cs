using System.Net;
using System.Net.Http.Json;
using Kagura.Core.Agents;
using Kagura.Core.Domain;
using Kagura.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Kagura.Tests;

public class SourcePromptEndpointTests : IClassFixture<SourcePromptEndpointTests.AppFactory>
{
    private readonly AppFactory _app;

    public SourcePromptEndpointTests(AppFactory app) => _app = app;

    [Fact]
    public async Task GET_prompts_returns_builtin_defaults_with_isOverride_false()
    {
        var sourceId = await SeedAsync();
        using var client = _app.CreateClient();

        var rows = await client.GetFromJsonAsync<List<RolePromptRow>>($"/api/sources/{sourceId}/prompts");
        Assert.NotNull(rows);
        // All five Roles must surface in the response so the UI can render every textarea.
        Assert.Equal(5, rows!.Count);
        Assert.All(rows, r => Assert.False(r.IsOverride));

        var triage = rows.Single(r => r.Role == Role.Triage);
        Assert.Equal(RolePromptDefaults.For(Role.Triage), triage.PromptText);
    }

    [Fact]
    public async Task PUT_prompt_creates_override_then_GET_marks_it_as_override()
    {
        var sourceId = await SeedAsync();
        using var client = _app.CreateClient();

        var put = await client.PutAsJsonAsync(
            $"/api/sources/{sourceId}/prompts/{(int)Role.Triage}",
            new { promptText = "MY CUSTOM TRIAGE" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var rows = await client.GetFromJsonAsync<List<RolePromptRow>>($"/api/sources/{sourceId}/prompts");
        var triage = rows!.Single(r => r.Role == Role.Triage);
        Assert.True(triage.IsOverride);
        Assert.Equal("MY CUSTOM TRIAGE", triage.PromptText);
        // Other roles still show built-in defaults — overriding Triage doesn't leak.
        Assert.False(rows!.Single(r => r.Role == Role.Task).IsOverride);
    }

    [Fact]
    public async Task PUT_prompt_twice_updates_existing_row_not_duplicates_it()
    {
        var sourceId = await SeedAsync();
        using var client = _app.CreateClient();

        await client.PutAsJsonAsync(
            $"/api/sources/{sourceId}/prompts/{(int)Role.Triage}",
            new { promptText = "v1" });
        await client.PutAsJsonAsync(
            $"/api/sources/{sourceId}/prompts/{(int)Role.Triage}",
            new { promptText = "v2" });

        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var rows = await db.SourcePromptOverrides.Where(o => o.SourceId == sourceId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal("v2", rows[0].PromptText);
    }

    [Fact]
    public async Task DELETE_prompt_removes_override_and_resets_to_default()
    {
        var sourceId = await SeedAsync();
        using var client = _app.CreateClient();

        await client.PutAsJsonAsync(
            $"/api/sources/{sourceId}/prompts/{(int)Role.Triage}",
            new { promptText = "custom" });
        var del = await client.DeleteAsync($"/api/sources/{sourceId}/prompts/{(int)Role.Triage}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var rows = await client.GetFromJsonAsync<List<RolePromptRow>>($"/api/sources/{sourceId}/prompts");
        var triage = rows!.Single(r => r.Role == Role.Triage);
        Assert.False(triage.IsOverride);
        Assert.Equal(RolePromptDefaults.For(Role.Triage), triage.PromptText);
    }

    [Fact]
    public async Task PUT_empty_promptText_returns_400()
    {
        var sourceId = await SeedAsync();
        using var client = _app.CreateClient();

        var resp = await client.PutAsJsonAsync(
            $"/api/sources/{sourceId}/prompts/{(int)Role.Triage}",
            new { promptText = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private async Task<Guid> SeedAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
        var source = new Source { Name = "src-" + Guid.NewGuid(), LocalRepoPath = "/tmp/repo" };
        db.Sources.Add(source);
        await db.SaveChangesAsync();
        return source.Id;
    }

    private record RolePromptRow(Role Role, string PromptText, bool IsOverride, DateTime? UpdatedAt);

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        private readonly string _connStr = $"Data Source=src-prompt-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        private readonly SqliteConnection _keepAlive;

        public AppFactory()
        {
            _keepAlive = new SqliteConnection(_connStr);
            _keepAlive.Open();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<KaguraDbContext>>();
                services.RemoveAll<KaguraDbContext>();
                services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite(_connStr));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            using var scope = host.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<KaguraDbContext>().Database.EnsureCreated();
            return host;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _keepAlive.Dispose();
        }
    }
}
