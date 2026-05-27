using Kagura.Api.Endpoints;
using Kagura.Api.Hubs;
using Kagura.Core.Agents;
using Kagura.Core.Git;
using Kagura.Core.Sources;
using Kagura.Core.Triage;
using Kagura.Data;
using Kagura.Data.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var devflow = builder.Configuration.GetSection("Devflow");
var dbPath = ResolvePath(devflow["DbPath"] ?? "~/.devflow/kagura.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDataProtection()
    .SetApplicationName("Kagura")
    .PersistKeysToFileSystem(new DirectoryInfo(ResolvePath("~/.devflow/keys")));

builder.Services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddSingleton<IIssueProvider, MarkdownIssueProvider>();
builder.Services.AddSingleton<IIssueProvider, GitHubIssueProvider>();
builder.Services.AddSingleton<IIssueProvider, AzureDevOpsIssueProvider>();
builder.Services.AddSingleton<IIssueProvider, BeadsIssueProvider>();
builder.Services.AddSingleton<IIssueProviderFactory, IssueProviderFactory>();
builder.Services.AddScoped<SourceSyncService>();

builder.Services.Configure<TriageOptions>(opt =>
{
    opt.ApiKey = builder.Configuration["Anthropic:ApiKey"] ?? "";
    opt.Model = builder.Configuration["Anthropic:Model"] ?? Anthropic.SDK.Constants.AnthropicModels.Claude46Sonnet;
});
builder.Services.AddScoped<ITriageService, AnthropicTriageService>();

builder.Services.AddSingleton(sp =>
    new GitService(devflow["WorktreesRoot"] ?? "~/.devflow/worktrees",
        sp.GetRequiredService<ILogger<GitService>>()));
builder.Services.AddSingleton(new AgentRunnerOptions
{
    MaxConcurrentAgents = devflow.GetValue<int?>("MaxConcurrentAgents") ?? 3,
    ClaudeBinary = devflow["ClaudeBinary"] ?? "claude",
    TranscriptsRoot = devflow["TranscriptsRoot"] ?? "~/.devflow/transcripts",
});
builder.Services.AddSingleton<IAgentBroadcaster, SignalRAgentBroadcaster>();
builder.Services.AddSingleton<IAgentRunner, AgentRunner>();

builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));
builder.Services.AddSignalR();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<KaguraDbContext>();
    db.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapGet("/", () => Results.Ok(new { app = "Kagura", status = "ok" }));
app.MapSourceEndpoints();
app.MapWorkItemEndpoints();
app.MapTriageEndpoints();
app.MapAgentEndpoints();
app.MapHub<AgentHub>("/hubs/agent");

app.Run();

static string ResolvePath(string path) =>
    path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
