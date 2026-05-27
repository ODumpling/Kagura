namespace Kagura.Core.Review;

public record ReviewVerdict(bool AutoMerge, string Reasoning);

public interface IReviewService
{
    Task<ReviewVerdict> ReviewAsync(
        string taskTitle,
        string taskDescription,
        string diff,
        CancellationToken ct = default);
}
