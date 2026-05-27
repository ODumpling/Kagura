using Kagura.Core.Domain;

namespace Kagura.Core.Sources;

public record FetchedIssue(
    string ExternalId,
    string Title,
    string Body,
    string? Url,
    string? Labels);

public interface IIssueProvider
{
    SourceType Type { get; }
    Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default);
}
