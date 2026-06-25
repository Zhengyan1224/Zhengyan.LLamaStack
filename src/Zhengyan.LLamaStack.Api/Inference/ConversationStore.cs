using System.Collections.Concurrent;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ConversationInfo
{
    public string Id { get; init; } = string.Empty;

    public List<string> ResponseIds { get; init; } = [];

    public long CreatedAt { get; init; }

    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class ConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationInfo> _conversations = new(StringComparer.Ordinal);

    public ConversationInfo GetOrCreate(string conversationId, IReadOnlyDictionary<string, string>? metadata = null)
    {
        return _conversations.GetOrAdd(conversationId, id => new ConversationInfo
        {
            Id = id,
            ResponseIds = [],
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Metadata = metadata
        });
    }

    public ConversationInfo? Get(string conversationId)
    {
        _conversations.TryGetValue(conversationId, out var info);
        return info;
    }

    public void AddResponse(string conversationId, string responseId)
    {
        if (_conversations.TryGetValue(conversationId, out var info))
        {
            lock (info.ResponseIds)
            {
                info.ResponseIds.Add(responseId);
            }
        }
    }

    public string? GetLastResponseId(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var info))
        {
            lock (info.ResponseIds)
            {
                return info.ResponseIds.Count > 0 ? info.ResponseIds[^1] : null;
            }
        }

        return null;
    }

    public IReadOnlyList<string> GetResponseIds(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var info))
        {
            lock (info.ResponseIds)
            {
                return info.ResponseIds.ToArray();
            }
        }

        return [];
    }

    public IReadOnlyList<ConversationInfo> GetAll()
    {
        return _conversations.Values.ToArray();
    }
}
