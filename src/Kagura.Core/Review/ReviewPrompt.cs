namespace Kagura.Core.Review;

public record ReviewPromptOption(string Id, string Label, string? Description);

public record ReviewPrompt(
    Guid Id,
    Guid WorkItemId,
    Guid? TaskId,
    Guid? RunId,
    string Question,
    IReadOnlyList<ReviewPromptOption> Options,
    DateTime CreatedAt);

public record ReviewPromptResponse(
    Guid PromptId,
    Guid WorkItemId,
    string SelectedOptionId,
    string? Notes,
    DateTime AnsweredAt);
