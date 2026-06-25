using System.Threading.Channels;
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class ResponseCompactScheduler : BackgroundService, IResponseCompactScheduler
{
    private readonly IOpenAiStore _store;
    private readonly LLamaInferenceService _inference;
    private readonly ILogger<ResponseCompactScheduler> _logger;
    private readonly Channel<CompactWorkItem> _channel;

    public ResponseCompactScheduler(
        IOpenAiStore store,
        LLamaInferenceService inference,
        ILogger<ResponseCompactScheduler> logger)
    {
        _store = store;
        _inference = inference;
        _logger = logger;
        _channel = Channel.CreateBounded<CompactWorkItem>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    public async Task<string> ScheduleCompactAsync(string sourceResponseId, string? instructions, CancellationToken cancellationToken)
    {
        var taskId = "task_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var taskInfo = new ResponseTaskInfo
        {
            Id = taskId,
            Type = "compact",
            Status = ResponseTaskStatus.Pending,
            SourceResponseId = sourceResponseId,
            CreatedAt = now
        };

        await _store.AddResponseTaskAsync(taskInfo, cancellationToken);
        await _channel.Writer.WriteAsync(new CompactWorkItem(taskId, sourceResponseId, instructions), cancellationToken);

        _logger.LogDebug("Scheduled compact task {TaskId} for response {ResponseId}", taskId, sourceResponseId);
        return taskId;
    }

    public async Task<ResponseTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        return await _store.GetResponseTaskAsync(taskId, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ResponseCompactScheduler started");

        await foreach (var work in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessWorkItemAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compact task {TaskId} failed with unexpected error", work.TaskId);
                try
                {
                    await _store.UpdateResponseTaskAsync(work.TaskId, ResponseTaskStatus.Failed,
                        errorMessage: $"Unexpected error: {ex.Message}", cancellationToken: CancellationToken.None);
                }
                catch
                {
                    // ignore store error during failure reporting
                }
            }
        }
    }

    private async Task ProcessWorkItemAsync(CompactWorkItem work, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Processing compact task {TaskId}", work.TaskId);

        await _store.UpdateResponseTaskAsync(work.TaskId, ResponseTaskStatus.Running, cancellationToken: cancellationToken);

        var source = await _store.GetResponseAsync(work.SourceResponseId, cancellationToken);
        if (source is null)
        {
            await _store.UpdateResponseTaskAsync(work.TaskId, ResponseTaskStatus.Failed,
                errorMessage: $"Source response `{work.SourceResponseId}` was not found.",
                cancellationToken: cancellationToken);
            return;
        }

        var summary = await GenerateSummaryAsync(source, work.Instructions, cancellationToken);

        var compactedId = "resp_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var compacted = new StoredResponse
        {
            Id = compactedId,
            CreatedAt = now,
            Status = "completed",
            Model = source.Model,
            Metadata = source.Metadata,
            User = source.User,
            ServiceTier = source.ServiceTier,
            Store = true,
            PreviousResponseId = source.Id,
            InputMessages = BuildCompactedInputMessages(source, summary),
            OutputText = summary,
            ToolCalls = source.ToolCalls,
            InputTokens = summary.Length / 4,
            OutputTokens = 0,
            CompatibilityWarnings = source.CompatibilityWarnings
        };

        await _store.AddResponseAsync(compacted, cancellationToken);

        await _store.UpdateResponseTaskAsync(work.TaskId, ResponseTaskStatus.Completed,
            resultResponseId: compactedId, cancellationToken: cancellationToken);

        _logger.LogInformation("Compact task {TaskId} completed: {CompactedId}", work.TaskId, compactedId);
    }

    private async Task<string> GenerateSummaryAsync(StoredResponse source, string? instructions, CancellationToken cancellationToken)
    {
        var conversationText = string.Join("\n", source.InputMessages.Select(m => $"{m.Role}: {m.Content}"));
        if (!string.IsNullOrWhiteSpace(source.OutputText))
        {
            conversationText += $"\nassistant: {source.OutputText}";
        }

        var systemPrompt = string.IsNullOrWhiteSpace(instructions)
            ? "You are a conversation summarizer. Summarize the following conversation concisely, preserving all key facts, decisions, and context."
            : instructions;

        var summarizationRequest = new InferenceRequest
        {
            RequestedModel = source.Model,
            Messages =
            [
                new InferenceMessage { Role = "system", Content = systemPrompt },
                new InferenceMessage { Role = "user", Content = conversationText }
            ],
            MaxTokens = 1024,
            Temperature = 0.3f,
            ForceJson = false,
            MaxToolCalls = 0
        };

        try
        {
            var completion = await _inference.CompleteAsync(summarizationRequest, cancellationToken);
            var result = completion?.Text ?? string.Empty;
            return string.IsNullOrWhiteSpace(result)
                ? "Summary generation produced no output."
                : result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Model-driven summary failed for response {ResponseId}, falling back to snapshot", source.Id);
            return $"Summary unavailable (model error: {ex.Message}). Original output: {source.OutputText}";
        }
    }

    private static IReadOnlyList<InferenceMessage> BuildCompactedInputMessages(StoredResponse source, string summary)
    {
        var messages = new List<InferenceMessage>
        {
            new()
            {
                Role = "system",
                Content = "This is a compacted conversation. The following is a summary of the original conversation which has been compressed."
            },
            new()
            {
                Role = "system",
                Content = $"Original response id: {source.Id}, created at: {source.CreatedAt}"
            }
        };

        if (!string.IsNullOrWhiteSpace(summary))
        {
            messages.Add(new InferenceMessage
            {
                Role = "system",
                Content = $"Conversation summary: {summary}"
            });
        }

        return messages;
    }

    private sealed record CompactWorkItem(string TaskId, string SourceResponseId, string? Instructions);
}
