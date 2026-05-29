using System.Text.Json;
using Kagura.Core.Domain;
using Kagura.Core.Git;
using Kagura.Core.Sources;

namespace Kagura.Tests;

public class BeadsIssueProviderTests
{
    [Fact]
    public async Task Fetch_defaults_status_filter_to_open()
    {
        var runner = new RecordingRunner(new ProcessResult(0, "[]", string.Empty));
        var provider = new BeadsIssueProvider(runner);

        var source = new Source
        {
            Name = "beads-src",
            Type = SourceType.Beads,
            LocalRepoPath = "/tmp/beads-repo",
            ConfigJson = "{}",
        };

        await provider.FetchIssuesAsync(source);

        Assert.Equal("bd", runner.LastFileName);
        Assert.Equal("/tmp/beads-repo", runner.LastWorkingDirectory);
        Assert.Equal(new[] { "list", "--status", "open", "--json" }, runner.LastArgs);
    }

    [Fact]
    public async Task Fetch_honours_custom_status_from_config()
    {
        var runner = new RecordingRunner(new ProcessResult(0, "[]", string.Empty));
        var provider = new BeadsIssueProvider(runner);

        var source = new Source
        {
            Name = "beads-src",
            Type = SourceType.Beads,
            LocalRepoPath = "/tmp/beads-repo",
            ConfigJson = JsonSerializer.Serialize(new BeadsConfig("closed")),
        };

        await provider.FetchIssuesAsync(source);

        Assert.Equal(new[] { "list", "--status", "closed", "--json" }, runner.LastArgs);
    }

    [Fact]
    public async Task Fetch_maps_bead_fields_into_fetched_issue()
    {
        const string json = """
        [
          {
            "id": "bd-1",
            "title": "First bead",
            "description": "Body of first",
            "status": "open",
            "labels": ["foo", "bar"]
          },
          {
            "id": "bd-2",
            "title": "Second bead",
            "description": "",
            "status": "open"
          }
        ]
        """;

        var runner = new RecordingRunner(new ProcessResult(0, json, string.Empty));
        var provider = new BeadsIssueProvider(runner);

        var source = new Source
        {
            Name = "beads-src",
            Type = SourceType.Beads,
            LocalRepoPath = "/tmp/beads-repo",
            ConfigJson = "{}",
        };

        var fetched = await provider.FetchIssuesAsync(source);

        Assert.Collection(fetched,
            f =>
            {
                Assert.Equal("bd-1", f.ExternalId);
                Assert.Equal("First bead", f.Title);
                Assert.Equal("Body of first", f.Body);
                Assert.Null(f.Url);
                Assert.Equal("foo,bar", f.Labels);
            },
            f =>
            {
                Assert.Equal("bd-2", f.ExternalId);
                Assert.Equal("Second bead", f.Title);
                Assert.Equal(string.Empty, f.Body);
                Assert.Null(f.Url);
                Assert.Null(f.Labels);
            });
    }

    [Fact]
    public async Task Fetch_throws_clean_error_when_bd_binary_is_missing()
    {
        var runner = new ThrowingRunner(new System.ComponentModel.Win32Exception("file not found"));
        var provider = new BeadsIssueProvider(runner);

        var source = new Source
        {
            Name = "beads-src",
            Type = SourceType.Beads,
            LocalRepoPath = "/tmp/beads-repo",
            ConfigJson = "{}",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.FetchIssuesAsync(source));
        Assert.Contains("bd", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_throws_clean_error_when_bd_exits_nonzero()
    {
        var runner = new RecordingRunner(new ProcessResult(1, string.Empty, "Error: no beads database found"));
        var provider = new BeadsIssueProvider(runner);

        var source = new Source
        {
            Name = "beads-src",
            Type = SourceType.Beads,
            LocalRepoPath = "/tmp/not-bd-init",
            ConfigJson = "{}",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.FetchIssuesAsync(source));
        Assert.Contains("bd init", ex.Message);
        Assert.Contains("no beads database found", ex.Message);
    }

    [Fact]
    public async Task Fetch_throws_clean_error_on_malformed_json()
    {
        var runner = new RecordingRunner(new ProcessResult(0, "this is not json", string.Empty));
        var provider = new BeadsIssueProvider(runner);

        var source = new Source
        {
            Name = "beads-src",
            Type = SourceType.Beads,
            LocalRepoPath = "/tmp/beads-repo",
            ConfigJson = "{}",
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.FetchIssuesAsync(source));
        Assert.Contains("malformed JSON", ex.Message);
        Assert.IsType<JsonException>(ex.InnerException);
    }

    private sealed class RecordingRunner : IBeadsProcessRunner
    {
        private readonly ProcessResult _result;
        public RecordingRunner(ProcessResult result) => _result = result;

        public string? LastFileName { get; private set; }
        public string? LastWorkingDirectory { get; private set; }
        public string[] LastArgs { get; private set; } = Array.Empty<string>();

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory, CancellationToken ct)
        {
            LastFileName = fileName;
            LastWorkingDirectory = workingDirectory;
            LastArgs = args.ToArray();
            return Task.FromResult(_result);
        }
    }

    private sealed class ThrowingRunner : IBeadsProcessRunner
    {
        private readonly Exception _ex;
        public ThrowingRunner(Exception ex) => _ex = ex;

        public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, string workingDirectory, CancellationToken ct)
            => throw _ex;
    }
}
