namespace Zhengyan.LLamaStack.Api.Inference;

public enum ResponseTaskStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed record ResponseTaskInfo
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = "compact";

    public ResponseTaskStatus Status { get; init; } = ResponseTaskStatus.Pending;

    public string? SourceResponseId { get; init; }

    public string? ResultResponseId { get; init; }

    public string? ErrorMessage { get; init; }

    public long CreatedAt { get; init; }

    public long? CompletedAt { get; init; }
}
