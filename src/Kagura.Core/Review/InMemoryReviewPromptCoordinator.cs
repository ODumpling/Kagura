using System.Collections.Concurrent;

namespace Kagura.Core.Review;

public class InMemoryReviewPromptCoordinator : IReviewPromptCoordinator
{
    private readonly ConcurrentDictionary<Guid, ReviewPrompt> _prompts = new();

    public event Action<ReviewPrompt>? PromptRaised;
    public event Action<ReviewPromptResponse>? PromptResolved;

    public ReviewPrompt Raise(Guid workItemId, Guid? taskId, Guid? runId, string question, IReadOnlyList<ReviewPromptOption> options)
    {
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("Question must not be empty.", nameof(question));
        if (options is null || options.Count == 0)
            throw new ArgumentException("At least one option is required.", nameof(options));

        var prompt = new ReviewPrompt(
            Id: Guid.NewGuid(),
            WorkItemId: workItemId,
            TaskId: taskId,
            RunId: runId,
            Question: question.Trim(),
            Options: options,
            CreatedAt: DateTime.UtcNow);

        _prompts[prompt.Id] = prompt;
        PromptRaised?.Invoke(prompt);
        return prompt;
    }

    public IReadOnlyList<ReviewPrompt> GetPending(Guid workItemId) =>
        _prompts.Values
            .Where(p => p.WorkItemId == workItemId)
            .OrderBy(p => p.CreatedAt)
            .ToList();

    public bool TryResolve(Guid promptId, string selectedOptionId, string? notes, out ReviewPromptResponse response)
    {
        if (!_prompts.TryGetValue(promptId, out var prompt) ||
            !prompt.Options.Any(o => o.Id == selectedOptionId))
        {
            response = default!;
            return false;
        }

        if (!_prompts.TryRemove(promptId, out _))
        {
            response = default!;
            return false;
        }

        response = new ReviewPromptResponse(promptId, prompt.WorkItemId, selectedOptionId, notes, DateTime.UtcNow);
        PromptResolved?.Invoke(response);
        return true;
    }
}
