using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    /// Built-in MergeResolver prompt. Surfaced as the lazy default in
    /// <see cref="Kagura.Core.Agents.RolePromptDefaults"/> so the Source's "Prompts" tab can
    /// render it even though MergeResolver hasn't been migrated to the PTY-Agent path yet
    /// (that's #70's job). When MergeResolver is migrated this constant becomes the template
    /// resolved by <c>PromptResolver</c> at Agent spawn time.
    /// </summary>
    public const string DefaultPromptTemplate =
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
    private readonly ILogger<ClaudeCliMergeResolver> _log;

    public ClaudeCliMergeResolver(IOptions<MergeResolverOptions> options, ILogger<ClaudeCliMergeResolver> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<MergeResolutionResult> ResolveAsync(
        string worktreePath, string taskTitle, CancellationToken ct = default)
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
        psi.ArgumentList.Add(SystemPrompt);
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
            _log.LogInformation("Merge resolver verdict for '{Title}': success={Success}", taskTitle, verdict.Success);
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
