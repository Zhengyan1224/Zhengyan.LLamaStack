using Zhengyan.LLamaStack.Api.Inference;

namespace Zhengyan.LLamaStack.Api.Storage;

public static class OpenAiStoreHelpers
{
    public static int EstimateInputTokens(IReadOnlyList<InferenceMessage> messages)
    {
        var length = messages.Sum(x => x.Role.Length + x.Content.Length + x.Media.Count * 64);
        return Math.Max(1, (length + 3) / 4);
    }

    public static StoredResponse CreateCompactedResponse(string id, long createdAt, StoredResponse source, string? instructions)
    {
        var messages = new List<InferenceMessage>();
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            messages.Add(new InferenceMessage { Role = "system", Content = instructions });
        }

        messages.AddRange(source.InputMessages);
        messages.Add(new InferenceMessage
        {
            Role = "assistant",
            Content = source.OutputText
        });

        return new StoredResponse
        {
            Id = id,
            CreatedAt = createdAt,
            Status = "completed",
            Model = source.Model,
            Metadata = source.Metadata,
            User = source.User,
            ServiceTier = source.ServiceTier,
            Store = true,
            PreviousResponseId = source.Id,
            InputMessages = messages,
            OutputText = source.OutputText,
            ToolCalls = source.ToolCalls,
            InputTokens = EstimateInputTokens(messages),
            OutputTokens = source.OutputTokens,
            CompatibilityWarnings = source.CompatibilityWarnings
        };
    }

    public static CursorResult<T> ApplyCursor<T>(
        IEnumerable<T> values,
        Func<T, string> idSelector,
        Func<T, long> createdSelector,
        int limit,
        string? after,
        string? before)
    {
        var safeLimit = Math.Clamp(limit, 1, 100);
        var ordered = values
            .OrderByDescending(createdSelector)
            .ThenBy(idSelector, StringComparer.Ordinal)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(after))
        {
            var index = Array.FindIndex(ordered, x => string.Equals(idSelector(x), after, StringComparison.Ordinal));
            if (index >= 0)
            {
                ordered = ordered.Skip(index + 1).ToArray();
            }
        }

        if (!string.IsNullOrWhiteSpace(before))
        {
            var index = Array.FindIndex(ordered, x => string.Equals(idSelector(x), before, StringComparison.Ordinal));
            if (index >= 0)
            {
                ordered = ordered.Take(index).ToArray();
            }
        }

        var result = ordered.Take(safeLimit).ToArray();
        var hasMore = ordered.Length > safeLimit;
        return new CursorResult<T>(result, hasMore);
    }
}

public sealed record CursorResult<T>(IReadOnlyList<T> Items, bool HasMore);
