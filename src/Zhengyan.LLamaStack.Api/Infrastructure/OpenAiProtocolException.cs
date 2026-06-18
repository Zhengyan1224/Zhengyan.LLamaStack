namespace Zhengyan.LLamaStack.Api.Infrastructure;

public sealed class OpenAiProtocolException : Exception
{
    public OpenAiProtocolException(
        int statusCode,
        string message,
        string type = "invalid_request_error",
        string? code = null,
        string? param = null)
        : base(message)
    {
        StatusCode = statusCode;
        Type = type;
        Code = code;
        Param = param;
    }

    public int StatusCode { get; }

    public string Type { get; }

    public string? Code { get; }

    public string? Param { get; }
}
