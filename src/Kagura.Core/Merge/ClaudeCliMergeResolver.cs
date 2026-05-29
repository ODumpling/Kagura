using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kagura.Core.Merge;

public class MergeResolverOptions
{
    public string ClaudeBinary { get; set; } = "claude";
    public string? Model { get; set; }
}

public class ClaudeCliMergeResolver : IMergeConflictResolver
{
    /// <summary>
    /// Built-in MergeResolver prompt template. Per ADR 0002 this is the default a Source
    /// resolves at Agent spawn time when it hasn't customised its own MergeResolver prompt;
    /// per-Source overrides are tracked separately and intentionally out of scope here.
    /// Placeholders: <c>{{TASK_TITLE}}</c>, <c>{{SUBMIT_TOOL}}</c>.
    /// </summary>
    public const string DefaultPromptTemplate =
        """
        You are a git merge-conflict resolver. The working directory is a git worktree
        in the middle of a `git merge` that hit conflicts while integrating the task
        below into its parent work-item branch. Your only job is to resolve those
        conflicts and finalize the merge.

        Task: {{TASK_TITLE}}

        Rules:
        - Use `git status` and `git diff` to understand the conflicts.
        - For each conflicted file, choose the resolution that preserves both branches'
          intent. Prefer combining when both sides contribute meaningful changes.
        - Do NOT touch files that are not currently conflicted.
        - Do NOT run `git merge --abort` or otherwise discard the in-progress merge.
        - When all files are resolved: `git add -A` then `git commit --no-edit` to
          finalize the merge.
        - If you cannot resolve safely (semantic conflict, opposing edits, unclear
          intent), STOP. Do not commit. Do not abort. Leave the merge in-progress for a
          human.

        When you are finished — either because you committed the merge or because you
        decided to abandon — call the MCP tool `{{SUBMIT_TOOL}}` with an argument
        shaped as:

        {
          "resolved": true | false,
          "notes": "1-3 sentences describing what you did or why you gave up"
        }

        Set `resolved` true ONLY after `git commit` finalized the merge; otherwise set
        it false. Calling the tool is what hands the verdict back to Kagura — do not
        print the JSON to stdout, do not create any files documenting the result, and
        do not edit any non-conflicted files. After the tool call succeeds, exit
        cleanly.
        """;

    // Legacy one-shot SystemPrompt used when no MergeResolverAgentContext is supplied
    // (e.g. tests with a stub resolver, or callers that never set up an AgentRun).
    private const string LegacySystemPrompt =
        """
        You are a git merge-conflict resolver. The working directory is a git worktree
        in the middle of a `git merge` that hit conflicts. Your only job is to resolve
        those conflicts and finalize the merge.

        Rules:
        - Use `git status` and `git diff` to understand the conflicts.
        - For each conflicted file, choose the resolution that preserves both branches'
          intent. Prefer combining when both sides contribute meaningful changes.
        - Do NOT touch files that are not currently conflicted.
        - Do NOT run `git merge --abort` or otherwise discard the in-progress merge.
        - When all files are resolved: `git add -A` then `git commit --no-edit` to finalize.
        - If you cannot resolve safely (semantic conflict, opposing edits, unclear intent),
          STOP. Do not commit. Do not abort. Leave the merge in-progress for a human.

        Reply with ONLY a JSON object as your final output, no prose, no markdown fences:
        {"success": true|false, "notes": "1-3 sentences describing what you did or why you gave up"}
        """;

    private const string SystemPrompt = DefaultPromptTemplate;

    private readonly MergeResolverOptions _options;
    private readonly MergeResolverAgentContext _context;
    private readonly Lazy<IAgentRunner> _runner;
    private readonly ILogger<ClaudeCliMergeResolver> _log;

    /// <summary>
    /// <see cref="IAgentRunner"/> is wrapped in <see cref="Lazy{T}"/> because the production DI
    /// graph has a cycle on paper: <c>GitService → IMergeConflictResolver → IAgentRunner → GitService</c>.
    /// The cycle is benign in practice — the resolver only touches IAgentRunner inside
    /// <see cref="ResolveAsync"/>, never during construction — but the eager DI graph still
    /// needs an indirection to break it.
    /// </summary>
    public ClaudeCliMergeResolver(
        IOptions<MergeResolverOptions> options,
        MergeResolverAgentContext context,
        Lazy<IAgentRunner> runner,
        ILogger<ClaudeCliMergeResolver> log)
    {
        _options = options.Value;
        _context = context;
        _runner = runner;
        _log = log;
    }

    public async Task<MergeResolutionResult> ResolveAsync(
        string worktreePath, string taskTitle, CancellationToken ct = default)
    {
        // Per ADR 0001: when invoked inside the Agent kickoff path (GitService.MergeTaskBranchAsync
        // populates the context immediately before calling), spawn a PTY MergeResolver Agent
        // in the WorkItem's merge worktree and block on its MCP submission. Without a context
        // we fall back to the legacy one-shot `claude -p` invocation so the strings-only
        // IMergeConflictResolver interface remains usable from places that don't yet
        // populate the ambient.
        if (_context.IsSet)
            return await ResolveViaAgentAsync(worktreePath, taskTitle, ct);

        return await ResolveViaLegacyCliAsync(worktreePath, taskTitle, ct);
    }

