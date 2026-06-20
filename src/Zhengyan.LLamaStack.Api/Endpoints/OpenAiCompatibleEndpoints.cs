using System.Text;
using System.Text.Json;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Infrastructure;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Api.Endpoints;

public static class OpenAiCompatibleEndpoints
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public static IEndpointRouteBuilder MapOpenAiCompatibleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Json(new
        {
            name = "Zhengyan.LLamaStack",
            openai_compatible = true,
            endpoints = new[] { "/v1/models", "/v1/chat/completions", "/v1/responses", "/health" }
        }));

        app.MapGet("/health", (LLamaInferenceService inference) => Results.Json(new
        {
            status = "ok",
            model_loaded = inference.IsLoaded,
            default_model = inference.DefaultModelId,
            models = inference.GetModels().Select(x => new
            {
                id = x.Id,
                loaded = x.Loaded
            })
        }));

        app.MapPost("/v1/embeddings", HandleEmbeddingsAsync);

        app.MapGet("/v1/models", (LLamaInferenceService inference) => Results.Json(new
        {
            @object = "list",
            data = inference.GetModels().Select(model => new
                {
                    id = model.Id,
                    @object = "model",
                    created = model.Created,
                    owned_by = model.OwnedBy,
                    loaded = model.Loaded,
                    model_path = model.ModelPath,
                    mmproj_path = model.MmprojPath,
                    capabilities = model.Capabilities
                })
        }));

        app.MapPost("/v1/chat/completions", HandleChatCompletionsAsync);
        app.MapPost("/chat/completions", HandleChatCompletionsAsync);
        app.MapGet("/v1/chat/completions", ListChatCompletions);
        app.MapGet("/v1/chat/completions/{completionId}", GetChatCompletion);
        app.MapPost("/v1/chat/completions/{completionId}", UpdateChatCompletion);
        app.MapDelete("/v1/chat/completions/{completionId}", DeleteChatCompletion);
        app.MapGet("/v1/chat/completions/{completionId}/messages", ListChatCompletionMessages);
        app.MapPost("/v1/responses", HandleResponsesAsync);
        app.MapPost("/responses", HandleResponsesAsync);
        app.MapGet("/v1/responses", ListResponses);
        app.MapGet("/v1/responses/{responseId}", GetResponse);
        app.MapPost("/v1/responses/{responseId}", UpdateResponse);
        app.MapDelete("/v1/responses/{responseId}", DeleteResponse);
        app.MapPost("/v1/responses/{responseId}/cancel", CancelResponse);
        app.MapGet("/v1/responses/{responseId}/input_items", ListResponseInputItems);
        app.MapPost("/v1/responses/{responseId}/count_tokens", CountStoredResponseTokens);
        app.MapPost("/v1/responses/input_tokens", CountResponseInputTokensAsync);
        app.MapPost("/v1/responses/compact", CompactResponseAsync);
        app.MapGet("/v1/queue/{entryId}", GetQueueStatus);
        app.MapPost("/v1/tokenize", HandleTokenize);
        app.MapPost("/v1/detokenize", HandleDetokenize);
        app.MapGet("/v1/health", HandleHealth);
        app.MapPost("/v1/models/{modelId}/load", HandleModelLoad);
        app.MapPost("/v1/models/{modelId}/unload", HandleModelUnload);

        return app;
    }

    private static async Task<IResult> HandleEmbeddingsAsync(
        EmbeddingRequest request,
        LLamaInferenceService inference,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.Input is null || request.Input.Value.ValueKind == JsonValueKind.Null)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` is required.", param: "input");
            }

            var inputs = ParseEmbeddingInputs(request.Input.Value);
            if (inputs.Count == 0)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` must contain at least one string.", param: "input");
            }

            var result = await inference.EmbeddingsAsync(inputs, request.Model, cancellationToken);
            return Results.Json(new
            {
                @object = "list",
                data = result.Data.Select(d => new
                {
                    @object = d.Object,
                    index = d.Index,
                    embedding = d.Embedding
                }).ToArray(),
                model = inference.DefaultModelId,
                usage = new
                {
                    prompt_tokens = result.TotalTokens,
                    total_tokens = result.TotalTokens
                }
            });
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static IReadOnlyList<string> ParseEmbeddingInputs(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
        {
            return [input.GetString() ?? string.Empty];
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            var result = new List<string>();
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    result.Add(item.GetString() ?? string.Empty);
                }
                else if (item.ValueKind == JsonValueKind.Array)
                {
                    foreach (var inner in item.EnumerateArray())
                    {
                        if (inner.ValueKind == JsonValueKind.Number)
                        {
                            result.Add(inner.GetRawText());
                        }
                    }
                }
            }

            return result;
        }

        throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` must be a string or an array.", param: "input");
    }

    private static async Task<IResult> HandleChatCompletionsAsync(
        ChatCompletionRequest request,
        OpenAiRequestMapper mapper,
        LLamaInferenceService inference,
        IOpenAiStore store,
        ModelQueueManager queueManager,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var inferenceRequest = await mapper.FromChatAsync(request, cancellationToken);
            inference.ValidateRequest(inferenceRequest, InferenceEndpointKind.ChatCompletions, request.Stream);
            var responseId = "chatcmpl_" + Guid.NewGuid().ToString("N");

            var modelId = inferenceRequest.RequestedModel ?? inference.DefaultModelId;
            var queue = queueManager.GetOrCreate(modelId);
            var queueEntry = queue.Enqueue(modelId);
            httpContext.Response.Headers["X-Queue-Position"] = queueEntry.Position.ToString();
            httpContext.Response.Headers["X-Queue-Entry-Id"] = queueEntry.Id;

            try
            {
                await queueEntry.TurnTcs.Task.WaitAsync(cancellationToken);

                if (request.Stream)
                {
                    var promptUsage = request.StreamOptions?.IncludeUsage == true
                        ? await inference.EstimatePromptUsageAsync(inferenceRequest, cancellationToken)
                        : default;
                    httpContext.Response.Headers.CacheControl = "no-cache";
                    httpContext.Response.Headers.Connection = "keep-alive";
                    httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
                    var outputText = new StringBuilder();
                    await foreach (var evt in inference.StreamChatEventsAsync(inferenceRequest, responseId, cancellationToken))
                    {
                        await httpContext.Response.WriteAsync(evt, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                        outputText.Append(evt);
                    }

                    if (request.StreamOptions?.IncludeUsage == true)
                    {
                        var outputTokens = await inference.CountTokensAsync(outputText.ToString(), inferenceRequest.RequestedModel, cancellationToken);
                        await httpContext.Response.WriteAsync(ToSse(ToChatUsageChunk(responseId, promptUsage.Model, promptUsage.PromptTokens, outputTokens)), cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                    }

                    await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);

                    return Results.Empty;
                }

                var completion = await inference.CompleteAsync(inferenceRequest, cancellationToken);
                var created = UnixNow();
                if (request.Store == true)
                {
                    await store.AddChatCompletionAsync(responseId, created, inferenceRequest, completion, cancellationToken);
                }

                return Results.Json(OpenAiResponseFactory.ToChatCompletionResponse(completion, responseId, created));
            }
            finally
            {
                queue.RemoveEntry(queueEntry.Id);
            }
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static async Task<IResult> HandleResponsesAsync(
        ResponsesRequest request,
        OpenAiRequestMapper mapper,
        LLamaInferenceService inference,
        IOpenAiStore store,
        ModelQueueManager queueManager,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var inferenceRequest = await mapper.FromResponsesAsync(request, cancellationToken);
            inferenceRequest = await ApplyPreviousResponseAsync(inferenceRequest, store, cancellationToken);
            inference.ValidateRequest(inferenceRequest, InferenceEndpointKind.Responses, request.Stream);
            var responseId = "resp_" + Guid.NewGuid().ToString("N");

            var modelId = inferenceRequest.RequestedModel ?? inference.DefaultModelId;
            var queue = queueManager.GetOrCreate(modelId);
            var queueEntry = queue.Enqueue(modelId);
            httpContext.Response.Headers["X-Queue-Position"] = queueEntry.Position.ToString();
            httpContext.Response.Headers["X-Queue-Entry-Id"] = queueEntry.Id;
            try
            {
                await queueEntry.TurnTcs.Task.WaitAsync(cancellationToken);

                if (request.Stream)
                {
                    var promptUsage = request.StreamOptions?.IncludeUsage == true
                        ? await inference.EstimatePromptUsageAsync(inferenceRequest, cancellationToken)
                        : default;
                    httpContext.Response.Headers.CacheControl = "no-cache";
                    httpContext.Response.Headers.Connection = "keep-alive";
                    httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
                    var outputText = new StringBuilder();
                    await foreach (var evt in inference.StreamResponsesEventsAsync(inferenceRequest, responseId, cancellationToken))
                    {
                        await httpContext.Response.WriteAsync(evt, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                        outputText.Append(evt);
                    }

                    if (request.StreamOptions?.IncludeUsage == true)
                    {
                        var outputTokens = await inference.CountTokensAsync(outputText.ToString(), inferenceRequest.RequestedModel, cancellationToken);
                        await httpContext.Response.WriteAsync(ToSse(new
                        {
                            type = "response.usage.delta",
                            response_id = responseId,
                            usage = new
                            {
                                input_tokens = promptUsage.PromptTokens,
                                output_tokens = outputTokens,
                                total_tokens = promptUsage.PromptTokens + outputTokens
                            }
                        }), cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                    }

                    await httpContext.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);

                    return Results.Empty;
                }

                var completion = await inference.CompleteAsync(inferenceRequest, cancellationToken);
                var created = UnixNow();
                if (request.Store != false)
                {
                    await store.AddResponseAsync(responseId, created, inferenceRequest, completion, cancellationToken);
                }

                return Results.Json(OpenAiResponseFactory.ToResponsesResponse(completion, responseId, created, inferenceRequest.Include));
            }
            finally
            {
                queue.RemoveEntry(queueEntry.Id);
            }
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static async Task<IResult> ListChatCompletions(
        IOpenAiStore store,
        int? limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        var result = await store.ListChatCompletionsAsync(limit ?? 20, after, before, cancellationToken);
        var data = result.Items
            .Select(OpenAiResponseFactory.ToChatCompletionResponse)
            .ToArray();
        return Results.Json(OpenAiResponseFactory.ToList(data, result.HasMore));
    }

    private static async Task<IResult> GetChatCompletion(
        string completionId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        var completion = await store.GetChatCompletionAsync(completionId, cancellationToken);
        return completion is not null
            ? Results.Json(OpenAiResponseFactory.ToChatCompletionResponse(completion))
            : ToNotFound(completionId, "chat_completion_not_found");
    }

    private static async Task<IResult> UpdateChatCompletion(
        string completionId,
        JsonElement body,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.CompletedTask.WaitAsync(cancellationToken);
            var metadata = ParseOptionalMetadata(body, required: true);
            var completion = await store.UpdateChatCompletionMetadataAsync(completionId, metadata, cancellationToken);
            return completion is not null
                ? Results.Json(OpenAiResponseFactory.ToChatCompletionResponse(completion))
                : ToNotFound(completionId, "chat_completion_not_found");
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static async Task<IResult> DeleteChatCompletion(
        string completionId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        return await store.DeleteChatCompletionAsync(completionId, cancellationToken)
            ? Results.Json(OpenAiResponseFactory.ToDeleted(completionId, "chat.completion.deleted"))
            : ToNotFound(completionId, "chat_completion_not_found");
    }

    private static async Task<IResult> ListChatCompletionMessages(
        string completionId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        var completion = await store.GetChatCompletionAsync(completionId, cancellationToken);
        return completion is not null
            ? Results.Json(OpenAiResponseFactory.ToChatMessages(completion))
            : ToNotFound(completionId, "chat_completion_not_found");
    }

    private static async Task<IResult> ListResponses(
        IOpenAiStore store,
        int? limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        var result = await store.ListResponsesAsync(limit ?? 20, after, before, cancellationToken);
        var data = result.Items
            .Select(r => OpenAiResponseFactory.ToResponsesResponse(r))
            .ToArray();
        return Results.Json(OpenAiResponseFactory.ToList(data, result.HasMore));
    }

    private static async Task<IResult> GetResponse(
        string responseId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        return response is not null
            ? Results.Json(OpenAiResponseFactory.ToResponsesResponse(response))
            : ToNotFound(responseId, "response_not_found");
    }

    private static async Task<IResult> UpdateResponse(
        string responseId,
        JsonElement body,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = ParseOptionalMetadata(body, required: true);
            var response = await store.UpdateResponseMetadataAsync(responseId, metadata, cancellationToken);
            return response is not null
                ? Results.Json(OpenAiResponseFactory.ToResponsesResponse(response))
                : ToNotFound(responseId, "response_not_found");
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static async Task<IResult> DeleteResponse(
        string responseId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        return await store.DeleteResponseAsync(responseId, cancellationToken)
            ? Results.Json(OpenAiResponseFactory.ToDeleted(responseId, "response.deleted"))
            : ToNotFound(responseId, "response_not_found");
    }

    private static async Task<IResult> CancelResponse(
        string responseId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        var response = await store.CancelResponseAsync(responseId, cancellationToken);
        return response is not null
            ? Results.Json(OpenAiResponseFactory.ToResponsesResponse(response))
            : ToNotFound(responseId, "response_not_found");
    }

    private static async Task<IResult> ListResponseInputItems(
        string responseId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        return response is not null
            ? Results.Json(OpenAiResponseFactory.ToResponsesInputItems(response))
            : ToNotFound(responseId, "response_not_found");
    }

    private static async Task<IResult> CountStoredResponseTokens(
        string responseId,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        return response is not null
            ? Results.Json(OpenAiResponseFactory.ToTokenCount(response))
            : ToNotFound(responseId, "response_not_found");
    }

    private static async Task<IResult> CountResponseInputTokensAsync(
        ResponsesRequest request,
        OpenAiRequestMapper mapper,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            var inferenceRequest = await mapper.FromResponsesAsync(request, cancellationToken);
            inferenceRequest = await ApplyPreviousResponseAsync(inferenceRequest, store, cancellationToken);
            var inputTokens = OpenAiStoreHelpers.EstimateInputTokens(inferenceRequest.Messages);
            return Results.Json(new
            {
                @object = "response.input_tokens",
                input_tokens = inputTokens,
                model = string.IsNullOrWhiteSpace(inferenceRequest.RequestedModel) ? null : inferenceRequest.RequestedModel
            });
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static async Task<IResult> CompactResponseAsync(
        JsonElement body,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.CompletedTask.WaitAsync(cancellationToken);
            var responseId = TryGetString(body, "response_id") ?? TryGetString(body, "id");
            if (string.IsNullOrWhiteSpace(responseId))
            {
                return ToError(new OpenAiProtocolException(
                    StatusCodes.Status400BadRequest,
                    "`response_id` is required.",
                    param: "response_id"));
            }

            var response = await store.GetResponseAsync(responseId, cancellationToken);
            if (response is null)
            {
                return ToNotFound(responseId, "response_not_found");
            }

            var compacted = OpenAiStoreHelpers.CreateCompactedResponse(
                "resp_" + Guid.NewGuid().ToString("N"),
                UnixNow(),
                response,
                TryGetString(body, "instructions"));
            await store.AddResponseAsync(compacted, cancellationToken);

            return Results.Json(OpenAiResponseFactory.ToResponsesResponse(compacted));
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(exception);
        }
    }

    private static async Task<InferenceRequest> ApplyPreviousResponseAsync(
        InferenceRequest request,
        IOpenAiStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.PreviousResponseId))
        {
            return request;
        }

        var previous = await store.GetResponseAsync(request.PreviousResponseId, cancellationToken);
        if (previous is null)
        {
            throw new OpenAiProtocolException(
                StatusCodes.Status404NotFound,
                $"Response `{request.PreviousResponseId}` was not found.",
                code: "response_not_found",
                param: "previous_response_id");
        }

        var messages = new List<InferenceMessage>(previous.InputMessages);
        if (!string.IsNullOrWhiteSpace(previous.OutputText) || previous.ToolCalls.Count > 0)
        {
            messages.Add(new InferenceMessage
            {
                Role = "assistant",
                Content = previous.ToolCalls.Count > 0
                    ? JsonSerializer.Serialize(previous.ToolCalls, OpenAiJson.CreateOptions())
                    : previous.OutputText
            });
        }

        messages.AddRange(request.Messages);
        var warnings = request.CompatibilityWarnings
            .Where(x => !x.Contains("previous_response_id", StringComparison.OrdinalIgnoreCase))
            .Concat(["`previous_response_id` was resolved from the local in-memory response store."])
            .ToArray();

        return request.WithMessages(messages, warnings);
    }

    private static IResult ToError(OpenAiProtocolException exception)
    {
        return Results.Json(
            new OpenAiErrorEnvelope
            {
                Error = new OpenAiError
                {
                    Message = exception.Message,
                    Type = exception.Type,
                    Code = exception.Code,
                    Param = exception.Param
                }
            },
            OpenAiJson.CreateOptions(),
            statusCode: exception.StatusCode);
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static object ToChatUsageChunk(string responseId, string model, int promptTokens, int outputTokens = 0)
    {
        return OpenAiResponseFactory.ToChatUsageChunk(responseId, model, promptTokens, outputTokens, UnixNow());
    }

    private static string ToSse(object payload)
    {
        return "data: " + JsonSerializer.Serialize(payload, OpenAiJson.CreateOptions()) + "\n\n";
    }

    private static IResult ToNotFound(string id, string code)
    {
        return ToError(new OpenAiProtocolException(
            StatusCodes.Status404NotFound,
            $"Object `{id}` was not found in the local in-memory store.",
            code: code));
    }

    private static IReadOnlyDictionary<string, string>? ParseOptionalMetadata(JsonElement body, bool required)
    {
        if (!body.TryGetProperty("metadata", out var metadata) || metadata.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`metadata` is required.", param: "metadata");
            }

            return null;
        }

        if (metadata.ValueKind != JsonValueKind.Object)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`metadata` must be an object.", param: "metadata");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in metadata.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }

    private static IResult GetQueueStatus(
        string entryId,
        ModelQueueManager queueManager)
    {
        foreach (var kvp in queueManager.GetAll())
        {
            var entry = kvp.Value.GetEntry(entryId);
            if (entry is not null)
            {
                return Results.Json(new
                {
                    id = entry.Id,
                    model_id = entry.ModelId,
                    position = entry.Position,
                    status = entry.Status,
                    created_at = entry.CreatedAt
                });
            }
        }

        return Results.NotFound(new { error = "queue_entry_not_found", message = $"Queue entry `{entryId}` not found." });
    }

    private static IResult HandleHealth(LLamaInferenceService inference)
    {
        return Results.Json(new
        {
            status = inference.IsLoaded ? "healthy" : "loading",
            models_loaded = inference.IsLoaded,
            models = inference.GetModels().Select(m => new
            {
                id = m.Id,
                loaded = m.Loaded
            }).ToArray(),
            uptime = (DateTime.UtcNow - _startTime).TotalSeconds
        });
    }

    private static async Task<IResult> HandleModelLoad(
        string modelId,
        LLamaInferenceService inference,
        CancellationToken cancellationToken)
    {
        await inference.LoadModelAsync(modelId, cancellationToken);
        return Results.Json(new { status = "loaded", model_id = modelId });
    }

    private static async Task<IResult> HandleModelUnload(
        string modelId,
        LLamaInferenceService inference,
        CancellationToken cancellationToken)
    {
        await inference.UnloadModelAsync(modelId, cancellationToken);
        return Results.Json(new { status = "unloaded", model_id = modelId });
    }

    private static async Task<IResult> HandleTokenize(
        TokenizeRequest request,
        LLamaInferenceService inference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` is required.", param: "input");
        }

        var tokens = await inference.TokenizeAsync(request.Input, request.Model, cancellationToken);
        return Results.Json(new
        {
            @object = "list",
            data = tokens.Select((t, i) => new { token = t, index = i }).ToArray(),
            model = request.Model ?? inference.DefaultModelId,
            usage = new { total_tokens = tokens.Count }
        });
    }

    private static async Task<IResult> HandleDetokenize(
        DetokenizeRequest request,
        LLamaInferenceService inference,
        CancellationToken cancellationToken)
    {
        if (request.Tokens is null || request.Tokens.Count == 0)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`tokens` is required.", param: "tokens");
        }

        var text = await inference.DetokenizeAsync(request.Tokens, request.Model, cancellationToken);
        return Results.Json(new
        {
            @object = "text",
            text,
            model = request.Model ?? inference.DefaultModelId
        });
    }

    private static string? TryGetString(JsonElement body, string propertyName)
    {
        return body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
