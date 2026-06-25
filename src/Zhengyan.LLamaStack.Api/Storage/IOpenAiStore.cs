using Zhengyan.LLamaStack.Api.Inference;

namespace Zhengyan.LLamaStack.Api.Storage;

public interface IOpenAiStore
{
    Task AddChatCompletionAsync(string id, long created, InferenceRequest request, InferenceCompletion completion, CancellationToken cancellationToken);

    Task<StoredListResult<StoredChatCompletion>> ListChatCompletionsAsync(int limit, string? after, string? before, CancellationToken cancellationToken);

    Task<StoredChatCompletion?> GetChatCompletionAsync(string id, CancellationToken cancellationToken);

    Task<StoredChatCompletion?> UpdateChatCompletionMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

    Task<bool> DeleteChatCompletionAsync(string id, CancellationToken cancellationToken);

    Task AddResponseAsync(string id, long createdAt, InferenceRequest request, InferenceCompletion completion, CancellationToken cancellationToken);

    Task AddResponseAsync(StoredResponse response, CancellationToken cancellationToken);

    Task<StoredListResult<StoredResponse>> ListResponsesAsync(int limit, string? after, string? before, CancellationToken cancellationToken);

    Task<StoredResponse?> GetResponseAsync(string id, CancellationToken cancellationToken);

    Task<bool> DeleteResponseAsync(string id, CancellationToken cancellationToken);

    Task<StoredResponse?> CancelResponseAsync(string id, CancellationToken cancellationToken);

    Task<StoredResponse?> UpdateResponseMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

    Task AddResponseTaskAsync(ResponseTaskInfo task, CancellationToken cancellationToken);

    Task<ResponseTaskInfo?> GetResponseTaskAsync(string id, CancellationToken cancellationToken);

    Task UpdateResponseTaskAsync(string id, ResponseTaskStatus status, string? resultResponseId = null, string? errorMessage = null, CancellationToken cancellationToken = default);

    Task<StoredListResult<ResponseTaskInfo>> ListResponseTasksAsync(int limit, string? after, string? before, CancellationToken cancellationToken);
}

public sealed record StoredListResult<T>(IReadOnlyList<T> Items, bool HasMore);
