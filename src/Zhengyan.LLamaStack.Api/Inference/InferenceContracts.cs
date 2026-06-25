using Zhengyan.LLamaStack.Api.OpenAi;

namespace Zhengyan.LLamaStack.Api.Inference;

public enum InferenceEndpointKind
{
    ChatCompletions,
    Responses
}

public sealed class InferenceRequest
{
    public string? RequestedModel { get; set; }

    public IReadOnlyList<InferenceMessage> Messages { get; set; } = [];

    public IReadOnlyList<OpenAiTool> Tools { get; set; } = [];

    public string? ToolChoiceDescription { get; set; }

    public int? N { get; set; }

    public int? MaxToolCalls { get; set; }

    public int? MaxTokens { get; set; }

    public float? Temperature { get; set; }

    public float? TopP { get; set; }

    public int? TopK { get; set; }

    public float? PresencePenalty { get; set; }

    public float? FrequencyPenalty { get; set; }

    public uint? Seed { get; set; }

    public IReadOnlyList<string> Stop { get; set; } = [];

    public bool ForceJson { get; set; }

    public string? JsonSchema { get; set; }

    public bool StrictJsonSchema { get; set; }

    public bool StreamIncludeUsage { get; set; }

    public bool? Store { get; set; }

    public string? User { get; set; }

    public string? ServiceTier { get; set; }

    public bool? ParallelToolCalls { get; set; }

    public string? PreviousResponseId { get; set; }

    public string? Truncation { get; set; }

    public IReadOnlyDictionary<string, string>? Metadata { get; set; }

    public IReadOnlyList<string>? Include { get; set; }

    public string? ReasoningEffort { get; set; }

    public string? Prompt { get; set; }

    public IReadOnlyDictionary<int, float>? LogitBias { get; set; }

    public int ChoiceIndex { get; set; }

    public IReadOnlyList<string> CompatibilityWarnings { get; set; } = [];

    public InferenceRequest WithChoiceIndex(int choiceIndex)
    {
        var copy = new InferenceRequest();
        CopyTo(copy);
        copy.ChoiceIndex = choiceIndex;
        return copy;
    }

    public InferenceRequest WithMessages(
        IReadOnlyList<InferenceMessage> messages,
        IReadOnlyList<string>? compatibilityWarnings = null)
    {
        var copy = new InferenceRequest();
        CopyTo(copy);
        copy.Messages = messages;
        copy.CompatibilityWarnings = compatibilityWarnings ?? CompatibilityWarnings;
        return copy;
    }

    public InferenceRequest WithCompatibilityWarnings(IReadOnlyList<string> compatibilityWarnings)
    {
        return WithMessages(Messages, compatibilityWarnings);
    }

    private void CopyTo(InferenceRequest target)
    {
        target.RequestedModel = RequestedModel;
        target.Messages = Messages;
        target.Tools = Tools;
        target.ToolChoiceDescription = ToolChoiceDescription;
        target.N = N;
        target.MaxToolCalls = MaxToolCalls;
        target.MaxTokens = MaxTokens;
        target.Temperature = Temperature;
        target.TopP = TopP;
        target.TopK = TopK;
        target.PresencePenalty = PresencePenalty;
        target.FrequencyPenalty = FrequencyPenalty;
        target.Seed = Seed;
        target.Stop = Stop;
        target.ForceJson = ForceJson;
        target.JsonSchema = JsonSchema;
        target.StrictJsonSchema = StrictJsonSchema;
        target.StreamIncludeUsage = StreamIncludeUsage;
        target.Store = Store;
        target.User = User;
        target.ServiceTier = ServiceTier;
        target.ParallelToolCalls = ParallelToolCalls;
        target.PreviousResponseId = PreviousResponseId;
        target.Truncation = Truncation;
        target.Metadata = Metadata;
        target.Include = Include;
        target.ReasoningEffort = ReasoningEffort;
        target.Prompt = Prompt;
        target.LogitBias = LogitBias;
        target.ChoiceIndex = ChoiceIndex;
        target.CompatibilityWarnings = CompatibilityWarnings;
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

public sealed record ModelMemoryInfo(long WeightBytes, long ContextBytes, long TotalBytes, bool IsLoaded);

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

    public int EmbeddingDimensions { get; init; }
}
