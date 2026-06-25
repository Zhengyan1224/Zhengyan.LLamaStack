using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Options;

namespace Zhengyan.LLamaStack.Api.Storage;

public sealed class OpenAiRedisStore : IOpenAiStore
{
    private const string ChatCompletionsSortedSet = "chat:completions:by_created";
    private const string ResponsesSortedSet = "responses:by_created";
    private const string ChatCompletionPrefix = "chat:completion:";
    private const string ResponsePrefix = "response:";

    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public OpenAiRedisStore(IOptions<LLamaStackOptions> options)
    {
        var config = options.Value.Store.ConnectionString ?? "localhost:6379";
        _redis = ConnectionMultiplexer.Connect(config);
        _db = _redis.GetDatabase();
    }

    public async Task AddChatCompletionAsync(
        string id,
        long created,
        InferenceRequest request,
        InferenceCompletion completion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ChatCompletionPrefix + id;
        var entries = new HashEntry[]
        {
            new("id", id),
            new("created", created),
            new("model", completion.Model ?? string.Empty),
            new("metadata_json", SerializeNullable(completion.Metadata)),
            new("user", completion.User ?? string.Empty),
            new("service_tier", completion.ServiceTier ?? string.Empty),
            new("store", "1"),
            new("messages_json", Serialize(request.Messages)),
            new("output_text", completion.Text ?? string.Empty),
            new("tool_calls_json", Serialize(completion.ToolCalls)),
            new("finish_reason", completion.FinishReason ?? "stop"),
            new("prompt_tokens", completion.PromptTokens.ToString()),
            new("completion_tokens", completion.CompletionTokens.ToString()),
            new("compatibility_warnings_json", Serialize(completion.CompatibilityWarnings))
        };

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(key, entries);
        _ = tran.SortedSetAddAsync(ChatCompletionsSortedSet, id, created);
        await tran.ExecuteAsync();
    }

    public async Task<StoredListResult<StoredChatCompletion>> ListChatCompletionsAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeLimit = Math.Clamp(limit, 1, 100);
        var ids = await FetchSortedIdsAsync(ChatCompletionsSortedSet, safeLimit, after, before);
        var all = new List<StoredChatCompletion>();
        foreach (var id in ids)
        {
            var completion = await GetChatCompletionAsync(id, cancellationToken);
            if (completion is not null)
            {
                all.Add(completion);
            }
        }

