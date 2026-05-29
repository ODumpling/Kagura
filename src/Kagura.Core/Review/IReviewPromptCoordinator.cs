namespace Kagura.Core.Review;

public interface IReviewPromptCoordinator
{
    ReviewPrompt Raise(Guid workItemId, Guid? taskId, Guid? runId, string question, IReadOnlyList<ReviewPromptOption> options);
    IReadOnlyList<ReviewPrompt> GetPending(Guid workItemId);
    bool TryResolve(Guid promptId, string selectedOptionId, string? notes, out ReviewPromptResponse response);

    event Action<ReviewPrompt>? PromptRaised;
    event Action<ReviewPromptResponse>? PromptResolved;
}
