using Kagura.Core.Domain;

namespace Kagura.Core.Grill;

public record GrillTurn(WorkItemCommentRole Role, string Content);

public interface IGrillService
{
    Task<string> RespondAsync(
        string workItemTitle,
        string workItemBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct = default);

    Task<string> SynthesizeAsync(
        string workItemTitle,
        string originalBody,
        string? labels,
        IReadOnlyList<GrillTurn> history,
        CancellationToken ct = default);
}
