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
    private readonly ToolExecutor? _toolExecutor;

    public LLamaInferenceService(IOptions<LLamaStackOptions> options, ILogger<LLamaInferenceService> logger, ToolExecutor? toolExecutor = null)
    {
        _options = options.Value;
        _logger = logger;
        _toolExecutor = toolExecutor;
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
            await EnsureModelLoadedAsync(model, cancellationToken);
        }
    }

    public async Task LoadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(modelId);
        await EnsureModelLoadedAsync(model, cancellationToken);
    }

    public Task UnloadModelAsync(string modelId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = ResolveRuntime(modelId);
        return UnloadModelInternalAsync(model);
    }

    private static async Task UnloadModelInternalAsync(ModelRuntime model)
    {
        await model.UnloadAsync();
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

        var modelPath = ResolvePath(model.Options.ModelPath);
        if (!File.Exists(modelPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Configured GGUF model file was not found for `{model.Options.Id}`: {modelPath}",
                type: "server_error",
                code: "model_not_found");
        }

        if ((hasImage || hasAudio) && !string.IsNullOrWhiteSpace(model.Options.MmprojPath) && !File.Exists(ResolvePath(model.Options.MmprojPath)))
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
        var n = Math.Max(1, request.N ?? 1);
        if (n > 1)
        {
            return await MultiChoiceCompleteAsync(request, n, cancellationToken);
        }

        return await CompleteWithToolLoopAsync(request, cancellationToken);
    }

    private async Task<InferenceCompletion> MultiChoiceCompleteAsync(InferenceRequest request, int n, CancellationToken cancellationToken)
    {
        var first = await CompleteWithToolLoopAsync(request, cancellationToken);
        var choices = new List<InferenceChoice>
        {
            new()
            {
                Index = 0,
                Text = first.Text,
                ToolCalls = first.ToolCalls,
                CompletionTokens = first.CompletionTokens
            }
        };

        for (var i = 1; i < n; i++)
        {
            var choice = await CompleteWithToolLoopAsync(request.WithChoiceIndex(i), cancellationToken);
            choices.Add(new InferenceChoice
            {
                Index = i,
                Text = choice.Text,
                ToolCalls = choice.ToolCalls,
                CompletionTokens = choice.CompletionTokens
            });
        }

        return new InferenceCompletion
        {
            Id = first.Id,
            Model = first.Model,
            Text = choices[0].Text,
            ToolCalls = choices[0].ToolCalls,
            Choices = choices,
            PromptTokens = first.PromptTokens,
            CompletionTokens = choices.Sum(x => x.CompletionTokens),
            Metadata = request.Metadata,
            User = request.User,
            ServiceTier = request.ServiceTier,
            Store = request.Store,
            CompatibilityWarnings = request.CompatibilityWarnings
        };
    }

    private async Task<InferenceCompletion> CompleteWithToolLoopAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        var maxRounds = Math.Max(1, request.MaxToolCalls ?? 10);
        var currentMessages = request.Messages.ToList();
        var toolRounds = new List<ToolRound>();

        InferenceCompletion? completion = null;
        var currentRequest = request;

        for (var round = 0; round < maxRounds; round++)
        {
            completion = await SingleCompleteAsync(currentRequest, cancellationToken);

            if (completion.ToolCalls.Count == 0)
            {
                break;
            }

            if (_toolExecutor is null || !_toolExecutor.CanExecute(completion.ToolCalls))
            {
                break;
            }

            var parallel = request.ParallelToolCalls == true;
            var results = await _toolExecutor.ExecuteAsync(completion.ToolCalls, cancellationToken, parallel);
            toolRounds.Add(new ToolRound
            {
                ToolCalls = completion.ToolCalls,
                Results = results
            });

            currentMessages = _toolExecutor.BuildToolResultMessages(
                completion.ToolCalls, results, currentMessages).ToList();

            var warnings = currentRequest.CompatibilityWarnings
                .Concat([$"Tool call round {round + 1}: `{string.Join(", ", completion.ToolCalls.Select(x => x.Function.Name))}` executed."])
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            currentRequest = currentRequest.WithMessages(currentMessages, warnings);
        }

        return new InferenceCompletion
        {
            Id = completion?.Id ?? CreateCompletionId("chatcmpl"),
            Model = completion?.Model ?? ResolveRuntime(request.RequestedModel).Options.Id,
            Text = completion?.Text ?? string.Empty,
            ToolCalls = toolRounds.Count > 0 ? toolRounds.Last().ToolCalls : (completion?.ToolCalls ?? []),
            ToolRounds = toolRounds,
            PromptTokens = completion?.PromptTokens ?? 0,
            CompletionTokens = completion?.CompletionTokens ?? 0,
            Metadata = request.Metadata,
            User = request.User,
            ServiceTier = request.ServiceTier,
            Store = request.Store,
            CompatibilityWarnings = currentRequest.CompatibilityWarnings
        };
    }

    private async Task<InferenceCompletion> SingleCompleteAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
        request = ApplyTruncation(request, loaded, model);
        var createdText = new StringBuilder();
            await foreach (var token in StreamTextAsync(request, loaded, model, cancellationToken))
            {
                createdText.Append(token);
            }

            var text = createdText.ToString();
            var toolCalls = TryExtractToolCalls(text, request.Tools, out var cleanText);
            var prompt = BuildPrompt(loaded.Weights, request, loaded.MediaMarker, out _, out _);
            var completionTokens = CountTokens(loaded, cleanText);
            var finishReason = DetermineFinishReason(toolCalls.Count > 0, completionTokens, request.MaxTokens, model.Options.DefaultMaxTokens);
            return new InferenceCompletion
            {
                Id = CreateCompletionId("chatcmpl"),
                Model = model.Options.Id,
                Text = cleanText,
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                PromptTokens = CountTokens(loaded, prompt),
                CompletionTokens = completionTokens,
                Metadata = request.Metadata,
                User = request.User,
                ServiceTier = request.ServiceTier,
                Store = request.Store,
                CompatibilityWarnings = request.CompatibilityWarnings
            };
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    private static string DetermineFinishReason(bool hasToolCalls, int completionTokens, int? requestMaxTokens, int defaultMaxTokens)
    {
        if (hasToolCalls)
        {
            return "tool_calls";
        }

        var effectiveMax = requestMaxTokens ?? defaultMaxTokens;
        if (completionTokens >= effectiveMax)
        {
            return "length";
        }

        return "stop";
    }

    public async Task<(string Model, int PromptTokens)> EstimatePromptUsageAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
            var truncated = ApplyTruncation(request, loaded, model);
            var prompt = BuildPrompt(loaded.Weights, truncated, loaded.MediaMarker, out _, out _);
            return (model.Options.Id, CountTokens(loaded, prompt));
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    public async Task<int> CountTokensAsync(string text, string? requestedModel, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(requestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
            return CountTokens(loaded, text);
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    public async Task<IReadOnlyList<int>> TokenizeAsync(string text, string? requestedModel, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(requestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
            return loaded.Context.Tokenize(text, addBos: false, special: true).Select(t => (int)t).ToArray();
        }
        catch
        {
            return [];
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    public async Task<string> DetokenizeAsync(IReadOnlyList<int> tokens, string? requestedModel, CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(requestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
            var decoder = new StreamingTokenDecoder(loaded.Context);
            foreach (var token in tokens)
            {
                decoder.Add(token);
            }

            return decoder.Read();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    public async Task<EmbeddingResult> EmbeddingsAsync(
        IReadOnlyList<string> inputs,
        string? requestedModel,
        CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(requestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {

        var embedderParams = new ModelParams(ResolvePath(model.Options.ModelPath!))
        {
            ContextSize = model.Options.ContextSize,
            GpuLayerCount = model.Options.GpuLayerCount,
            Threads = model.Options.Threads,
            BatchThreads = model.Options.BatchThreads,
            UseMemorymap = model.Options.UseMemoryMap,
            UseMemoryLock = model.Options.UseMemoryLock,
            BatchSize = model.Options.BatchSize ?? 512,
            Embeddings = true
        };

        var data = new List<EmbeddingData>();
        var totalTokens = 0;
        var embedder = new LLamaEmbedder(loaded.Weights, embedderParams, _logger);
        try
        {
            foreach (var input in inputs)
            {
                var tokens = loaded.Context.Tokenize(input, addBos: true, special: true);
                totalTokens += tokens.Count();
                var vectors = await embedder.GetEmbeddings(input, cancellationToken);
                if (vectors is { Count: > 0 })
                {
                    data.Add(new EmbeddingData
                    {
                        Index = data.Count,
                        Embedding = vectors[0],
                        Object = "embedding"
                    });
                }
            }
        }
        finally
        {
            embedder.Dispose();
        }

        return new EmbeddingResult
        {
            Data = data,
            TotalTokens = totalTokens
        };
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    private async IAsyncEnumerable<string> StreamTextAsync(
        InferenceRequest request,
        LoadedModel loaded,
        ModelRuntime model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request = ApplyTruncation(request, loaded, model);
        var prompt = BuildPrompt(loaded.Weights, request, loaded.MediaMarker, out var media, out var mediaCount);
        var inferenceParams = CreateInferenceParams(model.Options, request);
        ResetExecutorState(loaded);
        LoadMedia(loaded, media, mediaCount);

        try
        {
            await foreach (var token in loaded.Executor.InferAsync(prompt, inferenceParams, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return token;
            }
        }
        finally
        {
            CleanupMedia(loaded);
        }
    }

    public async IAsyncEnumerable<string> StreamChatEventsAsync(
        InferenceRequest request,
        string responseId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
        var completion = new StringBuilder();
        await foreach (var token in StreamTextAsync(request, loaded, model, cancellationToken))
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
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    public async IAsyncEnumerable<string> StreamResponsesEventsAsync(
        InferenceRequest request,
        string responseId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
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
        await foreach (var token in StreamTextAsync(request, loaded, model, cancellationToken))
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
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    private async Task EnsureModelLoadedAsync(ModelRuntime model, CancellationToken cancellationToken)
    {
        if (model.IsLoaded)
            return;

        await model.LoadLock.WaitAsync(cancellationToken);
        try
        {
            if (model.IsLoaded)
                return;

            await LoadModelInstancesAsync(model, cancellationToken);
        }
        finally
        {
            model.LoadLock.Release();
        }
    }

    private async Task<LoadedModel> AcquireLoadedModelAsync(ModelRuntime model, CancellationToken cancellationToken)
    {
        if (!model.IsLoaded)
        {
            await model.LoadLock.WaitAsync(cancellationToken);
            try
            {
                if (!model.IsLoaded)
                {
                    await LoadModelInstancesAsync(model, cancellationToken);
                }
            }
            finally
            {
                model.LoadLock.Release();
            }
        }

        return await model.AcquireAsync(cancellationToken);
    }

    private static void ReleaseLoadedModel(ModelRuntime model, LoadedModel loaded)
    {
        model.Release(loaded);
    }

    private async Task LoadModelInstancesAsync(ModelRuntime model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model.Options.ModelPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Model `{model.Options.Id}` is not configured with a GGUF file path. Set LLamaStack:Models[].ModelPath or LLamaStack:ModelPath before serving inference.",
                type: "server_error",
                code: "model_path_not_configured");
        }

        var modelPath = ResolvePath(model.Options.ModelPath);
        if (!File.Exists(modelPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Configured GGUF model file was not found for `{model.Options.Id}`: {modelPath}",
                type: "server_error",
                code: "model_not_found");
        }

        var parameters = CreateModelParams(modelPath, model.Options);
        _logger.LogInformation("Loading GGUF model {ModelId} from {ModelPath}", model.Options.Id, model.Options.ModelPath);
        var weights = await LLamaWeights.LoadFromFileAsync(parameters, cancellationToken);

        MtmdWeights? mtmd = null;
        string? mediaMarker = null;
        if (!string.IsNullOrWhiteSpace(model.Options.MmprojPath))
        {
            var mmprojPath = ResolvePath(model.Options.MmprojPath);
            if (!File.Exists(mmprojPath))
            {
                throw new OpenAiProtocolException(
                    StatusCodes.Status503ServiceUnavailable,
                    $"Configured mmproj file was not found for `{model.Options.Id}`: {mmprojPath}",
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

        var shared = new ModelWeights(weights, mtmd, mediaMarker);
        var maxConcurrency = model.Options.MaxConcurrency;
        var instances = new List<LoadedModel>(maxConcurrency);

        for (var i = 0; i < maxConcurrency; i++)
        {
            var context = weights.CreateContext(parameters);
            var executor = mtmd is null ? new InteractiveExecutor(context) : new InteractiveExecutor(context, mtmd);
            instances.Add(new LoadedModel(shared, context, executor));
        }

        model.Initialize(instances, shared);
    }

    private static ModelParams CreateModelParams(string modelPath, LLamaModelRuntimeOptions model)
    {
        var parameters = new ModelParams(modelPath)
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
    }

    private static void CleanupMedia(LoadedModel loaded)
    {
        foreach (var embed in loaded.Executor.Embeds)
        {
            embed.Dispose();
        }

        loaded.Executor.Embeds.Clear();
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

    private InferenceRequest ApplyTruncation(InferenceRequest request, LoadedModel loaded, ModelRuntime model)
    {
        if (!string.Equals(request.Truncation, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return request;
        }

        var ctxSize = (int)(model.Options.ContextSize ?? 4096);
        var headroom = Math.Min(2048, ctxSize / 5);
        var maxPromptTokens = ctxSize - headroom;
        var truncated = request.Messages.ToList();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var testRequest = request.WithMessages(truncated);
            var allMedia = new List<InferenceMedia>();
            var messages = BuildPromptMessages(testRequest, loaded.MediaMarker, allMedia);
            var tempRequest = request.WithMessages(truncated);
            // quick token estimate without building full template
            var roughText = string.Join("\n", messages.Select(m => m.Role + ": " + m.Content));
            var tokenCount = CountTokens(loaded, roughText);
            if (tokenCount <= maxPromptTokens)
            {
                break;
            }

            var removed = RemoveMiddleMessage(truncated);
            if (!removed)
            {
                break;
            }
        }

        return request.WithMessages(truncated);
    }

    private static bool RemoveMiddleMessage(List<InferenceMessage> messages)
    {
        // find index range of non-system messages
        var nonSystem = messages.Select((m, i) => i).Where(i => !string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (nonSystem.Length <= 1)
        {
            return false;
        }

        var mid = nonSystem[nonSystem.Length / 2];
        messages.RemoveAt(mid);
        return true;
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

        if (request.ParallelToolCalls == true)
        {
            builder.AppendLine("You may call multiple tools at once. Respond only with JSON in this exact shape:");
            builder.AppendLine("""{"tool_calls":[{"id":"call_<unique>","type":"function","function":{"name":"tool_name","arguments":"{\"arg\":\"value\"}"}},{"id":"call_<unique>","type":"function","function":{"name":"another_tool","arguments":"{\"arg\":\"value\"}"}}]}""");
        }
        else
        {
            builder.AppendLine("When calling a tool, respond only with JSON in this exact shape:");
            builder.AppendLine("""{"tool_calls":[{"id":"call_<unique>","type":"function","function":{"name":"tool_name","arguments":"{\"arg\":\"value\"}"}}]}""");
        }
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
                    var valid = ValidateToolCalls(calls, requestTools);
                    if (valid.Count > 0)
                    {
                        cleanText = string.Empty;
                        return valid;
                    }
                }
            }

            if (document.RootElement.TryGetProperty("function_call", out var functionCallElement) && functionCallElement.ValueKind == JsonValueKind.Object)
            {
                var call = JsonSerializer.Deserialize<OpenAiFunctionCall>(functionCallElement.GetRawText(), OpenAiJson.CreateOptions());
                if (call is not null)
                {
                    var toolCall = new OpenAiToolCall
                    {
                        Id = "call_" + Guid.NewGuid().ToString("N"),
                        Type = "function",
                        Function = call
                    };
                    var single = ValidateToolCalls([toolCall], requestTools);
                    if (single.Count > 0)
                    {
                        cleanText = string.Empty;
                        return single;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [];
    }

    private static IReadOnlyList<OpenAiToolCall> ValidateToolCalls(
        IReadOnlyList<OpenAiToolCall> calls,
        IReadOnlyList<OpenAiTool> requestTools)
    {
        var valid = new List<OpenAiToolCall>();
        var schemaIndex = requestTools
            .Where(t => t.Function is not null)
            .ToDictionary(t => t.Function!.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var call in calls)
        {
            if (call.Function is null || string.IsNullOrWhiteSpace(call.Function.Name))
            {
                continue;
            }

            if (!schemaIndex.TryGetValue(call.Function.Name, out var toolDef))
            {
                valid.Add(call);
                continue;
            }

            if (toolDef.Function?.Parameters is null || toolDef.Function.Parameters.Value.ValueKind != JsonValueKind.Object)
            {
                valid.Add(call);
                continue;
            }

            var schema = toolDef.Function.Parameters.Value;
            var argsValid = ValidateArgumentsAgainstSchema(call.Function.Arguments, schema);
            if (argsValid)
            {
                valid.Add(call);
            }
        }

        return valid;
    }

    private static bool ValidateArgumentsAgainstSchema(string argumentsJson, JsonElement schema)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return false;
        }

        JsonDocument argsDoc;
        try
        {
            argsDoc = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (argsDoc)
        {
            var args = argsDoc.RootElement;
            if (args.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!schema.TryGetProperty("required", out var requiredElement) || requiredElement.ValueKind != JsonValueKind.Array)
            {
                return true;
            }

            foreach (var required in requiredElement.EnumerateArray())
            {
                if (required.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var propName = required.GetString();
                if (string.IsNullOrWhiteSpace(propName))
                {
                    continue;
                }

                if (!args.TryGetProperty(propName, out _))
                {
                    return false;
                }
            }

            if (!schema.TryGetProperty("properties", out var propsElement) || propsElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            foreach (var prop in propsElement.EnumerateObject())
            {
                if (!args.TryGetProperty(prop.Name, out var argValue))
                {
                    continue;
                }

                if (!prop.Value.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var expectedType = typeElement.GetString();
                if (!IsJsonTypeCompatible(argValue, expectedType))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsJsonTypeCompatible(JsonElement value, string? expectedType)
    {
        return expectedType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => true
        };
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
            await model.UnloadAsync();
            model.LoadLock.Dispose();
        }
    }

    public int GetModelMaxConcurrency(string? modelId)
    {
        return ResolveRuntime(modelId).Options.MaxConcurrency;
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

    private sealed class ModelWeights : IDisposable
    {
        public LLamaWeights LlamaWeights { get; }
        public MtmdWeights? Mtmd { get; }
        public string? MediaMarker { get; }

        public ModelWeights(LLamaWeights llamaWeights, MtmdWeights? mtmd, string? mediaMarker)
        {
            LlamaWeights = llamaWeights;
            Mtmd = mtmd;
            MediaMarker = mediaMarker;
        }

        public void Dispose()
        {
            Mtmd?.Dispose();
            LlamaWeights.Dispose();
        }
    }

    private sealed class ModelRuntime
    {
        public LLamaModelRuntimeOptions Options { get; }
        public SemaphoreSlim LoadLock { get; } = new(1, 1);

        private readonly object _lock = new();
        private readonly List<LoadedModel> _allInstances = [];
        private readonly Queue<LoadedModel> _available = new();
        private SemaphoreSlim? _instanceSemaphore;
        private ModelWeights? _shared;
        private volatile bool _loaded;

        public bool IsLoaded => _loaded;

        public ModelRuntime(LLamaModelRuntimeOptions options)
        {
            Options = options;
        }

        public void Initialize(List<LoadedModel> instances, ModelWeights shared)
        {
            _shared = shared;
            foreach (var inst in instances)
            {
                _allInstances.Add(inst);
                _available.Enqueue(inst);
            }
            _instanceSemaphore = new SemaphoreSlim(instances.Count, instances.Count);
            _loaded = true;
        }

        public async Task<LoadedModel> AcquireAsync(CancellationToken ct)
        {
            await _instanceSemaphore!.WaitAsync(ct);
            lock (_lock)
            {
                return _available.Dequeue();
            }
        }

        public void Release(LoadedModel model)
        {
            lock (_lock)
            {
                if (!_loaded)
                {
                    model.Dispose();
                    return;
                }
                _available.Enqueue(model);
            }
            _instanceSemaphore!.Release();
        }

        public async Task UnloadAsync()
        {
            await LoadLock.WaitAsync();
            try
            {
                if (!_loaded) return;
                _loaded = false;

                List<LoadedModel> toDispose;
                lock (_lock)
                {
                    toDispose = [.. _allInstances];
                    _allInstances.Clear();
                    _available.Clear();
                }

                foreach (var instance in toDispose)
                {
                    instance.Dispose();
                }

                _shared?.Dispose();
                _shared = null;
                _instanceSemaphore?.Dispose();
                _instanceSemaphore = null;
            }
            finally
            {
                LoadLock.Release();
            }
        }
    }

    private static string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }

    private sealed class LoadedModel : IDisposable
    {
        private readonly ModelWeights _shared;

        public LLamaWeights Weights => _shared.LlamaWeights;
        public MtmdWeights? Mtmd => _shared.Mtmd;
        public string? MediaMarker => _shared.MediaMarker;
        public LLamaContext Context { get; }
        public InteractiveExecutor Executor { get; }

        public LoadedModel(ModelWeights shared, LLamaContext context, InteractiveExecutor executor)
        {
            _shared = shared;
            Context = context;
            Executor = executor;
        }

        public void Dispose()
        {
            Context.Dispose();
        }
    }
}
