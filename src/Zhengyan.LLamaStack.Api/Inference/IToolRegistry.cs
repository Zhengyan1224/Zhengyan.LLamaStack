namespace Zhengyan.LLamaStack.Api.Inference;

public interface IToolRegistry
{
    IAgentTool? GetTool(string name);

    IReadOnlyList<IAgentTool> GetAllTools();

    bool Register(IAgentTool tool);

    bool Unregister(string name);

    bool IsRegistered(string name);

    event EventHandler<IAgentTool>? ToolRegistered;

    event EventHandler<string>? ToolUnregistered;

    int Count { get; }
}
