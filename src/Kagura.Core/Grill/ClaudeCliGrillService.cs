using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Domain;
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
    // Adapted from the grill-me skill: interview one question at a time, each with a
    // "My take:" recommendation, until the issue is fleshed out enough to act on.
    private const string GrillSystemPrompt =
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

    private const string SynthesizeSystemPrompt =
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

    private readonly GrillOptions _options;
    private readonly ILogger<ClaudeCliGrillService> _log;

    public ClaudeCliGrillService(IOptions<GrillOptions> options, ILogger<ClaudeCliGrillService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public Task<string> RespondAsync(
        string workItemTitle,
        string workItemBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct = default)
    {
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

        return InvokeClaudeAsync(GrillSystemPrompt, userPrompt, ct);
    }

    public Task<string> SynthesizeAsync(
        string workItemTitle,
        string originalBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct = default)
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

        return InvokeClaudeAsync(SynthesizeSystemPrompt, userPrompt, ct);
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
