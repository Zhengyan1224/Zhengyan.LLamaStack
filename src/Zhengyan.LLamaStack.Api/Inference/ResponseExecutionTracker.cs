using System.Collections.Concurrent;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ResponseExecutionTracker
{
    private readonly ConcurrentDictionary<string, ExecutionState> _executions = new(StringComparer.Ordinal);

    public CancellationTokenSource Track(string responseId, CancellationToken parentToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var state = new ExecutionState(cts, DateTimeOffset.UtcNow);

        _executions[responseId] = state;

        cts.Token.Register(() =>
        {
            if (_executions.TryGetValue(responseId, out var existing) && existing.Cts == cts)
            {
                _executions.TryRemove(responseId, out _);
            }
        });

        return cts;
    }

    public bool Cancel(string responseId)
    {
        if (_executions.TryRemove(responseId, out var state))
        {
            try
            {
                state.Cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return false;
    }

    public void Untrack(string responseId)
    {
        if (_executions.TryRemove(responseId, out var state))
        {
            state.Cts.Dispose();
        }
    }

    public bool IsExecuting(string responseId)
    {
        return _executions.ContainsKey(responseId);
    }

    private sealed record ExecutionState(CancellationTokenSource Cts, DateTimeOffset StartedAt);
}
