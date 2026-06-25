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

    public string? Warning { get; init; }

    public TimeSpan? Duration { get; init; }
}

public sealed class ToolExecutor
{
    private readonly IToolRegistry _registry;

    public ToolExecutor(IToolRegistry registry)
    {
        _registry = registry;
    }

    public bool CanExecute(IReadOnlyList<OpenAiToolCall> toolCalls)
    {
        return toolCalls.Count > 0 && toolCalls.Any(x => _registry.IsRegistered(x.Function.Name));
    }

    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(
        IReadOnlyList<OpenAiToolCall> toolCalls,
        CancellationToken cancellationToken,
        bool parallel = false)
    {
        return await ExecuteAsync(toolCalls, null, cancellationToken, parallel);
    }

    public async Task<IReadOnlyList<ToolResult>> ExecuteAsync(
        IReadOnlyList<OpenAiToolCall> toolCalls,
        ToolExecutionContext? context,
        CancellationToken cancellationToken,
        bool parallel = false)
    {
        context ??= new ToolExecutionContext { CancellationToken = cancellationToken };

        if (parallel && toolCalls.Count > 1)
        {
            var tasks = toolCalls.Select(call => ExecuteOneAsync(call, context));
            return await Task.WhenAll(tasks);
        }

        var results = new List<ToolResult>(toolCalls.Count);
        foreach (var call in toolCalls)
        {
            results.Add(await ExecuteOneAsync(call, context));
        }

        return results;
    }

    private async Task<ToolResult> ExecuteOneAsync(OpenAiToolCall call, ToolExecutionContext context)
    {
        var tool = _registry.GetTool(call.Function.Name);
        if (tool is null)
        {
            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Function.Name,
                Arguments = call.Function.Arguments,
                Output = $"Tool `{call.Function.Name}` is not registered.",
                Executed = false
            };
        }

        if (!context.SkipPermissionCheck && !CheckPermissions(tool, context.Permissions))
        {
            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Function.Name,
                Arguments = call.Function.Arguments,
                Output = $"Permission denied: `{call.Function.Name}` requires one of: [{string.Join(", ", tool.RequiredPermissions)}].",
                Executed = false
            };
        }

        var timeoutSeconds = context.TimeoutSeconds ?? tool.TimeoutSeconds;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var output = await tool.ExecuteAsync(call.Function.Arguments, timeoutCts.Token);

            if (!context.SkipOutputValidation)
            {
                var validation = ValidateOutput(tool, output);
                if (!validation.IsValid)
                {
                    return new ToolResult
                    {
                        ToolCallId = call.Id,
                        Name = call.Function.Name,
                        Arguments = call.Function.Arguments,
                        Output = output,
                        Executed = true,
                        Duration = DateTimeOffset.UtcNow - startedAt,
                        Warning = $"Output validation warning: {validation.ErrorMessage}"
                    };
                }
            }

            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Function.Name,
                Arguments = call.Function.Arguments,
                Output = output,
                Executed = true,
                Duration = DateTimeOffset.UtcNow - startedAt
            };
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Function.Name,
                Arguments = call.Function.Arguments,
                Output = $"Error: tool `{call.Function.Name}` timed out after {timeoutSeconds} seconds.",
                Executed = true,
                Duration = DateTimeOffset.UtcNow - startedAt
            };
        }
        catch (Exception exception)
        {
            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Function.Name,
                Arguments = call.Function.Arguments,
                Output = $"Error: {exception.Message}",
                Executed = true,
                Duration = DateTimeOffset.UtcNow - startedAt
            };
        }
    }

    private static bool CheckPermissions(IAgentTool tool, IReadOnlyList<string>? callerPermissions)
    {
        if (tool.RequiredPermissions.Count == 0)
        {
            return true;
        }

        if (callerPermissions is null || callerPermissions.Count == 0)
        {
            return false;
        }

        return tool.RequiredPermissions.Any(p =>
            callerPermissions.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private static ToolValidationResult ValidateOutput(IAgentTool tool, string output)
    {
        if (tool.Parameters is null)
        {
            return ToolValidationResult.Success();
        }

        if (!tool.Parameters.Value.TryGetProperty("type", out var typeProp) ||
            typeProp.GetString() != "object")
        {
            return ToolValidationResult.Success();
        }

        if (!tool.Parameters.Value.TryGetProperty("required", out var requiredProp))
        {
            return ToolValidationResult.Success();
        }

        if (requiredProp.ValueKind != JsonValueKind.Array || requiredProp.GetArrayLength() == 0)
        {
            return ToolValidationResult.Success();
        }

        JsonDocument outputDoc;
        try
        {
            outputDoc = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return ToolValidationResult.Failure("Output is not valid JSON.");
        }

        using (outputDoc)
        {
            if (outputDoc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ToolValidationResult.Failure("Output root must be a JSON object.");
            }

            var missing = new List<string>();
            foreach (var required in requiredProp.EnumerateArray())
            {
                var requiredName = required.GetString();
                if (requiredName is not null && !outputDoc.RootElement.TryGetProperty(requiredName, out _))
                {
                    missing.Add(requiredName);
                }
            }

            if (missing.Count > 0)
            {
                return ToolValidationResult.Failure(
                    $"Output missing required field(s): {string.Join(", ", missing)}.");
            }
        }

        return ToolValidationResult.Success();
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
