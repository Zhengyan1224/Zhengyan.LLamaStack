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
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Api.Inference;

public sealed class LLamaInferenceService : IAsyncDisposable
{
    private readonly LLamaStackOptions _options;
    private readonly ILogger<LLamaInferenceService> _logger;
    private readonly Dictionary<string, ModelRuntime> _models;
    private readonly Dictionary<string, EmbeddingModelRuntime> _embeddingRuntimes;

    public LLamaInferenceService(IOptions<LLamaStackOptions> options, ILogger<LLamaInferenceService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _models = _options.GetModelRegistrations()
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => new ModelRuntime(x.First()), StringComparer.OrdinalIgnoreCase);
        _embeddingRuntimes = _options.GetEmbeddingModelRegistrations()
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => new EmbeddingModelRuntime(x.First()), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsLoaded => _models.Values.Any(x => x.IsLoaded) || _embeddingRuntimes.Values.Any(x => x.IsLoaded);

    public string DefaultModelId => ResolveDefaultModelId();

    public string DefaultEmbeddingModelId => ResolveDefaultEmbeddingModelId();

    public IReadOnlyList<ModelDescriptor> GetModels()
    {
        var chatModels = _models.Values
            .OrderBy(x => x.Options.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ModelDescriptor
            {
                Id = x.Options.Id,
                Created = x.Options.Created,
                OwnedBy = x.Options.OwnedBy,
                Loaded = x.IsLoaded,
                ModelPath = x.Options.ModelPath,
                MmprojPath = x.Options.MmprojPath,
                Capabilities = x.Options.Capabilities,
                EmbeddingDimensions = 0
            });
        var embeddingModels = _embeddingRuntimes.Values
            .OrderBy(x => x.Options.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ModelDescriptor
            {
                Id = x.Options.Id,
                Created = 0,
                OwnedBy = "local",
                Loaded = x.IsLoaded,
                ModelPath = x.Options.ModelPath,
                Capabilities = new LLamaModelCapabilities { Embeddings = true },
                EmbeddingDimensions = x.Options.Dimensions
            });
        return chatModels.Concat(embeddingModels).ToArray();
    }

