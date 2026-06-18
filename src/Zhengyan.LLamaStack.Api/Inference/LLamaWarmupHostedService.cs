using Zhengyan.LLamaStack.Api.Infrastructure;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class LLamaWarmupHostedService : IHostedService
{
    private readonly LLamaInferenceService _inference;
    private readonly ILogger<LLamaWarmupHostedService> _logger;

    public LLamaWarmupHostedService(
        LLamaInferenceService inference,
        ILogger<LLamaWarmupHostedService> logger)
    {
        _inference = inference;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inference.WarmupAsync(cancellationToken);
        }
        catch (OpenAiProtocolException exception)
        {
            _logger.LogError(exception, "Failed to load model on startup: {Message}", exception.Message);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
