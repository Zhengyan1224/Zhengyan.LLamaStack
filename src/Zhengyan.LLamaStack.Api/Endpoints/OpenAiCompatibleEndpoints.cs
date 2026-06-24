using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Infrastructure;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Api.Endpoints;

internal sealed class OpenAiEndpointLogger { }

public static class OpenAiCompatibleEndpoints
{
    private static readonly DateTime _startTime = DateTime.UtcNow;

    public static IEndpointRouteBuilder MapOpenAiCompatibleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", (ILogger<OpenAiEndpointLogger> logger) =>
        {
            var value = new
            {
                name = "Zhengyan.LLamaStack",
                openai_compatible = true,
                endpoints = new[] { "/v1/models", "/v1/chat/completions", "/v1/responses", "/health" }
            };
            LogResponse(logger, "GET /", 200, value);
            return Results.Json(value);
        });

        app.MapGet("/health", (LLamaInferenceService inference, ILogger<OpenAiEndpointLogger> logger) =>
        {
            var value = new
            {
                status = "ok",
                model_loaded = inference.IsLoaded,
                default_model = inference.DefaultModelId,
                models = inference.GetModels().Select(x => new
                {
                    id = x.Id,
                    loaded = x.Loaded
                })
            };
            LogResponse(logger, "GET /health", 200, value);
            return Results.Json(value);
        });

        app.MapPost("/v1/embeddings", HandleEmbeddingsAsync);

        app.MapGet("/v1/models", (LLamaInferenceService inference, ILogger<OpenAiEndpointLogger> logger) =>
        {
            var value = new
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
            };
            LogResponse(logger, "GET /v1/models", 200, value);
            return Results.Json(value);
        });

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

    private static readonly JsonSerializerOptions _logJsonOptions = new(OpenAiJson.CreateOptions())
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private static void LogRequest<T>(ILogger logger, string endpoint, T request)
    {
        var json = JsonSerializer.Serialize(request, _logJsonOptions);
        logger.LogDebug("[{Endpoint}] Request: {Json}", endpoint, json);
    }

    private static void LogSseEvent(ILogger logger, string endpoint, string evt)
    {
        var trimmed = evt.AsSpan().TrimEnd();
        const string prefix = "data: ";
        if (trimmed.StartsWith(prefix))
        {
            var json = trimmed.Slice(prefix.Length);
            try
            {
                var parsed = JsonSerializer.Deserialize<JsonElement>(json, _logJsonOptions);
                var reSerialized = JsonSerializer.Serialize(parsed, _logJsonOptions);
                logger.LogDebug("[{Endpoint}] SSE: data: {Json}", endpoint, reSerialized);
                return;
            }
            catch
            {
            }
        }
        logger.LogDebug("[{Endpoint}] SSE: {Event}", endpoint, trimmed.ToString());
    }

    private static void LogResponse<T>(ILogger logger, string endpoint, int statusCode, T value)
    {
        var json = JsonSerializer.Serialize(value, _logJsonOptions);
        logger.LogDebug("[{Endpoint}] {StatusCode} {Json}", endpoint, statusCode, json);
    }

    private static void LogResponse(ILogger logger, string endpoint, int statusCode, string body)
    {
        logger.LogDebug("[{Endpoint}] {StatusCode} {Body}", endpoint, statusCode, body);
    }

    private static async Task<IResult> HandleEmbeddingsAsync(
        EmbeddingRequest request,
        LLamaInferenceService inference,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        LogRequest(logger, "POST /v1/embeddings", request);
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
            var value = new
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
            };
            LogResponse(logger, "POST /v1/embeddings", 200, value);
            return Results.Json(value);
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/embeddings", exception);
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
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<OpenAiEndpointLogger>>();
        LogRequest(logger, "POST /v1/chat/completions", request);
        try
        {
            var inferenceRequest = await mapper.FromChatAsync(request, cancellationToken);
            inference.ValidateRequest(inferenceRequest, InferenceEndpointKind.ChatCompletions, request.Stream);
            var responseId = "chatcmpl_" + Guid.NewGuid().ToString("N");

            var modelId = inferenceRequest.RequestedModel ?? inference.DefaultModelId;
            var maxConcurrency = inference.GetModelMaxConcurrency(modelId);
            var queue = queueManager.GetOrCreate(modelId, maxConcurrency);
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

                    logger.LogDebug("[POST /v1/chat/completions] Streaming started for {ResponseId}", responseId);

                    var outputText = new StringBuilder();
                    await foreach (var evt in inference.StreamChatEventsAsync(inferenceRequest, responseId, cancellationToken))
                    {
                        LogSseEvent(logger, "POST /v1/chat/completions", evt);
                        await httpContext.Response.WriteAsync(evt, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                        outputText.Append(evt);
                    }

                    if (request.StreamOptions?.IncludeUsage == true)
                    {
                        var outputTokens = await inference.CountTokensAsync(outputText.ToString(), inferenceRequest.RequestedModel, cancellationToken);
                        var usageChunk = ToSse(ToChatUsageChunk(responseId, promptUsage.Model, promptUsage.PromptTokens, outputTokens));
                        LogSseEvent(logger, "POST /v1/chat/completions", usageChunk);
                        await httpContext.Response.WriteAsync(usageChunk, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                    }

                    var doneEvent = "data: [DONE]\n\n";
                    logger.LogDebug("[POST /v1/chat/completions] SSE: {Event}", doneEvent.TrimEnd());
                    await httpContext.Response.WriteAsync(doneEvent, cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);

                    logger.LogDebug("[POST /v1/chat/completions] Streaming finished for {ResponseId}", responseId);
                    return Results.Empty;
                }

                var completion = await inference.CompleteAsync(inferenceRequest, cancellationToken);
                var created = UnixNow();
                if (request.Store == true)
                {
                    await store.AddChatCompletionAsync(responseId, created, inferenceRequest, completion, cancellationToken);
                }

                var response = OpenAiResponseFactory.ToChatCompletionResponse(completion, responseId, created);
                LogResponse(logger, "POST /v1/chat/completions", 200, response);
                return Results.Json(response);
            }
            finally
            {
                queue.RemoveEntry(queueEntry.Id);
            }
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/chat/completions", exception);
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
        var logger = httpContext.RequestServices.GetRequiredService<ILogger<OpenAiEndpointLogger>>();
        LogRequest(logger, "POST /v1/responses", request);
        try
        {
            var inferenceRequest = await mapper.FromResponsesAsync(request, cancellationToken);
            inferenceRequest = await ApplyPreviousResponseAsync(inferenceRequest, store, cancellationToken);
            inference.ValidateRequest(inferenceRequest, InferenceEndpointKind.Responses, request.Stream);
            var responseId = "resp_" + Guid.NewGuid().ToString("N");

            var modelId = inferenceRequest.RequestedModel ?? inference.DefaultModelId;
            var maxConcurrency = inference.GetModelMaxConcurrency(modelId);
            var queue = queueManager.GetOrCreate(modelId, maxConcurrency);
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

                    logger.LogDebug("[POST /v1/responses] Streaming started for {ResponseId}", responseId);

                    var outputText = new StringBuilder();
                    await foreach (var evt in inference.StreamResponsesEventsAsync(inferenceRequest, responseId, cancellationToken))
                    {
                        LogSseEvent(logger, "POST /v1/responses", evt);
                        await httpContext.Response.WriteAsync(evt, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                        outputText.Append(evt);
                    }

                    if (request.StreamOptions?.IncludeUsage == true)
                    {
                        var outputTokens = await inference.CountTokensAsync(outputText.ToString(), inferenceRequest.RequestedModel, cancellationToken);
                        var usageChunk = ToSse(new
                        {
                            type = "response.usage.delta",
                            response_id = responseId,
                            usage = new
                            {
                                input_tokens = promptUsage.PromptTokens,
                                output_tokens = outputTokens,
                                total_tokens = promptUsage.PromptTokens + outputTokens
                            }
                        });
                        LogSseEvent(logger, "POST /v1/responses", usageChunk);
                        await httpContext.Response.WriteAsync(usageChunk, cancellationToken);
                        await httpContext.Response.Body.FlushAsync(cancellationToken);
                    }

                    var doneEvent = "data: [DONE]\n\n";
                    logger.LogDebug("[POST /v1/responses] SSE: {Event}", doneEvent.TrimEnd());
                    await httpContext.Response.WriteAsync(doneEvent, cancellationToken);
                    await httpContext.Response.Body.FlushAsync(cancellationToken);

                    logger.LogDebug("[POST /v1/responses] Streaming finished for {ResponseId}", responseId);
                    return Results.Empty;
                }

                var completion = await inference.CompleteAsync(inferenceRequest, cancellationToken);
                var created = UnixNow();
                if (request.Store != false)
                {
                    await store.AddResponseAsync(responseId, created, inferenceRequest, completion, cancellationToken);
                }

                var response = OpenAiResponseFactory.ToResponsesResponse(completion, responseId, created, inferenceRequest.Include);
                LogResponse(logger, "POST /v1/responses", 200, response);
                return Results.Json(response);
            }
            finally
            {
                queue.RemoveEntry(queueEntry.Id);
            }
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/responses", exception);
        }
    }

    private static async Task<IResult> ListChatCompletions(
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        int? limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        var result = await store.ListChatCompletionsAsync(limit ?? 20, after, before, cancellationToken);
        var data = result.Items
            .Select(OpenAiResponseFactory.ToChatCompletionResponse)
            .ToArray();
        var value = OpenAiResponseFactory.ToList(data, result.HasMore);
        LogResponse(logger, "GET /v1/chat/completions", 200, value);
        return Results.Json(value);
    }

    private static async Task<IResult> GetChatCompletion(
        string completionId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var completion = await store.GetChatCompletionAsync(completionId, cancellationToken);
        if (completion is not null)
        {
            var value = OpenAiResponseFactory.ToChatCompletionResponse(completion);
            LogResponse(logger, "GET /v1/chat/completions/{id}", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "GET /v1/chat/completions/{id}", completionId, "chat_completion_not_found");
    }

    private static async Task<IResult> UpdateChatCompletion(
        string completionId,
        JsonElement body,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("[POST /v1/chat/completions/{{id}}] Request: id={CompletionId}", completionId);
        try
        {
            await Task.CompletedTask.WaitAsync(cancellationToken);
            var metadata = ParseOptionalMetadata(body, required: true);
            var completion = await store.UpdateChatCompletionMetadataAsync(completionId, metadata, cancellationToken);
            if (completion is not null)
            {
                var value = OpenAiResponseFactory.ToChatCompletionResponse(completion);
                LogResponse(logger, "POST /v1/chat/completions/{id}", 200, value);
                return Results.Json(value);
            }
            return ToNotFound(logger, "POST /v1/chat/completions/{id}", completionId, "chat_completion_not_found");
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/chat/completions/{id}", exception);
        }
    }

    private static async Task<IResult> DeleteChatCompletion(
        string completionId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteChatCompletionAsync(completionId, cancellationToken);
        if (deleted)
        {
            var value = OpenAiResponseFactory.ToDeleted(completionId, "chat.completion.deleted");
            LogResponse(logger, "DELETE /v1/chat/completions/{id}", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "DELETE /v1/chat/completions/{id}", completionId, "chat_completion_not_found");
    }

    private static async Task<IResult> ListChatCompletionMessages(
        string completionId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var completion = await store.GetChatCompletionAsync(completionId, cancellationToken);
        if (completion is not null)
        {
            var value = OpenAiResponseFactory.ToChatMessages(completion);
            LogResponse(logger, "GET /v1/chat/completions/{id}/messages", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "GET /v1/chat/completions/{id}/messages", completionId, "chat_completion_not_found");
    }

    private static async Task<IResult> ListResponses(
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        int? limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        var result = await store.ListResponsesAsync(limit ?? 20, after, before, cancellationToken);
        var data = result.Items
            .Select(r => OpenAiResponseFactory.ToResponsesResponse(r))
            .ToArray();
        var value = OpenAiResponseFactory.ToList(data, result.HasMore);
        LogResponse(logger, "GET /v1/responses", 200, value);
        return Results.Json(value);
    }

    private static async Task<IResult> GetResponse(
        string responseId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        if (response is not null)
        {
            var value = OpenAiResponseFactory.ToResponsesResponse(response);
            LogResponse(logger, "GET /v1/responses/{id}", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "GET /v1/responses/{id}", responseId, "response_not_found");
    }

    private static async Task<IResult> UpdateResponse(
        string responseId,
        JsonElement body,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        logger.LogDebug("[POST /v1/responses/{{id}}] Request: id={ResponseId}", responseId);
        try
        {
            var metadata = ParseOptionalMetadata(body, required: true);
            var response = await store.UpdateResponseMetadataAsync(responseId, metadata, cancellationToken);
            if (response is not null)
            {
                var value = OpenAiResponseFactory.ToResponsesResponse(response);
                LogResponse(logger, "POST /v1/responses/{id}", 200, value);
                return Results.Json(value);
            }
            return ToNotFound(logger, "POST /v1/responses/{id}", responseId, "response_not_found");
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/responses/{id}", exception);
        }
    }

    private static async Task<IResult> DeleteResponse(
        string responseId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var deleted = await store.DeleteResponseAsync(responseId, cancellationToken);
        if (deleted)
        {
            var value = OpenAiResponseFactory.ToDeleted(responseId, "response.deleted");
            LogResponse(logger, "DELETE /v1/responses/{id}", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "DELETE /v1/responses/{id}", responseId, "response_not_found");
    }

    private static async Task<IResult> CancelResponse(
        string responseId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var response = await store.CancelResponseAsync(responseId, cancellationToken);
        if (response is not null)
        {
            var value = OpenAiResponseFactory.ToResponsesResponse(response);
            LogResponse(logger, "POST /v1/responses/{id}/cancel", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "POST /v1/responses/{id}/cancel", responseId, "response_not_found");
    }

    private static async Task<IResult> ListResponseInputItems(
        string responseId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        if (response is not null)
        {
            var value = OpenAiResponseFactory.ToResponsesInputItems(response);
            LogResponse(logger, "GET /v1/responses/{id}/input_items", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "GET /v1/responses/{id}/input_items", responseId, "response_not_found");
    }

    private static async Task<IResult> CountStoredResponseTokens(
        string responseId,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var response = await store.GetResponseAsync(responseId, cancellationToken);
        if (response is not null)
        {
            var value = OpenAiResponseFactory.ToTokenCount(response);
            LogResponse(logger, "POST /v1/responses/{id}/count_tokens", 200, value);
            return Results.Json(value);
        }
        return ToNotFound(logger, "POST /v1/responses/{id}/count_tokens", responseId, "response_not_found");
    }

    private static async Task<IResult> CountResponseInputTokensAsync(
        ResponsesRequest request,
        OpenAiRequestMapper mapper,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        LogRequest(logger, "POST /v1/responses/input_tokens", request);
        try
        {
            var inferenceRequest = await mapper.FromResponsesAsync(request, cancellationToken);
            inferenceRequest = await ApplyPreviousResponseAsync(inferenceRequest, store, cancellationToken);
            var inputTokens = OpenAiStoreHelpers.EstimateInputTokens(inferenceRequest.Messages);
            var value = new
            {
                @object = "response.input_tokens",
                input_tokens = inputTokens,
                model = string.IsNullOrWhiteSpace(inferenceRequest.RequestedModel) ? null : inferenceRequest.RequestedModel
            };
            LogResponse(logger, "POST /v1/responses/input_tokens", 200, value);
            return Results.Json(value);
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/responses/input_tokens", exception);
        }
    }

    private static async Task<IResult> CompactResponseAsync(
        JsonElement body,
        IOpenAiStore store,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        var rawBody = body.ValueKind == JsonValueKind.Object ? JsonSerializer.Serialize(body, _logJsonOptions) : body.ToString();
        logger.LogDebug("[POST /v1/responses/compact] Request: {Json}", rawBody);
        try
        {
            await Task.CompletedTask.WaitAsync(cancellationToken);
            var responseId = TryGetString(body, "response_id") ?? TryGetString(body, "id");
            if (string.IsNullOrWhiteSpace(responseId))
            {
                var err = new OpenAiProtocolException(
                    StatusCodes.Status400BadRequest,
                    "`response_id` is required.",
                    param: "response_id");
                return ToError(logger, "POST /v1/responses/compact", err);
            }

            var response = await store.GetResponseAsync(responseId, cancellationToken);
            if (response is null)
            {
                return ToNotFound(logger, "POST /v1/responses/compact", responseId, "response_not_found");
            }

            var compacted = OpenAiStoreHelpers.CreateCompactedResponse(
                "resp_" + Guid.NewGuid().ToString("N"),
                UnixNow(),
                response,
                TryGetString(body, "instructions"));
            await store.AddResponseAsync(compacted, cancellationToken);

            var value = OpenAiResponseFactory.ToResponsesResponse(compacted);
            LogResponse(logger, "POST /v1/responses/compact", 200, value);
            return Results.Json(value);
        }
        catch (OpenAiProtocolException exception)
        {
            return ToError(logger, "POST /v1/responses/compact", exception);
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

    private static IResult ToError(ILogger logger, string endpoint, OpenAiProtocolException exception)
    {
        var envelope = new OpenAiErrorEnvelope
        {
            Error = new OpenAiError
            {
                Message = exception.Message,
                Type = exception.Type,
                Code = exception.Code,
                Param = exception.Param
            }
        };
        LogResponse(logger, endpoint, exception.StatusCode, envelope);
        return Results.Json(envelope, OpenAiJson.CreateOptions(), statusCode: exception.StatusCode);
    }

    private static IResult ToNotFound(ILogger logger, string endpoint, string id, string code)
    {
        return ToError(logger, endpoint, new OpenAiProtocolException(
            StatusCodes.Status404NotFound,
            $"Object `{id}` was not found in the local in-memory store.",
            code: code));
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
        ModelQueueManager queueManager,
        ILogger<OpenAiEndpointLogger> logger)
    {
        foreach (var kvp in queueManager.GetAll())
        {
            var entry = kvp.Value.GetEntry(entryId);
            if (entry is not null)
            {
                var value = new
                {
                    id = entry.Id,
                    model_id = entry.ModelId,
                    position = entry.Position,
                    status = entry.Status,
                    created_at = entry.CreatedAt
                };
                LogResponse(logger, "GET /v1/queue/{id}", 200, value);
                return Results.Json(value);
            }
        }

        var notFound = new { error = "queue_entry_not_found", message = $"Queue entry `{entryId}` not found." };
        LogResponse(logger, "GET /v1/queue/{id}", 404, notFound);
        return Results.NotFound(notFound);
    }

    private static IResult HandleHealth(LLamaInferenceService inference, ILogger<OpenAiEndpointLogger> logger)
    {
        var value = new
        {
            status = inference.IsLoaded ? "healthy" : "loading",
            models_loaded = inference.IsLoaded,
            models = inference.GetModels().Select(m => new
            {
                id = m.Id,
                loaded = m.Loaded
            }).ToArray(),
            uptime = (DateTime.UtcNow - _startTime).TotalSeconds
        };
        LogResponse(logger, "GET /v1/health", 200, value);
        return Results.Json(value);
    }

    private static async Task<IResult> HandleModelLoad(
        string modelId,
        LLamaInferenceService inference,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        await inference.LoadModelAsync(modelId, cancellationToken);
        var value = new { status = "loaded", model_id = modelId };
        LogResponse(logger, "POST /v1/models/{id}/load", 200, value);
        return Results.Json(value);
    }

    private static async Task<IResult> HandleModelUnload(
        string modelId,
        LLamaInferenceService inference,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        await inference.UnloadModelAsync(modelId, cancellationToken);
        var value = new { status = "unloaded", model_id = modelId };
        LogResponse(logger, "POST /v1/models/{id}/unload", 200, value);
        return Results.Json(value);
    }

    private static async Task<IResult> HandleTokenize(
        TokenizeRequest request,
        LLamaInferenceService inference,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        LogRequest(logger, "POST /v1/tokenize", request);
        if (string.IsNullOrWhiteSpace(request.Input))
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`input` is required.", param: "input");
        }

        var tokens = await inference.TokenizeAsync(request.Input, request.Model, cancellationToken);
        var value = new
        {
            @object = "list",
            data = tokens.Select((t, i) => new { token = t, index = i }).ToArray(),
            model = request.Model ?? inference.DefaultModelId,
            usage = new { total_tokens = tokens.Count }
        };
        LogResponse(logger, "POST /v1/tokenize", 200, value);
        return Results.Json(value);
    }

    private static async Task<IResult> HandleDetokenize(
        DetokenizeRequest request,
        LLamaInferenceService inference,
        ILogger<OpenAiEndpointLogger> logger,
        CancellationToken cancellationToken)
    {
        LogRequest(logger, "POST /v1/detokenize", request);
        if (request.Tokens is null || request.Tokens.Count == 0)
        {
            throw new OpenAiProtocolException(StatusCodes.Status400BadRequest, "`tokens` is required.", param: "tokens");
        }

        var text = await inference.DetokenizeAsync(request.Tokens, request.Model, cancellationToken);
        var value = new
        {
            @object = "text",
            text,
            model = request.Model ?? inference.DefaultModelId
        };
        LogResponse(logger, "POST /v1/detokenize", 200, value);
        return Results.Json(value);
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
