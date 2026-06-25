using System.Collections.Concurrent;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class QueueEntry
{
    public string Id { get; init; } = string.Empty;
    public int Position { get; set; }
    public string ModelId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string Status { get; set; } = "queued";
    public TaskCompletionSource TurnTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ModelRequestQueue
{
    private readonly object _lock = new();
    private readonly LinkedList<QueueEntry> _queue = new();
    private int _maxConcurrency;
    private int _activeCount;

    public ModelRequestQueue(int maxConcurrency = 1)
    {
        _maxConcurrency = Math.Max(1, maxConcurrency);
    }

    public QueueEntry Enqueue(string modelId)
    {
        var entry = new QueueEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            ModelId = modelId,
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            entry.Position = _queue.Count + 1;
            _queue.AddLast(entry);

            if (_activeCount < _maxConcurrency)
            {
                _activeCount++;
                entry.Status = "processing";
                entry.TurnTcs.TrySetResult();
            }
        }

        return entry;
    }

    public void RemoveEntry(string entryId)
    {
        lock (_lock)
        {
            var node = _queue.First;
            while (node is not null)
            {
                if (string.Equals(node.Value.Id, entryId, StringComparison.Ordinal))
                {
                    _queue.Remove(node);

                    if (node.Value.Status == "processing")
                    {
                        _activeCount = Math.Max(0, _activeCount - 1);
                    }

                    SignalNext();
                    return;
                }

                node = node.Next;
            }
        }
    }

    private void SignalNext()
    {
        var pos = 0;
        foreach (var entry in _queue)
        {
            entry.Position = pos++;
        }

        while (_activeCount < _maxConcurrency)
        {
            var next = _queue.FirstOrDefault(e => e.Status == "queued");
            if (next is null)
                break;

            _activeCount++;
            next.Status = "processing";
            next.TurnTcs.TrySetResult();
        }
    }

    public QueueEntry? GetEntry(string entryId)
    {
        lock (_lock)
        {
            return _queue.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
        }
    }

    public void SetMaxConcurrency(int maxConcurrency)
    {
        lock (_lock)
        {
            _maxConcurrency = Math.Max(1, maxConcurrency);
            SignalNext();
        }
    }
}

public sealed class ModelQueueManager
{
    private readonly ConcurrentDictionary<string, ModelRequestQueue> _queues = new(StringComparer.OrdinalIgnoreCase);

    public ModelRequestQueue GetOrCreate(string modelId, int maxConcurrency = 1)
    {
        return _queues.GetOrAdd(modelId, _ => new ModelRequestQueue(maxConcurrency));
    }

    public IReadOnlyCollection<KeyValuePair<string, ModelRequestQueue>> GetAll()
    {
        return _queues.ToArray();
    }
}
