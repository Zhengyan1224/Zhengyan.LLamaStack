using Zhengyan.LLamaStack.Api.Inference;

namespace Zhengyan.LLamaStack.Api.Storage;

public interface IOpenAiStore
{
    Task AddChatCompletionAsync(string id, long created, InferenceRequest request, InferenceCompletion completion, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredChatCompletion>> ListChatCompletionsAsync(int limit, string? after, string? before, CancellationToken cancellationToken);

    Task<StoredChatCompletion?> GetChatCompletionAsync(string id, CancellationToken cancellationToken);

    Task<StoredChatCompletion?> UpdateChatCompletionMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

    Task<bool> DeleteChatCompletionAsync(string id, CancellationToken cancellationToken);

    Task AddResponseAsync(string id, long createdAt, InferenceRequest request, InferenceCompletion completion, CancellationToken cancellationToken);

    Task AddResponseAsync(StoredResponse response, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredResponse>> ListResponsesAsync(int limit, string? after, string? before, CancellationToken cancellationToken);

    Task<StoredResponse?> GetResponseAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteResponseAsync(string id, CancellationToken cancellationToken);

    Task<StoredResponse?> CancelResponseAsync(string id, CancellationToken cancellationToken);

    Task<StoredResponse?> UpdateResponseMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);
}