        var hasMore = ids.Count > safeLimit;
        return new StoredListResult<StoredChatCompletion>(all, hasMore);
    }

    public async Task<StoredChatCompletion?> GetChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ChatCompletionPrefix + id;
        var entries = await _db.HashGetAllAsync(key);
        if (entries.Length == 0) return null;

        var dict = entries.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
        return new StoredChatCompletion
        {
            Id = GetString(dict, "id"),
            Created = GetLong(dict, "created"),
            Model = GetString(dict, "model"),
            Metadata = DeserializeNullable<IReadOnlyDictionary<string, string>>(dict, "metadata_json"),
            User = GetStringOrNull(dict, "user"),
            ServiceTier = GetStringOrNull(dict, "service_tier"),
            Store = GetString(dict, "store") == "1",
            Messages = Deserialize<IReadOnlyList<InferenceMessage>>(dict, "messages_json"),
            OutputText = GetString(dict, "output_text"),
            ToolCalls = Deserialize<IReadOnlyList<OpenAiToolCall>>(dict, "tool_calls_json"),
            FinishReason = GetString(dict, "finish_reason"),
            PromptTokens = GetInt(dict, "prompt_tokens"),
            CompletionTokens = GetInt(dict, "completion_tokens"),
            CompatibilityWarnings = Deserialize<IReadOnlyList<string>>(dict, "compatibility_warnings_json")
        };
    }

    public async Task<StoredChatCompletion?> UpdateChatCompletionMetadataAsync(
        string id,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ChatCompletionPrefix + id;
        var exists = await _db.KeyExistsAsync(key);
        if (!exists) return null;

        await _db.HashSetAsync(key, new[] { new HashEntry("metadata_json", SerializeNullable(metadata)) });
        return await GetChatCompletionAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ChatCompletionPrefix + id;
        var tran = _db.CreateTransaction();
        _ = tran.KeyDeleteAsync(key);
        _ = tran.SortedSetRemoveAsync(ChatCompletionsSortedSet, id);
        return await tran.ExecuteAsync();
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

    public async Task AddResponseAsync(StoredResponse response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ResponsePrefix + response.Id;
        var entries = new HashEntry[]
        {
            new("id", response.Id),
            new("created_at", response.CreatedAt),
            new("status", response.Status ?? "completed"),
            new("model", response.Model ?? string.Empty),
            new("metadata_json", SerializeNullable(response.Metadata)),
            new("user", response.User ?? string.Empty),
            new("service_tier", response.ServiceTier ?? string.Empty),
            new("store", response.Store ? "1" : "0"),
            new("previous_response_id", response.PreviousResponseId ?? string.Empty),
            new("input_messages_json", Serialize(response.InputMessages)),
            new("output_text", response.OutputText ?? string.Empty),
            new("tool_calls_json", Serialize(response.ToolCalls)),
            new("input_tokens", response.InputTokens.ToString()),
            new("output_tokens", response.OutputTokens.ToString()),
            new("compatibility_warnings_json", Serialize(response.CompatibilityWarnings))
        };

        var tran = _db.CreateTransaction();
        _ = tran.HashSetAsync(key, entries);
        _ = tran.SortedSetAddAsync(ResponsesSortedSet, response.Id, response.CreatedAt);
        await tran.ExecuteAsync();
    }

    public async Task<StoredListResult<StoredResponse>> ListResponsesAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safeLimit = Math.Clamp(limit, 1, 100);
        var ids = await FetchSortedIdsAsync(ResponsesSortedSet, safeLimit, after, before);
        var all = new List<StoredResponse>();
        foreach (var id in ids)
        {
            var response = await GetResponseAsync(id, cancellationToken);
            if (response is not null)
            {
                all.Add(response);
            }
        }

        var hasMore = ids.Count > safeLimit;
        return new StoredListResult<StoredResponse>(all, hasMore);
    }

    public async Task<StoredResponse?> GetResponseAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ResponsePrefix + id;
        var entries = await _db.HashGetAllAsync(key);
        if (entries.Length == 0) return null;

        var dict = entries.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);
        return new StoredResponse
        {
            Id = GetString(dict, "id"),
            CreatedAt = GetLong(dict, "created_at"),
            Status = GetString(dict, "status"),
            Model = GetString(dict, "model"),
            Metadata = DeserializeNullable<IReadOnlyDictionary<string, string>>(dict, "metadata_json"),
            User = GetStringOrNull(dict, "user"),
            ServiceTier = GetStringOrNull(dict, "service_tier"),
            Store = GetString(dict, "store") == "1",
            PreviousResponseId = GetStringOrNull(dict, "previous_response_id"),
            InputMessages = Deserialize<IReadOnlyList<InferenceMessage>>(dict, "input_messages_json"),
            OutputText = GetString(dict, "output_text"),
            ToolCalls = Deserialize<IReadOnlyList<OpenAiToolCall>>(dict, "tool_calls_json"),
            InputTokens = GetInt(dict, "input_tokens"),
            OutputTokens = GetInt(dict, "output_tokens"),
            CompatibilityWarnings = Deserialize<IReadOnlyList<string>>(dict, "compatibility_warnings_json")
        };
    }

    public async Task<bool> DeleteResponseAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ResponsePrefix + id;
        var tran = _db.CreateTransaction();
        _ = tran.KeyDeleteAsync(key);
        _ = tran.SortedSetRemoveAsync(ResponsesSortedSet, id);
        return await tran.ExecuteAsync();
    }

    public async Task<StoredResponse?> CancelResponseAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ResponsePrefix + id;
        var exists = await _db.KeyExistsAsync(key);
        if (!exists) return null;

        await _db.HashSetAsync(key, new[] { new HashEntry("status", "cancelled") });
        return await GetResponseAsync(id, cancellationToken);
    }

    public async Task<StoredResponse?> UpdateResponseMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ResponsePrefix + id;
        var exists = await _db.KeyExistsAsync(key);
        if (!exists) return null;

        await _db.HashSetAsync(key, new[] { new HashEntry("metadata_json", SerializeNullable(metadata)) });
        return await GetResponseAsync(id, cancellationToken);
    }

    private async Task<List<string>> FetchSortedIdsAsync(string sortedSetKey, int limit, string? after, string? before)
    {
        var all = await _db.SortedSetRangeByRankAsync(sortedSetKey, order: Order.Descending);
        var ids = all.Select(x => (string)x!).ToList();

        if (!string.IsNullOrWhiteSpace(after))
        {
            var index = ids.IndexOf(after);
            if (index >= 0)
            {
                ids = ids.Skip(index + 1).ToList();
            }
        }

        if (!string.IsNullOrWhiteSpace(before))
        {
            var index = ids.IndexOf(before);
            if (index >= 0)
            {
                ids = ids.Take(index).ToList();
            }
        }

        var result = ids.Take(limit).ToList();
        return result;
    }

    private static string GetString(IReadOnlyDictionary<string, string?> dict, string key)
    {
        return dict.GetValueOrDefault(key) ?? string.Empty;
    }

    private static string? GetStringOrNull(IReadOnlyDictionary<string, string?> dict, string key)
    {
        var value = dict.GetValueOrDefault(key);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static long GetLong(IReadOnlyDictionary<string, string?> dict, string key)
    {
        var value = GetString(dict, key);
        return long.TryParse(value, out var result) ? result : 0;
    }

    private static int GetInt(IReadOnlyDictionary<string, string?> dict, string key)
    {
        var value = GetString(dict, key);
        return int.TryParse(value, out var result) ? result : 0;
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, OpenAiJson.CreateOptions());
    }

    private static string? SerializeNullable<T>(T? value)
    {
        return value is null ? null : Serialize(value);
    }

    private static T Deserialize<T>(IReadOnlyDictionary<string, string?> dict, string key)
    {
        var json = GetString(dict, key);
        return JsonSerializer.Deserialize<T>(json, OpenAiJson.CreateOptions())!;
    }

    private static T? DeserializeNullable<T>(IReadOnlyDictionary<string, string?> dict, string key)
    {
        var json = GetStringOrNull(dict, key);
        return json is null ? default : JsonSerializer.Deserialize<T>(json, OpenAiJson.CreateOptions());
    }

    public Task AddResponseTaskAsync(ResponseTaskInfo task, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Response task management is not supported in Redis store. Use Memory store instead.");
    }

    public Task<ResponseTaskInfo?> GetResponseTaskAsync(string id, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Response task management is not supported in Redis store. Use Memory store instead.");
    }

    public Task UpdateResponseTaskAsync(string id, ResponseTaskStatus status, string? resultResponseId = null, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Response task management is not supported in Redis store. Use Memory store instead.");
    }

    public Task<StoredListResult<ResponseTaskInfo>> ListResponseTasksAsync(int limit, string? after, string? before, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Response task management is not supported in Redis store. Use Memory store instead.");
    }
}
