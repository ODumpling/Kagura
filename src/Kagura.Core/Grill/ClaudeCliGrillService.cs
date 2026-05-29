using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Agents;
using Kagura.Core.Agents.Mcp;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Triage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kagura.Core.Grill;

public class GrillOptions
{
    public string ClaudeBinary { get; set; } = "claude";
    public string? Model { get; set; }
}

public class ClaudeCliGrillService : IGrillService
{
    /// <summary>
    /// Built-in Grill prompt. Surfaced as the lazy default in
    /// <see cref="Kagura.Core.Agents.RolePromptDefaults"/> so the Source's "Prompts" tab can
    /// render it even though Grill hasn't been migrated to the PTY-Agent path yet
    /// (that's #66's job). When Grill is migrated this constant becomes the template
    /// resolved by <c>PromptResolver</c> at Agent spawn time.
    /// </summary>
    // Adapted from the grill-me skill: interview one question at a time, each with a
    // "My take:" recommendation, until the issue is fleshed out enough to act on.
    public const string DefaultPromptTemplate =
        """
        You are interviewing the user about a software work item that was imported from a tracker
        and needs more detail before it can be acted on. Your job is to grill them relentlessly
        until the work item is well understood.

        Rules:
        - Ask ONE question at a time. Wait for the user's reply before moving on.
        - Every question MUST include your recommended answer in the form "My take: …" with the
          reason. Never ask an open question without offering your own take.
        - Resolve dependencies in order. Don't ask question B if it only matters after A is settled.
        - Refer back to anything the user has already shared (the work item body, earlier replies).
          Don't make them repeat themselves.
        - When the decision tree forks ("if we go with X, then…"), name the branch you're on out loud.
        - Push back if the user's answer contradicts something they said earlier. Surface the conflict.

        Cover at least these axes before you stop: goal & success criteria, scope boundaries,
        assumptions, constraints, alternatives considered, risks & failure modes, stakeholders,
        trade-offs, rollback/reversibility, and the next concrete step. Skip an axis only when it
        is genuinely irrelevant and say so explicitly.

        Stop when every branch has a resolved answer (or an explicit "defer until X"), the user
        has stopped offering new information, or remaining open questions are smaller than the
        cost of resolving them. When you stop, summarise the shared understanding as a short
        bulleted recap.

        Output: write only your next message to the user as plain markdown. No JSON, no preamble,
        no role labels.
        """;

    private const string LegacySynthesizeSystemPrompt =
        """
        You are rewriting a software work item description from scratch, using the grilling
        transcript between a user and an interviewer to produce a complete, actionable write-up.

        The write-up should replace the original imported issue body. Include, where the
        transcript supports it:
        - Goal and success criteria
        - Scope (in / out)
        - Key assumptions
        - Constraints
        - Alternatives considered and why rejected
        - Risks and failure modes
        - Stakeholders / decision-makers (if relevant)
        - Trade-offs
        - Rollback / reversibility
        - Concrete next step

        Output ONLY the markdown body for the work item. No preamble like "Here's the write-up",
        no JSON, no fenced markdown wrapper. Use `##` for section headings. Be precise and
        terse — every sentence should carry weight.
        """;

    /// <summary>
    /// Built-in Grill synthesis prompt template used when the service runs as a PTY Agent.
    /// Mirrors the Triage default-prompt shape: the body of the message is the work item
    /// context + transcript, and the last paragraph teaches Claude to deliver its result
    /// via the MCP submission tool rather than printing it.
    /// Placeholders: <c>{{TITLE}}</c>, <c>{{LABELS}}</c>, <c>{{ORIGINAL_BODY}}</c>,
    /// <c>{{TRANSCRIPT}}</c>, <c>{{SUBMIT_TOOL}}</c>.
    /// </summary>
    public const string DefaultSynthesizePromptTemplate =
        """
        You are rewriting a software work item description from scratch, using the grilling
        transcript between a user and an interviewer to produce a complete, actionable write-up.

        The write-up replaces the original imported issue body. Include, where the transcript
        supports it:
        - Goal and success criteria
        - Scope (in / out)
        - Key assumptions
        - Constraints
        - Alternatives considered and why rejected
        - Risks and failure modes
        - Stakeholders / decision-makers (if relevant)
        - Trade-offs
        - Rollback / reversibility
        - Concrete next step

        Use `##` for section headings. Be precise and terse — every sentence should carry weight.

        # Work item

        Title: {{TITLE}}

        Labels: {{LABELS}}

        Original body:
        {{ORIGINAL_BODY}}

        # Grilling transcript

        {{TRANSCRIPT}}

        # Delivering the result

        When the rewritten body is ready, call the MCP tool `{{SUBMIT_TOOL}}` with an argument
        shaped as:

        {
          "body": "<the rewritten markdown body>"
        }

        Calling the tool is what hands the result back to Kagura — do not print the markdown to
        stdout, do not create any files, do not edit the working tree. After the tool call
        succeeds, exit cleanly.
        """;

    private readonly GrillOptions _options;
    private readonly GrillAgentContext _context;
    private readonly IAgentRunner _runner;
    private readonly GitService _git;
    private readonly IPromptSnapshotSink _promptSink;
    private readonly ILogger<ClaudeCliGrillService> _log;

    public ClaudeCliGrillService(
        IOptions<GrillOptions> options,
        GrillAgentContext context,
        IAgentRunner runner,
        GitService git,
        IPromptSnapshotSink promptSink,
        ILogger<ClaudeCliGrillService> log)
    {
        _options = options.Value;
        _context = context;
        _runner = runner;
        _git = git;
        _promptSink = promptSink;
        _log = log;
    }

