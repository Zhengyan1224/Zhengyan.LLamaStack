using System.Buffers.Text;
using System.Net;
using System.Net.Mime;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SkiaSharp;
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
        var toolChoice = ParseToolChoice(request.ToolChoice ?? request.FunctionCall);
        var warnings = new List<string>();
        AddChatCompatibilityWarnings(request, warnings);
        return new InferenceRequest
        {
            RequestedModel = request.Model,
            Messages = messages,
            Tools = tools,
            ToolChoiceDescription = toolChoice.Description,
            ToolChoiceMode = toolChoice.Mode,
            ToolChoiceName = toolChoice.Name,
            N = Math.Max(1, request.N ?? 1),
            MaxTokens = request.MaxCompletionTokens ?? request.MaxTokens,
            Temperature = ToSingle(request.Temperature),
            TopP = ToSingle(request.TopP),
            TopK = request.TopK,
            PresencePenalty = ToSingle(request.PresencePenalty),
            FrequencyPenalty = ToSingle(request.FrequencyPenalty),
            Seed = request.Seed is null ? null : unchecked((uint)request.Seed.Value),
            Stop = ParseStop(request.Stop),
        JsonSchema = ParseJsonSchema(request.ResponseFormat),
        StrictJsonSchema = IsStrictJsonSchema(request.ResponseFormat),
        ForceJson = IsJsonResponseFormat(request.ResponseFormat),
        StreamIncludeUsage = request.StreamOptions?.IncludeUsage == true,
            Store = request.Store,
            User = request.User,
            ServiceTier = request.ServiceTier,
            ParallelToolCalls = request.ParallelToolCalls,
            LogitBias = ParseLogitBias(request.LogitBias),
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

        var promptText = ParsePromptText(request.Prompt);
        if (!string.IsNullOrWhiteSpace(promptText))
        {
            messages.Add(new InferenceMessage { Role = "system", Content = promptText });
        }

        if (request.Input is null)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` is required.", param: "input");
        }

        foreach (var message in await ConvertResponsesInputAsync(request.Input.Value, cancellationToken))
        {
            messages.Add(message);
        }

        NormalizeOpenAiTools(request);
        ValidateUnsupportedResponsesFields(request);
        var toolChoice = ParseToolChoice(request.ToolChoice);
        var warnings = new List<string>();
        return new InferenceRequest
        {
            RequestedModel = request.Model,
            Messages = messages,
            Tools = request.Tools ?? [],
            ToolChoiceDescription = toolChoice.Description,
            ToolChoiceMode = toolChoice.Mode,
            ToolChoiceName = toolChoice.Name,
            MaxToolCalls = request.MaxToolCalls,
            MaxTokens = ApplyReasoningMaxTokens(ToSingle(request.Temperature), request.MaxOutputTokens, request.Reasoning),
            Temperature = ApplyReasoningTemperature(ToSingle(request.Temperature), request.Reasoning),
            TopP = ToSingle(request.TopP),
            TopK = request.TopK,
            PresencePenalty = ToSingle(request.PresencePenalty),
            FrequencyPenalty = ToSingle(request.FrequencyPenalty),
            Seed = request.Seed is null ? null : unchecked((uint)request.Seed.Value),
            Stop = ParseStop(request.Stop),
            JsonSchema = ParseResponsesJsonSchema(request.Text),
            StrictJsonSchema = IsStrictResponsesJsonSchema(request.Text),
            ForceJson = IsResponsesJsonMode(request.Text),
            StreamIncludeUsage = request.StreamOptions?.IncludeUsage == true,
            Store = request.Store,
            User = request.User,
            ServiceTier = request.ServiceTier,
            ParallelToolCalls = request.ParallelToolCalls,
            PreviousResponseId = request.PreviousResponseId,
            Truncation = request.Truncation,
            Include = ParseInclude(request.Include),
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
            var toolCallsJson = JsonSerializer.Serialize(new { tool_calls = message.ToolCalls }, OpenAiJson.CreateOptions());
            content = string.IsNullOrWhiteSpace(content) ? toolCallsJson : AppendLine(content, toolCallsJson);
        }

        if (message.FunctionCall is not null)
        {
            var functionCallJson = JsonSerializer.Serialize(new { function_call = message.FunctionCall }, OpenAiJson.CreateOptions());
            content = string.IsNullOrWhiteSpace(content) ? functionCallJson : AppendLine(content, functionCallJson);
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
                    var imageDetail = part.TryGetProperty("image_url", out var imgUrlEl) && imgUrlEl.ValueKind == JsonValueKind.Object
                        ? GetString(imgUrlEl, "detail")
                        : null;
                    media.Add(await ReadMediaAsync(ReadNestedUrl(part, "image_url") ?? GetString(part, "url"), expectedKind: "image", cancellationToken, detail: imageDetail));
                    AppendNonEmptyLine(textBuilder, "[image]");
                    break;
                case "input_image":
                    media.Add(await ReadMediaAsync(GetString(part, "image_url") ?? GetString(part, "file_id"), expectedKind: "image", cancellationToken, detail: GetString(part, "detail")));
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

    private async Task<InferenceMedia> ReadMediaAsync(string? source, string expectedKind, CancellationToken cancellationToken, string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, $"A {expectedKind} content block must contain a URL, data URL, base64 data, or allowed local path.");
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ReadDataUrl(source, expectedKind, detail);
        }

        if (IsLikelyBase64(source))
        {
            var bytes = Convert.FromBase64String(source);
            ValidateMediaSize(bytes.Length);
            var mimeType = GuessMimeType(bytes, expectedKind);
            bytes = MaybeResizeImage(bytes, mimeType, detail);
            return new InferenceMedia { Source = "inline", MimeType = mimeType, Bytes = bytes };
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (!_options.AllowRemoteMedia)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Remote media URLs are disabled by configuration.", code: "remote_media_disabled");
            }

            await EnsureRemoteMediaHostAllowedAsync(uri, cancellationToken);
            var client = _httpClientFactory.CreateClient(MediaHttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > _options.MaxMediaBytes)
            {
                throw new OpenAiProtocolException(StatusCodes.Status413PayloadTooLarge, "Media payload is larger than configured MaxMediaBytes.", code: "media_too_large");
            }

            var bytes = await ReadRemoteMediaBytesAsync(response.Content, cancellationToken);
            ValidateMediaSize(bytes.Length);
            var mimeType = response.Content.Headers.ContentType?.MediaType ?? GuessMimeType(bytes, expectedKind);
            ValidateMediaKind(mimeType, expectedKind);
            bytes = MaybeResizeImage(bytes, mimeType, detail);
            return new InferenceMedia
            {
                Source = uri.ToString(),
                MimeType = mimeType,
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
        fileBytes = MaybeResizeImage(fileBytes, GuessMimeTypeFromExtension(fileInfo.Extension, expectedKind), detail);
        return new InferenceMedia
        {
            Source = fileInfo.FullName,
            MimeType = GuessMimeTypeFromExtension(fileInfo.Extension, expectedKind),
            Bytes = fileBytes
        };
    }

    private InferenceMedia ReadDataUrl(string dataUrl, string expectedKind, string? detail = null)
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
        bytes = MaybeResizeImage(bytes, mimeType, detail);
        return new InferenceMedia { Source = "data-url", MimeType = mimeType, Bytes = bytes };
    }

    private byte[] MaybeResizeImage(byte[] imageBytes, string mimeType, string? detail)
    {
        if (!ImageMimeTypes.Contains(mimeType))
        {
            return imageBytes;
        }

        int? configMax = _options.MaxImageDimension;
        if (configMax < 0 && string.IsNullOrWhiteSpace(detail))
        {
            return imageBytes;
        }

        return ResizeImageWithSkia(imageBytes, configMax, detail);
    }

    private static byte[] ResizeImageWithSkia(byte[] imageBytes, int? configMaxDimension, string? detail)
    {
        try
        {
            using var input = new SKMemoryStream(imageBytes);
            using var bitmap = SKBitmap.Decode(input);
            if (bitmap is null)
            {
                return imageBytes;
            }

            var width = bitmap.Width;
            var height = bitmap.Height;

            int targetWidth, targetHeight;
            if (configMaxDimension >= 0)
            {
                if (width <= configMaxDimension && height <= configMaxDimension)
                {
                    return imageBytes;
                }

                var scale = Math.Min((double)configMaxDimension / width, (double)configMaxDimension / height);
                if (scale >= 1.0)
                {
                    return imageBytes;
                }

                targetWidth = (int)(width * scale);
                targetHeight = (int)(height * scale);
            }
            else
            {
                switch (detail?.ToLowerInvariant())
                {
                    case "low":
                        if (width <= 512 && height <= 512)
                        {
                            return imageBytes;
                        }

                        var lowScale = Math.Min(512.0 / width, 512.0 / height);
                        if (lowScale >= 1.0)
                        {
                            return imageBytes;
                        }

                        targetWidth = (int)(width * lowScale);
                        targetHeight = (int)(height * lowScale);
                        break;

                    case "high":
                    {
                        var shortest = Math.Min(width, height);
                        var longest = Math.Max(width, height);

                        if (shortest <= 768 && longest <= 2048)
                        {
                            return imageBytes;
                        }

                        var highScale = 768.0 / shortest;
                        if (longest * highScale > 2048)
                        {
                            highScale = 2048.0 / longest;
                        }

                        if (highScale >= 1.0)
                        {
                            return imageBytes;
                        }

                        targetWidth = (int)(width * highScale);
                        targetHeight = (int)(height * highScale);
                        break;
                    }

                    default:
                        return imageBytes;
                }
            }

            targetWidth = Math.Max(1, targetWidth);
            targetHeight = Math.Max(1, targetHeight);

            using var resized = bitmap.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear));
            if (resized is null)
            {
                return imageBytes;
            }

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        }
        catch
        {
            return imageBytes;
        }
    }

    private async Task<byte[]> ReadRemoteMediaBytesAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > _options.MaxMediaBytes)
            {
                throw new OpenAiProtocolException(StatusCodes.Status413PayloadTooLarge, "Media payload is larger than configured MaxMediaBytes.", code: "media_too_large");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static async Task EnsureRemoteMediaHostAllowedAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri.Host) ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Remote media URL host is not allowed.", code: "remote_media_host_not_allowed");
        }

        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            if (IsPrivateOrLocalAddress(literal))
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Remote media URL host is not allowed.", code: "remote_media_host_not_allowed");
            }

            return;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, $"Remote media URL host could not be resolved: {uri.Host}", code: "remote_media_host_unresolved");
        }

        if (addresses.Length == 0 || addresses.Any(IsPrivateOrLocalAddress))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "Remote media URL host is not allowed.", code: "remote_media_host_not_allowed");
        }
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.Broadcast))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes[0] == 127
                || bytes[0] == 0
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || (bytes[0] & 0xFE) == 0xFC;
        }

        return true;
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

    private static string? ParseJsonSchema(JsonElement? responseFormat)
    {
        if (responseFormat is null || responseFormat.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(responseFormat.Value, "type");
        if (!string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!responseFormat.Value.TryGetProperty("json_schema", out var jsonSchema) ||
            jsonSchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (jsonSchema.TryGetProperty("schema", out var schema) &&
            schema.ValueKind == JsonValueKind.Object)
        {
            return schema.GetRawText();
        }

        if (jsonSchema.TryGetProperty("name", out var name) &&
            name.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(name.GetString()))
        {
            return $"{{\"type\":\"object\",\"title\":\"{name.GetString()}\"}}";
        }

        return null;
    }

    private static bool IsStrictJsonSchema(JsonElement? responseFormat)
    {
        if (responseFormat is null || responseFormat.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = GetString(responseFormat.Value, "type");
        if (!string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!responseFormat.Value.TryGetProperty("json_schema", out var jsonSchema) ||
            jsonSchema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (jsonSchema.TryGetProperty("strict", out var strict) &&
            strict.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return false;
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

    private static string? ParseResponsesJsonSchema(JsonElement? text)
    {
        if (text is null || text.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!text.Value.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetString(format, "type");
        if (!string.Equals(type, "json_schema", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!format.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return schema.GetRawText();
    }

    private static bool IsStrictResponsesJsonSchema(JsonElement? text)
    {
        if (text is null || text.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!text.Value.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!format.TryGetProperty("strict", out var strict) || strict.ValueKind != JsonValueKind.True)
        {
            return false;
        }

        return string.Equals(GetString(format, "type"), "json_schema", StringComparison.OrdinalIgnoreCase);
    }

    private static ParsedToolChoice ParseToolChoice(JsonElement? toolChoice)
    {
        if (toolChoice is null || toolChoice.Value.ValueKind == JsonValueKind.Null)
        {
            return new ParsedToolChoice(InferenceToolChoiceMode.Auto, null, null);
        }

        if (toolChoice.Value.ValueKind == JsonValueKind.String)
        {
            var value = toolChoice.Value.GetString();
            return value?.ToLowerInvariant() switch
            {
                "none" => new ParsedToolChoice(InferenceToolChoiceMode.None, null, value),
                "required" => new ParsedToolChoice(InferenceToolChoiceMode.Required, null, value),
                _ => new ParsedToolChoice(InferenceToolChoiceMode.Auto, null, value)
            };
        }

        if (toolChoice.Value.ValueKind != JsonValueKind.Object)
        {
            return new ParsedToolChoice(InferenceToolChoiceMode.Auto, null, toolChoice.Value.GetRawText());
        }

        var functionName = ReadToolChoiceFunctionName(toolChoice.Value);
        if (!string.IsNullOrWhiteSpace(functionName))
        {
            return new ParsedToolChoice(InferenceToolChoiceMode.Function, functionName, toolChoice.Value.GetRawText());
        }

        return new ParsedToolChoice(InferenceToolChoiceMode.Auto, null, toolChoice.Value.GetRawText());
    }

    private static string? ReadToolChoiceFunctionName(JsonElement toolChoice)
    {
        if (toolChoice.TryGetProperty("function", out var functionElement) &&
            functionElement.ValueKind == JsonValueKind.Object &&
            functionElement.TryGetProperty("name", out var functionName) &&
            functionName.ValueKind == JsonValueKind.String)
        {
            return functionName.GetString();
        }

        if (toolChoice.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
        {
            return nameElement.GetString();
        }

        return null;
    }

    private sealed record ParsedToolChoice(InferenceToolChoiceMode Mode, string? Name, string? Description);

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
            var parsed = ParseLogitBias(request.LogitBias);
            if (parsed is null || parsed.Count == 0)
            {
                warnings.Add("`logit_bias` could not be parsed; expected a JSON object with integer token IDs as keys and float biases as values.");
            }
        }

        if (request.Modalities is { Count: > 0 } && request.Modalities.Any(x => !string.Equals(x, "text", StringComparison.OrdinalIgnoreCase)))
        {
            var nonText = string.Join(", ", request.Modalities.Where(x => !string.Equals(x, "text", StringComparison.OrdinalIgnoreCase)));
            warnings.Add($"`modalities` values `{nonText}` are not supported by this server; only `text` output is available.");
        }
    }

    private static IReadOnlyList<string>? ParseInclude(JsonElement? include)
    {
        if (include is null || include.Value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<string>();
        foreach (var item in include.Value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                result.Add(item.GetString()!);
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static IReadOnlyDictionary<int, float>? ParseLogitBias(JsonElement? logitBias)
    {
        if (logitBias is null || logitBias.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new Dictionary<int, float>();
        foreach (var property in logitBias.Value.EnumerateObject())
        {
            if (int.TryParse(property.Name, out var tokenId) && property.Value.ValueKind == JsonValueKind.Number)
            {
                result[tokenId] = property.Value.GetSingle();
            }
        }

        return result.Count > 0 ? result : null;
    }

    private static string? ParsePromptText(JsonElement? prompt)
    {
        if (prompt is null || prompt.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (prompt.Value.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString();
        }

        if (prompt.Value.TryGetProperty("template", out var template) && template.ValueKind == JsonValueKind.String)
        {
            return template.GetString();
        }

        return null;
    }

    private static float? ApplyReasoningTemperature(float? baseTemperature, JsonElement? reasoning)
    {
        var effort = ParseReasoningEffort(reasoning);
        return effort switch
        {
            "low" => baseTemperature is not null ? Math.Min(1.5f, baseTemperature.Value + 0.3f) : 1.0f,
            "high" => baseTemperature is not null ? Math.Max(0.1f, baseTemperature.Value - 0.2f) : 0.5f,
            _ => baseTemperature
        };
    }

    private static int? ApplyReasoningMaxTokens(float? baseTemperature, int? requestedMaxTokens, JsonElement? reasoning)
    {
        var effort = ParseReasoningEffort(reasoning);
        var baseMax = requestedMaxTokens ?? 2048;
        return effort switch
        {
            "low" => Math.Min(baseMax, 1024),
            "high" => Math.Max(baseMax, 4096),
            _ => requestedMaxTokens
        };
    }

    private static string? ParseReasoningEffort(JsonElement? reasoning)
    {
        if (reasoning is null || reasoning.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (reasoning.Value.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.String)
        {
            var value = effort.GetString();
            if (string.Equals(value, "low", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "medium", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "high", StringComparison.OrdinalIgnoreCase))
            {
                return value!.ToLowerInvariant();
            }
        }

        return null;
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

    private static void NormalizeOpenAiTools(ResponsesRequest request)
    {
        if (request.Tools is null)
        {
            return;
        }

        foreach (var tool in request.Tools)
        {
            if (tool.Function is null && !string.IsNullOrWhiteSpace(tool.Name))
            {
                tool.Function = new OpenAiFunction
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Parameters = tool.Parameters,
                    Strict = tool.Strict
                };
            }
        }
    }
}
