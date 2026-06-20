using Zhengyan.LLamaStack.Api.OpenAi;

namespace Zhengyan.LLamaStack.Api.Inference;

public enum InferenceEndpointKind
{
    ChatCompletions,
    Responses
}

public sealed class InferenceRequest
{
    public string? RequestedModel { get; init; }

    public IReadOnlyList<InferenceMessage> Messages { get; init; } = [];

    public IReadOnlyList<OpenAiTool> Tools { get; init; } = [];

    public string? ToolChoiceDescription { get; init; }

    public int? N { get; init; }

    public int? MaxToolCalls { get; init; }

    public int? MaxTokens { get; init; }

    public float? Temperature { get; init; }

    public float? TopP { get; init; }

    public int? TopK { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    public uint? Seed { get; init; }

    public IReadOnlyList<string> Stop { get; init; } = [];

    public bool ForceJson { get; init; }

    public bool StreamIncludeUsage { get; init; }

    public bool? Store { get; init; }

    public string? User { get; init; }

    public string? ServiceTier { get; init; }

    public bool? ParallelToolCalls { get; init; }

    public string? PreviousResponseId { get; init; }

    public string? Truncation { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public int ChoiceIndex { get; init; }

    public IReadOnlyList<string> CompatibilityWarnings { get; init; } = [];

    public InferenceRequest WithChoiceIndex(int choiceIndex)
    {
        return new InferenceRequest
        {
            RequestedModel = RequestedModel,
            Messages = Messages,
            Tools = Tools,
            ToolChoiceDescription = ToolChoiceDescription,
            N = N,
            ChoiceIndex = choiceIndex,
            MaxTokens = MaxTokens,
            Temperature = Temperature,
            TopP = TopP,
            TopK = TopK,
            PresencePenalty = PresencePenalty,
            FrequencyPenalty = FrequencyPenalty,
            Seed = Seed,
            Stop = Stop,
            ForceJson = ForceJson,
            StreamIncludeUsage = StreamIncludeUsage,
            Store = Store,
            User = User,
            ServiceTier = ServiceTier,
            ParallelToolCalls = ParallelToolCalls,
            PreviousResponseId = PreviousResponseId,
            Truncation = Truncation,
            Metadata = Metadata,
            CompatibilityWarnings = CompatibilityWarnings
        };
    }

    public InferenceRequest WithMessages(
        IReadOnlyList<InferenceMessage> messages,
        IReadOnlyList<string>? compatibilityWarnings = null)
    {
        return new InferenceRequest
        {
            RequestedModel = RequestedModel,
            Messages = messages,
            Tools = Tools,
            ToolChoiceDescription = ToolChoiceDescription,
            N = N,
            MaxToolCalls = MaxToolCalls,
            MaxTokens = MaxTokens,
            Temperature = Temperature,
            TopP = TopP,
            TopK = TopK,
            PresencePenalty = PresencePenalty,
            FrequencyPenalty = FrequencyPenalty,
            Seed = Seed,
            Stop = Stop,
            ForceJson = ForceJson,
            StreamIncludeUsage = StreamIncludeUsage,
            Store = Store,
            User = User,
            ServiceTier = ServiceTier,
            ParallelToolCalls = ParallelToolCalls,
            PreviousResponseId = PreviousResponseId,
            Truncation = Truncation,
            Metadata = Metadata,
            CompatibilityWarnings = compatibilityWarnings ?? CompatibilityWarnings
        };
    }

    public InferenceRequest WithCompatibilityWarnings(IReadOnlyList<string> compatibilityWarnings)
    {
        return WithMessages(Messages, compatibilityWarnings);
    }
}

public sealed class InferenceMessage
{
    public string Role { get; init; } = "user";

    public string Content { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? ToolCallId { get; init; }

    public IReadOnlyList<OpenAiToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<InferenceMedia> Media { get; init; } = [];
}

public sealed class InferenceMedia
{
    public string Source { get; init; } = "inline";

    public string? MimeType { get; init; }

    public byte[] Bytes { get; init; } = [];
}

public sealed class InferenceCompletion
{
    public string Id { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<OpenAiToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<ToolRound> ToolRounds { get; init; } = [];

    public IReadOnlyList<InferenceChoice> Choices { get; init; } = [];

    public string FinishReason { get; init; } = "stop";

    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int TotalTokens => PromptTokens + CompletionTokens;

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    public string? User { get; init; }

    public string? ServiceTier { get; init; }

    public bool? Store { get; init; }

    public IReadOnlyList<string> CompatibilityWarnings { get; init; } = [];
}

public sealed class InferenceChoice
{
    public int Index { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<OpenAiToolCall> ToolCalls { get; init; } = [];

    public string FinishReason { get; init; } = "stop";

    public int CompletionTokens { get; init; }
}

public sealed class ToolRound
{
    public IReadOnlyList<OpenAiToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<ToolResult> Results { get; init; } = [];
}

public sealed class EmbeddingResult
{
    public IReadOnlyList<EmbeddingData> Data { get; init; } = [];

    public int TotalTokens { get; init; }
}

public sealed class EmbeddingData
{
    public int Index { get; init; }

    public float[] Embedding { get; init; } = [];

    public string Object { get; init; } = "embedding";
}

public sealed class ModelDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Object { get; init; } = "model";

    public long Created { get; init; }

    public string OwnedBy { get; init; } = "local";

    public bool Loaded { get; init; }

    public string? ModelPath { get; init; }

    public string? MmprojPath { get; init; }

    public object Capabilities { get; init; } = new();
}
