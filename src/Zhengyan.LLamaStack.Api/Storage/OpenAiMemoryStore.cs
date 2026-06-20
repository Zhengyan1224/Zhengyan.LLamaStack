using System.Collections.Concurrent;
using Zhengyan.LLamaStack.Api.Inference;

namespace Zhengyan.LLamaStack.Api.Storage;

public sealed class OpenAiMemoryStore : IOpenAiStore
{
    private readonly ConcurrentDictionary<string, StoredChatCompletion> _chatCompletions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, StoredResponse> _responses = new(StringComparer.Ordinal);

    public Task AddChatCompletionAsync(
        string id,
        long created,
        InferenceRequest request,
        InferenceCompletion completion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _chatCompletions[id] = new StoredChatCompletion
        {
            Id = id,
            Created = created,
            Model = completion.Model,
            Metadata = completion.Metadata,
            User = completion.User,
            ServiceTier = completion.ServiceTier,
            Store = true,
            Messages = request.Messages,
            OutputText = completion.Text,
            ToolCalls = completion.ToolCalls,
            FinishReason = completion.FinishReason,
            PromptTokens = completion.PromptTokens,
            CompletionTokens = completion.CompletionTokens,
            CompatibilityWarnings = completion.CompatibilityWarnings
        };

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredChatCompletion>> ListChatCompletionsAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenAiStoreHelpers.ApplyCursor(_chatCompletions.Values, x => x.Id, x => x.Created, limit, after, before));
    }

    public Task<StoredChatCompletion?> GetChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _chatCompletions.TryGetValue(id, out var completion);
        return Task.FromResult(completion);
    }

    public Task<StoredChatCompletion?> UpdateChatCompletionMetadataAsync(
        string id,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        while (_chatCompletions.TryGetValue(id, out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = current with { Metadata = metadata };
            if (_chatCompletions.TryUpdate(id, updated, current))
            {
                return Task.FromResult<StoredChatCompletion?>(updated);
            }
        }

        return Task.FromResult<StoredChatCompletion?>(null);
    }

    public Task<bool> DeleteChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_chatCompletions.TryRemove(id, out _));
    }

    public Task AddResponseAsync(
        string id,
        long createdAt,
        InferenceRequest request,
        InferenceCompletion completion,
        CancellationToken cancellationToken)
    {
        return AddResponseAsync(new StoredResponse
        {
            Id = id,
            CreatedAt = createdAt,
            Status = "completed",
            Model = completion.Model,
            Metadata = completion.Metadata,
            User = completion.User,
            ServiceTier = completion.ServiceTier,
            Store = request.Store ?? true,
            PreviousResponseId = request.PreviousResponseId,
            InputMessages = request.Messages,
            OutputText = completion.Text,
            ToolCalls = completion.ToolCalls,
            InputTokens = completion.PromptTokens,
            OutputTokens = completion.CompletionTokens,
            CompatibilityWarnings = completion.CompatibilityWarnings
        }, cancellationToken);
    }

    public Task AddResponseAsync(StoredResponse response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _responses[response.Id] = response;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredResponse>> ListResponsesAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OpenAiStoreHelpers.ApplyCursor(_responses.Values, x => x.Id, x => x.CreatedAt, limit, after, before));
    }

    public Task<StoredResponse?> GetResponseAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _responses.TryGetValue(id, out var response);
        return Task.FromResult(response);
    }

    public Task<bool> DeleteResponseAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responses.TryRemove(id, out _));
    }

    public Task<StoredResponse?> CancelResponseAsync(string id, CancellationToken cancellationToken)
    {
        while (_responses.TryGetValue(id, out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = current with { Status = "cancelled" };
            if (_responses.TryUpdate(id, updated, current))
            {
                return Task.FromResult<StoredResponse?>(updated);
            }
        }

        return Task.FromResult<StoredResponse?>(null);
    }

    public Task<StoredResponse?> UpdateResponseMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        while (_responses.TryGetValue(id, out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = current with { Metadata = metadata };
            if (_responses.TryUpdate(id, updated, current))
            {
                return Task.FromResult<StoredResponse?>(updated);
            }
        }

        return Task.FromResult<StoredResponse?>(null);
    }
}
