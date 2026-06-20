using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Api.OpenAi;

public static class OpenAiResponseFactory
{
    public static object ToChatCompletionResponse(InferenceCompletion completion, string responseId, long created)
    {
        var choices = BuildChoices(completion);

        return new
        {
            id = responseId,
            @object = "chat.completion",
            created,
            model = completion.Model,
            metadata = completion.Metadata,
            user = completion.User,
            service_tier = completion.ServiceTier,
            store = completion.Store,
            system_fingerprint = "llamasharp-local",
            choices,
            usage = ToChatUsage(completion.PromptTokens, completion.CompletionTokens),
            compatibility_warnings = completion.CompatibilityWarnings.Count == 0 ? null : completion.CompatibilityWarnings
        };
    }

    private static object[] BuildChoices(InferenceCompletion completion)
    {
        if (completion.Choices.Count > 0)
        {
            return completion.Choices.Select(c =>
            {
                var choiceMessage = c.ToolCalls.Count > 0
                    ? new ChatChoiceMessage("assistant", null, c.ToolCalls)
                    : new ChatChoiceMessage("assistant", c.Text, null);
                return (object)new
                {
                    index = c.Index,
                    message = choiceMessage,
                    finish_reason = c.FinishReason
                };
            }).ToArray();
        }

        var message = completion.ToolCalls.Count > 0
            ? new ChatChoiceMessage("assistant", null, completion.ToolCalls)
            : new ChatChoiceMessage("assistant", completion.Text, null);

        return
        [
            new
            {
                index = 0,
                message,
                finish_reason = completion.FinishReason
            }
        ];
    }

    public static object ToChatCompletionResponse(StoredChatCompletion completion)
    {
        var message = completion.ToolCalls.Count > 0
            ? new ChatChoiceMessage("assistant", null, completion.ToolCalls)
            : new ChatChoiceMessage("assistant", completion.OutputText, null);

