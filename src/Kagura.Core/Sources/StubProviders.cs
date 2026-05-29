using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kagura.Core.Domain;
using Kagura.Core.Git;

namespace Kagura.Core.Sources;

public class AzureDevOpsIssueProvider : IIssueProvider
{
    public const string HttpClientName = "azuredevops";

    internal const string DefaultWiql =
        "SELECT [System.Id], [System.Title], [System.Description], [System.Tags] " +
        "FROM WorkItems " +
        "WHERE [System.State] NOT IN ('Closed','Done','Removed') " +
        "AND [System.AssignedTo] = @Me";

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;

    public AzureDevOpsIssueProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public SourceType Type => SourceType.AzureDevOps;

    public async Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default)
    {
        var cfg = JsonSerializer.Deserialize<AzureDevOpsConfig>(source.ConfigJson, JsonOpts)
            ?? throw new InvalidOperationException($"Source '{source.Name}' has empty Azure DevOps config");

        if (string.IsNullOrWhiteSpace(cfg.Organization))
            throw new InvalidOperationException($"Source '{source.Name}' is missing the Azure DevOps organization");
        if (string.IsNullOrWhiteSpace(cfg.Project))
            throw new InvalidOperationException($"Source '{source.Name}' is missing the Azure DevOps project");

        var query = string.IsNullOrWhiteSpace(cfg.Query) ? DefaultWiql : cfg.Query!;
        var org = cfg.Organization.Trim();
        var project = cfg.Project.Trim();

        using var http = _httpClientFactory.CreateClient(HttpClientName);
        if (http.BaseAddress is null)
            http.BaseAddress = new Uri("https://dev.azure.com/");
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(cfg.Pat))
        {
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + cfg.Pat));
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        var wiqlUrl = $"{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/wit/wiql?api-version=7.0";
        using var wiqlContent = new StringContent(
            JsonSerializer.Serialize(new { query }, JsonOpts),
            Encoding.UTF8,
            "application/json");

        using var wiqlResp = await http.PostAsync(wiqlUrl, wiqlContent, ct);
        if (!wiqlResp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(wiqlResp, ct);
            throw new HttpRequestException(
                $"Azure DevOps WIQL request failed ({(int)wiqlResp.StatusCode} {wiqlResp.ReasonPhrase}) for source '{source.Name}': {body}");
        }

        WiqlResponse? wiql;
        try
        {
            wiql = await wiqlResp.Content.ReadFromJsonAsync<WiqlResponse>(JsonOpts, ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Azure DevOps WIQL response for source '{source.Name}' was not valid JSON", ex);
        }

        if (wiql is null)
            throw new InvalidOperationException($"Azure DevOps WIQL response for source '{source.Name}' was empty");

        var ids = (wiql.WorkItems ?? new List<WiqlWorkItemRef>())
            .Select(w => w.Id)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return Array.Empty<FetchedIssue>();

        var results = new List<FetchedIssue>(ids.Count);
        const int batchSize = 200; // ADO caps the workitems batch at 200 ids
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = ids.Skip(i).Take(batchSize);
            var idsParam = string.Join(',', batch);
            var itemsUrl =
                $"{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/wit/workitems" +
                $"?ids={idsParam}&$expand=fields&api-version=7.0";

            using var itemsResp = await http.GetAsync(itemsUrl, ct);
            if (!itemsResp.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(itemsResp, ct);
                throw new HttpRequestException(
                    $"Azure DevOps work-items request failed ({(int)itemsResp.StatusCode} {itemsResp.ReasonPhrase}) for source '{source.Name}': {body}");
            }

            WorkItemsResponse? items;
            try
            {
                items = await itemsResp.Content.ReadFromJsonAsync<WorkItemsResponse>(JsonOpts, ct);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Azure DevOps work-items response for source '{source.Name}' was not valid JSON", ex);
            }

            foreach (var item in items?.Value ?? new List<AdoWorkItem>())
            {
                var fields = item.Fields ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                var title = GetStringField(fields, "System.Title") ?? string.Empty;
                var description = GetStringField(fields, "System.Description") ?? string.Empty;
                var tagsRaw = GetStringField(fields, "System.Tags");
                var labels = NormaliseTags(tagsRaw);
                var url = BuildPortalUrl(org, project, item.Id) ?? item.Url;

                results.Add(new FetchedIssue(
                    ExternalId: item.Id.ToString(),
                    Title: title,
                    Body: description,
                    Url: url,
                    Labels: labels));
            }
        }

        return results;
    }

    internal static string? NormaliseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // ADO returns tags as "foo; bar; baz" — emit comma-separated to match the GitHub provider.
        var parts = raw
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToArray();
        return parts.Length == 0 ? null : string.Join(',', parts);
    }

    internal static string? BuildPortalUrl(string org, string project, int id)
    {
        if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(project) || id <= 0) return null;
        return $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}";
    }

    private static string? GetStringField(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.ToString(),
        };
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return string.Empty; }
    }

    private sealed record WiqlResponse(
        [property: JsonPropertyName("workItems")] List<WiqlWorkItemRef>? WorkItems);

    private sealed record WiqlWorkItemRef(int Id, string? Url);

    private sealed record WorkItemsResponse(List<AdoWorkItem>? Value);

    private sealed record AdoWorkItem(
        int Id,
        string? Url,
        Dictionary<string, JsonElement>? Fields);
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
