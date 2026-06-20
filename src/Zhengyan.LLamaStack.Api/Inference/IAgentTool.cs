namespace Zhengyan.LLamaStack.Api.Inference;

public interface IAgentTool
{
    string Name { get; }

    Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken);
}
