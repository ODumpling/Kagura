using System.Collections.Concurrent;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Porta.Pty;

namespace Kagura.Core.Agents;

public class AgentRunnerOptions
{
    public int MaxConcurrentAgents { get; set; } = 3;
    public string ClaudeBinary { get; set; } = "claude";
    public string TranscriptsRoot { get; set; } = "~/.devflow/transcripts";
    public string ApiBaseUrl { get; set; } = "http://localhost:5253";
    public string PromptTemplate { get; set; } = DefaultPromptTemplate;

    // Hard cap on a single agent run before the Ralph loop kills it and counts it as a failure.
    public TimeSpan MaxRunDuration { get; set; } = TimeSpan.FromMinutes(30);

    public const string DefaultPromptTemplate = """
        # THE TASK

        You have ONE task to complete. Nothing else.

        Task: {{TASK}}
        Branch: {{BRANCH}}

        Do not pick up adjacent work, related cleanup, or anything else you notice while exploring. If it's not required to finish this single task, leave it alone.
        {{PRIOR_ATTEMPTS}}
        # PARENT CONTEXT (read-only)

        Pull the parent work item using `{{VIEW_TASK_COMMAND}}` for context only. If it references a parent PRD, skim that too. Use this to understand scope — not to expand it.

        Recent commits on this repo:

        <recent-commits>

        !`git log -n 10 --format="%H%n%ad%n%B---" --date=short`

        </recent-commits>

        # EXPLORATION

        Explore only the parts of the repo needed to complete this task. Pay attention to tests that touch the code you'll change.

        Stop exploring once you have enough context for this task. Do not survey the whole codebase.

        # EXECUTION

        If applicable, use RGR:

        1. RED: write one test for this task
        2. GREEN: implement just enough to pass
        3. REPEAT until this task is done
        4. REFACTOR only what you touched

        # FEEDBACK LOOPS

        Before committing, run the test and build commands to confirm they pass.

        # COMMIT

        Make a single git commit for this task. The commit message must:

        1. Start with `Kagura:` prefix
        2. State the task completed + parent reference
        3. Key decisions made
        4. Files changed
        5. Blockers or notes for the next iteration

        Keep it concise. One task, one commit.

        # COMPLETION

        If the task is not complete, leave a comment on the parent work item describing what was done and what remains. Do not close it — that happens later.

        Once the task is complete, mark it ready for review:

        `curl -fsS -X POST "{{COMPLETE_URL}}"`

        Then output <promise>COMPLETE</promise> and stop.

        # FINAL RULES

        ONLY WORK ON THIS ONE TASK. Do not start, plan, or commit work for any other task — even if it looks trivial or related.
        """;
}

public interface IAgentRunner
{
    AgentSession? Get(Guid runId);
    IReadOnlyCollection<AgentSession> Active { get; }
    Task<AgentSession> StartAsync(WorkItem wi, AgentTask task, string repoPath, CancellationToken ct = default);
    Task StopAsync(Guid runId);
    void MarkExitReason(Guid runId, AgentExitReason reason);
}

public class AgentRunner : IAgentRunner
{
    private readonly GitService _git;
    private readonly AgentRunnerOptions _opts;
    private readonly IAgentBroadcaster _broadcaster;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, AgentExitReason> _exitOverrides = new();
    private readonly ILogger<AgentRunner> _log;
    private readonly ILoggerFactory _loggerFactory;

    public AgentRunner(
        GitService git,
        AgentRunnerOptions opts,
        IAgentBroadcaster broadcaster,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _git = git;
        _opts = opts;
        _broadcaster = broadcaster;
        _scopeFactory = scopeFactory;
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
            var taskBranch = _git.TaskBranchName(wi, task);

            var runId = Guid.NewGuid();
            var transcriptPath = Path.Combine(
                ResolveHome(_opts.TranscriptsRoot),
                wi.Id.ToString("N"),
                $"{task.Id:N}_{runId:N}.log");

            var prompt = RenderPrompt(wi, task, taskBranch);

            var options = new PtyOptions
            {
                Name = "xterm-256color",
                Cols = 120,
                Rows = 32,
                Cwd = worktreePath,
                App = _opts.ClaudeBinary,
                CommandLine = new[] { "--permission-mode", "auto", prompt },
                Environment = BuildEnv(task),
            };

            var pty = await PtyProvider.SpawnAsync(options, ct);
            var session = new AgentSession(
                runId, task.Id, wi.Id,
                worktreePath, transcriptPath,
                pty, _loggerFactory.CreateLogger<AgentSession>(),
                kind: AgentRunKind.TaskAgent,
                title: task.Title,
                workItemExternalId: wi.ExternalId);

            _sessions[runId] = session;
            session.OnData += data => _ = _broadcaster.DataAsync(runId, data);
            session.OnExit += code =>
            {
                _ = _broadcaster.ExitAsync(runId, code);
                _ = RecordExitAsync(runId, code);
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
        _exitOverrides.TryAdd(runId, AgentExitReason.KilledByUser);
        await session.DisposeAsync();
        _slots.Release();
    }

    public void MarkExitReason(Guid runId, AgentExitReason reason)
    {
        _exitOverrides[runId] = reason;
    }

    private async Task RecordExitAsync(Guid runId, int? exitCode)
    {
        AgentExitReason? overrideReason = _exitOverrides.TryRemove(runId, out var r) ? r : null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sink = scope.ServiceProvider.GetRequiredService<IAgentRunSink>();
            await sink.RecordExitAsync(runId, exitCode, overrideReason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to record agent run exit for {RunId}", runId);
        }
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

    private string RenderPrompt(WorkItem wi, AgentTask task, string branch) =>
        _opts.PromptTemplate
            .Replace("{{TASK}}", task.Description)
            .Replace("{{ISSUE_TITLE}}", task.Title)
            .Replace("{{VIEW_TASK_COMMAND}}", ViewTaskCommand(wi))
            .Replace("{{BRANCH}}", branch)
            .Replace("{{COMPLETE_URL}}", $"{_opts.ApiBaseUrl.TrimEnd('/')}/api/agents/complete/{task.Id}")
            .Replace("{{PRIOR_ATTEMPTS}}", BuildPriorAttemptsSection(task));

    private static string BuildPriorAttemptsSection(AgentTask task)
    {
        if (task.RetryAttempts <= 0) return string.Empty;

        var reason = string.IsNullOrWhiteSpace(task.LastFailureReason)
            ? "unknown"
            : task.LastFailureReason.Trim();
        var attemptNumber = task.RetryAttempts + 1;

        return $"""


            # PRIOR ATTEMPTS

            This is attempt {attemptNumber} of 3. Earlier attempts failed.

            Last failure: {reason}

            The branch has been reset to the work item base. Do not assume any partial work from prior attempts persists. Use the failure note to avoid the same path.
            """;
    }

    private static string ViewTaskCommand(WorkItem wi) => wi.Source.Type switch
    {
        SourceType.GitHub => $"gh issue view {wi.ExternalId}",
        _ => $"echo 'Issue {wi.ExternalId}: {wi.Title.Replace("'", "")}'",
    };

    private static string ResolveHome(string path) =>
        path.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]) : path;
}
