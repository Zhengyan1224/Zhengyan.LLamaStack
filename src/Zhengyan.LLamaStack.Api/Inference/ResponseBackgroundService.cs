using System.Threading.Channels;
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed record BackgroundWorkItem(
    string ResponseId,
    InferenceRequest InferenceRequest,
    string ModelId,
    int MaxConcurrency);

public sealed class ResponseBackgroundService : BackgroundService
{
    private readonly Channel<BackgroundWorkItem> _channel;
    private readonly LLamaInferenceService _inference;
    private readonly IOpenAiStore _store;
    private readonly ModelQueueManager _queueManager;
    private readonly ResponseExecutionTracker _tracker;
    private readonly ILogger<ResponseBackgroundService> _logger;

    public ResponseBackgroundService(
        LLamaInferenceService inference,
        IOpenAiStore store,
        ModelQueueManager queueManager,
        ResponseExecutionTracker tracker,
        ILogger<ResponseBackgroundService> logger)
    {
        _inference = inference;
        _store = store;
        _queueManager = queueManager;
        _tracker = tracker;
        _logger = logger;
        _channel = Channel.CreateBounded<BackgroundWorkItem>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public async Task<string> EnqueueAsync(BackgroundWorkItem work, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(work, cancellationToken);
        _logger.LogDebug("Background response {ResponseId} enqueued", work.ResponseId);
        return work.ResponseId;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ResponseBackgroundService started");

        await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessWorkAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background response {ResponseId} failed with unexpected error", work.ResponseId);
                try
                {
                    var failed = await _store.GetResponseAsync(work.ResponseId, CancellationToken.None);
                    if (failed is not null)
                    {
                        var updated = failed with
                        {
                            Status = "failed",
                            OutputText = $"Background execution failed: {ex.Message}"
                        };
                        await _store.AddResponseAsync(updated, CancellationToken.None);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task ProcessWorkAsync(BackgroundWorkItem work, CancellationToken stoppingToken)
    {
        _logger.LogDebug("Processing background response {ResponseId}", work.ResponseId);

        var queue = _queueManager.GetOrCreate(work.ModelId, work.MaxConcurrency);
        var queueEntry = queue.Enqueue(work.ModelId);

        try
        {
            await queueEntry.TurnTcs.Task.WaitAsync(stoppingToken);

            using var linkedCts = _tracker.Track(work.ResponseId, stoppingToken);
            var token = linkedCts.Token;

            try
            {
                _logger.LogDebug("Background response {ResponseId} starting inference", work.ResponseId);
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var completion = await _inference.CompleteAsync(work.InferenceRequest, token);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                await _store.AddResponseAsync(work.ResponseId, now, work.InferenceRequest, completion, token);

                _logger.LogInformation("Background response {ResponseId} completed", work.ResponseId);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("Background response {ResponseId} was cancelled", work.ResponseId);
                var current = await _store.GetResponseAsync(work.ResponseId, CancellationToken.None);
                if (current is not null)
                {
                    await _store.AddResponseAsync(current with { Status = "cancelled" }, CancellationToken.None);
                }
            }
            finally
            {
                _tracker.Untrack(work.ResponseId);
            }
        }
        finally
        {
            queue.RemoveEntry(queueEntry.Id);
        }
    }
}
