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
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Kagura.Api;

public static class KaguraApiHost
{
    /// <summary>
    /// Boot the Kagura API in-process. The CLI passes a <paramref name="spaFileProvider"/> with the
    /// embedded React bundle so unknown routes fall through to <c>index.html</c>; dev callers
    /// (the AppHost flow) pass <c>null</c> and Vite serves the SPA on its own port.
    /// </summary>
    public const int DefaultPort = 5253;

    public static Task RunAsync(
        string[] args,
        IFileProvider? spaFileProvider = null,
        int? port = null,
        bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Explicit port wins. Otherwise default to :5253 unless something else
        // (Aspire / ASPNETCORE_URLS / launchSettings) already set a URL.
        if (port is int p)
        {
            builder.WebHost.UseUrls($"http://localhost:{p}");
        }
        else if (string.IsNullOrEmpty(builder.Configuration["urls"]) &&
                 string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            builder.WebHost.UseUrls($"http://localhost:{DefaultPort}");
        }

        if (quiet)
        {
            // Suppress the chatty ASP.NET / EF Core defaults; let `Warning` and above through.
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        builder.AddServiceDefaults();

        var devflow = builder.Configuration.GetSection("Devflow");
        var dbPath = ResolvePath(devflow["DbPath"] ?? "~/.devflow/kagura.db");
        var stateDir = Path.GetDirectoryName(dbPath)!;
        var firstRun = !Directory.Exists(stateDir);
        if (firstRun)
        {
            // One-time creation banner — printed before the "Kagura running at" line below.
            Console.WriteLine($"Creating {DisplayPath(stateDir)} (database, keys, worktrees)");
        }
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(Path.Combine(stateDir, "keys"));
        Directory.CreateDirectory(Path.Combine(stateDir, "worktrees"));
        Directory.CreateDirectory(Path.Combine(stateDir, "transcripts"));

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

            // In the Testing environment, the WebApplicationFactory swaps the DbContext for an
            // in-memory SQLite connection; backing up the real on-disk DB is meaningless and
            // would race across parallel test fixtures.
            if (!app.Environment.IsEnvironment("Testing"))
            {
                BackupBeforeMigrate(stateDir, dbPath, db);
            }

            db.Database.Migrate();

            if (!app.Environment.IsEnvironment("Testing"))
            {
                WriteStateFile(stateDir);
            }
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseCors();
        app.MapDefaultEndpoints();
        app.MapSourceEndpoints();
        app.MapWorkItemEndpoints();
        app.MapTriageEndpoints();
        app.MapAgentEndpoints();
        app.MapHub<AgentHub>("/hubs/agent");

        if (spaFileProvider is not null)
        {
            UseSpa(app, spaFileProvider);
        }
        else
        {
            app.MapGet("/", () => Results.Ok(new { app = "Kagura", status = "ok" }));
        }

        if (quiet)
        {
            // Replace the suppressed "Now listening on" line with our own one-liner.
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var addresses = app.Services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
                var url = addresses?.FirstOrDefault() ?? "http://localhost:5253";
                Console.WriteLine($"Kagura running at {url}/");
            });
        }

        return app.RunAsync(cancellationToken);
    }

    private static void UseSpa(WebApplication app, IFileProvider spa)
    {
        var defaultFiles = new DefaultFilesOptions { FileProvider = spa };
        defaultFiles.DefaultFileNames.Clear();
        defaultFiles.DefaultFileNames.Add("index.html");
        app.UseDefaultFiles(defaultFiles);

        app.UseStaticFiles(new StaticFileOptions { FileProvider = spa });

        // SPA fallback: any unmatched non-API route serves index.html.
        app.MapFallback(async context =>
        {
            var file = spa.GetFileInfo("index.html");
            if (!file.Exists)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await using var stream = file.CreateReadStream();
            await stream.CopyToAsync(context.Response.Body);
        });
    }

    private static string ResolvePath(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string DisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.Ordinal)
            ? "~" + path[home.Length..]
            : path;
    }

    private record StateFile(string LastVersion);

    private static StateFile? ReadStateFile(string stateDir)
    {
        var path = Path.Combine(stateDir, "state.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<StateFile>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStateFile(string stateDir)
    {
        var path = Path.Combine(stateDir, "state.json");
        var version = GetInformationalVersion();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new StateFile(version)));
        }
        catch
        {
            // Best-effort: state.json is only used to name future backups; running without it is fine.
        }
    }

    private static string GetInformationalVersion()
    {
        var raw = typeof(KaguraApiHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }

    /// <summary>
    /// If <paramref name="dbPath"/> exists and the EF Core context reports pending migrations,
    /// copy the database to <c>kagura.db.bak-{oldVersion}</c> in <paramref name="stateDir"/>
    /// before letting the caller apply the migrations. Older backups are pruned so only the
    /// 3 most recent remain. If the backup itself fails, the exception is propagated and the
    /// caller skips migration — better to crash visibly than silently corrupt the DB.
    /// </summary>
    private static void BackupBeforeMigrate(string stateDir, string dbPath, KaguraDbContext db)
    {
        if (!File.Exists(dbPath))
        {
            return;
        }

        List<string> pending;
        try
        {
            pending = db.Database.GetPendingMigrations().ToList();
        }
        catch
        {
            // If we can't even enumerate pending migrations, let Migrate() surface the error.
            return;
        }
        if (pending.Count == 0)
        {
            return;
        }

        var oldVersion = ReadStateFile(stateDir)?.LastVersion ?? "unknown";
        var safeVersion = SanitizeForFilename(oldVersion);
        var backupPath = Path.Combine(stateDir, $"kagura.db.bak-{safeVersion}");

        // If a backup at this name already exists (same version, prior failed attempt) keep
        // the older one — it's the more pristine pre-migration snapshot.
        try
        {
            if (!File.Exists(backupPath))
            {
                File.Copy(dbPath, backupPath);
            }
        }
        catch (IOException)
        {
            // Best-effort: if the backup itself fails we'd rather start with an unbackuped DB
            // than refuse to boot. Migration may still succeed.
            return;
        }

        PruneBackups(stateDir, keep: 3);
    }

    private static void PruneBackups(string stateDir, int keep)
    {
        var backups = Directory.EnumerateFiles(stateDir, "kagura.db.bak-*")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
        foreach (var old in backups.Skip(keep))
        {
            try { old.Delete(); } catch { /* best effort */ }
        }
    }

    private static string SanitizeForFilename(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = s.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
