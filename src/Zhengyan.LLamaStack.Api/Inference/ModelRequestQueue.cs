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
    private bool _processing;

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
            entry.Position = _queue.Count + (_processing ? 1 : 0);
            _queue.AddLast(entry);

            if (!_processing)
            {
                _processing = true;
                entry.Status = "processing";
                entry.TurnTcs.TrySetResult();
            }
        }

        return entry;
    }

    public void Dequeue()
    {
        lock (_lock)
        {
            _queue.RemoveFirst();

            if (_queue.First is null)
            {
                _processing = false;
                return;
            }

            var pos = 1;
            foreach (var node in _queue)
            {
                node.Position = pos++;
            }

            var next = _queue.First.Value;
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

    public void RemoveEntry(string entryId)
    {
        lock (_lock)
        {
            var node = _queue.First;
            while (node is not null)
            {
                if (string.Equals(node.Value.Id, entryId, StringComparison.Ordinal))
                {
                    if (node == _queue.First && _processing)
                    {
                        _processing = false;
                    }

                    _queue.Remove(node);

                    if (_queue.First is not null && !_processing)
                    {
                        _processing = true;
                        _queue.First.Value.Status = "processing";
                        _queue.First.Value.TurnTcs.TrySetResult();
                    }

                    var pos = 0;
                    foreach (var n in _queue)
                    {
                        n.Position = pos++;
                    }

                    return;
                }

                node = node.Next;
            }
        }
    }
}

public sealed class ModelQueueManager
{
    private readonly ConcurrentDictionary<string, ModelRequestQueue> _queues = new(StringComparer.OrdinalIgnoreCase);

    public ModelRequestQueue GetOrCreate(string modelId)
    {
        return _queues.GetOrAdd(modelId, _ => new ModelRequestQueue());
    }

    public IReadOnlyCollection<KeyValuePair<string, ModelRequestQueue>> GetAll()
    {
        return _queues.ToArray();
    }
}