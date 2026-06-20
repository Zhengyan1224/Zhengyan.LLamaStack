using System.Text.Json;
using Zhengyan.LLamaStack.Api.OpenAi;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ToolResult
{
    public string ToolCallId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Arguments { get; init; } = "{}";

    public string Output { get; init; } = string.Empty;

    public bool Executed { get; init; }
}

public sealed class ToolExecutor
{
    private readonly Dictionary<string, IAgentTool> _tools;

    public ToolExecutor(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    }

    public bool CanExecute(IReadOnlyList<OpenAiToolCall> toolCalls)
    {
        return toolCalls.Count > 0 && toolCalls.Any(x => _tools.ContainsKey(x.Function.Name));
    }

    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(
        IReadOnlyList<OpenAiToolCall> toolCalls,
        CancellationToken cancellationToken)
    {
        var results = new List<ToolResult>();
        foreach (var call in toolCalls)
        {
            if (_tools.TryGetValue(call.Function.Name, out var tool))
            {
                try
                {
                    var output = await tool.ExecuteAsync(call.Function.Arguments, cancellationToken);
                    results.Add(new ToolResult
                    {
                        ToolCallId = call.Id,
                        Name = call.Function.Name,
                        Arguments = call.Function.Arguments,
                        Output = output,
                        Executed = true
                    });
                }
                catch (Exception exception)
                {
                    results.Add(new ToolResult
                    {
                        ToolCallId = call.Id,
                        Name = call.Function.Name,
                        Arguments = call.Function.Arguments,
                        Output = $"Error: {exception.Message}",
                        Executed = true
                    });
                }
            }
            else
            {
                results.Add(new ToolResult
                {
                    ToolCallId = call.Id,
                    Name = call.Function.Name,
                    Arguments = call.Function.Arguments,
                    Output = $"Tool `{call.Function.Name}` is not registered locally.",
                    Executed = false
                });
            }
        }

        return results;
    }

    public IReadOnlyList<InferenceMessage> BuildToolResultMessages(
        IReadOnlyList<OpenAiToolCall> toolCalls,
        IReadOnlyList<ToolResult> results,
        IReadOnlyList<InferenceMessage> existingMessages)
    {
        var messages = new List<InferenceMessage>(existingMessages);

        messages.Add(new InferenceMessage
        {
            Role = "assistant",
            Content = string.Empty,
            ToolCalls = toolCalls
        });

        foreach (var result in results)
        {
            messages.Add(new InferenceMessage
            {
                Role = "tool",
                ToolCallId = result.ToolCallId,
                Name = result.Name,
                Content = result.Output
            });
        }

        return messages;
    }
}
