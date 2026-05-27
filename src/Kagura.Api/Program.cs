using Kagura.Api.Endpoints;
using Kagura.Api.HostedServices;
using Kagura.Api.Hubs;
using Kagura.Core.Agents;
using Kagura.Core.Git;
using Kagura.Core.Merge;
using Kagura.Core.Review;
using Kagura.Core.Sources;
using Kagura.Core.Triage;
using Kagura.Data;
using Kagura.Data.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var devflow = builder.Configuration.GetSection("Devflow");
var dbPath = ResolvePath(devflow["DbPath"] ?? "~/.devflow/kagura.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDataProtection()
    .SetApplicationName("Kagura")
    .PersistKeysToFileSystem(new DirectoryInfo(ResolvePath("~/.devflow/keys")));

builder.Services.AddDbContext<KaguraDbContext>(opt => opt.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IIssueProvider, MarkdownIssueProvider>();
builder.Services.AddSingleton<IIssueProvider, GitHubIssueProvider>();
builder.Services.AddSingleton<IIssueProvider, AzureDevOpsIssueProvider>();
builder.Services.AddSingleton<IIssueProvider, BeadsIssueProvider>();
builder.Services.AddSingleton<IIssueProviderFactory, IssueProviderFactory>();
builder.Services.AddScoped<SourceSyncService>();

builder.Services.Configure<TriageOptions>(opt =>
{
    opt.ClaudeBinary = devflow["ClaudeBinary"] ?? "claude";
    opt.Model = builder.Configuration["Triage:Model"];
});
builder.Services.AddScoped<ITriageService, ClaudeCliTriageService>();

builder.Services.Configure<ReviewOptions>(opt =>
{
    opt.ClaudeBinary = devflow["ClaudeBinary"] ?? "claude";
    opt.Model = builder.Configuration["Review:Model"];
});
builder.Services.AddScoped<IReviewService, ClaudeCliReviewService>();

builder.Services.Configure<MergeResolverOptions>(opt =>
{
    opt.ClaudeBinary = devflow["ClaudeBinary"] ?? "claude";
    opt.Model = builder.Configuration["MergeResolver:Model"];
});
builder.Services.AddSingleton<IMergeConflictResolver, ClaudeCliMergeResolver>();

builder.Services.AddSingleton(sp =>
    new GitService(devflow["WorktreesRoot"] ?? "~/.devflow/worktrees",
        sp.GetRequiredService<IMergeConflictResolver>(),
        sp.GetRequiredService<ILogger<GitService>>()));
builder.Services.AddSingleton<MergePreviewService>();
builder.Services.AddSingleton<IPrPreviewService, GitPrPreviewService>();
builder.Services.AddSingleton(new AgentRunnerOptions
{
    MaxConcurrentAgents = devflow.GetValue<int?>("MaxConcurrentAgents") ?? 3,
    ClaudeBinary = devflow["ClaudeBinary"] ?? "claude",
    TranscriptsRoot = devflow["TranscriptsRoot"] ?? "~/.devflow/transcripts",
    ApiBaseUrl = devflow["ApiBaseUrl"] ?? "http://localhost:5253",
    PromptTemplate = devflow["PromptTemplate"] ?? AgentRunnerOptions.DefaultPromptTemplate,
    MaxRunDuration = devflow.GetValue<TimeSpan?>("MaxRunDuration") ?? TimeSpan.FromMinutes(30),
});
builder.Services.AddSingleton<IAgentBroadcaster, SignalRAgentBroadcaster>();
builder.Services.AddSingleton<IAgentRunner, AgentRunner>();
builder.Services.AddScoped<IAgentRunSink, AgentRunSink>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new WorkItemCleanupOptions
{
    Interval = devflow.GetValue<TimeSpan?>("WorkItemCleanup:Interval") ?? TimeSpan.FromHours(1),
    Retention = devflow.GetValue<TimeSpan?>("WorkItemCleanup:Retention") ?? TimeSpan.FromDays(7),
});
builder.Services.AddHostedService<WorkItemCleanupService>();

builder.Services.AddSingleton(new RalphLoopOptions
{
    TickInterval = devflow.GetValue<TimeSpan?>("RalphLoop:TickInterval") ?? TimeSpan.FromSeconds(5),
    MaxRetryAttempts = devflow.GetValue<int?>("RalphLoop:MaxRetryAttempts") ?? 3,
    MaxConcurrentTasksPerWorkItem = devflow.GetValue<int?>("RalphLoop:MaxConcurrentTasksPerWorkItem") ?? 3,
});
builder.Services.AddScoped<RalphLoopDriver>();
builder.Services.AddHostedService<RalphLoopService>();

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
app.MapDefaultEndpoints();
app.MapGet("/", () => Results.Ok(new { app = "Kagura", status = "ok" }));
app.MapSourceEndpoints();
app.MapWorkItemEndpoints();
app.MapTriageEndpoints();
app.MapAgentEndpoints();
app.MapHub<AgentHub>("/hubs/agent");

app.Run();

static string ResolvePath(string path) =>
    path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;

public partial class Program;
