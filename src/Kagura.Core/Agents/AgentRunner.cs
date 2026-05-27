using System.Collections.Concurrent;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Kagura.Core.Agents;

public class AgentRunnerOptions
{
    public int MaxConcurrentAgents { get; set; } = 3;
    public string ClaudeBinary { get; set; } = "claude";
    public string TranscriptsRoot { get; set; } = "~/.devflow/transcripts";
}

public interface IAgentRunner
{
    AgentSession? Get(Guid runId);
    IReadOnlyCollection<AgentSession> Active { get; }
    Task<AgentSession> StartAsync(WorkItem wi, AgentTask task, string repoPath, CancellationToken ct = default);
    Task StopAsync(Guid runId);
}

public class AgentRunner : IAgentRunner
{
    private readonly GitService _git;
    private readonly AgentRunnerOptions _opts;
    private readonly IAgentBroadcaster _broadcaster;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    private readonly ILogger<AgentRunner> _log;
    private readonly ILoggerFactory _loggerFactory;

    public AgentRunner(GitService git, AgentRunnerOptions opts, IAgentBroadcaster broadcaster, ILoggerFactory loggerFactory)
    {
        _git = git;
        _opts = opts;
        _broadcaster = broadcaster;
        _slots = new SemaphoreSlim(opts.MaxConcurrentAgents, opts.MaxConcurrentAgents);
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<AgentRunner>();
    }

    public AgentSession? Get(Guid runId) => _sessions.TryGetValue(runId, out var s) ? s : null;

    public IReadOnlyCollection<AgentSession> Active => _sessions.Values.ToList();

    public async Task<AgentSession> StartAsync(WorkItem wi, AgentTask task, string repoPath, CancellationToken ct = default)
    {
        await _slots.WaitAsync(ct);

        try
        {
            var worktreePath = await _git.CreateTaskWorktreeAsync(repoPath, wi, task, ct);

            var runId = Guid.NewGuid();
            var transcriptPath = Path.Combine(
                ResolveHome(_opts.TranscriptsRoot),
                wi.Id.ToString("N"),
                $"{task.Id:N}_{runId:N}.log");

            var options = new PtyOptions
            {
                Name = "xterm-256color",
                Cols = 120,
                Rows = 32,
                Cwd = worktreePath,
                App = _opts.ClaudeBinary,
                CommandLine = Array.Empty<string>(),
                Environment = BuildEnv(task),
            };

            var pty = await PtyProvider.SpawnAsync(options, ct);
            var session = new AgentSession(
                runId, task.Id, wi.Id,
                worktreePath, transcriptPath,
                pty, _loggerFactory.CreateLogger<AgentSession>());

            _sessions[runId] = session;
            session.OnData += data => _ = _broadcaster.DataAsync(runId, data);
            session.OnExit += code =>
            {
                _ = _broadcaster.ExitAsync(runId, code);
                Cleanup(runId);
            };

            _log.LogInformation("Started agent run {RunId} for task {TaskId} in {Cwd}", runId, task.Id, worktreePath);
            return session;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public async Task StopAsync(Guid runId)
    {
        if (!_sessions.TryRemove(runId, out var session)) return;
        await session.DisposeAsync();
        _slots.Release();
    }

    private void Cleanup(Guid runId)
    {
        if (_sessions.TryRemove(runId, out var session))
        {
            _ = session.DisposeAsync().AsTask();
            _slots.Release();
        }
    }

    private static IDictionary<string, string> BuildEnv(AgentTask task)
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
            env[(string)de.Key] = (string?)de.Value ?? "";

        env["KAGURA_TASK_ID"] = task.Id.ToString();
        env["KAGURA_TASK_TITLE"] = task.Title;
        return env;
    }

    private static string ResolveHome(string path) =>
        path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
}
