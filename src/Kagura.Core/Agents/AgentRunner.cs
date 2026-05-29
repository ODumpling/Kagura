using System.Collections.Concurrent;
using System.Text.Json;
using Kagura.Core.Agents.Mcp;
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
    Task DismissAsync(Guid runId);
    void MarkExitReason(Guid runId, AgentExitReason reason);

    /// <summary>
    /// Spawn a PTY Agent for the given Role + WorkItem and block until either the Agent
    /// calls its MCP submission tool (returns the typed payload) or the PTY exits without
    /// submitting (throws <see cref="AgentSubmissionMissingException"/>) or the user stops
    /// the Agent (throws <see cref="AgentInterruptedException"/>).
    ///
    /// The Agent is registered with <see cref="IAgentBroadcaster"/> so the sidebar sees
    /// it live. The resolved prompt is snapshotted onto the AgentRun row by the caller
    /// before invoking this method (see <c>TriageKickoffService</c>).
    /// </summary>
    Task<TResult> StartAndAwaitResultAsync<TResult>(
        Guid runId,
        WorkItem wi,
        Role role,
        string prompt,
        string cwd,
        CancellationToken ct = default);
}

public class AgentRunner : IAgentRunner
{
    private readonly GitService _git;
    private readonly AgentRunnerOptions _opts;
    private readonly IAgentBroadcaster _broadcaster;
    private readonly IAgentSubmissionCoordinator _submissions;
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
        IAgentSubmissionCoordinator submissions,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        _git = git;
        _opts = opts;
        _broadcaster = broadcaster;
        _submissions = submissions;
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
                runId, task.Id, wi.Id, AgentRunKind.TaskAgent,
                worktreePath, transcriptPath,
                pty, _loggerFactory.CreateLogger<AgentSession>(),
                title: task.Title,
                workItemExternalId: wi.ExternalId);

            _sessions[runId] = session;
            session.OnData += data => _ = _broadcaster.DataAsync(runId, data);
            session.OnExit += code =>
            {
                _ = _broadcaster.ExitAsync(runId, code);
                _ = RecordExitAsync(runId, code);
                // Clean exit → drop from sidebar. Failure → linger so the user can see the
                // failed row and dismiss it with the X affordance (mirrors the non-task path).
                if (code == 0)
                    _ = _broadcaster.AgentDismissedAsync(runId);
                Cleanup(runId);
            };

            await _broadcaster.AgentAppearedAsync(new AgentSidebarEvent(
                RunId: runId,
                WorkItemId: wi.Id,
                SourceId: wi.SourceId,
                SourceName: wi.Source?.Name ?? "",
                WorkItemTitle: wi.Title,
                WorkItemExternalId: wi.ExternalId,
                Kind: AgentRunKind.TaskAgent,
                StatusLine: DefaultStatusLineFor(Role.Task),
                StartedAt: session.StartedAt,
                TaskId: task.Id,
                TaskTitle: task.Title));

            _log.LogInformation("Started agent run {RunId} for task {TaskId} in {Cwd}", runId, task.Id, worktreePath);
            return session;
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    public async Task<TResult> StartAndAwaitResultAsync<TResult>(
        Guid runId,
        WorkItem wi,
        Role role,
        string prompt,
        string cwd,
        CancellationToken ct = default)
    {
        if (role == Role.Task)
            throw new ArgumentException(
                "Role.Task uses the StartAsync(WorkItem, AgentTask, ...) path; this overload is for orchestrated non-Task roles.",
                nameof(role));

        var kind = role.ToAgentRunKind();
        var transcriptPath = Path.Combine(
            ResolveHome(_opts.TranscriptsRoot),
            wi.Id.ToString("N"),
            $"{kind}_{runId:N}.log");

        // Per CONTEXT.md → "MCP transport": one URL per Agent. Claude is launched with
        // --mcp-config pointing at this Agent's own URL, so identity is structural.
        var mcpConfig = BuildMcpConfigJson(runId);
        var mcpConfigPath = WriteTempMcpConfig(mcpConfig);

        var options = new PtyOptions
        {
            Name = "xterm-256color",
            Cols = 120,
            Rows = 32,
            Cwd = cwd,
            App = _opts.ClaudeBinary,
            // claude's `--mcp-config <configs...>` is variadic — if it's the last flag
            // before the positional prompt, commander.js eats the prompt as a second
            // config path and claude exits with `ENAMETOOLONG`. Keep `--mcp-config`
            // before a non-variadic flag so the variadic terminates cleanly.
            CommandLine = new[]
            {
                "--mcp-config", mcpConfigPath,
                "--permission-mode", "auto",
                prompt,
            },
            Environment = BuildRoleEnv(wi, role),
        };

        // Register before spawn so the Agent can't outrace us with an instant submission.
        var submissionTask = _submissions.RegisterAsync(runId, ct);

        IPtyConnection? pty;
        try
        {
            pty = await PtyProvider.SpawnAsync(options, ct);
        }
        catch
        {
            _submissions.Fail(runId, new InvalidOperationException("Failed to spawn PTY."));
            TryDeleteMcpConfig(mcpConfigPath);
            throw;
        }

        var session = new AgentSession(
            runId, taskId: Guid.Empty, wi.Id, kind,
            cwd, transcriptPath,
            pty, _loggerFactory.CreateLogger<AgentSession>(),
            title: wi.Title,
            workItemExternalId: wi.ExternalId);

        _sessions[runId] = session;
        var exitTcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);

        session.OnData += data => _ = _broadcaster.DataAsync(runId, data);
        session.OnExit += code =>
        {
            _ = _broadcaster.ExitAsync(runId, code);
            _ = RecordExitAsync(runId, code);
            // Don't call Cleanup here — non-task kinds linger by design for ring-buffer
            // replay. We dismiss them explicitly after a successful submission below.
            exitTcs.TrySetResult(code);
        };

        await _broadcaster.AgentAppearedAsync(new AgentSidebarEvent(
            RunId: runId,
            WorkItemId: wi.Id,
            SourceId: wi.SourceId,
            SourceName: wi.Source?.Name ?? "",
            WorkItemTitle: wi.Title,
            WorkItemExternalId: wi.ExternalId,
            Kind: kind,
            StatusLine: DefaultStatusLineFor(role),
            StartedAt: session.StartedAt));

        _log.LogInformation(
            "Started {Role} agent run {RunId} for work item {WorkItemId} in {Cwd}",
            role, runId, wi.Id, cwd);

        try
        {
            // Race three outcomes: submission arrives, PTY exits, user cancels.
            var winner = await Task.WhenAny(submissionTask, exitTcs.Task);
            ct.ThrowIfCancellationRequested();

            if (winner == submissionTask)
            {
                var payload = await submissionTask;
                _log.LogInformation("Agent run {RunId} submitted via MCP", runId);

                // Successful submission — kill the PTY so the user doesn't see it linger,
                // then auto-dismiss from the sidebar. Mark the exit reason BEFORE disposing
                // so the sink's RecordExitAsync (fired from OnExit) sees CompletedCleanly
                // rather than inferring Crashed from the missing AwaitingReview task signal.
                _exitOverrides.TryAdd(runId, AgentExitReason.CompletedCleanly);
                await session.DisposeAsync();
                _sessions.TryRemove(runId, out _);
                await _broadcaster.AgentDismissedAsync(runId);

                return DeserializePayload<TResult>(payload);
            }

            // PTY exited without submitting — failure case. Don't dismiss; per CONTEXT.md
            // "Agent lifecycle / Failure" the Agent lingers in the sidebar until the user
            // explicitly dismisses it.
            _submissions.Fail(runId, new AgentSubmissionMissingException(runId, exitTcs.Task.Result));
            throw new AgentSubmissionMissingException(runId, exitTcs.Task.Result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User-initiated stop — kill PTY and propagate as the structured exception.
            _exitOverrides.TryAdd(runId, AgentExitReason.KilledByUser);
            _submissions.Fail(runId, new AgentInterruptedException(runId));
            try { await session.DisposeAsync(); } catch { /* best effort */ }
            _sessions.TryRemove(runId, out _);
            await _broadcaster.AgentDismissedAsync(runId);
            throw new AgentInterruptedException(runId);
        }
        finally
        {
            TryDeleteMcpConfig(mcpConfigPath);
        }
    }

    private static TResult DeserializePayload<TResult>(JsonElement payload)
    {
        if (typeof(TResult) == typeof(JsonElement))
            return (TResult)(object)payload;
        var deserialized = payload.Deserialize<TResult>(McpJsonOptions);
        if (deserialized is null)
            throw new InvalidOperationException(
                $"MCP submission payload could not be deserialized into {typeof(TResult).Name}: {payload.GetRawText()}");
        return deserialized;
    }

    private static readonly JsonSerializerOptions McpJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static string DefaultStatusLineFor(Role role) => role switch
    {
        Role.Triage => "Triage — proposing tasks",
        Role.AutoReview => "Auto-review — analyzing diff",
        Role.Grill => "Grill — refining body",
        Role.MergeResolver => "Merge — resolving conflicts",
        Role.Task => "Task — running",
        _ => role.ToString(),
    };

    private string BuildMcpConfigJson(Guid runId)
    {
        // claude --mcp-config expects a JSON file in the standard Claude Code format:
        // { "mcpServers": { "<name>": { "type": "http", "url": "..." } } }
        var url = $"{_opts.ApiBaseUrl.TrimEnd('/')}/mcp/{runId:D}";
        var doc = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["kagura"] = new { type = "http", url },
            },
        };
        return JsonSerializer.Serialize(doc);
    }

    private static string WriteTempMcpConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kagura-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static void TryDeleteMcpConfig(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort cleanup */ }
    }

    private static IDictionary<string, string> BuildRoleEnv(WorkItem wi, Role role)
    {
        var env = new Dictionary<string, string>();
        foreach (System.Collections.DictionaryEntry de in Environment.GetEnvironmentVariables())
            env[(string)de.Key] = (string?)de.Value ?? "";
        env["KAGURA_ROLE"] = role.ToString();
        env["KAGURA_WORK_ITEM_ID"] = wi.Id.ToString();
        env["KAGURA_WORK_ITEM_EXTERNAL_ID"] = wi.ExternalId;
        return env;
    }

    public async Task StopAsync(Guid runId)
    {
        // Task agents are removed on stop (their disk transcript persists for replay).
        // Non-task kinds linger in the registry so their in-memory ring buffer survives
        // until DismissAsync — the PTY is still killed via DisposeAsync.
        if (!_sessions.TryGetValue(runId, out var session)) return;

        // Pre-claim the exit reason BEFORE OnExit fires so the sink sees KilledByUser. Doing
        // this *before* Dispose is critical: DisposeAsync fires OnExit (and thus the
        // fire-and-forget RecordExitAsync) before it returns, and we need the override to be
        // visible to that task.
        _exitOverrides.TryAdd(runId, AgentExitReason.KilledByUser);

        // If an orchestrated Agent was awaiting an MCP submission, surface as interrupted.
        // Idempotent — Fail returns false if nothing was waiting.
        _submissions.Fail(runId, new AgentInterruptedException(runId));

        if (session.Kind == AgentRunKind.TaskAgent)
        {
            if (_sessions.TryRemove(runId, out _))
            {
                await session.DisposeAsync();
                _slots.Release();
            }
        }
        else
        {
            await session.DisposeAsync();
        }

        // Per issue #70 (Stop-halts-Ralph): explicitly drive the sink synchronously after
        // the PTY is torn down so the WorkItem's RalphLoop state is updated before this
        // method returns. The DisposeAsync path also kicks off a fire-and-forget
        // RecordExitAsync via OnExit, but that runs on a background continuation and may
        // not finish before the calling endpoint writes its own row updates — leaving a
        // window where the sink sees an already-Killed run and skips the halt. Running it
        // inline here guarantees the halt is visible the moment Stop returns to the caller.
        await RecordExitAsync(runId, session.ExitCode);
    }

    public async Task DismissAsync(Guid runId)
    {
        if (!_sessions.TryRemove(runId, out var session)) return;
        if (session.Alive)
        {
            // Re-insert so callers can stop first; we don't kill via Dismiss.
            _sessions[runId] = session;
            throw new InvalidOperationException("Cannot dismiss a live session; stop it first.");
        }
        await session.DisposeAsync();
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
        if (!_sessions.TryGetValue(runId, out var session)) return;

        // Non-task-agent kinds linger in the registry after exit so their ring buffer is
        // available for replay until DismissAsync removes them.
        if (session.Kind != AgentRunKind.TaskAgent) return;

        if (_sessions.TryRemove(runId, out session))
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
