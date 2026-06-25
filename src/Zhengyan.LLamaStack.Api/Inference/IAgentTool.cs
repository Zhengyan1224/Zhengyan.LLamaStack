using System.Text.Json;

namespace Zhengyan.LLamaStack.Api.Inference;

public interface IAgentTool
{
    string Name { get; }

    string? Description { get; }

    JsonElement? Parameters { get; }

    int TimeoutSeconds { get; }

    IReadOnlyList<string> RequiredPermissions { get; }

    Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken);
}