        return new
        {
            id = completion.Id,
            @object = "chat.completion",
            created = completion.Created,
            model = completion.Model,
            metadata = completion.Metadata,
            user = completion.User,
            service_tier = completion.ServiceTier,
            store = completion.Store,
            system_fingerprint = "llamasharp-local",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message,
                    finish_reason = completion.FinishReason
                }
            },
            usage = ToChatUsage(completion.PromptTokens, completion.CompletionTokens),
            compatibility_warnings = completion.CompatibilityWarnings.Count == 0 ? null : completion.CompatibilityWarnings
        };
    }

    public static object ToResponsesResponse(InferenceCompletion completion, string responseId, long created)
    {
        return ToResponsesResponse(
            responseId,
            created,
            "completed",
            completion.Model,
            completion.Metadata,
            completion.User,
            completion.ServiceTier,
            completion.Store,
            completion.Text,
            completion.ToolCalls,
            completion.PromptTokens,
            completion.CompletionTokens,
            completion.CompatibilityWarnings);
    }

    public static object ToResponsesResponse(StoredResponse response)
    {
        return ToResponsesResponse(
            response.Id,
            response.CreatedAt,
            response.Status,
            response.Model,
            response.Metadata,
            response.User,
            response.ServiceTier,
            response.Store,
            response.OutputText,
            response.ToolCalls,
            response.InputTokens,
            response.OutputTokens,
            response.CompatibilityWarnings);
    }

    public static object ToChatMessages(StoredChatCompletion completion)
    {
        var data = new List<object>();
        data.AddRange(completion.Messages.Select(ToMessageListItem));
        data.Add(ToMessageListItem(new InferenceMessage
        {
            Role = "assistant",
            Content = completion.OutputText
        }));

        return ToList(data);
    }

    public static object ToResponsesInputItems(StoredResponse response)
    {
        return ToList(response.InputMessages.Select(ToInputItem).ToArray());
    }

    public static object ToTokenCount(StoredResponse response)
    {
        return new
        {
            @object = "response.token_count",
            response_id = response.Id,
            input_tokens = response.InputTokens,
            output_tokens = response.OutputTokens,
            total_tokens = response.TotalTokens
        };
    }

    public static object ToChatUsageChunk(string responseId, string model, int promptTokens, int outputTokens, long created)
    {
        return new
        {
            id = responseId,
            @object = "chat.completion.chunk",
            created,
            model,
            choices = Array.Empty<object>(),
            usage = ToChatUsage(promptTokens, outputTokens)
        };
    }

    public static object ToList(IReadOnlyList<object> data)
    {
        return new
        {
            @object = "list",
            data,
            has_more = false,
            first_id = TryReadId(data.FirstOrDefault()),
            last_id = TryReadId(data.LastOrDefault())
        };
    }

    public static object ToDeleted(string id, string objectName)
    {
        return new
        {
            id,
            @object = objectName,
            deleted = true
        };
    }

    private static object ToResponsesResponse(
        string responseId,
        long created,
        string status,
        string model,
        IReadOnlyDictionary<string, string>? metadata,
        string? user,
        string? serviceTier,
        bool? store,
        string outputText,
        IReadOnlyList<OpenAiToolCall> toolCalls,
        int inputTokens,
        int outputTokens,
        IReadOnlyList<string> compatibilityWarnings)
    {
        var output = new List<object>();
        if (!string.IsNullOrEmpty(outputText))
        {
            output.Add(new
            {
                id = "msg_" + responseId,
                type = "message",
                status,
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text = outputText,
                        annotations = Array.Empty<object>()
                    }
                }
            });
        }

        foreach (var toolCall in toolCalls)
        {
            output.Add(new
            {
                id = toolCall.Id,
                type = "function_call",
                status,
                call_id = toolCall.Id,
                name = toolCall.Function.Name,
                arguments = toolCall.Function.Arguments
            });
        }

        return new
        {
            id = responseId,
            @object = "response",
            created_at = created,
            status,
            model,
            metadata,
            user,
            service_tier = serviceTier,
            store,
            output,
            output_text = outputText,
            usage = new
            {
                input_tokens = inputTokens,
                output_tokens = outputTokens,
                total_tokens = inputTokens + outputTokens
            },
            compatibility_warnings = compatibilityWarnings.Count == 0 ? null : compatibilityWarnings
        };
    }

    private static object ToMessageListItem(InferenceMessage message)
    {
        return new
        {
            id = "msg_" + Guid.NewGuid().ToString("N"),
            @object = "chat.completion.message",
            role = message.Role,
            content = message.Content,
            name = message.Name,
            tool_call_id = message.ToolCallId,
            media = message.Media.Count == 0
                ? null
                : message.Media.Select(x => new
                {
                    source = x.Source,
                    mime_type = x.MimeType,
                    bytes = x.Bytes.Length
                }).ToArray()
        };
    }

    private static object ToInputItem(InferenceMessage message)
    {
        return new
        {
            id = "item_" + Guid.NewGuid().ToString("N"),
            type = "message",
            role = message.Role,
            content = new[]
            {
                new
                {
                    type = "input_text",
                    text = message.Content
                }
            },
            media = message.Media.Count == 0
                ? null
                : message.Media.Select(x => new
                {
                    source = x.Source,
                    mime_type = x.MimeType,
                    bytes = x.Bytes.Length
                }).ToArray()
        };
    }

    private static object ToChatUsage(int promptTokens, int completionTokens)
    {
        return new
        {
            prompt_tokens = promptTokens,
            completion_tokens = completionTokens,
            total_tokens = promptTokens + completionTokens
        };
    }

    private static object? TryReadId(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.GetType().GetProperty("id")?.GetValue(value);
    }

    private sealed record ChatChoiceMessage(string Role, string? Content, IReadOnlyList<OpenAiToolCall>? ToolCalls);
}
