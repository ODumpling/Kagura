using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Domain;

namespace Kagura.Core.Sources;

public class GitHubIssueProvider : IIssueProvider
{
    public const string HttpClientName = "github";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public GitHubIssueProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public SourceType Type => SourceType.GitHub;

    public async Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<GitHubConfig>(source.ConfigJson, JsonOpts)
            ?? throw new InvalidOperationException($"Source '{source.Name}' has empty GitHub config");

        var (owner, repo) = ParseRepoUrl(cfg.Url)
            ?? throw new InvalidOperationException($"Source '{source.Name}' has an invalid GitHub URL: '{cfg.Url}'");

        using var http = _httpClientFactory.CreateClient(HttpClientName);
        if (http.BaseAddress is null)
            http.BaseAddress = new Uri("https://api.github.com/");
        http.DefaultRequestHeaders.UserAgent.TryParseAdd("Kagura");
        http.DefaultRequestHeaders.Accept.TryParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(cfg.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.Token);

        var labelsParam = string.IsNullOrWhiteSpace(cfg.Labels)
            ? string.Empty
            : $"&labels={Uri.EscapeDataString(cfg.Labels)}";

        const int perPage = 100;
        var results = new List<FetchedIssue>();
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"repos/{owner}/{repo}/issues?state=open&per_page={perPage}&page={page}{labelsParam}";
            using var resp = await http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var batch = await resp.Content.ReadFromJsonAsync<List<GhIssue>>(JsonOpts, ct) ?? new();
            foreach (var issue in batch)
            {
                if (issue.PullRequest is not null) continue;
                var labels = issue.Labels is { Count: > 0 }
                    ? string.Join(',', issue.Labels.Select(l => l.Name).Where(n => !string.IsNullOrEmpty(n)))
                    : null;
                results.Add(new FetchedIssue(
                    ExternalId: issue.Number.ToString(),
                    Title: issue.Title ?? string.Empty,
                    Body: issue.Body ?? string.Empty,
                    Url: issue.HtmlUrl,
                    Labels: string.IsNullOrEmpty(labels) ? null : labels));
            }
            if (batch.Count < perPage) break;
        }

        return results;
    }

    internal static (string Owner, string Repo)? ParseRepoUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        s = s.TrimEnd('/');

        // git@github.com:owner/repo
        if (s.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var colon = s.IndexOf(':');
            if (colon < 0) return null;
            var path = s[(colon + 1)..];
            return SplitOwnerRepo(path);
        }

        // owner/repo shorthand
        if (!s.Contains("://") && !s.StartsWith("github.com", StringComparison.OrdinalIgnoreCase))
            return SplitOwnerRepo(s);

        // https://github.com/owner/repo[/...]
        var withScheme = s.Contains("://") ? s : "https://" + s;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)) return null;
        if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase))
            return null;
        return SplitOwnerRepo(uri.AbsolutePath.Trim('/'));

        static (string, string)? SplitOwnerRepo(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            return (parts[0], parts[1]);
        }
    }

    private sealed record GhIssue(
        long Number,
        string? Title,
        string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        List<GhLabel>? Labels,
        [property: JsonPropertyName("pull_request")] GhPullRequestRef? PullRequest);

    private sealed record GhLabel(string Name);

    private sealed record GhPullRequestRef([property: JsonPropertyName("url")] string? Url);
}
