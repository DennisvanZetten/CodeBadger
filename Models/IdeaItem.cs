namespace CodeBadger.Models;

public enum IdeaType
{
    Idea,
    Wish,
    Task,
    Bug
}

public enum IdeaStatus
{
    Open,
    InProgress,
    Done
}

public sealed class IdeaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public IdeaType Type { get; set; } = IdeaType.Task;
    public IdeaStatus Status { get; set; } = IdeaStatus.Open;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
