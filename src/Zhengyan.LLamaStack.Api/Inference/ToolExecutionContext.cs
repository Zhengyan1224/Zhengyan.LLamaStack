namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ToolExecutionContext
{
    public CancellationToken CancellationToken { get; init; }

    public IReadOnlyList<string>? Permissions { get; init; }

    public int? TimeoutSeconds { get; init; }

    public bool SkipPermissionCheck { get; init; }

    public bool SkipOutputValidation { get; init; }
}
