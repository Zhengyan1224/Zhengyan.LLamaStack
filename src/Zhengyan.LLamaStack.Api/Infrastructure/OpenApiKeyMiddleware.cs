using System.Text.Json;
using Microsoft.Extensions.Options;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Options;

namespace Zhengyan.LLamaStack.Api.Infrastructure;

public sealed class OpenApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LLamaAuthOptions _options;

    public OpenApiKeyMiddleware(RequestDelegate next, IOptions<LLamaStackOptions> options)
    {
        _next = next;
        _options = options.Value.Auth;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            await _next(context);
            return;
        }

        var headerName = string.IsNullOrWhiteSpace(_options.ApiKeyHeader)
            ? "Authorization"
            : _options.ApiKeyHeader;

        if (!context.Request.Headers.TryGetValue(headerName, out var authHeader))
        {
            await WriteUnauthorized(context, $"Missing {headerName} header.");
            return;
        }

        var value = authHeader.ToString();
        const string prefix = "Bearer ";
        var apiKey = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].Trim()
            : value.Trim();

        if (!string.Equals(apiKey, _options.ApiKey, StringComparison.Ordinal))
        {
            await WriteUnauthorized(context, "Invalid API key.");
            return;
        }

        await _next(context);
    }

    private static async Task WriteUnauthorized(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        var envelope = new OpenAiErrorEnvelope
        {
            Error = new OpenAiError
            {
                Message = message,
                Type = "invalid_request_error",
                Code = "invalid_api_key"
            }
        };
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(envelope, OpenAiJson.CreateOptions()));
    }
}
