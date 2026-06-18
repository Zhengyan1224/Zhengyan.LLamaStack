using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.OpenAi;

namespace Zhengyan.LLamaStack.Api.Storage;

public sealed record StoredChatCompletion
{
    public string Id { get; init; } = string.Empty;

    public long Created { get; init; }

    public string Model { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public string? User { get; init; }

    public string? ServiceTier { get; init; }

    public bool Store { get; init; }

    public IReadOnlyList<InferenceMessage> Messages { get; init; } = [];

    public string OutputText { get; init; } = string.Empty;

    public IReadOnlyList<OpenAiToolCall> ToolCalls { get; init; } = [];

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public IReadOnlyList<string> CompatibilityWarnings { get; init; } = [];
}

public sealed record StoredResponse
{
    public string Id { get; init; } = string.Empty;

    public long CreatedAt { get; init; }

    public string Status { get; init; } = "completed";

    public string Model { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public string? User { get; init; }

    public string? ServiceTier { get; init; }

    public bool Store { get; init; }

    public string? PreviousResponseId { get; init; }

    public IReadOnlyList<InferenceMessage> InputMessages { get; init; } = [];

    public string OutputText { get; init; } = string.Empty;

    public IReadOnlyList<OpenAiToolCall> ToolCalls { get; init; } = [];

    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    public int TotalTokens => InputTokens + OutputTokens;

    public IReadOnlyList<string> CompatibilityWarnings { get; init; } = [];
}
