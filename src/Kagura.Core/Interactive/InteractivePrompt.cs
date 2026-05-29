namespace Kagura.Core.Interactive;

public record InteractivePrompt(
    Guid Id,
    Guid RunId,
    string Question,
    IReadOnlyList<string>? Choices,
    DateTime CreatedAt);
