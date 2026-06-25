namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ToolValidationResult
{
    public bool IsValid { get; init; }

    public string? ErrorMessage { get; init; }

    public static ToolValidationResult Success() => new() { IsValid = true };

    public static ToolValidationResult Failure(string message) => new() { IsValid = false, ErrorMessage = message };
}