    public Task<string> RespondAsync(
        string workItemTitle,
        string workItemBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct = default)
    {
        // Per-turn interview replies are not a typed-result submission — they are
        // conversational text that the chat UI appends as the next assistant comment.
        // RespondAsync therefore stays on the legacy one-shot path; the Agent migration
        // covers the terminal SynthesizeAsync step that produces the refined body.
        var userPrompt =
            $"""
             # Work item
             Title: {workItemTitle}

             Labels: {labels ?? "(none)"}

             Body:
             {(string.IsNullOrWhiteSpace(workItemBody) ? "(empty)" : workItemBody)}

             # Conversation so far
             {FormatHistory(history)}

             # Your turn
             Write your next message to the user. One question, with your "My take:" line.
             If the user's latest reply finished the grilling, write the summary recap instead.
             """;

        return InvokeClaudeAsync(DefaultPromptTemplate, userPrompt, ct);
    }

    public async Task<string> SynthesizeAsync(
        string workItemTitle,
        string originalBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct = default)
    {
        // Per ADR 0001: when invoked inside the Agent kickoff path, spawn a PTY Grill Agent
        // and block on its MCP submission. Without a context we fall back to the legacy
        // one-shot `claude -p` invocation so the strings-only interface still works.
        if (_context.IsSet)
            return await SynthesizeViaAgentAsync(workItemTitle, originalBody, labels, history, ct);

        return await SynthesizeViaLegacyCliAsync(workItemTitle, originalBody, labels, history, ct);
    }

    private async Task<string> SynthesizeViaAgentAsync(
        string workItemTitle,
        string originalBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct)
    {
        var wi = _context.WorkItem!;
        var runId = _context.RunId;

        // Per CONTEXT.md → "Agent working directory": Grill runs in the Source's scratch
        // worktree on detached HEAD at the default branch. Refresh on every spawn.
        var cwd = await _git.EnsureScratchWorktreeAsync(wi.Source, ct);

        var prompt = RenderSynthesizePrompt(workItemTitle, originalBody, labels, history);

        // Snapshot the resolved prompt onto AgentRun.PromptText BEFORE spawning so the audit
        // trail is correct even if the PTY crashes immediately. (ADR 0002.)
        await _promptSink.SaveAsync(runId, prompt, ct);

        _log.LogInformation(
            "Spawning Grill Agent for work item {WorkItemId} in scratch worktree {Cwd}",
            wi.Id, cwd);

        var submission = await _runner.StartAndAwaitResultAsync<GrillSubmission>(
            runId, wi, Role.Grill, prompt, cwd, ct);

        if (string.IsNullOrWhiteSpace(submission.Body))
            throw new InvalidOperationException(
                "Grill Agent submitted an empty body via kagura.submit_grill.");

        _log.LogInformation("Grill Agent produced {Chars} chars", submission.Body.Length);
        return submission.Body.Trim();
    }

    public static string RenderSynthesizePrompt(
        string workItemTitle,
        string originalBody,
        string? labels,
        IReadOnlyList<GrillTurn> history)
    {
        return DefaultSynthesizePromptTemplate
            .Replace("{{TITLE}}", workItemTitle)
            .Replace("{{LABELS}}", labels ?? "(none)")
            .Replace("{{ORIGINAL_BODY}}", string.IsNullOrWhiteSpace(originalBody) ? "(empty)" : originalBody)
            .Replace("{{TRANSCRIPT}}", FormatHistory(history))
            .Replace("{{SUBMIT_TOOL}}", Role.Grill.McpSubmitToolName());
    }

    // ---------------- Legacy one-shot fallback ----------------

    private Task<string> SynthesizeViaLegacyCliAsync(
        string workItemTitle,
        string originalBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct)
    {
        var userPrompt =
            $"""
             # Work item
             Title: {workItemTitle}

             Labels: {labels ?? "(none)"}

             Original body:
             {(string.IsNullOrWhiteSpace(originalBody) ? "(empty)" : originalBody)}

             # Grilling transcript
             {FormatHistory(history)}

             # Your turn
             Produce the rewritten work item description in markdown.
             """;

        return InvokeClaudeAsync(LegacySynthesizeSystemPrompt, userPrompt, ct);
    }

    private async Task<string> InvokeClaudeAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.ClaudeBinary,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(userPrompt);
        psi.ArgumentList.Add("--append-system-prompt");
        psi.ArgumentList.Add(systemPrompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
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
            throw new InvalidOperationException(
                $"claude CLI exited with code {process.ExitCode}. stderr: {stderr.ToString().Trim()}");
        }

        var envelope = JsonSerializer.Deserialize<ClaudeCliResult>(stdout.ToString(), JsonOpts)
            ?? throw new InvalidOperationException($"Could not parse claude CLI JSON envelope. stdout: {stdout}");

        if (envelope.IsError || string.IsNullOrWhiteSpace(envelope.Result))
        {
            throw new InvalidOperationException(
                $"claude CLI returned error envelope. subtype={envelope.Subtype} result={envelope.Result}");
        }

        _log.LogInformation("Grill turn produced {Chars} chars", envelope.Result.Length);
        return envelope.Result.Trim();
    }

    private static string FormatHistory(IReadOnlyList<GrillTurn> history)
    {
        if (history.Count == 0) return "(no messages yet)";
        var sb = new StringBuilder();
        foreach (var turn in history)
        {
            var label = turn.Role == WorkItemCommentRole.Assistant ? "Interviewer" : "User";
            sb.Append("## ").Append(label).AppendLine().AppendLine(turn.Content).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record ClaudeCliResult(
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("subtype")] string? Subtype,
        [property: JsonPropertyName("is_error")] bool IsError);
}
