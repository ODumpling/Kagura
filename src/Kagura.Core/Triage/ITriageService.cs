namespace Kagura.Core.Triage;

public record TriagedTaskProposal(string Title, string Description, int Order);

public interface ITriageService
{
    Task<IReadOnlyList<TriagedTaskProposal>> ProposeTasksAsync(
        string workItemTitle,
        string workItemBody,
        string? labels,
        CancellationToken ct = default);
}
