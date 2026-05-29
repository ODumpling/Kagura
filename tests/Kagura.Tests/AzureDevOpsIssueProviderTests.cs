using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kagura.Core.Domain;
using Kagura.Core.Sources;

namespace Kagura.Tests;

public class AzureDevOpsIssueProviderTests
{
    private static Source MakeSource(AzureDevOpsConfig cfg) => new()
    {
        Name = "ado",
        Type = SourceType.AzureDevOps,
        LocalRepoPath = "/tmp/repo",
        ConfigJson = JsonSerializer.Serialize(cfg),
    };

    [Fact]
    public async Task Uses_default_wiql_when_query_is_null()
    {
        var handler = new FakeHandler();
        handler.Enqueue(JsonContent(new
        {
            workItems = new[] { new { id = 42, url = "https://dev.azure.com/_apis/wit/workitems/42" } }
        }));
        handler.Enqueue(JsonContent(new
        {
            value = new[]
            {
                new
                {
                    id = 42,
                    url = "https://dev.azure.com/_apis/wit/workitems/42",
                    fields = new Dictionary<string, object?>
                    {
                        ["System.Title"] = "Hello",
                        ["System.Description"] = "<div>body</div>",
                        ["System.Tags"] = "alpha; beta",
                    },
                }
            }
        }));

        var provider = new AzureDevOpsIssueProvider(new FakeFactory(handler));
        var issues = await provider.FetchIssuesAsync(MakeSource(new AzureDevOpsConfig("acme", "prj", "pat-token")));

        var first = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, first.Method);
        Assert.Contains("/acme/prj/_apis/wit/wiql", first.RequestUri!.AbsolutePath);
        Assert.Contains("api-version=7.0", first.RequestUri.Query);
        var bodyJson = JsonSerializer.Deserialize<JsonElement>(first.SentBody);
        Assert.Equal(
            "SELECT [System.Id], [System.Title], [System.Description], [System.Tags] " +
            "FROM WorkItems " +
            "WHERE [System.State] NOT IN ('Closed','Done','Removed') " +
            "AND [System.AssignedTo] = @Me",
            bodyJson.GetProperty("query").GetString());

        // Auth header
        Assert.NotNull(first.Authorization);
        Assert.Equal("Basic", first.Authorization!.Scheme);
        var decoded = Encoding.ASCII.GetString(Convert.FromBase64String(first.Authorization.Parameter!));
        Assert.Equal(":pat-token", decoded);

        Assert.Single(issues);
        var issue = issues[0];
        Assert.Equal("42", issue.ExternalId);
        Assert.Equal("Hello", issue.Title);
        Assert.Equal("<div>body</div>", issue.Body);
        Assert.Equal("https://dev.azure.com/acme/prj/_workitems/edit/42", issue.Url);
        Assert.Equal("alpha,beta", issue.Labels);
    }

    [Fact]
    public async Task Custom_query_overrides_default()
    {
        var handler = new FakeHandler();
        handler.Enqueue(JsonContent(new { workItems = Array.Empty<object>() }));

        var customQuery = "SELECT [System.Id] FROM WorkItems WHERE [System.Tags] CONTAINS 'kagura'";
        var provider = new AzureDevOpsIssueProvider(new FakeFactory(handler));
        var issues = await provider.FetchIssuesAsync(MakeSource(new AzureDevOpsConfig("acme", "prj", "pat", customQuery)));

        Assert.Empty(issues);
        var bodyJson = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].SentBody);
        Assert.Equal(customQuery, bodyJson.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Empty_wiql_result_does_not_call_workitems_endpoint()
    {
        var handler = new FakeHandler();
        handler.Enqueue(JsonContent(new { workItems = Array.Empty<object>() }));

        var provider = new AzureDevOpsIssueProvider(new FakeFactory(handler));
        var issues = await provider.FetchIssuesAsync(MakeSource(new AzureDevOpsConfig("acme", "prj", "pat")));

        Assert.Empty(issues);
        Assert.Single(handler.Requests); // only the WIQL POST, no workitems GET
    }

    [Fact]
    public async Task Auth_failure_surfaces_as_http_exception()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("token rejected"),
        });

        var provider = new AzureDevOpsIssueProvider(new FakeFactory(handler));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.FetchIssuesAsync(MakeSource(new AzureDevOpsConfig("acme", "prj", "bad-pat"))));
        Assert.Contains("401", ex.Message);
        Assert.Contains("token rejected", ex.Message);
    }

    [Fact]
    public async Task Malformed_wiql_response_throws_invalid_operation()
    {
        var handler = new FakeHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json at all", Encoding.UTF8, "application/json"),
        });

        var provider = new AzureDevOpsIssueProvider(new FakeFactory(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.FetchIssuesAsync(MakeSource(new AzureDevOpsConfig("acme", "prj", "pat"))));
    }

    [Fact]
    public async Task Missing_organization_throws()
    {
        var handler = new FakeHandler();
        var provider = new AzureDevOpsIssueProvider(new FakeFactory(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.FetchIssuesAsync(MakeSource(new AzureDevOpsConfig("", "prj", "pat"))));
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage JsonContent(object payload)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class FakeFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public List<RecordedRequest> Requests { get; } = new();

        public void Enqueue(HttpResponseMessage resp) => _responses.Enqueue(resp);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = string.Empty;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization,
                body));
            if (_responses.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no response queued"),
                };
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        AuthenticationHeaderValue? Authorization,
        string SentBody);
}
