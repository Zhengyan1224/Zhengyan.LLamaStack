using System.Data;
using System.Text.Json;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class CalculatorTool : IAgentTool
{
    public string Name => "calculator";

    public string? Description => "Evaluates a mathematical expression and returns the computed result. Supports basic arithmetic: +, -, *, /, and parentheses.";

    public JsonElement? Parameters { get; }

    public int TimeoutSeconds => 15;

    public IReadOnlyList<string> RequiredPermissions => [];

    public CalculatorTool()
    {
        Parameters = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "expression": {
                        "type": "string",
                        "description": "The mathematical expression to evaluate (e.g. \"2 + 3 * 4\")"
                    }
                },
                "required": ["expression"]
            }
            """).RootElement;
    }

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var expression = document.RootElement.TryGetProperty("expression", out var expr)
                ? expr.GetString()
                : document.RootElement.TryGetProperty("input", out var input)
                    ? input.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(expression))
            {
                return Task.FromResult("Error: missing `expression` field.");
            }

            var result = new DataTable().Compute(expression, null);
            return Task.FromResult(result?.ToString() ?? "Error: could not compute.");
        }
        catch (Exception exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }
}

public sealed class CurrentTimeTool : IAgentTool
{
    public string Name => "current_time";

    public string? Description => "Returns the current date and time. Optionally supports timezone conversion by IANA timezone name (e.g. \"Asia/Shanghai\", \"America/New_York\").";

    public JsonElement? Parameters { get; }

    public int TimeoutSeconds => 10;

    public IReadOnlyList<string> RequiredPermissions => [];

    public CurrentTimeTool()
    {
        Parameters = JsonDocument.Parse("""
            {
                "type": "object",
                "properties": {
                    "timezone": {
                        "type": "string",
                        "description": "Optional IANA timezone name (e.g. \"Asia/Shanghai\", \"America/New_York\"). Defaults to UTC if not provided."
                    }
                }
            }
            """).RootElement;
    }

    public Task<string> ExecuteAsync(string argumentsJson, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var timezone = document.RootElement.TryGetProperty("timezone", out var tz)
                ? tz.GetString()
                : null;

            var now = string.IsNullOrWhiteSpace(timezone)
                ? DateTimeOffset.UtcNow
                : TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TryGetTimeZone(timezone));

            return Task.FromResult(JsonSerializer.Serialize(new
            {
                utc_unix_seconds = now.ToUnixTimeSeconds(),
                iso_8601 = now.ToString("o"),
                timezone = timezone ?? "UTC"
            }));
        }
        catch (Exception exception)
        {
            return Task.FromResult($"Error: {exception.Message}");
        }
    }

    private static TimeZoneInfo TryGetTimeZone(string timezone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }
}
