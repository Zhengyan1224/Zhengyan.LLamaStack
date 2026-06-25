namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event EventHandler<IAgentTool>? ToolRegistered;
    public event EventHandler<string>? ToolUnregistered;

    public int Count
    {
        get { lock (_lock) return _tools.Count; }
    }

    public IAgentTool? GetTool(string name)
    {
        lock (_lock)
        {
            return _tools.TryGetValue(name, out var tool) ? tool : null;
        }
    }

    public IReadOnlyList<IAgentTool> GetAllTools()
    {
        lock (_lock)
        {
            return _tools.Values.ToArray();
        }
    }

    public bool Register(IAgentTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        lock (_lock)
        {
            if (_tools.ContainsKey(tool.Name))
            {
                return false;
            }

            _tools[tool.Name] = tool;
        }

        ToolRegistered?.Invoke(this, tool);
        return true;
    }

    public bool Unregister(string name)
    {
        lock (_lock)
        {
            if (!_tools.Remove(name))
            {
                return false;
            }
        }

        ToolUnregistered?.Invoke(this, name);
        return true;
    }

    public bool IsRegistered(string name)
    {
        lock (_lock)
        {
            return _tools.ContainsKey(name);
        }
    }
}
