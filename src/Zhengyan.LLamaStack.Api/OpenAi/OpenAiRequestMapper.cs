using System.Buffers.Text;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Infrastructure;
using Zhengyan.LLamaStack.Api.Options;

namespace Zhengyan.LLamaStack.Api.OpenAi;

public sealed class OpenAiRequestMapper
{
    public const string MediaHttpClientName = "openai-media";

    private static readonly HashSet<string> ImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/bmp",
        "image/gif"
    };

    private static readonly HashSet<string> AudioMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/wav",
        "audio/mpeg",
        "audio/flac",
        "audio/ogg",
        "audio/mp4",
        "audio/aac",
        "audio/opus"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly LLamaStackOptions _options;

    public OpenAiRequestMapper(IHttpClientFactory httpClientFactory, IOptions<LLamaStackOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
    }

    public async Task<InferenceRequest> FromChatAsync(ChatCompletionRequest request, CancellationToken cancellationToken)
    {
        if (request.Messages.Count == 0)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`messages` must contain at least one message.", param: "messages");
        }

        var messages = new List<InferenceMessage>();
        foreach (var message in request.Messages)
        {
            messages.Add(await ConvertMessageAsync(message, cancellationToken));
        }

        var tools = MergeChatTools(request.Tools, request.Functions);
        var warnings = new List<string>();
        AddChatCompatibilityWarnings(request, warnings);
        AddWarningIf(request.ParallelToolCalls == true, warnings, "`parallel_tool_calls` is accepted but tool execution is not implemented yet.");
        AddWarningIf(!string.IsNullOrWhiteSpace(request.ServiceTier), warnings, "`service_tier` is accepted for compatibility and does not affect local inference.");
        return new InferenceRequest
        {
            RequestedModel = request.Model,
            Messages = messages,
            Tools = tools,
            ToolChoiceDescription = DescribeToolChoice(request.ToolChoice ?? request.FunctionCall),
            N = Math.Max(1, request.N ?? 1),
            MaxTokens = request.MaxCompletionTokens ?? request.MaxTokens,
            Temperature = ToSingle(request.Temperature),
            TopP = ToSingle(request.TopP),
            TopK = request.TopK,
            PresencePenalty = ToSingle(request.PresencePenalty),
            FrequencyPenalty = ToSingle(request.FrequencyPenalty),
            Seed = request.Seed is null ? null : unchecked((uint)request.Seed.Value),
            Stop = ParseStop(request.Stop),
            ForceJson = IsJsonResponseFormat(request.ResponseFormat),
            StreamIncludeUsage = request.StreamOptions?.IncludeUsage == true,
            Store = request.Store,
            User = request.User,
            ServiceTier = request.ServiceTier,
            ParallelToolCalls = request.ParallelToolCalls,
            Metadata = ParseMetadata(request.Metadata),
            CompatibilityWarnings = warnings
        };
    }

    public async Task<InferenceRequest> FromResponsesAsync(ResponsesRequest request, CancellationToken cancellationToken)
    {
        var messages = new List<InferenceMessage>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(new InferenceMessage { Role = "system", Content = request.Instructions });
        }

        if (request.Input is null)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` is required.", param: "input");
        }

        foreach (var message in await ConvertResponsesInputAsync(request.Input.Value, cancellationToken))
        {
            messages.Add(message);
        }

        ValidateUnsupportedResponsesFields(request);
        var warnings = new List<string>();
        AddWarningIf(request.Background == true, warnings, "`background` is accepted but background execution is not implemented yet.");
        AddWarningIf(request.ParallelToolCalls == true, warnings, "`parallel_tool_calls` is accepted but tool execution is not implemented yet.");
        AddWarningIf(!string.IsNullOrWhiteSpace(request.ServiceTier), warnings, "`service_tier` is accepted for compatibility and does not affect local inference.");
        AddWarningIf(request.Conversation is not null, warnings, "`conversation` is accepted but persistent conversations are not implemented yet.");
        AddWarningIf(request.Include is not null, warnings, "`include` is accepted but only basic output fields are returned.");
        AddWarningIf(request.Reasoning is not null, warnings, "`reasoning` is accepted but local reasoning controls are not implemented yet.");
        AddWarningIf(request.Prompt is not null, warnings, "`prompt` is accepted but prompt templates are not implemented yet.");
        return new InferenceRequest
        {
            RequestedModel = request.Model,
            Messages = messages,
            Tools = request.Tools ?? [],
            ToolChoiceDescription = DescribeToolChoice(request.ToolChoice),
            MaxToolCalls = request.MaxToolCalls,
            MaxTokens = request.MaxOutputTokens,
            Temperature = ToSingle(request.Temperature),
            TopP = ToSingle(request.TopP),
            TopK = request.TopK,
            PresencePenalty = ToSingle(request.PresencePenalty),
            FrequencyPenalty = ToSingle(request.FrequencyPenalty),
            Seed = request.Seed is null ? null : unchecked((uint)request.Seed.Value),
            Stop = ParseStop(request.Stop),
            ForceJson = IsResponsesJsonMode(request.Text),
            StreamIncludeUsage = request.StreamOptions?.IncludeUsage == true,
            Store = request.Store,
            User = request.User,
            ServiceTier = request.ServiceTier,
            ParallelToolCalls = request.ParallelToolCalls,
            PreviousResponseId = request.PreviousResponseId,
            Truncation = request.Truncation,
            Metadata = ParseMetadata(request.Metadata),
            CompatibilityWarnings = warnings
        };
    }

    private async Task<InferenceMessage> ConvertMessageAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        var parts = await ExtractContentAsync(message.Content, defaultRole: message.Role, cancellationToken);
        var content = parts.Text;

        if (message.ToolCalls is { Count: > 0 })
        {
            content = AppendLine(content, "Assistant tool calls: " + JsonSerializer.Serialize(message.ToolCalls, OpenAiJson.CreateOptions()));
        }

        if (message.FunctionCall is not null)
        {
            content = AppendLine(content, "Assistant function call: " + JsonSerializer.Serialize(message.FunctionCall, OpenAiJson.CreateOptions()));
        }

        return new InferenceMessage
        {
            Role = NormalizeRole(message.Role),
            Name = message.Name,
            ToolCallId = message.ToolCallId,
            Content = content,
            Media = parts.Media
        };
    }

    private async Task<IReadOnlyList<InferenceMessage>> ConvertResponsesInputAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            return [new InferenceMessage { Role = "user", Content = input.GetString() ?? string.Empty }];
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` must be a string or an array.", param: "input");
        }

        var messages = new List<InferenceMessage>();
        var looseText = new StringBuilder();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AppendNonEmptyLine(looseText, item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                AppendNonEmptyLine(looseText, item.GetRawText());
                continue;
            }

            var type = GetString(item, "type");
            var role = GetString(item, "role") ?? "user";
            if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase) || item.TryGetProperty("content", out _))
            {
                var content = item.GetProperty("content");
                var parts = await ExtractContentAsync(content, defaultRole: role, cancellationToken);
                messages.Add(new InferenceMessage
                {
                    Role = NormalizeRole(role),
                    Content = parts.Text,
                    Media = parts.Media
                });
                continue;
            }

            if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
            {
                var callId = GetString(item, "call_id");
                var output = GetString(item, "output") ?? item.GetRawText();
                messages.Add(new InferenceMessage
                {
                    Role = "tool",
                    ToolCallId = callId,
                    Content = output
                });
                continue;
            }

            AppendNonEmptyLine(looseText, item.GetRawText());
        }

        if (looseText.Length > 0)
        {
            messages.Add(new InferenceMessage { Role = "user", Content = looseText.ToString() });
        }

        if (messages.Count == 0)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` did not contain any usable user content.", param: "input");
        }

        return messages;
    }

    private async Task<(string Text, IReadOnlyList<InferenceMedia> Media)> ExtractContentAsync(
        JsonElement? content,
        string defaultRole,
        CancellationToken cancellationToken)
    {
        if (content is null || content.Value.ValueKind == JsonValueKind.Null)
        {
            return (string.Empty, []);
        }

        var value = content.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            return (value.GetString() ?? string.Empty, []);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return (value.GetRawText(), []);
        }

        var textBuilder = new StringBuilder();
        var media = new List<InferenceMedia>();
        foreach (var part in value.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                AppendNonEmptyLine(textBuilder, part.GetString());
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                AppendNonEmptyLine(textBuilder, part.GetRawText());
                continue;
            }

            var type = GetString(part, "type");
            switch (type)
            {
                case "text":
                case "input_text":
                case "output_text":
                    AppendNonEmptyLine(textBuilder, GetString(part, "text"));
                    break;
                case "image_url":
                    media.Add(await ReadMediaAsync(ReadNestedUrl(part, "image_url") ?? GetString(part, "url"), expectedKind: "image", cancellationToken));
                    AppendNonEmptyLine(textBuilder, "[image]");
                    break;
                case "input_image":
                    media.Add(await ReadMediaAsync(GetString(part, "image_url") ?? GetString(part, "file_id"), expectedKind: "image", cancellationToken));
                    AppendNonEmptyLine(textBuilder, "[image]");
                    break;
                case "input_audio":
                    media.Add(await ReadMediaAsync(ReadNestedUrl(part, "input_audio") ?? GetString(part, "audio_url") ?? GetString(part, "data"), expectedKind: "audio", cancellationToken));
                    AppendNonEmptyLine(textBuilder, "[audio]");
                    break;
                default:
                    AppendNonEmptyLine(textBuilder, part.GetRawText());
                    break;
            }
        }

        if (textBuilder.Length == 0 && media.Count > 0)
        {
            AppendNonEmptyLine(textBuilder, defaultRole.Equals("user", StringComparison.OrdinalIgnoreCase)
                ? "Describe the attached media."
                : "Attached media.");
        }

        return (textBuilder.ToString(), media);
    }

    private async Task<InferenceMedia> ReadMediaAsync(string? source, string expectedKind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, $"A {expectedKind} content block must contain a URL, data URL, base64 data, or allowed local path.");
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ReadDataUrl(source, expectedKind);
        }

        if (IsLikelyBase64(source))
        {
            var bytes = Convert.FromBase64String(source);
            ValidateMediaSize(bytes.Length);
            return new InferenceMedia { Source = "inline", MimeType = GuessMimeType(bytes, expectedKind), Bytes = bytes };
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (!_options.AllowRemoteMedia)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Remote media URLs are disabled by configuration.", code: "remote_media_disabled");
            }

            var client = _httpClientFactory.CreateClient(MediaHttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > _options.MaxMediaBytes)
            {
                throw new OpenAiProtocolException(StatusCodes.Status413PayloadTooLarge, "Media payload is larger than configured MaxMediaBytes.", code: "media_too_large");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            ValidateMediaSize(bytes.Length);
            return new InferenceMedia
            {
                Source = uri.ToString(),
                MimeType = response.Content.Headers.ContentType?.MediaType ?? GuessMimeType(bytes, expectedKind),
                Bytes = bytes
            };
        }

        if (!_options.AllowLocalMediaPaths)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Local media paths are disabled by configuration.", code: "local_media_disabled");
        }

        if (!File.Exists(source))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, $"Media path was not found: {source}", code: "media_not_found");
        }

        var fileInfo = new FileInfo(source);
        ValidateMediaSize(fileInfo.Length);
        var fileBytes = await File.ReadAllBytesAsync(source, cancellationToken);
        return new InferenceMedia
        {
            Source = fileInfo.FullName,
            MimeType = GuessMimeTypeFromExtension(fileInfo.Extension, expectedKind),
            Bytes = fileBytes
        };
    }

    private InferenceMedia ReadDataUrl(string dataUrl, string expectedKind)
    {
        var commaIndex = dataUrl.IndexOf(',');
        if (commaIndex < 0)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Invalid data URL media content.");
        }

        var metadata = dataUrl[5..commaIndex];
        var payload = dataUrl[(commaIndex + 1)..];
        var mimeType = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(x => x.Contains('/')) ?? GuessMimeType([], expectedKind);
        var bytes = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));

        ValidateMediaKind(mimeType, expectedKind);
        ValidateMediaSize(bytes.Length);
        return new InferenceMedia { Source = "data-url", MimeType = mimeType, Bytes = bytes };
    }

    private void ValidateMediaKind(string mimeType, string expectedKind)
    {
        if (expectedKind == "image" && !ImageMimeTypes.Contains(mimeType))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, $"Unsupported image MIME type `{mimeType}`.");
        }

        if (expectedKind == "audio" && !AudioMimeTypes.Contains(mimeType))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, $"Unsupported audio MIME type `{mimeType}`.");
        }
    }

    private void ValidateMediaSize(long bytes)
    {
        if (bytes > _options.MaxMediaBytes)
        {
            throw new OpenAiProtocolException(StatusCodes.Status413PayloadTooLarge, "Media payload is larger than configured MaxMediaBytes.", code: "media_too_large");
        }
    }

    private static List<OpenAiTool> MergeChatTools(List<OpenAiTool>? tools, List<OpenAiFunction>? functions)
    {
        var result = tools is null ? [] : new List<OpenAiTool>(tools);
        if (functions is not null)
        {
            foreach (var function in functions)
            {
                result.Add(new OpenAiTool { Type = "function", Function = function });
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ParseStop(JsonElement? stop)
    {
        if (stop is null || stop.Value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (stop.Value.ValueKind == JsonValueKind.String)
        {
            return [stop.Value.GetString() ?? string.Empty];
        }

        if (stop.Value.ValueKind == JsonValueKind.Array)
        {
            return stop.Value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToArray();
        }

        return [];
    }

    private static bool IsJsonResponseFormat(JsonElement? responseFormat)
    {
        if (responseFormat is null || responseFormat.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = GetString(responseFormat.Value, "type");
        return string.Equals(type, "json_object", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResponsesJsonMode(JsonElement? text)
    {
        if (text is null || text.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!text.Value.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = GetString(format, "type");
        return string.Equals(type, "json_object", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DescribeToolChoice(JsonElement? toolChoice)
    {
        if (toolChoice is null || toolChoice.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return toolChoice.Value.ValueKind == JsonValueKind.String
            ? toolChoice.Value.GetString()
            : toolChoice.Value.GetRawText();
    }

    private static IReadOnlyDictionary<string, string>? ParseMetadata(JsonElement? metadata)
    {
        if (metadata is null || metadata.Value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (metadata.Value.ValueKind != JsonValueKind.Object)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`metadata` must be an object.", param: "metadata");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in metadata.Value.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }

    private static void ValidateUnsupportedChatFields(ChatCompletionRequest request)
    {
    }

    public static void AddChatCompatibilityWarnings(ChatCompletionRequest request, List<string> warnings)
    {
        if (request.Logprobs == true || request.TopLogprobs is not null)
        {
            warnings.Add("`logprobs` and `top_logprobs` are accepted but not supported by this LLamaSharp service yet.");
        }

        if (request.LogitBias is not null && request.LogitBias.Value.ValueKind != JsonValueKind.Null)
        {
            warnings.Add("`logit_bias` is accepted but not supported by this LLamaSharp service yet.");
        }
    }

    private static void ValidateUnsupportedResponsesFields(ResponsesRequest request)
    {
        if (string.Equals(request.Truncation, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.Truncation) && !string.Equals(request.Truncation, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status400BadRequest,
                "`truncation` must be `auto` or `disabled` when provided.",
                code: "unsupported_parameter",
                param: "truncation");
        }
    }

    private static void AddWarningIf(bool condition, List<string> warnings, string warning)
    {
        if (condition)
        {
            warnings.Add(warning);
        }
    }

    private static string? ReadNestedUrl(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return value.ValueKind == JsonValueKind.Object ? GetString(value, "url") : null;
    }

    private static string? GetString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static float? ToSingle(double? value) => value is null ? null : (float)value.Value;

    private static string NormalizeRole(string? role)
    {
        return role?.ToLowerInvariant() switch
        {
            "developer" => "system",
            "system" => "system",
            "assistant" => "assistant",
            "tool" => "tool",
            "function" => "tool",
            _ => "user"
        };
    }

    private static string AppendLine(string existing, string line)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return line;
        }

        return existing + Environment.NewLine + line;
    }

    private static void AppendNonEmptyLine(StringBuilder builder, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(line);
    }

    private static bool IsLikelyBase64(string value)
    {
        if (value.Length < 16 || value.Contains(' ') || value.Contains(':') || value.Contains('\\') || value.Contains('/'))
        {
            return false;
        }

        return Base64.IsValid(value);
    }

    private static string GuessMimeType(byte[] bytes, string expectedKind)
    {
        if (expectedKind == "image")
        {
            if (bytes is [0x89, 0x50, 0x4E, 0x47, ..])
            {
                return MediaTypeNames.Image.Png;
            }

            if (bytes is [0xFF, 0xD8, ..])
            {
                return MediaTypeNames.Image.Jpeg;
            }

            if (bytes is [0x47, 0x49, 0x46, ..])
            {
                return MediaTypeNames.Image.Gif;
            }

            return MediaTypeNames.Image.Png;
        }

        return "audio/wav";
    }

    private static string GuessMimeTypeFromExtension(string extension, string expectedKind)
    {
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => MediaTypeNames.Image.Jpeg,
            ".png" => MediaTypeNames.Image.Png,
            ".gif" => MediaTypeNames.Image.Gif,
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            ".m4a" or ".mp4" => "audio/mp4",
            ".aac" => "audio/aac",
            ".opus" => "audio/opus",
            _ => expectedKind == "image" ? MediaTypeNames.Image.Png : "audio/wav"
        };
    }
}
