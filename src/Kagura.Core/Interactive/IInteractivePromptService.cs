namespace Kagura.Core.Interactive;

public interface IInteractivePromptService
{
    Task<string> AskAsync(
        Guid runId,
        string question,
        IReadOnlyList<string>? choices = null,
        CancellationToken ct = default);

    bool TryAnswer(Guid promptId, string answer);

    IReadOnlyList<InteractivePrompt> GetPending(Guid runId);
}
