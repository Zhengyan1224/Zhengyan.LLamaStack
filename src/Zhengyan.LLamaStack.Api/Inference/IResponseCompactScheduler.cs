namespace Zhengyan.LLamaStack.Api.Inference;

public interface IResponseCompactScheduler
{
    Task<string> ScheduleCompactAsync(string sourceResponseId, string? instructions, CancellationToken cancellationToken);

    Task<ResponseTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken);
}
