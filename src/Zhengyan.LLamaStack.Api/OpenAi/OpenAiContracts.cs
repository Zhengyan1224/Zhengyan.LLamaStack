using System.Text.Json;

namespace Zhengyan.LLamaStack.Api.OpenAi;

public sealed class ChatCompletionRequest
{
    public string? Model { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];

    public JsonElement? Metadata { get; set; }

    public string? User { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public int? MaxTokens { get; set; }

    public int? MaxCompletionTokens { get; set; }

    public bool Stream { get; set; }

    public OpenAiStreamOptions? StreamOptions { get; set; }

    public JsonElement? Stop { get; set; }

    public double? PresencePenalty { get; set; }

    public double? FrequencyPenalty { get; set; }

    public int? Seed { get; set; }

    public int? N { get; set; }

    public bool? Store { get; set; }

    public string? ServiceTier { get; set; }

    public bool? ParallelToolCalls { get; set; }

    public bool? Logprobs { get; set; }

    public int? TopLogprobs { get; set; }

    public JsonElement? LogitBias { get; set; }

    public JsonElement? ResponseFormat { get; set; }

    public List<OpenAiTool>? Tools { get; set; }

    public JsonElement? ToolChoice { get; set; }

    public List<OpenAiFunction>? Functions { get; set; }

    public JsonElement? FunctionCall { get; set; }
}

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";

    public JsonElement? Content { get; set; }

    public string? Name { get; set; }

    public string? ToolCallId { get; set; }

    public List<OpenAiToolCall>? ToolCalls { get; set; }

    public OpenAiFunctionCall? FunctionCall { get; set; }
}

public sealed class ResponsesRequest
{
    public string? Model { get; set; }

    public JsonElement? Input { get; set; }

    public string? Instructions { get; set; }

    public JsonElement? Metadata { get; set; }

    public string? User { get; set; }

    public string? PreviousResponseId { get; set; }

    public JsonElement? Conversation { get; set; }

    public bool? Background { get; set; }

    public bool Stream { get; set; }

    public OpenAiStreamOptions? StreamOptions { get; set; }

    public double? Temperature { get; set; }

    public double? TopP { get; set; }

    public int? TopK { get; set; }

    public int? MaxOutputTokens { get; set; }

    public int? MaxToolCalls { get; set; }

    public JsonElement? Stop { get; set; }

    public double? PresencePenalty { get; set; }

    public double? FrequencyPenalty { get; set; }

    public int? Seed { get; set; }

    public bool? Store { get; set; }

    public string? ServiceTier { get; set; }

    public bool? ParallelToolCalls { get; set; }

    public string? Truncation { get; set; }

    public JsonElement? Include { get; set; }

    public JsonElement? Reasoning { get; set; }

    public JsonElement? Prompt { get; set; }

    public JsonElement? Moderation { get; set; }

    public JsonElement? Text { get; set; }

    public List<OpenAiTool>? Tools { get; set; }

    public JsonElement? ToolChoice { get; set; }
}

public sealed class OpenAiStreamOptions
{
    public bool? IncludeUsage { get; set; }

    public bool? IncludeObfuscation { get; set; }
}

public sealed class OpenAiTool
{
    public string Type { get; set; } = "function";

    public OpenAiFunction? Function { get; set; }
}

public sealed class OpenAiFunction
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public JsonElement? Parameters { get; set; }

    public bool? Strict { get; set; }
}

public sealed class OpenAiToolCall
{
    public string Id { get; set; } = "call_" + Guid.NewGuid().ToString("N");

    public string Type { get; set; } = "function";

    public OpenAiFunctionCall Function { get; set; } = new();
}

public sealed class OpenAiFunctionCall
{
    public string Name { get; set; } = string.Empty;

    public string Arguments { get; set; } = "{}";
}

public sealed class OpenAiErrorEnvelope
{
    public OpenAiError Error { get; set; } = new();
}

public sealed class OpenAiError
{
    public string Message { get; set; } = string.Empty;

    public string Type { get; set; } = "invalid_request_error";

    public string? Param { get; set; }

    public string? Code { get; set; }
}
