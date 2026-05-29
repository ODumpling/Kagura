using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Domain;
using Kagura.Core.Git;

namespace Kagura.Core.Sources;

public class AzureDevOpsIssueProvider : IIssueProvider
{
    public SourceType Type => SourceType.AzureDevOps;
    public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default) =>
        throw new NotImplementedException("Azure DevOps provider not yet implemented");
}

/// <summary>
/// Minimal seam over <see cref="ProcessRunner"/> so the Beads provider can be unit-tested
/// without launching real <c>bd</c> processes. Kept inside this file to localise the surface
/// area — there is no broader process-runner abstraction in the codebase yet.
/// </summary>
public interface IBeadsProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory, CancellationToken ct);
}

internal sealed class DefaultBeadsProcessRunner : IBeadsProcessRunner
{
    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory, CancellationToken ct)
        => ProcessRunner.RunAsync(fileName, args, workingDirectory, ct);
}

public class BeadsIssueProvider : IIssueProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IBeadsProcessRunner _runner;

    public BeadsIssueProvider() : this(new DefaultBeadsProcessRunner()) { }

    [EditorBrowsable(EditorBrowsableState.Never)] // test/DI seam
    public BeadsIssueProvider(IBeadsProcessRunner runner)
    {
        _runner = runner;
    }

    public SourceType Type => SourceType.Beads;

    public async Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.LocalRepoPath))
            throw new InvalidOperationException($"Beads source '{source.Name}' has no LocalRepoPath configured");

        var cfg = string.IsNullOrWhiteSpace(source.ConfigJson)
            ? new BeadsConfig()
            : JsonSerializer.Deserialize<BeadsConfig>(source.ConfigJson, JsonOpts) ?? new BeadsConfig();

        var status = string.IsNullOrWhiteSpace(cfg.Status) ? "open" : cfg.Status!;

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                "bd",
                new[] { "list", "--status", status, "--json" },
                source.LocalRepoPath,
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to launch `bd` for source '{source.Name}'. Ensure the Beads CLI is installed and on PATH.",
                ex);
        }

        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            throw new InvalidOperationException(
                $"`bd list` failed for source '{source.Name}' (exit {result.ExitCode}). Is '{source.LocalRepoPath}' initialised with `bd init`? Detail: {detail.Trim()}");
        }

        List<BdIssue>? issues;
        try
        {
            issues = JsonSerializer.Deserialize<List<BdIssue>>(result.Stdout, JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"`bd list` for source '{source.Name}' returned malformed JSON: {ex.Message}",
                ex);
        }

        if (issues is null) return Array.Empty<FetchedIssue>();

        var fetched = new List<FetchedIssue>(issues.Count);
        foreach (var issue in issues)
        {
            if (string.IsNullOrWhiteSpace(issue.Id)) continue;
            var labels = issue.Labels is { Count: > 0 }
                ? string.Join(',', issue.Labels.Where(l => !string.IsNullOrWhiteSpace(l)))
                : null;
            fetched.Add(new FetchedIssue(
                ExternalId: issue.Id!,
                Title: issue.Title ?? string.Empty,
                Body: issue.Description ?? string.Empty,
                Url: null,
                Labels: string.IsNullOrEmpty(labels) ? null : labels));
        }
        return fetched;
    }

    private sealed record BdIssue(
        string? Id,
        string? Title,
        [property: JsonPropertyName("description")] string? Description,
        List<string>? Labels);
}
