using System.Text.Json;
using System.Text.RegularExpressions;
using Kagura.Core.Domain;

namespace Kagura.Core.Sources;

public partial class MarkdownIssueProvider : IIssueProvider
{
    public SourceType Type => SourceType.Markdown;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<MarkdownConfig>(source.ConfigJson, JsonOpts);
        var issuesPath = string.IsNullOrWhiteSpace(cfg?.IssuesPath)
            ? Path.Combine(source.LocalRepoPath, ".devflow", "issues")
            : cfg!.IssuesPath;

        var issuesDir = Path.IsPathRooted(issuesPath)
            ? issuesPath
            : Path.Combine(source.LocalRepoPath, issuesPath);

        if (!Directory.Exists(issuesDir))
            return Task.FromResult<IReadOnlyList<FetchedIssue>>(Array.Empty<FetchedIssue>());

        var results = new List<FetchedIssue>();
        foreach (var path in Directory.EnumerateFiles(issuesDir, "*.md", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var text = File.ReadAllText(path);
            var (frontmatter, body) = SplitFrontmatter(text);

            var externalId = frontmatter.GetValueOrDefault("id") ?? Path.GetFileNameWithoutExtension(path);
            var title = frontmatter.GetValueOrDefault("title") ?? FirstHeading(body) ?? Path.GetFileNameWithoutExtension(path);
            var labels = frontmatter.GetValueOrDefault("labels");

            results.Add(new FetchedIssue(externalId, title, body.Trim(), null, labels));
        }
        return Task.FromResult<IReadOnlyList<FetchedIssue>>(results);
    }

    [GeneratedRegex(@"^---\s*\n(?<fm>[\s\S]*?)\n---\s*\n(?<body>[\s\S]*)$", RegexOptions.Compiled)]
    private static partial Regex FrontmatterRegex();

    private static (Dictionary<string, string> Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var m = FrontmatterRegex().Match(text);
        if (!m.Success) return (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), text);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in m.Groups["fm"].Value.Split('\n'))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim().Trim('"', '\'');
            dict[key] = value;
        }
        return (dict, m.Groups["body"].Value);
    }

    private static string? FirstHeading(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart('#', ' ').Trim();
            if (trimmed.Length > 0 && line.TrimStart().StartsWith('#')) return trimmed;
        }
        return null;
    }
}