    public async Task WarmupAsync(CancellationToken cancellationToken)
    {
        foreach (var model in _models.Values.Where(x => x.Options.LoadModelOnStartup))
        {
            await EnsureModelLoadedAsync(model, cancellationToken);
        }

        foreach (var model in _embeddingRuntimes.Values.Where(x => _options.LoadModelOnStartup))
        {
            await EnsureEmbeddingModelLoadedAsync(model, cancellationToken);
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

        return await CompleteOnceAsync(request, cancellationToken);
    }

    private async Task<InferenceCompletion> MultiChoiceCompleteAsync(InferenceRequest request, int n, CancellationToken cancellationToken)
    {
        var first = await CompleteOnceAsync(request, cancellationToken);
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
            var choice = await CompleteOnceAsync(request.WithChoiceIndex(i), cancellationToken);
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

    private async Task<InferenceCompletion> CompleteOnceAsync(InferenceRequest request, CancellationToken cancellationToken)
    {
        return await SingleCompleteAsync(request, cancellationToken);
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
            var toolCalls = TryExtractToolCalls(text, request, out var cleanText);

            if (toolCalls.Count == 0 && ShouldRetryToolProtocol(request, cleanText))
            {
                var retryRequest = AddToolProtocolRetryNudge(request);
                createdText.Clear();
                await foreach (var token in StreamTextAsync(retryRequest, loaded, model, cancellationToken))
                {
                    createdText.Append(token);
                }

                request = retryRequest;
                text = createdText.ToString();
                toolCalls = TryExtractToolCalls(text, request, out cleanText);
            }

            if (toolCalls.Count == 0 && HasToolResultMessage(request) && IsNonAnswer(cleanText))
            {
                var retryRequest = AddToolResultContinuationNudge(request);
                createdText.Clear();
                await foreach (var token in StreamTextAsync(retryRequest, loaded, model, cancellationToken))
                {
                    createdText.Append(token);
                }

                request = retryRequest;
                text = createdText.ToString();
                toolCalls = TryExtractToolCalls(text, request, out cleanText);

                if (toolCalls.Count == 0 && IsNonAnswer(cleanText))
                {
                    cleanText = BuildToolResultNonAnswerFallback(request);
                }
            }

            if (request.StrictJsonSchema && toolCalls.Count == 0 && !string.IsNullOrWhiteSpace(request.JsonSchema) && !string.IsNullOrWhiteSpace(cleanText))
            {
                try
                {
                    ValidateJsonOutput(cleanText, request.JsonSchema);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("Strict JSON Schema validation failed: {Error}", ex.Message);
                    throw new OpenAiProtocolException(
                        StatusCodes.Status422UnprocessableEntity,
                        $"Model output did not satisfy the requested strict JSON schema: {ex.Message}",
                        code: "json_schema_validation_failed",
                        param: "response_format");
                }
            }

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
        int? dimensions,
        CancellationToken cancellationToken)
    {
        var model = ResolveEmbeddingRuntime(requestedModel);
        EmbeddingLoadedModel embedder;
        if (model.IsLoaded)
        {
            embedder = await model.AcquireAsync(cancellationToken);
        }
        else
        {
            embedder = await CreateEmbedderAsync(model.Options, cancellationToken);
        }

        try
        {
            var data = new List<EmbeddingData>();
            var totalTokens = 0;
            foreach (var input in inputs)
            {
                var tokens = embedder.Embedder.Context.Tokenize(input, addBos: true, special: true);
                totalTokens += tokens.Count();
                var vectors = await embedder.Embedder.GetEmbeddings(input, cancellationToken);
                if (vectors is { Count: > 0 })
                {
                    var embedding = vectors[0];
                    if (dimensions.HasValue && dimensions.Value > 0 && dimensions.Value < embedding.Length)
                    {
                        var truncated = new float[dimensions.Value];
                        Array.Copy(embedding, truncated, dimensions.Value);
                        embedding = truncated;
                    }

                    data.Add(new EmbeddingData
                    {
                        Index = data.Count,
                        Embedding = embedding,
                        Object = "embedding"
                    });
                }
            }

            return new EmbeddingResult
            {
                Data = data,
                TotalTokens = totalTokens
            };
        }
        finally
        {
            if (model.IsLoaded)
            {
                model.Release(embedder);
            }
            else
            {
                embedder.Dispose();
            }
        }
    }

    private async Task<EmbeddingLoadedModel> CreateEmbedderAsync(LLamaEmbeddingModelRuntimeOptions options, CancellationToken cancellationToken)
    {
        var modelPath = ResolvePath(options.ModelPath!);
        var embedderParams = new ModelParams(modelPath)
        {
            ContextSize = 512,
            GpuLayerCount = options.GpuLayerCount,
            Threads = options.Threads,
            BatchThreads = options.BatchThreads,
            UseMemorymap = options.UseMemoryMap,
            UseMemoryLock = options.UseMemoryLock,
            BatchSize = options.BatchSize,
            Embeddings = true
        };

        _logger.LogInformation("Loading embedding model {ModelId} from {ModelPath}", options.Id, options.ModelPath);
        var weights = await LLamaWeights.LoadFromFileAsync(embedderParams, cancellationToken);
        var embedder = new LLamaEmbedder(weights, embedderParams, _logger);
        return new EmbeddingLoadedModel(weights, embedder);
    }

    private async Task EnsureEmbeddingModelLoadedAsync(EmbeddingModelRuntime model, CancellationToken cancellationToken)
    {
        if (model.IsLoaded)
            return;

        await model.LoadLock.WaitAsync(cancellationToken);
        try
        {
            if (model.IsLoaded)
                return;

            await LoadEmbeddingModelInstancesAsync(model, cancellationToken);
        }
        finally
        {
            model.LoadLock.Release();
        }
    }

    private async Task LoadEmbeddingModelInstancesAsync(EmbeddingModelRuntime model, CancellationToken cancellationToken)
    {
        var options = model.Options;
        var modelPath = ResolvePath(options.ModelPath!);
        if (!File.Exists(modelPath))
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Configured embedding model file was not found for `{options.Id}`: {modelPath}",
                type: "server_error",
                code: "model_not_found");
        }

        var weightBytes = new FileInfo(modelPath).Length;
        var contextBytes = 512L * 1024;

        var totalWeightBytes = _models.Values.Where(m => m.IsLoaded).Sum(m => m.TotalEstimatedBytes)
            + _embeddingRuntimes.Values.Where(m => m.IsLoaded).Sum(m => m.TotalEstimatedBytes);
        var newTotalBytes = totalWeightBytes + weightBytes + contextBytes * options.MaxConcurrency;
        var maxVram = _options.MaxVramBytes;
        if (maxVram > 0 && newTotalBytes > maxVram)
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Loading embedding model `{options.Id}` would exceed VRAM budget of {maxVram} bytes "
                + $"(estimated: {newTotalBytes} bytes). Increase LLamaStack:MaxVramBytes or unload another model first.",
                type: "server_error",
                code: "vram_budget_exceeded");
        }

        var embedderParams = new ModelParams(modelPath)
        {
            ContextSize = 512,
            GpuLayerCount = options.GpuLayerCount,
            Threads = options.Threads,
            BatchThreads = options.BatchThreads,
            UseMemorymap = options.UseMemoryMap,
            UseMemoryLock = options.UseMemoryLock,
            BatchSize = options.BatchSize,
            Embeddings = true
        };

        _logger.LogInformation("Loading embedding model {ModelId} from {ModelPath}", options.Id, options.ModelPath);
        var weights = await LLamaWeights.LoadFromFileAsync(embedderParams, cancellationToken);

        var maxConcurrency = options.MaxConcurrency;
        var instances = new List<EmbeddingLoadedModel>(maxConcurrency);
        for (var i = 0; i < maxConcurrency; i++)
        {
            var embedder = new LLamaEmbedder(weights, embedderParams, _logger);
            instances.Add(new EmbeddingLoadedModel(weights, embedder));
        }

        model.Initialize(instances, weightBytes, contextBytes);
    }

    private (string Prompt, int PromptTokens, IReadOnlyList<InferenceMedia> Media, int MediaCount) BuildPromptWithCount(
        InferenceRequest request, LoadedModel loaded, ModelRuntime model)
    {
        request = ApplyTruncation(request, loaded, model);
        var prompt = BuildPrompt(loaded.Weights, request, loaded.MediaMarker, out var media, out var mediaCount);
        var promptTokens = CountTokens(loaded, prompt);
        return (prompt, promptTokens, media, mediaCount);
    }

    private async IAsyncEnumerable<string> StreamTextAsync(
        string prompt, InferenceParams inferenceParams, LoadedModel loaded,
        IReadOnlyList<InferenceMedia> media, int mediaCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

    private async IAsyncEnumerable<string> StreamTextAsync(
        InferenceRequest request,
        LoadedModel loaded,
        ModelRuntime model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (prompt, _, media, mediaCount) = BuildPromptWithCount(request, loaded, model);
        var inferenceParams = CreateInferenceParams(model.Options, request);
        await foreach (var token in StreamTextAsync(prompt, inferenceParams, loaded, media, mediaCount, cancellationToken))
        {
            yield return token;
        }
    }

    public async IAsyncEnumerable<string> StreamChatEventsAsync(
        InferenceRequest request,
        string responseId,
        bool includeUsage,
        IOpenAiStore? store,
        long? created,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.Tools.Count > 0)
        {
            await foreach (var evt in StreamBufferedChatCompletionAsync(request, responseId, includeUsage, store, created, cancellationToken))
            {
                yield return evt;
            }

            yield break;
        }

        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
        var (prompt, promptTokens, media, mediaCount) = BuildPromptWithCount(request, loaded, model);
        var inferenceParams = CreateInferenceParams(model.Options, request);
        var completion = new StringBuilder();
        await foreach (var token in StreamTextAsync(prompt, inferenceParams, loaded, media, mediaCount, cancellationToken))
        {
            completion.Append(token);
            var chunk = new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = UnixNow(),
                model = model.Options.Id,
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

        var generatedText = completion.ToString();
        var toolCalls = TryExtractToolCalls(generatedText, request, out _);
        var finishReason = toolCalls.Count > 0 ? "tool_calls" : "stop";
        if (toolCalls.Count > 0)
        {
            var toolChunk = new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = UnixNow(),
                model = model.Options.Id,
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
            model = model.Options.Id,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = finishReason
                }
            }
        });

        var outputTokens = CountTokens(loaded, generatedText);

        if (includeUsage)
        {
            yield return ToSse(new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = UnixNow(),
                model = model.Options.Id,
                choices = Array.Empty<object>(),
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = outputTokens,
                    total_tokens = promptTokens + outputTokens
                }
            });
        }

        if (store is not null && request.Store == true)
        {
            var now = created ?? UnixNow();
            await store.AddChatCompletionAsync(responseId, now, request, new InferenceCompletion
            {
                Id = responseId,
                Model = model.Options.Id,
                Text = toolCalls.Count > 0 ? string.Empty : generatedText,
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                PromptTokens = promptTokens,
                CompletionTokens = outputTokens,
                Metadata = request.Metadata,
                User = request.User,
                ServiceTier = request.ServiceTier,
                Store = true,
                CompatibilityWarnings = request.CompatibilityWarnings
            }, cancellationToken);
        }
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    public async IAsyncEnumerable<string> StreamResponsesEventsAsync(
        InferenceRequest request,
        string responseId,
        bool includeUsage,
        IOpenAiStore? store,
        long? created,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (request.Tools.Count > 0)
        {
            await foreach (var evt in StreamBufferedResponsesCompletionAsync(request, responseId, includeUsage, store, created, cancellationToken))
            {
                yield return evt;
            }

            yield break;
        }

        var model = ResolveRuntime(request.RequestedModel);
        var loaded = await AcquireLoadedModelAsync(model, cancellationToken);
        try
        {
        var (prompt, promptTokens, media, mediaCount) = BuildPromptWithCount(request, loaded, model);
        var inferenceParams = CreateInferenceParams(model.Options, request);

        yield return ToSse(new
        {
            type = "response.created",
            response = new
            {
                id = responseId,
                @object = "response",
                created_at = UnixNow(),
                status = "in_progress",
                model = model.Options.Id
            }
        });

        var completion = new StringBuilder();
        await foreach (var token in StreamTextAsync(prompt, inferenceParams, loaded, media, mediaCount, cancellationToken))
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

        var generatedText = completion.ToString();
        var toolCalls = TryExtractToolCalls(generatedText, request, out var cleanText);
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

        var outputTokens = CountTokens(loaded, generatedText);

        if (includeUsage)
        {
            yield return ToSse(new
            {
                type = "response.usage.delta",
                response_id = responseId,
                usage = new
                {
                    input_tokens = promptTokens,
                    output_tokens = outputTokens,
                    total_tokens = promptTokens + outputTokens
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
                model = model.Options.Id
            }
        });

        if (store is not null && request.Store != false)
        {
            var now = created ?? UnixNow();
            await store.AddResponseAsync(responseId, now, request, new InferenceCompletion
            {
                Id = responseId,
                Model = model.Options.Id,
                Text = toolCalls.Count > 0 ? string.Empty : cleanText,
                ToolCalls = toolCalls,
                FinishReason = "stop",
                PromptTokens = promptTokens,
                CompletionTokens = outputTokens,
                Metadata = request.Metadata,
                User = request.User,
                ServiceTier = request.ServiceTier,
                Store = request.Store,
                CompatibilityWarnings = request.CompatibilityWarnings
            }, cancellationToken);
        }
        }
        finally
        {
            ReleaseLoadedModel(model, loaded);
        }
    }

    private async IAsyncEnumerable<string> StreamBufferedChatCompletionAsync(
        InferenceRequest request,
        string responseId,
        bool includeUsage,
        IOpenAiStore? store,
        long? created,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await CompleteAsync(request, cancellationToken);
        var createdAt = created ?? UnixNow();
        var hasToolCalls = completion.ToolCalls.Count > 0 && string.IsNullOrWhiteSpace(completion.Text);

        if (!string.IsNullOrEmpty(completion.Text))
        {
            yield return ToSse(new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = createdAt,
                model = completion.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { content = completion.Text },
                        finish_reason = (string?)null
                    }
                }
            });
        }

        if (hasToolCalls)
        {
            yield return ToSse(new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = createdAt,
                model = completion.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { tool_calls = ToStreamingToolCallDeltas(completion.ToolCalls) },
                        finish_reason = (string?)null
                    }
                }
            });
        }

        yield return ToSse(new
        {
            id = responseId,
            @object = "chat.completion.chunk",
            created = createdAt,
            model = completion.Model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = hasToolCalls ? "tool_calls" : completion.FinishReason
                }
            }
        });

        if (includeUsage)
        {
            yield return ToSse(new
            {
                id = responseId,
                @object = "chat.completion.chunk",
                created = createdAt,
                model = completion.Model,
                choices = Array.Empty<object>(),
                usage = new
                {
                    prompt_tokens = completion.PromptTokens,
                    completion_tokens = completion.CompletionTokens,
                    total_tokens = completion.TotalTokens
                }
            });
        }

        if (store is not null && request.Store == true)
        {
            await store.AddChatCompletionAsync(responseId, createdAt, request, completion, cancellationToken);
        }
    }

    private static object[] ToStreamingToolCallDeltas(IReadOnlyList<OpenAiToolCall> toolCalls)
    {
        return toolCalls.Select((toolCall, index) => new
        {
            index,
            id = toolCall.Id,
            type = toolCall.Type,
            function = new
            {
                name = toolCall.Function.Name,
                arguments = toolCall.Function.Arguments
            }
        }).Cast<object>().ToArray();
    }

    private async IAsyncEnumerable<string> StreamBufferedResponsesCompletionAsync(
        InferenceRequest request,
        string responseId,
        bool includeUsage,
        IOpenAiStore? store,
        long? created,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var createdAt = created ?? UnixNow();
        yield return ToSse(new
        {
            type = "response.created",
            response = new
            {
                id = responseId,
                @object = "response",
                created_at = createdAt,
                status = "in_progress",
                model = request.RequestedModel ?? DefaultModelId
            }
        });

        var completion = await CompleteAsync(request, cancellationToken);

        if (!string.IsNullOrEmpty(completion.Text))
        {
            yield return ToSse(new
            {
                type = "response.output_text.delta",
                item_id = "msg_" + responseId,
                output_index = 0,
                content_index = 0,
                delta = completion.Text
            });
        }

        yield return ToSse(new
        {
            type = "response.output_text.done",
            item_id = "msg_" + responseId,
            output_index = 0,
            content_index = 0,
            text = completion.Text
        });

        foreach (var toolCall in completion.ToolCalls)
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

        if (includeUsage)
        {
            yield return ToSse(new
            {
                type = "response.usage.delta",
                response_id = responseId,
                usage = new
                {
                    input_tokens = completion.PromptTokens,
                    output_tokens = completion.CompletionTokens,
                    total_tokens = completion.TotalTokens
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
                created_at = createdAt,
                status = "completed",
                model = completion.Model
            }
        });

        if (store is not null && request.Store != false)
        {
            await store.AddResponseAsync(responseId, createdAt, request, completion, cancellationToken);
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

        var weightBytes = new FileInfo(modelPath).Length;
        var contextSize = model.Options.ContextSize ?? 4096;
        var contextBytes = (long)contextSize * 2048;
        var totalWeightBytes = _models.Values.Where(m => m.IsLoaded).Sum(m => m.TotalEstimatedBytes)
            + _embeddingRuntimes.Values.Where(m => m.IsLoaded).Sum(m => m.TotalEstimatedBytes);

        var newTotalBytes = totalWeightBytes + weightBytes + contextBytes * model.Options.MaxConcurrency;
        var maxVram = _options.MaxVramBytes;
        if (maxVram > 0 && newTotalBytes > maxVram)
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status503ServiceUnavailable,
                $"Loading model `{model.Options.Id}` would exceed VRAM budget of {maxVram} bytes "
                + $"(estimated: {newTotalBytes} bytes). Increase LLamaStack:MaxVramBytes or unload another model first.",
                type: "server_error",
                code: "vram_budget_exceeded");
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

        model.Initialize(instances, shared, weightBytes, contextBytes);
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
        var logitBias = request.LogitBias is { Count: > 0 }
            ? request.LogitBias.ToDictionary(kv => (LLamaToken)kv.Key, kv => kv.Value)
            : null;

        string? grammar = null;
        if (request.ForceToolCallJson)
        {
            grammar = BuildToolCallGrammar(request);
        }
        else if (!string.IsNullOrWhiteSpace(request.JsonSchema))
        {
            try
            {
                using var schemaDoc = JsonDocument.Parse(request.JsonSchema);
                grammar = JsonSchemaToGbnfConverter.Convert(schemaDoc.RootElement);
            }
            catch
            {
                // grammar conversion failed, fall back to prompt-based
            }
        }

        var pipeline = new DefaultSamplingPipeline
        {
            Temperature = request.Temperature ?? model.DefaultTemperature,
            TopP = request.TopP ?? model.DefaultTopP,
            TopK = request.TopK ?? model.DefaultTopK,
            PresencePenalty = request.PresencePenalty ?? 0,
            FrequencyPenalty = request.FrequencyPenalty ?? 0,
            LogitBias = logitBias ?? new Dictionary<LLamaToken, float>(),
            Grammar = !string.IsNullOrEmpty(grammar) ? new Grammar(grammar, "root") : null
        };

        if (request.Seed.HasValue)
        {
            pipeline.Seed = request.Seed.Value;
        }

        var antiPrompts = model.AntiPrompts.Concat(request.Stop)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
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
            var prompt = BuildPrompt(loaded.Weights, testRequest, loaded.MediaMarker, out _, out _);
            var tokenCount = CountTokens(loaded, prompt);
            if (tokenCount <= maxPromptTokens)
            {
                break;
            }

            var removed = RemoveLeastImportantMessage(truncated);
            if (!removed)
            {
                break;
            }
        }

        return request.WithMessages(truncated);
    }

    private static bool RemoveLeastImportantMessage(List<InferenceMessage> messages)
    {
        if (messages.Count <= 2)
        {
            return false;
        }

        var nonSystemIndices = messages
            .Select((m, i) => (Index: i, Message: m))
            .Where(x => !string.Equals(x.Message.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Index)
            .ToArray();

        if (nonSystemIndices.Length <= 1)
        {
            return false;
        }

        var keepFirst = 0;
        var keepLast = nonSystemIndices.Length - 1;

        var candidates = nonSystemIndices
            .Where((_, i) => i != keepFirst && i != keepLast)
            .ToArray();

        if (candidates.Length == 0)
        {
            return false;
        }

        var removeScore = candidates
            .Select(i => (Index: i, Score: ScoreMessageImportance(messages[i])))
            .OrderBy(x => x.Score)
            .First();

        messages.RemoveAt(removeScore.Index);
        return true;
    }

    private static int ScoreMessageImportance(InferenceMessage message)
    {
        var score = 0;
        if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            score += 10;
        else if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
            score += 8;
        else if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            score += 3;

        if (!string.IsNullOrWhiteSpace(message.Name))
            score += 5;

        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
            score += 2;

        if (message.ToolCalls.Count > 0)
            score += 5;

        if (message.Media.Count > 0)
            score += 10;

        score += message.Content.Length / 100;

        return score;
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

            var normalizedRole = NormalizeTemplateRole(requestMessage.Role);
            if (string.Equals(normalizedRole, "tool", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(("user", FormatToolResultForPrompt(requestMessage, content)));
                continue;
            }

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

            messages.Add((normalizedRole, content));
        }

        if (request.Tools.Count > 0 && request.ToolChoiceMode != InferenceToolChoiceMode.None)
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

    private static string FormatToolResultForPrompt(InferenceMessage message, string content)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Tool result received.");
        if (!string.IsNullOrWhiteSpace(message.ToolCallId))
        {
            builder.AppendLine("tool_call_id: " + message.ToolCallId);
        }

        if (!string.IsNullOrWhiteSpace(message.Name))
        {
            builder.AppendLine("tool_name: " + message.Name);
        }

        builder.AppendLine("tool_output:");
        builder.AppendLine(content);
        builder.AppendLine();
        builder.AppendLine("Continue the user's original task now. If this result is successful, answer the user. If it failed, call another appropriate tool with corrected arguments or explain the failure. Do not return an empty message.");
        return builder.ToString().TrimEnd();
    }

    private static bool HasToolResultMessage(InferenceRequest request)
    {
        return request.Messages.Any(message =>
            string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Role, "function", StringComparison.OrdinalIgnoreCase));
    }

    private static InferenceRequest AddToolResultContinuationNudge(InferenceRequest request)
    {
        var messages = request.Messages.Concat(
        [
            new InferenceMessage
            {
                Role = "user",
                Content = "The previous message contains a tool result. Continue the user's original task now: answer from the tool result if possible, or call another appropriate tool with corrected arguments if the tool failed. Do not return an empty response."
            }
        ]).ToArray();

        var warnings = request.CompatibilityWarnings
            .Concat(["Model returned a non-answer after a tool result; retried once with a continuation instruction."])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return request.WithMessages(messages, warnings);
    }

    private static bool IsNonAnswer(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || ContainsOnlyMarkupTagsAndWhitespace(trimmed);
    }

    private static bool ShouldRetryToolProtocol(InferenceRequest request, string cleanText)
    {
        if (request.Tools.Count == 0 ||
            request.ToolChoiceMode == InferenceToolChoiceMode.None ||
            HasToolResultMessage(request))
        {
            return false;
        }

        if (request.ToolChoiceMode is InferenceToolChoiceMode.Required or InferenceToolChoiceMode.Function)
        {
            return true;
        }

        var text = cleanText.Trim();
        return text.Length == 0 || ContainsOnlyMarkupTagsAndWhitespace(text);
    }

    private static bool ContainsOnlyMarkupTagsAndWhitespace(string text)
    {
        var sawTag = false;
        var index = 0;

        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            if (text[index] != '<')
            {
                return false;
            }

            var close = text.IndexOf('>', index + 1);
            if (close < 0)
            {
                return false;
            }

            sawTag = true;
            index = close + 1;
        }

        return sawTag;
    }

    private static InferenceRequest AddToolProtocolRetryNudge(InferenceRequest request)
    {
        var messages = request.Messages.Concat(
        [
            new InferenceMessage
            {
                Role = "user",
                Content = "The previous assistant message was not a valid final answer or a valid tool call. Continue the same user request. If the request needs current, external, local project, file, command, memory, skill, calculation, or other registered capabilities, call the appropriate available tool. Respond only with the JSON tool_calls object described above. In this retry, function.arguments may be a JSON object; the server will normalize it to the OpenAI string form."
            }
        ]).ToArray();

        var warnings = request.CompatibilityWarnings
            .Concat(["Model returned a non-answer while tools were available; retried once with constrained tool-call JSON."])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var retryRequest = request.WithMessages(messages, warnings);
        retryRequest.ForceToolCallJson = true;
        retryRequest.ToolChoiceMode = retryRequest.ToolChoiceMode == InferenceToolChoiceMode.Auto
            ? InferenceToolChoiceMode.Required
            : retryRequest.ToolChoiceMode;
        return retryRequest;
    }

    private static string BuildToolResultNonAnswerFallback(InferenceRequest request)
    {
        var toolMessage = request.Messages.LastOrDefault(message =>
            string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Role, "function", StringComparison.OrdinalIgnoreCase));

        if (toolMessage is null || string.IsNullOrWhiteSpace(toolMessage.Content))
        {
            return "工具调用后模型没有生成回复。请稍后重试，或检查工具返回内容是否为空。";
        }

        var content = toolMessage.Content.Trim();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var ok = TryGetBoolean(root, "ok");
            var command = TryGetString(root, "command");
            var exitCode = TryGetInt32(root, "exitCode");
            var timedOut = TryGetBoolean(root, "timedOut");
            var stdout = TryGetString(root, "stdout");
            var stderr = TryGetString(root, "stderr");

            if (ok == true && !string.IsNullOrWhiteSpace(stdout))
            {
                return "工具已经返回结果，但模型没有生成最终回复。工具输出如下：\n" + TruncateForFallback(stdout);
            }

            var builder = new StringBuilder("工具调用失败，所以这次没能拿到可用结果。");
            if (!string.IsNullOrWhiteSpace(command))
            {
                builder.AppendLine();
                builder.Append("命令：").Append(command);
            }

            if (exitCode is not null)
            {
                builder.AppendLine();
                builder.Append("退出码：").Append(exitCode.Value);
            }

            if (timedOut == true)
            {
                builder.AppendLine();
                builder.Append("状态：执行超时");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                builder.AppendLine();
                builder.Append("错误输出：").Append(TruncateForFallback(stderr));
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                builder.AppendLine();
                builder.Append("工具输出：").Append(TruncateForFallback(stdout));
            }
            else
            {
                builder.AppendLine();
                builder.Append("工具没有返回 stdout/stderr。");
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return "工具返回了结果，但模型没有生成最终回复。工具输出如下：\n" + TruncateForFallback(content);
        }
    }

    private static string BuildEmptyToolResultFallback(InferenceRequest request)
    {
        var toolMessage = request.Messages.LastOrDefault(message =>
            string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Role, "function", StringComparison.OrdinalIgnoreCase));

        if (toolMessage is null || string.IsNullOrWhiteSpace(toolMessage.Content))
        {
            return "工具调用后模型没有生成回复。请稍后重试，或检查工具返回内容是否为空。";
        }

        var content = toolMessage.Content.Trim();
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var ok = TryGetBoolean(root, "ok");
            var command = TryGetString(root, "command");
            var exitCode = TryGetInt32(root, "exitCode");
            var timedOut = TryGetBoolean(root, "timedOut");
            var stdout = TryGetString(root, "stdout");
            var stderr = TryGetString(root, "stderr");

            if (ok == true && !string.IsNullOrWhiteSpace(stdout))
            {
                return "工具已经返回结果，但模型没有生成最终回复。工具输出如下：\n" + TruncateForFallback(stdout);
            }

            var builder = new StringBuilder("工具调用失败，所以这次没能拿到可用结果。");
            if (!string.IsNullOrWhiteSpace(command))
            {
                builder.AppendLine();
                builder.Append("命令：").Append(command);
            }

            if (exitCode is not null)
            {
                builder.AppendLine();
                builder.Append("退出码：").Append(exitCode.Value);
            }

            if (timedOut == true)
            {
                builder.AppendLine();
                builder.Append("状态：执行超时");
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                builder.AppendLine();
                builder.Append("错误输出：").Append(TruncateForFallback(stderr));
            }
            else if (!string.IsNullOrWhiteSpace(stdout))
            {
                builder.AppendLine();
                builder.Append("工具输出：").Append(TruncateForFallback(stdout));
            }
            else
            {
                builder.AppendLine();
                builder.Append("工具没有返回 stdout/stderr。");
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return "工具返回了结果，但模型没有生成最终回复。工具输出如下：\n" + TruncateForFallback(content);
        }
    }

    private static string TruncateForFallback(string value)
    {
        value = value.Trim();
        const int maxLength = 1200;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string BuildToolCallGrammar(InferenceRequest request)
    {
        var functionNames = request.Tools
            .Where(tool => string.Equals(tool.Type, "function", StringComparison.OrdinalIgnoreCase))
            .Select(tool => tool.Function?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (request.ToolChoiceMode == InferenceToolChoiceMode.Function &&
            !string.IsNullOrWhiteSpace(request.ToolChoiceName) &&
            functionNames.Contains(request.ToolChoiceName, StringComparer.Ordinal))
        {
            functionNames = [request.ToolChoiceName];
        }

        var toolNameRule = functionNames.Length == 0
            ? "string"
            : string.Join(" | ", functionNames.Select(name => $"\"\\\"{EscapeGbnfString(name)}\\\"\""));

        return $$"""
root ::= ws "{" ws "\"tool_calls\"" ws ":" ws "[" ws tool-call (ws "," ws tool-call)* ws "]" ws "}" ws
tool-call ::= "{" ws "\"id\"" ws ":" ws string "," ws "\"type\"" ws ":" ws "\"function\"" "," ws "\"function\"" ws ":" ws function-call ws "}"
function-call ::= "{" ws "\"name\"" ws ":" ws tool-name "," ws "\"arguments\"" ws ":" ws object ws "}"
tool-name ::= {{toolNameRule}}
value ::= object | array | string | number | "true" ws | "false" ws | "null" ws
object ::= "{" ws (string ":" ws value ("," ws string ":" ws value)*)? ws "}" ws
array ::= "[" ws (value ("," ws value)*)? ws "]" ws
string ::= "\"" string-char* "\"" ws
string-char ::= [^"\\] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])
number ::= ("-"? ([0-9] | [1-9] [0-9]*)) ("." [0-9]+)? ([eE] [-+]? [0-9]+)? ws
ws ::= [ \t\n\r]*
""";
    }

    private static string EscapeGbnfString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static string BuildToolInstruction(InferenceRequest request)
    {
        var options = OpenAiJson.CreateOptions();
        var toolJson = JsonSerializer.Serialize(request.Tools, options);
        var builder = new StringBuilder();
        builder.AppendLine("Use tools whenever the user's request needs current, external, local project, file, command, memory, skill, calculation, or other registered capabilities.");
        builder.AppendLine("For those requests, do not answer from model memory and do not ask the user to perform the lookup.");
        builder.AppendLine("Available tools are provided as JSON:");
        builder.AppendLine(toolJson);
        if (!string.IsNullOrWhiteSpace(request.ToolChoiceDescription))
        {
            builder.AppendLine("Tool choice: " + request.ToolChoiceDescription);
        }

        if (request.ToolChoiceMode == InferenceToolChoiceMode.Function && !string.IsNullOrWhiteSpace(request.ToolChoiceName))
        {
            builder.AppendLine($"Only call the `{request.ToolChoiceName}` tool.");
        }
        else if (request.ToolChoiceMode == InferenceToolChoiceMode.Required)
        {
            builder.AppendLine("You must call one of the available tools.");
        }

        if (request.ParallelToolCalls == true)
        {
            builder.AppendLine("You may call multiple tools at once. When calling tools, do not output reasoning, Markdown, prose, or any text outside JSON. The first non-whitespace character must be `{`. Respond only with JSON in this exact shape:");
            builder.AppendLine("""{"tool_calls":[{"id":"call_<unique>","type":"function","function":{"name":"tool_name","arguments":"{\"arg\":\"value\"}"}},{"id":"call_<unique>","type":"function","function":{"name":"another_tool","arguments":"{\"arg\":\"value\"}"}}]}""");
        }
        else
        {
            builder.AppendLine("When calling a tool, do not output reasoning, Markdown, prose, or any text outside JSON. The first non-whitespace character must be `{`. Respond only with JSON in this exact shape:");
            builder.AppendLine("""{"tool_calls":[{"id":"call_<unique>","type":"function","function":{"name":"tool_name","arguments":"{\"arg\":\"value\"}"}}]}""");
        }
        builder.AppendLine("""If you already produced a <dw_tool_call>{"name":"tool_name","arguments":{}}</dw_tool_call> text-protocol call, output the equivalent JSON tool_calls object instead.""");
        builder.AppendLine("When a tool result is provided, continue the task. If the tool succeeded, answer the user from the tool result. If it failed, call another appropriate tool with corrected arguments or explain the failure. Never return an empty message after a tool result.");
        builder.AppendLine("When using shell commands, quote URLs or arguments that contain shell metacharacters such as `&`.");
        builder.AppendLine("If the request can be answered without any registered tool, answer normally.");
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
        InferenceRequest request,
        out string cleanText)
    {
        cleanText = generated.Trim();
        if (request.Tools.Count == 0 || request.ToolChoiceMode == InferenceToolChoiceMode.None)
        {
            return [];
        }

        var textProtocolCalls = TryExtractDwToolCalls(cleanText, request);
        if (textProtocolCalls.Count > 0)
        {
            cleanText = string.Empty;
            return textProtocolCalls;
        }

        foreach (var json in ExtractJsonObjects(cleanText))
        {
            var valid = TryParseToolCallsFromJson(json, request);
            if (valid.Count > 0)
            {
                cleanText = string.Empty;
                return valid;
            }
        }

        return [];
    }

    private static IReadOnlyList<OpenAiToolCall> TryParseToolCallsFromJson(string json, InferenceRequest request)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("tool_calls", out var toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                var calls = ParseToolCallArray(toolCallsElement);
                return calls.Count > 0 ? ValidateToolCalls(calls, request) : [];
            }

            if (root.TryGetProperty("function_call", out var functionCallElement) && functionCallElement.ValueKind == JsonValueKind.Object)
            {
                var call = ParseFunctionCall(functionCallElement);
                return call is null ? [] : ValidateToolCalls([CreateToolCall(call.Name, call.Arguments)], request);
            }

            if (TryReadTextProtocolToolCall(root, out var textProtocolCall))
            {
                return ValidateToolCalls([textProtocolCall], request);
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [];
    }

    private static IReadOnlyList<OpenAiToolCall> ParseToolCallArray(JsonElement toolCallsElement)
    {
        var calls = new List<OpenAiToolCall>();

        foreach (var item in toolCallsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("function", out var functionElement) ||
                functionElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var function = ParseFunctionCall(functionElement);
            if (function is null)
            {
                continue;
            }

            var id = item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;
            var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            calls.Add(new OpenAiToolCall
            {
                Id = string.IsNullOrWhiteSpace(id) ? "call_" + Guid.NewGuid().ToString("N") : id!,
                Type = string.IsNullOrWhiteSpace(type) ? "function" : type!,
                Function = function
            });
        }

        return calls;
    }

    private static OpenAiFunctionCall? ParseFunctionCall(JsonElement functionElement)
    {
        if (functionElement.ValueKind != JsonValueKind.Object ||
            !functionElement.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var arguments = "{}";
        if (functionElement.TryGetProperty("arguments", out var argumentsElement) &&
            argumentsElement.ValueKind != JsonValueKind.Null &&
            argumentsElement.ValueKind != JsonValueKind.Undefined)
        {
            arguments = argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString() ?? "{}"
                : argumentsElement.GetRawText();
        }

        return new OpenAiFunctionCall
        {
            Name = name,
            Arguments = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments
        };
    }

    private static IReadOnlyList<OpenAiToolCall> TryExtractDwToolCalls(string text, InferenceRequest request)
    {
        const string startTag = "<dw_tool_call>";
        const string endTag = "</dw_tool_call>";
        var calls = new List<OpenAiToolCall>();
        var searchStart = 0;

        while (searchStart < text.Length)
        {
            var start = text.IndexOf(startTag, searchStart, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var contentStart = start + startTag.Length;
            var end = text.IndexOf(endTag, contentStart, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            var json = text[contentStart..end].Trim();
            try
            {
                using var document = JsonDocument.Parse(json);
                if (TryReadTextProtocolToolCall(document.RootElement, out var call))
                {
                    calls.Add(call);
                }
            }
            catch (JsonException)
            {
                return [];
            }

            searchStart = end + endTag.Length;
        }

        return calls.Count > 0 ? ValidateToolCalls(calls, request) : [];
    }

    private static bool TryReadTextProtocolToolCall(JsonElement root, out OpenAiToolCall call)
    {
        call = new OpenAiToolCall();

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var arguments = "{}";
        if (root.TryGetProperty("arguments", out var argumentsElement) &&
            argumentsElement.ValueKind != JsonValueKind.Null &&
            argumentsElement.ValueKind != JsonValueKind.Undefined)
        {
            arguments = argumentsElement.ValueKind == JsonValueKind.String
                ? argumentsElement.GetString() ?? "{}"
                : argumentsElement.GetRawText();
        }

        call = CreateToolCall(name, arguments);
        return true;
    }

    private static OpenAiToolCall CreateToolCall(string name, string arguments)
    {
        return new OpenAiToolCall
        {
            Id = "call_" + Guid.NewGuid().ToString("N"),
            Type = "function",
            Function = new OpenAiFunctionCall
            {
                Name = name,
                Arguments = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments
            }
        };
    }

    private static IReadOnlyList<OpenAiToolCall> ValidateToolCalls(
        IReadOnlyList<OpenAiToolCall> calls,
        InferenceRequest request)
    {
        var valid = new List<OpenAiToolCall>();
        var schemaIndex = request.Tools
            .Where(t => string.Equals(t.Type, "function", StringComparison.OrdinalIgnoreCase) && t.Function is not null)
            .GroupBy(t => t.Function!.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToDictionary(t => t.Function!.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var call in calls)
        {
            if (call.Function is null || string.IsNullOrWhiteSpace(call.Function.Name))
            {
                continue;
            }

            if (!schemaIndex.TryGetValue(call.Function.Name, out var toolDef))
            {
                continue;
            }

            if (request.ToolChoiceMode == InferenceToolChoiceMode.Function &&
                !string.Equals(call.Function.Name, request.ToolChoiceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            valid.Add(call);
            if (request.ParallelToolCalls != true)
            {
                break;
            }
        }

        return valid;
    }

    private static void ValidateJsonOutput(string text, string jsonSchemaJson)
    {
        using var schemaDoc = JsonDocument.Parse(jsonSchemaJson);
        var schema = schemaDoc.RootElement;

        using var outputDoc = JsonDocument.Parse(text);
        var output = outputDoc.RootElement;

        ValidateNode(output, schema);

        static void ValidateNode(JsonElement node, JsonElement schema)
        {
            if (schema.TryGetProperty("type", out var typeProp) && typeProp.ValueKind == JsonValueKind.String)
            {
                var expectedType = typeProp.GetString();
                if (!IsJsonTypeCompatible(node, expectedType))
                {
                    throw new JsonException($"Expected type '{expectedType}', got '{GetJsonTypeName(node)}'.");
                }
            }

            if (node.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                var required = new HashSet<string>();
                if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in req.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.String) required.Add(r.GetString()!);
                    }
                }

                foreach (var prop in props.EnumerateObject())
                {
                    if (required.Contains(prop.Name) && !node.TryGetProperty(prop.Name, out _))
                    {
                        throw new JsonException($"Missing required property '{prop.Name}'.");
                    }

                    if (node.TryGetProperty(prop.Name, out var childValue))
                    {
                        ValidateNode(childValue, prop.Value);
                    }
                }
            }

            if (node.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            {
                foreach (var item in node.EnumerateArray())
                {
                    ValidateNode(item, items);
                }
            }

            if (schema.TryGetProperty("enum", out var enumVals) && enumVals.ValueKind == JsonValueKind.Array)
            {
                var match = false;
                foreach (var val in enumVals.EnumerateArray())
                {
                    if (JsonElement.DeepEquals(node, val))
                    {
                        match = true;
                        break;
                    }
                }

                if (!match)
                {
                    throw new JsonException($"Value '{node.GetRawText()}' is not in the enum.");
                }
            }
        }

        static string GetJsonTypeName(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => "unknown"
        };
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

    private static IEnumerable<string> ExtractJsonObjects(string text)
    {
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                if (depth == 0)
                {
                    start = i;
                }

                depth++;
                continue;
            }

            if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return text[start..(i + 1)];
                    start = -1;
                }
            }
        }
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

        foreach (var model in _embeddingRuntimes.Values)
        {
            await model.UnloadAsync();
            model.LoadLock.Dispose();
        }
    }

    public int GetModelMaxConcurrency(string? modelId)
    {
        return ResolveRuntime(modelId).MaxConcurrency;
    }

    public ModelMemoryInfo GetModelMemoryInfo(string? modelId)
    {
        if (_models.TryGetValue(modelId ?? "", out var m))
        {
            return new ModelMemoryInfo(m.EstimatedWeightBytes, m.EstimatedContextBytes, m.TotalEstimatedBytes, m.IsLoaded);
        }

        if (_embeddingRuntimes.TryGetValue(modelId ?? "", out var e))
        {
            return new ModelMemoryInfo(e.EstimatedWeightBytes, e.EstimatedContextBytes, e.TotalEstimatedBytes, e.IsLoaded);
        }

        throw new OpenAiProtocolException(
            StatusCodes.Status404NotFound,
            $"Model `{modelId}` was not found.",
            type: "invalid_request_error",
            code: "model_not_found",
            param: "model");
    }

    public long GetTotalEstimatedVramBytes()
    {
        return _models.Values.Where(m => m.IsLoaded).Sum(m => m.TotalEstimatedBytes)
            + _embeddingRuntimes.Values.Where(m => m.IsLoaded).Sum(m => m.TotalEstimatedBytes);
    }

    public async Task ResizeModelPoolAsync(string modelId, int newMaxConcurrency, CancellationToken ct)
    {
        if (_models.TryGetValue(modelId, out var model))
        {
            var contextSize = model.Options.ContextSize ?? 4096;
            var contextBytes = (long)contextSize * 2048;
            await model.ResizeAsync(newMaxConcurrency, contextBytes, _options.MaxVramBytes, ct);
            return;
        }

        if (_embeddingRuntimes.TryGetValue(modelId, out var embedding))
        {
            var contextBytes = 512L * 1024;
            await embedding.ResizeAsync(newMaxConcurrency, contextBytes, _options.MaxVramBytes, ct);
            return;
        }

        throw new OpenAiProtocolException(
            StatusCodes.Status404NotFound,
            $"Model `{modelId}` was not found.",
            type: "invalid_request_error",
            code: "model_not_found",
            param: "model");
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

    private EmbeddingModelRuntime ResolveEmbeddingRuntime(string? requestedModel)
    {
        var modelId = string.IsNullOrWhiteSpace(requestedModel) ? DefaultEmbeddingModelId : requestedModel;
        if (_embeddingRuntimes.TryGetValue(modelId, out var model))
        {
            return model;
        }

        if (_models.TryGetValue(modelId, out var chatModel))
        {
            return new EmbeddingModelRuntime(new LLamaEmbeddingModelRuntimeOptions
            {
                Id = chatModel.Options.Id,
                ModelPath = chatModel.Options.ModelPath,
                GpuLayerCount = chatModel.Options.GpuLayerCount,
                Threads = chatModel.Options.Threads,
                BatchThreads = chatModel.Options.BatchThreads,
                BatchSize = chatModel.Options.BatchSize ?? 512,
                UseMemoryMap = chatModel.Options.UseMemoryMap,
                UseMemoryLock = chatModel.Options.UseMemoryLock,
                MaxConcurrency = chatModel.Options.MaxConcurrency
            });
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

    private string ResolveDefaultEmbeddingModelId()
    {
        if (_embeddingRuntimes.Count > 0)
        {
            return _embeddingRuntimes.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First();
        }

        return DefaultModelId;
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

        public int MaxConcurrency => _loaded ? _allInstances.Count : Options.MaxConcurrency;

        public long EstimatedWeightBytes { get; private set; }

        public long EstimatedContextBytes { get; private set; }

        public long TotalEstimatedBytes => EstimatedWeightBytes + EstimatedContextBytes * _allInstances.Count;

        public ModelRuntime(LLamaModelRuntimeOptions options)
        {
            Options = options;
        }

        public void Initialize(List<LoadedModel> instances, ModelWeights shared, long weightBytes, long contextBytes)
        {
            _shared = shared;
            EstimatedWeightBytes = weightBytes;
            EstimatedContextBytes = contextBytes;
            foreach (var inst in instances)
            {
                _allInstances.Add(inst);
                _available.Enqueue(inst);
            }
            _instanceSemaphore = new SemaphoreSlim(instances.Count);
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

        public async Task ResizeAsync(int newMaxConcurrency, long contextBytes, long maxVramBytes, CancellationToken ct)
        {
            await LoadLock.WaitAsync(ct);
            try
            {
                if (!_loaded) return;
                if (newMaxConcurrency < 1)
                    throw new ArgumentOutOfRangeException(nameof(newMaxConcurrency), "MaxConcurrency must be at least 1.");

                var currentCount = _allInstances.Count;
                if (newMaxConcurrency == currentCount) return;

                if (newMaxConcurrency > currentCount)
                {
                    var addCount = newMaxConcurrency - currentCount;
                    var newTotalBytes = TotalEstimatedBytes + EstimatedContextBytes * addCount;
                    if (maxVramBytes > 0 && newTotalBytes > maxVramBytes)
                    {
                        throw new InvalidOperationException(
                            $"Resizing pool to {newMaxConcurrency} would exceed VRAM budget of {maxVramBytes} bytes.");
                    }

                    var newInstances = new List<LoadedModel>(addCount);
                    for (var i = 0; i < addCount; i++)
                    {
                        var context = _shared!.LlamaWeights.CreateContext(CreateModelParams(
                            ResolvePath(Options.ModelPath!), Options));
                        var executor = _shared.Mtmd is null
                            ? new InteractiveExecutor(context)
                            : new InteractiveExecutor(context, _shared.Mtmd);
                        newInstances.Add(new LoadedModel(_shared, context, executor));
                    }

                    lock (_lock)
                    {
                        _allInstances.AddRange(newInstances);
                        foreach (var inst in newInstances)
                        {
                            _available.Enqueue(inst);
                        }
                    }

                    _instanceSemaphore!.Release(addCount);
                }
                else
                {
                    var removeCount = currentCount - newMaxConcurrency;
                    var toRecycle = new List<LoadedModel>();

                    for (var i = 0; i < removeCount; i++)
                    {
                        await _instanceSemaphore!.WaitAsync(ct);
                        lock (_lock)
                        {
                            if (_available.Count == 0)
                            {
                                _instanceSemaphore.Release();
                                throw new InvalidOperationException("Could not shrink the model pool because no idle instance was available.");
                            }

                            var inst = _available.Dequeue();
                            _allInstances.Remove(inst);
                            toRecycle.Add(inst);
                        }
                    }

                    foreach (var inst in toRecycle)
                    {
                        inst.Dispose();
                    }
                }
            }
            finally
            {
                LoadLock.Release();
            }
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

    private sealed class EmbeddingLoadedModel : IDisposable
    {
        public LLamaWeights Weights { get; }
        public LLamaEmbedder Embedder { get; }

        public EmbeddingLoadedModel(LLamaWeights weights, LLamaEmbedder embedder)
        {
            Weights = weights;
            Embedder = embedder;
        }

        public void Dispose()
        {
            Embedder.Dispose();
        }
    }

    private sealed class EmbeddingModelRuntime
    {
        public LLamaEmbeddingModelRuntimeOptions Options { get; }
        public SemaphoreSlim LoadLock { get; } = new(1, 1);

        private readonly object _lock = new();
        private readonly List<EmbeddingLoadedModel> _allInstances = [];
        private readonly Queue<EmbeddingLoadedModel> _available = new();
        private SemaphoreSlim? _instanceSemaphore;
        private LLamaWeights? _sharedWeights;
        private volatile bool _loaded;

        public bool IsLoaded => _loaded;

        public int MaxConcurrency => _loaded ? _allInstances.Count : Options.MaxConcurrency;

        public long EstimatedWeightBytes { get; private set; }

        public long EstimatedContextBytes { get; private set; }

        public long TotalEstimatedBytes => EstimatedWeightBytes + EstimatedContextBytes * _allInstances.Count;

        public EmbeddingModelRuntime(LLamaEmbeddingModelRuntimeOptions options)
        {
            Options = options;
        }

        public void Initialize(List<EmbeddingLoadedModel> instances, long weightBytes, long contextBytes)
        {
            EstimatedWeightBytes = weightBytes;
            EstimatedContextBytes = contextBytes;
            _sharedWeights = instances.Count > 0 ? instances[0].Weights : null;
            foreach (var inst in instances)
            {
                _allInstances.Add(inst);
                _available.Enqueue(inst);
            }
            _instanceSemaphore = new SemaphoreSlim(instances.Count);
            _loaded = true;
        }

        public async Task<EmbeddingLoadedModel> AcquireAsync(CancellationToken ct)
        {
            await _instanceSemaphore!.WaitAsync(ct);
            lock (_lock)
            {
                return _available.Dequeue();
            }
        }

        public void Release(EmbeddingLoadedModel model)
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

        public async Task ResizeAsync(int newMaxConcurrency, long contextBytes, long maxVramBytes, CancellationToken ct)
        {
            await LoadLock.WaitAsync(ct);
            try
            {
                if (!_loaded) return;
                if (newMaxConcurrency < 1)
                    throw new ArgumentOutOfRangeException(nameof(newMaxConcurrency), "MaxConcurrency must be at least 1.");

                var currentCount = _allInstances.Count;
                if (newMaxConcurrency == currentCount) return;

                if (newMaxConcurrency > currentCount)
                {
                    var addCount = newMaxConcurrency - currentCount;
                    var newTotalBytes = TotalEstimatedBytes + EstimatedContextBytes * addCount;
                    if (maxVramBytes > 0 && newTotalBytes > maxVramBytes)
                    {
                        throw new InvalidOperationException(
                            $"Resizing embedding pool to {newMaxConcurrency} would exceed VRAM budget of {maxVramBytes} bytes.");
                    }

                    var modelPath = ResolvePath(Options.ModelPath!);
                    var embedderParams = new ModelParams(modelPath)
                    {
                        ContextSize = 512,
                        GpuLayerCount = Options.GpuLayerCount,
                        Threads = Options.Threads,
                        BatchThreads = Options.BatchThreads,
                        UseMemorymap = Options.UseMemoryMap,
                        UseMemoryLock = Options.UseMemoryLock,
                        BatchSize = Options.BatchSize,
                        Embeddings = true
                    };

                    var newInstances = new List<EmbeddingLoadedModel>(addCount);
                    for (var i = 0; i < addCount; i++)
                    {
                        var embedder = new LLamaEmbedder(_sharedWeights!, embedderParams, null!);
                        newInstances.Add(new EmbeddingLoadedModel(_sharedWeights!, embedder));
                    }

                    lock (_lock)
                    {
                        _allInstances.AddRange(newInstances);
                        foreach (var inst in newInstances)
                        {
                            _available.Enqueue(inst);
                        }
                    }

                    _instanceSemaphore!.Release(addCount);
                }
                else
                {
                    var removeCount = currentCount - newMaxConcurrency;
                    var toRecycle = new List<EmbeddingLoadedModel>();
                    for (var i = 0; i < removeCount; i++)
                    {
                        await _instanceSemaphore!.WaitAsync(ct);
                        lock (_lock)
                        {
                            if (_available.Count == 0)
                            {
                                _instanceSemaphore.Release();
                                throw new InvalidOperationException("Could not shrink the embedding pool because no idle instance was available.");
                            }

                            var inst = _available.Dequeue();
                            _allInstances.Remove(inst);
                            toRecycle.Add(inst);
                        }
                    }

                    foreach (var inst in toRecycle)
                    {
                        inst.Dispose();
                    }
                }
            }
            finally
            {
                LoadLock.Release();
            }
        }

        public async Task UnloadAsync()
        {
            await LoadLock.WaitAsync();
            try
            {
                if (!_loaded) return;
                _loaded = false;

                List<EmbeddingLoadedModel> toDispose;
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

                _sharedWeights?.Dispose();
                _sharedWeights = null;
                _instanceSemaphore?.Dispose();
                _instanceSemaphore = null;
            }
            finally
            {
                LoadLock.Release();
            }
        }
    }
}
