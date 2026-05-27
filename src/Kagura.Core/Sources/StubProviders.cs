using Kagura.Core.Domain;

namespace Kagura.Core.Sources;

public class AzureDevOpsIssueProvider : IIssueProvider
{
    public SourceType Type => SourceType.AzureDevOps;
    public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default) =>
        throw new NotImplementedException("Azure DevOps provider not yet implemented");
}

public class BeadsIssueProvider : IIssueProvider
{
    public SourceType Type => SourceType.Beads;
    public Task<IReadOnlyList<FetchedIssue>> FetchIssuesAsync(Source source, CancellationToken ct = default) =>
        throw new NotImplementedException("Beads provider not yet implemented");
}
