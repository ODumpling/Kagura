using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kagura.Core.Triage;

public class TriageOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = AnthropicModels.Claude46Sonnet;
    public int MaxTokens { get; set; } = 2000;
}

public class AnthropicTriageService : ITriageService
{
    private const string SystemPrompt =
        """
        You are a triage assistant for a developer workflow tool. You receive a software issue (title, body, labels)
        and propose a list of small, independently executable tasks that together complete the issue.

        Rules:
        - Each task should be small enough to be completed by one autonomous coding agent in a single session.
        - Prefer 1–5 tasks. Fewer if the issue is small.
        - Tasks should be as parallelizable as possible. If they MUST run in order, that's fine — set Order accordingly.
        - Titles are imperative, under 80 characters ("Add ...", "Refactor ...", "Wire ...").
        - Descriptions are 1-3 sentences explaining scope, files likely involved, and acceptance criteria.

        Respond with ONLY a JSON array, no prose, no markdown fences. Schema:
        [
          {"title": "string", "description": "string", "order": integer}
        ]
        """;

    private readonly TriageOptions _options;
    private readonly ILogger<AnthropicTriageService> _log;

    public AnthropicTriageService(IOptions<TriageOptions> options, ILogger<AnthropicTriageService> log)
    {
        _options = options.Value;
        _log = log;
    }

    public async Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
        string workItemTitle, string workItemBody, string? labels, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("Anthropic API key is not configured (Anthropic:ApiKey).");

        using var client = new AnthropicClient(_options.ApiKey);

        var userPrompt =
            $"""
             Title: {workItemTitle}

             Labels: {labels ?? "(none)"}

             Body:
             {workItemBody}
             """;

        var parameters = new MessageParameters
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            System = new List<SystemMessage>
            {
                new(SystemPrompt) { CacheControl = new CacheControl { Type = CacheControlType.ephemeral } },
            },
            Messages = new List<Message>
            {
                new(RoleType.User, userPrompt),
            },
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);
        var text = response.Message?.ToString() ?? throw new InvalidOperationException("Empty response from Anthropic");

        var json = ExtractJsonArray(text);
        var arr = JsonSerializer.Deserialize<List<TriagedTaskProposal>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidOperationException("Could not parse triage response as JSON array");

        _log.LogInformation("Triage proposed {Count} tasks", arr.Count);
        return arr;
    }

    private static string ExtractJsonArray(string text)
    {
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
            throw new InvalidOperationException($"Triage response did not contain a JSON array. Got: {text}");
        return text[start..(end + 1)];
    }
}