    private async Task<MergeResolutionResult> ResolveViaAgentAsync(
        string worktreePath, string taskTitle, CancellationToken ct)
    {
        var wi = _context.WorkItem!;
        var runId = _context.RunId;

        // The kickoff hook (see MergeResolverKickoffService) renders the prompt and snapshots
        // it onto AgentRun.PromptText inside its own scope before populating the context.
        // The resolver re-renders here only as a defence in depth — if the context didn't
        // include a pre-rendered prompt we still pass something coherent to the Agent.
        var prompt = _context.Prompt ?? RenderPrompt(taskTitle);

        // Per CONTEXT.md → "Agent working directory": MergeResolver runs in the WorkItem's
        // merge worktree — that's where the conflicting state lives. GitService has already
        // ensured the worktree exists and started the merge that hit conflicts; we just
        // hand its path down as the Agent's cwd.
        _log.LogInformation(
            "Spawning MergeResolver Agent for work item {WorkItemId} in merge worktree {Cwd}",
            wi.Id, worktreePath);

        try
        {
            var submission = await _runner.Value.StartAndAwaitResultAsync<MergeResolutionSubmission>(
                runId, wi, Role.MergeResolver, prompt, worktreePath, ct);

            _log.LogInformation(
                "MergeResolver verdict for '{Title}': resolved={Resolved}",
                taskTitle, submission.Resolved);
            return new MergeResolutionResult(submission.Resolved, submission.Notes);
        }
        catch (AgentInterruptedException)
        {
            // User stopped the Agent mid-flight — surface as an unresolved verdict so the
            // upstream merge path leaves the worktree in-conflict for manual handling.
            // Per CONTEXT.md "Stop vs Cancel" the orchestrator will also halt at this point.
            throw;
        }
        catch (AgentSubmissionMissingException ex)
        {
            _log.LogWarning(
                "MergeResolver Agent for '{Title}' exited without submitting (exit code {ExitCode})",
                taskTitle, ex.ExitCode);
            return new MergeResolutionResult(
                Success: false,
                Notes: $"MergeResolver agent exited without submitting (exit code {ex.ExitCode?.ToString() ?? "null"}).");
        }
    }

    public static string RenderPrompt(string taskTitle) =>
        DefaultPromptTemplate
            .Replace("{{TASK_TITLE}}", taskTitle)
            .Replace("{{SUBMIT_TOOL}}", Role.MergeResolver.McpSubmitToolName());

    // ---------------- Legacy one-shot fallback ----------------

    private async Task<MergeResolutionResult> ResolveViaLegacyCliAsync(
        string worktreePath, string taskTitle, CancellationToken ct)
    {
        var userPrompt =
            $"""
             Task: {taskTitle}

             A `git merge` of this task's branch into the work-item branch hit conflicts.
             Resolve them and finalize the merge as instructed in the system prompt.
             """;

        var psi = new ProcessStartInfo
        {
            FileName = _options.ClaudeBinary,
            WorkingDirectory = worktreePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(userPrompt);
        psi.ArgumentList.Add("--append-system-prompt");
        psi.ArgumentList.Add(LegacySystemPrompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("acceptEdits");
        psi.ArgumentList.Add("--allowedTools");
        psi.ArgumentList.Add("Bash,Read,Edit,Write,Glob,Grep");
        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(_options.Model);
        }

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var _ = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _log.LogWarning("claude CLI exited {Exit} during merge resolution. stderr: {Stderr}",
                process.ExitCode, stderr.ToString().Trim());
            return new MergeResolutionResult(false, $"Resolver process exited with code {process.ExitCode}");
        }

        ClaudeCliResult? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ClaudeCliResult>(stdout.ToString(), JsonOpts);
        }
        catch (Exception ex)
        {
            return new MergeResolutionResult(false, $"Could not parse resolver JSON envelope: {ex.Message}");
        }
        if (envelope is null || envelope.IsError || string.IsNullOrWhiteSpace(envelope.Result))
            return new MergeResolutionResult(false, $"Resolver returned error envelope (subtype={envelope?.Subtype})");

        try
        {
            var json = ExtractJsonObject(envelope.Result);
            var verdict = JsonSerializer.Deserialize<MergeResolutionResult>(json, JsonOpts)
                ?? throw new InvalidOperationException("Could not parse resolver verdict JSON");
            _log.LogInformation("Merge resolver verdict (legacy path) for '{Title}': success={Success}", taskTitle, verdict.Success);
            return verdict;
        }
        catch (Exception ex)
        {
            return new MergeResolutionResult(false, $"Resolver produced no valid verdict: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"Resolver response did not contain a JSON object. Got: {text}");
        return text[start..(end + 1)];
    }

    private sealed record ClaudeCliResult(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("subtype")] string? Subtype,
        [property: JsonPropertyName("is_error")] bool IsError);
}
