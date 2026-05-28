namespace Kagura.Core.Domain;

public enum WorkItemCommentRole
{
    User = 0,
    Assistant = 1,
}

public class WorkItemComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkItemId { get; set; }
    public WorkItem WorkItem { get; set; } = null!;

    public WorkItemCommentRole Role { get; set; }
    public required string Content { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
