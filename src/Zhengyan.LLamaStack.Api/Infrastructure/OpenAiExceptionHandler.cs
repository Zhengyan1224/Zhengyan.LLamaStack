using System.Text.Json;
using LLama.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Zhengyan.LLamaStack.Api.OpenAi;

namespace Zhengyan.LLamaStack.Api.Infrastructure;

public sealed class OpenAiExceptionHandler : IExceptionHandler
{
    private readonly ILogger<OpenAiExceptionHandler> _logger;

    public OpenAiExceptionHandler(ILogger<OpenAiExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OpenAiProtocolException protocol)
        {
            httpContext.Response.StatusCode = protocol.StatusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            var envelope = new OpenAiErrorEnvelope
            {
                Error = new OpenAiError
                {
                    Message = protocol.Message,
                    Type = protocol.Type,
                    Code = protocol.Code,
                    Param = protocol.Param
                }
            };
            await httpContext.Response.WriteAsync(
                JsonSerializer.Serialize(envelope, OpenAiJson.CreateOptions()),
                cancellationToken);
            return true;
        }

        if (exception is ContextOverflowException contextOverflow)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            httpContext.Response.ContentType = "application/json; charset=utf-8";
            var envelope = new OpenAiErrorEnvelope
            {
                Error = new OpenAiError
                {
                    Message = "The request exceeded the configured context window during generation. Reduce the prompt/history/tool definitions, lower max_tokens, or increase LLamaStack:Models[].ContextSize. LLamaSharp detail: " + contextOverflow.Message,
                    Type = "invalid_request_error",
                    Code = "context_length_exceeded",
                    Param = "messages"
                }
            };
            await httpContext.Response.WriteAsync(
                JsonSerializer.Serialize(envelope, OpenAiJson.CreateOptions()),
                cancellationToken);
            return true;
        }

        _logger.LogError(exception, "Unhandled exception processing request {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        var fallback = new OpenAiErrorEnvelope
        {
            Error = new OpenAiError
            {
                Message = "An internal server error occurred.",
                Type = "server_error",
                Code = "internal_error"
            }
        };
        await httpContext.Response.WriteAsync(
            JsonSerializer.Serialize(fallback, OpenAiJson.CreateOptions()),
            cancellationToken);
        return true;
    }
}
