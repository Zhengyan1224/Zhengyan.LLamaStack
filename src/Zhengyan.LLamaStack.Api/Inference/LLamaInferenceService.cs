using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;
using Zhengyan.LLamaStack.Api.Infrastructure;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Options;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class LLamaInferenceService : IAsyncDisposable
{
    private readonly LLamaStackOptions _options;
    private readonly ILogger<LLamaInferenceService> _logger;
    private readonly Dictionary<string, ModelRuntime> _models;

    public LLamaInferenceService(IOptions<LLamaStackOptions> options, ILogger<LLamaInferenceService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _models = _options.GetModelRegistrations()
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => new ModelRuntime(x.First()), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsLoaded => _models.Values.Any(x => x.IsLoaded);

    public string DefaultModelId => ResolveDefaultModelId();

    public IReadOnlyList<ModelDescriptor> GetModels()
    {
        return _models.Values
            .OrderBy(x => x.Options.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ModelDescriptor
            {
                Id = x.Options.Id,
                Created = x.Options.Created,
                OwnedBy = x.Options.OwnedBy,
                Loaded = x.IsLoaded,
                ModelPath = x.Options.ModelPath,
                MmprojPath = x.Options.MmprojPath,
                Capabilities = x.Options.Capabilities
            })
            .ToArray();
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        foreach (var model in _models.Values.Where(x => x.Options.LoadModelOnStartup))
        {
            await EnsureLoadedAsync(model, cancellationToken);
        }
    }

    public void ValidateRequest(InferenceRequest request, InferenceEndpointKind endpointKind, bool stream)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var capabilities = model.Options.Capabilities;
        if (endpointKind == InferenceEndpointKind.ChatCompletions && !capabilities.ChatCompletions)
        {
            throw CreateCapabilityError(model.Options.Id, "chat_completions");
        }

        if (endpointKind == InferenceEndpointKind.Responses && !capabilities.Responses)
        {
            throw CreateCapabilityError(model.Options.Id, "responses");
        }

        if (stream && !capabilities.Streaming)
        {
            throw CreateCapabilityError(model.Options.Id, "streaming");
        }

        if (request.Tools.Count > 0 && !capabilities.ToolCalling)
        {
            throw CreateCapabilityError(model.Options.Id, "tool_calling");
        }

        if (request.ForceJson && !capabilities.JsonMode)
        {
            throw CreateCapabilityError(model.Options.Id, "json_mode");
        }

        var hasImage = request.Messages.SelectMany(x => x.Media).Any(x => x.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
        if (hasImage && !capabilities.ImageInput)
        {
            throw CreateCapabilityError(model.Options.Id, "image_input");
        }

        var hasAudio = request.Messages.SelectMany(x => x.Media).Any(x => x.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true);
        if (hasAudio && !capabilities.AudioInput)
        {
            throw CreateCapabilityError(model.Options.Id, "audio_input");
        }

        if (string.IsNullOrWhiteSpace(model.Options.ModelPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Model `{model.Options.Id}` is not configured with a GGUF file path. Set LLamaStack:Models[].ModelPath or LLamaStack:ModelPath before serving inference.",
                type: "server_error",
                code: "model_path_not_configured");
        }

        if (!File.Exists(model.Options.ModelPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Configured GGUF model file was not found for `{model.Options.Id}`: {model.Options.ModelPath}",
                type: "server_error",
                code: "model_not_found");
        }

        if ((hasImage || hasAudio) && !string.IsNullOrWhiteSpace(model.Options.MmprojPath) && !File.Exists(model.Options.MmprojPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Configured mmproj file was not found for `{model.Options.Id}`: {model.Options.MmprojPath}",
                type: "server_error",
                code: "mmproj_not_found");
        }
    }

    public async Task<InferenceCompletion> CompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        var id = CreateCompletionId("chatcmpl");
        var createdText = new StringBuilder();
        await foreach (var token in StreamTextAsync(request, cancellationToken))
        {
            createdText.Append(token);
        }

        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await EnsureLoadedAsync(model, cancellationToken);
        var text = createdText.ToString();
        var toolCalls = TryExtractToolCalls(text, request.Tools, out var cleanText);
        var prompt = BuildPrompt(loaded.Weights, request, loaded.MediaMarker, out _, out _);
        return new InferenceCompletion
        {
            Id = id,
            Model = model.Options.Id,
            Text = cleanText,
            ToolCalls = toolCalls,
            PromptTokens = CountTokens(loaded, prompt),
            CompletionTokens = CountTokens(loaded, cleanText),
            Metadata = request.Metadata,
            User = request.User,
            ServiceTier = request.ServiceTier,
            Store = request.Store,
            CompatibilityWarnings = request.CompatibilityWarnings
        };
    }

    public async Task<(string Model, int PromptTokens)> EstimatePromptUsageAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await EnsureLoadedAsync(model, cancellationToken);
        var prompt = BuildPrompt(loaded.Weights, request, loaded.MediaMarker, out _, out _);
        return (model.Options.Id, CountTokens(loaded, prompt));
    }

    public async IAsyncEnumerable<string> StreamTextAsync(
        InferenceRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await EnsureLoadedAsync(model, cancellationToken);
        await model.InferenceLock.WaitAsync(cancellationToken);
        try
        {
            var prompt = BuildPrompt(loaded.Weights, request, loaded.MediaMarker, out var media, out var mediaCount);
            var inferenceParams = CreateInferenceParams(model.Options, request);
            ResetExecutorState(loaded);
            LoadMedia(loaded, media, mediaCount);

            await foreach (var token in loaded.Executor.InferAsync(prompt, inferenceParams, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return token;
            }
        }
        finally
        {
            CleanupMedia(loaded);
            model.InferenceLock.Release();
        }
    }

    public async IAsyncEnumerable<string> StreamChatEventsAsync(
        InferenceRequest request,
        string responseId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = new StringBuilder();
        await foreach (var token in StreamTextAsync(request, cancellationToken))
        {
            completion.Append(token);
            var chunk = new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = UnixNow(),
                model = ResolveRuntime(request.RequestedModel).Options.Id,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { content = token },
                        finish_reason = (string?)null
                    }
                }
            };

            yield return ToSse(chunk);
        }

        var toolCalls = TryExtractToolCalls(completion.ToString(), request.Tools, out _);
        if (toolCalls.Count > 0)
        {
            var toolChunk = new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = UnixNow(),
                model = ResolveRuntime(request.RequestedModel).Options.Id,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { tool_calls = toolCalls },
                        finish_reason = (string?)null
                    }
                }
            };
            yield return ToSse(toolChunk);
        }

        yield return ToSse(new
        {
            id = responseId,
            @object = "chat.completion.chunk",
            created = UnixNow(),
            model = ResolveRuntime(request.RequestedModel).Options.Id,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = toolCalls.Count > 0 ? "tool_calls" : "stop"
                }
            }
        });
    }

    public async IAsyncEnumerable<string> StreamResponsesEventsAsync(
        InferenceRequest request,
        string responseId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return ToSse(new
        {
            type = "response.created",
            response = new
            {
                id = responseId,
                @object = "response",
                created_at = UnixNow(),
                status = "in_progress",
                model = ResolveRuntime(request.RequestedModel).Options.Id
            }
        });

        var completion = new StringBuilder();
        await foreach (var token in StreamTextAsync(request, cancellationToken))
        {
            completion.Append(token);
            yield return ToSse(new
            {
                type = "response.output_text.delta",
                item_id = "msg_" + responseId,
                output_index = 0,
                content_index = 0,
                delta = token
            });
        }

        var toolCalls = TryExtractToolCalls(completion.ToString(), request.Tools, out var cleanText);
        yield return ToSse(new
        {
            type = "response.output_text.done",
            item_id = "msg_" + responseId,
            output_index = 0,
            content_index = 0,
            text = cleanText
        });

        foreach (var toolCall in toolCalls)
        {
            yield return ToSse(new
            {
                type = "response.output_item.done",
                output_index = 1,
                item = new
                {
                    id = toolCall.Id,
                    type = "function_call",
                    status = "completed",
                    call_id = toolCall.Id,
                    name = toolCall.Function.Name,
                    arguments = toolCall.Function.Arguments
                }
            });
        }

        yield return ToSse(new
        {
            type = "response.completed",
            response = new
            {
                id = responseId,
                @object = "response",
                created_at = UnixNow(),
                status = "completed",
                model = ResolveRuntime(request.RequestedModel).Options.Id
            }
        });
    }

    private async Task<LoadedModel> EnsureLoadedAsync(ModelRuntime model, CancellationToken cancellationToken)
    {
        if (model.Loaded is not null)
        {
            return model.Loaded;
        }

        await model.LoadLock.WaitAsync(cancellationToken);
        try
        {
            if (model.Loaded is not null)
            {
                return model.Loaded;
            }

            if (string.IsNullOrWhiteSpace(model.Options.ModelPath))
            {
                throw new OpenAiProtocolException(
                    StatusCodes.Status503ServiceUnavailable,
                    $"Model `{model.Options.Id}` is not configured with a GGUF file path. Set LLamaStack:Models[].ModelPath or LLamaStack:ModelPath before serving inference.",
                    type: "server_error",
                    code: "model_path_not_configured");
            }

            if (!File.Exists(model.Options.ModelPath))
            {
                throw new OpenAiProtocolException(
                    StatusCodes.Status503ServiceUnavailable,
                    $"Configured GGUF model file was not found for `{model.Options.Id}`: {model.Options.ModelPath}",
                    type: "server_error",
                    code: "model_not_found");
            }

            var parameters = CreateModelParams(model.Options);
            _logger.LogInformation("Loading GGUF model {ModelId} from {ModelPath}", model.Options.Id, model.Options.ModelPath);
            var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);
            var context = weights.CreateContext(parameters);

            MtmdWeights? mtmd = null;
            string? mediaMarker = null;
            if (!string.IsNullOrWhiteSpace(model.Options.MmprojPath))
            {
                if (!File.Exists(model.Options.MmprojPath))
                {
                    throw new OpenAiProtocolException(
                        StatusCodes.Status503ServiceUnavailable,
                        $"Configured mmproj file was not found for `{model.Options.Id}`: {model.Options.MmprojPath}",
                        type: "server_error",
                        code: "mmproj_not_found");
                }

                var mtmdParams = MtmdContextParams.Default();
                mtmdParams.UseGpu = model.Options.UseGpuForMtmd;
                mtmd = await MtmdWeights.LoadFromFileAsync(model.Options.MmprojPath, weights, mtmdParams, cancellationToken);
                mediaMarker = mtmdParams.MediaMarker ?? NativeApi.MtmdDefaultMarker() ?? "<media>";
                _logger.LogInformation(
                    "Loaded MTMD projection {ModelId} from {MmprojPath}; vision={Vision}, audio={Audio}",
                    model.Options.Id,
                    model.Options.MmprojPath,
                    mtmd.SupportsVision,
                    mtmd.SupportsAudio);
            }

            var executor = mtmd is null ? new InteractiveExecutor(context) : new InteractiveExecutor(context, mtmd);
            model.Loaded = new LoadedModel(weights, context, executor, mtmd, mediaMarker);
            return model.Loaded;
        }
        finally
        {
            model.LoadLock.Release();
        }
    }

    private static ModelParams CreateModelParams(LLamaModelRuntimeOptions model)
    {
        var parameters = new ModelParams(model.ModelPath!)
        {
            ContextSize = model.ContextSize,
            GpuLayerCount = model.GpuLayerCount,
            Threads = model.Threads,
            BatchThreads = model.BatchThreads,
            UseMemorymap = model.UseMemoryMap,
            UseMemoryLock = model.UseMemoryLock,
            FlashAttention = model.FlashAttention
        };

        if (model.BatchSize.HasValue)
        {
            parameters.BatchSize = model.BatchSize.Value;
        }

        if (model.UBatchSize.HasValue)
        {
            parameters.UBatchSize = model.UBatchSize.Value;
        }

        return parameters;
    }

    private static InferenceParams CreateInferenceParams(LLamaModelRuntimeOptions model, InferenceRequest request)
    {
        var pipeline = new DefaultSamplingPipeline
        {
            Temperature = request.Temperature ?? model.DefaultTemperature,
            TopP = request.TopP ?? model.DefaultTopP,
            TopK = request.TopK ?? model.DefaultTopK,
            PresencePenalty = request.PresencePenalty ?? 0,
            FrequencyPenalty = request.FrequencyPenalty ?? 0
        };

        if (request.Seed.HasValue)
        {
            pipeline.Seed = request.Seed.Value;
        }

        var antiPrompts = model.AntiPrompts.Concat(request.Stop).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        return new InferenceParams
        {
            MaxTokens = request.MaxTokens ?? model.DefaultMaxTokens,
            AntiPrompts = antiPrompts,
            SamplingPipeline = pipeline
        };
    }

    private static void ResetExecutorState(LoadedModel loaded)
    {
        loaded.Context.NativeHandle.MemoryClear();
        loaded.Executor.Embeds.Clear();
        loaded.Mtmd?.ClearMedia();
    }

    private static void CleanupMedia(LoadedModel loaded)
    {
        foreach (var embed in loaded.Executor.Embeds)
        {
            embed.Dispose();
        }

        loaded.Executor.Embeds.Clear();
        loaded.Mtmd?.ClearMedia();
    }

    private static void LoadMedia(LoadedModel loaded, IReadOnlyList<InferenceMedia> media, int mediaMarkerCount)
    {
        if (media.Count == 0)
        {
            return;
        }

        if (loaded.Mtmd is null)
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status400BadRequest,
                "This model is not configured for multimodal input. Set LLamaStack:MmprojPath to an mmproj GGUF file.",
                code: "multimodal_not_configured");
        }

        if (mediaMarkerCount < media.Count)
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status400BadRequest,
                "The formatted prompt did not contain enough media markers for the supplied media blocks.",
                code: "media_marker_mismatch");
        }

        foreach (var item in media)
        {
            if (item.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && !loaded.Mtmd.SupportsVision)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "The configured mmproj does not support vision inputs.", code: "vision_not_supported");
            }

            if (item.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true && !loaded.Mtmd.SupportsAudio)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "The configured mmproj does not support audio inputs.", code: "audio_not_supported");
            }

            loaded.Executor.Embeds.Add(loaded.Mtmd.LoadMedia(item.Bytes));
        }
    }

    private string BuildPrompt(
        LLamaWeights weights,
        InferenceRequest request,
        string? mediaMarker,
        out IReadOnlyList<InferenceMedia> media,
        out int mediaMarkerCount)
    {
        var allMedia = new List<InferenceMedia>();
        var promptMessages = BuildPromptMessages(request, mediaMarker, allMedia);
        string prompt;
        try
        {
            var template = new LLamaTemplate(weights, strict: false) { AddAssistant = true };
            foreach (var message in promptMessages)
            {
                template.Add(message.Role, message.Content);
            }

            prompt = LLamaTemplate.Encoding.GetString(template.Apply());
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not apply model chat template; falling back to plain chat transcript.");
            prompt = BuildPlainPrompt(promptMessages);
        }

        media = allMedia;
        mediaMarkerCount = string.IsNullOrEmpty(mediaMarker) ? 0 : CountOccurrences(prompt, mediaMarker);
        return prompt;
    }

    private IReadOnlyList<(string Role, string Content)> BuildPromptMessages(
        InferenceRequest request,
        string? mediaMarker,
        List<InferenceMedia> media)
    {
        var messages = new List<(string Role, string Content)>();
        foreach (var requestMessage in request.Messages)
        {
            var content = requestMessage.Content ?? string.Empty;
            if (requestMessage.Media.Count > 0)
            {
                foreach (var item in requestMessage.Media)
                {
                    media.Add(item);
                    content = AppendMediaMarker(content, mediaMarker);
                }
            }

            if (!string.IsNullOrWhiteSpace(requestMessage.Name))
            {
                content = $"name: {requestMessage.Name}{Environment.NewLine}{content}";
            }

            if (!string.IsNullOrWhiteSpace(requestMessage.ToolCallId))
            {
                content = $"tool_call_id: {requestMessage.ToolCallId}{Environment.NewLine}{content}";
            }

            messages.Add((NormalizeTemplateRole(requestMessage.Role), content));
        }

        if (request.Tools.Count > 0)
        {
            var toolInstruction = BuildToolInstruction(request);
            var systemIndex = messages.FindIndex(x => x.Role == "system");
            if (systemIndex >= 0)
            {
                var current = messages[systemIndex];
                messages[systemIndex] = (current.Role, current.Content + Environment.NewLine + Environment.NewLine + toolInstruction);
            }
            else
            {
                messages.Insert(0, ("system", toolInstruction));
            }
        }
        else if (request.ForceJson)
        {
            messages.Insert(0, ("system", "Return only valid JSON. Do not wrap it in Markdown."));
        }

        return messages;
    }

    private static string BuildToolInstruction(InferenceRequest request)
    {
        var options = OpenAiJson.CreateOptions();
        var toolJson = JsonSerializer.Serialize(request.Tools, options);
        var builder = new StringBuilder();
        builder.AppendLine("You can call tools when needed. Available tools are provided as JSON:");
        builder.AppendLine(toolJson);
        if (!string.IsNullOrWhiteSpace(request.ToolChoiceDescription))
        {
            builder.AppendLine("Tool choice: " + request.ToolChoiceDescription);
        }

        builder.AppendLine("When calling a tool, respond only with JSON in this exact shape:");
        builder.AppendLine("""{"tool_calls":[{"id":"call_<unique>","type":"function","function":{"name":"tool_name","arguments":"{\"arg\":\"value\"}"}}]}""");
        builder.AppendLine("If no tool is needed, answer normally.");
        if (request.ForceJson)
        {
            builder.AppendLine("If answering normally, return valid JSON only.");
        }

        return builder.ToString();
    }

    private static string BuildPlainPrompt(IReadOnlyList<(string Role, string Content)> messages)
    {
        var builder = new StringBuilder();
        foreach (var (role, content) in messages)
        {
            builder.Append(role);
            builder.Append(": ");
            builder.AppendLine(content);
        }

        builder.Append("assistant: ");
        return builder.ToString();
    }

    private static string AppendMediaMarker(string content, string? mediaMarker)
    {
        var marker = string.IsNullOrWhiteSpace(mediaMarker) ? "<media>" : mediaMarker;
        if (string.IsNullOrWhiteSpace(content))
        {
            return marker;
        }

        return content + Environment.NewLine + marker;
    }

    private static string NormalizeTemplateRole(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "system" => "system",
            "assistant" => "assistant",
            "tool" => "tool",
            _ => "user"
        };
    }

    private int CountTokens(LoadedModel loaded, string text)
    {
        try
        {
            return loaded.Context.Tokenize(text, addBos: false, special: true).Count();
        }
        catch
        {
            return Math.Max(1, text.Length / 4);
        }
    }

    private static IReadOnlyList<OpenAiToolCall> TryExtractToolCalls(
        string generated,
        IReadOnlyList<OpenAiTool> requestTools,
        out string cleanText)
    {
        cleanText = generated.Trim();
        if (requestTools.Count == 0)
        {
            return [];
        }

        var json = ExtractJsonObject(cleanText);
        if (json is null)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                var calls = JsonSerializer.Deserialize<List<OpenAiToolCall>>(toolCallsElement.GetRawText(), OpenAiJson.CreateOptions()) ?? [];
                if (calls.Count > 0)
                {
                    cleanText = string.Empty;
                    return calls;
                }
            }

            if (document.RootElement.TryGetProperty("function_call", out var functionCallElement) && functionCallElement.ValueKind == JsonValueKind.Object)
            {
                var call = JsonSerializer.Deserialize<OpenAiFunctionCall>(functionCallElement.GetRawText(), OpenAiJson.CreateOptions());
                if (call is not null)
                {
                    cleanText = string.Empty;
                    return
                    [
                        new OpenAiToolCall
                        {
                            Id = "call_" + Guid.NewGuid().ToString("N"),
                            Type = "function",
                            Function = call
                        }
                    ];
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [];
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static int CountOccurrences(string text, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static string CreateCompletionId(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string ToSse(object payload)
    {
        return "data: " + JsonSerializer.Serialize(payload, OpenAiJson.CreateOptions()) + "\n\n";
    }

    private static OpenAiProtocolException CreateCapabilityError(string modelId, string capability)
    {
        return new OpenAiProtocolException(
            StatusCodes.Status400BadRequest,
            $"Model `{modelId}` does not declare `{capability}` capability.",
            code: "model_capability_not_supported",
            param: "model");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var model in _models.Values)
        {
            await model.LoadLock.WaitAsync();
            try
            {
                model.Loaded?.Dispose();
                model.Loaded = null;
            }
            finally
            {
                model.LoadLock.Release();
                model.LoadLock.Dispose();
                model.InferenceLock.Dispose();
            }
        }
    }

    private ModelRuntime ResolveRuntime(string? requestedModel)
    {
        var modelId = string.IsNullOrWhiteSpace(requestedModel) ? DefaultModelId : requestedModel;
        if (_models.TryGetValue(modelId, out var model))
        {
            return model;
        }

        throw new OpenAiProtocolException(
            StatusCodes.Status404NotFound,
            $"Model `{modelId}` was not found. Use GET /v1/models to list configured models.",
            type: "invalid_request_error",
            code: "model_not_found",
            param: "model");
    }

    private string ResolveDefaultModelId()
    {
        if (!string.IsNullOrWhiteSpace(_options.DefaultModel) && _models.ContainsKey(_options.DefaultModel))
        {
            return _options.DefaultModel;
        }

        if (!string.IsNullOrWhiteSpace(_options.ModelId) && _models.ContainsKey(_options.ModelId))
        {
            return _options.ModelId;
        }

        return _models.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First();
    }

    private sealed class ModelRuntime
    {
        public ModelRuntime(LLamaModelRuntimeOptions options)
        {
            Options = options;
        }

        public LLamaModelRuntimeOptions Options { get; }

        public SemaphoreSlim LoadLock { get; } = new(1, 1);

        public SemaphoreSlim InferenceLock { get; } = new(1, 1);

        public LoadedModel? Loaded { get; set; }

        public bool IsLoaded => Loaded is not null;
    }

    private sealed class LoadedModel : IDisposable
    {
        public LoadedModel(LLamaWeights weights, LLamaContext context, InteractiveExecutor executor, MtmdWeights? mtmd, string? mediaMarker)
        {
            Weights = weights;
            Context = context;
            Executor = executor;
            Mtmd = mtmd;
            MediaMarker = mediaMarker;
        }

        public LLamaWeights Weights { get; }

        public LLamaContext Context { get; }

        public InteractiveExecutor Executor { get; }

        public MtmdWeights? Mtmd { get; }

        public string? MediaMarker { get; }

        public void Dispose()
        {
            CleanupMedia(this);
            Mtmd?.Dispose();
            Context.Dispose();
            Weights.Dispose();
        }
    }
}
