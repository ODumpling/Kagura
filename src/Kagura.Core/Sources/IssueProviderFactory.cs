using Kagura.Core.Domain;

namespace Kagura.Core.Sources;

public interface IIssueProviderFactory
{
    IIssueProvider Get(SourceType type);
}

public class IssueProviderFactory : IIssueProviderFactory
{
    private readonly IReadOnlyDictionary<SourceType, IIssueProvider> _byType;

    public IssueProviderFactory(IEnumerable<IIssueProvider> providers)
    {
        _byType = providers.ToDictionary(p => p.Type);
    }

    public IIssueProvider Get(SourceType type) =>
        _byType.TryGetValue(type, out var p)
            ? p
            : throw new NotSupportedException($"No provider registered for source type {type}");
}
