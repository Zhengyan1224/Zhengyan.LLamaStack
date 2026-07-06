using System.Net;
using System.Reflection;
using System.Text.Json;
using LLama.Sampling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Infrastructure;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Options;
using Zhengyan.LLamaStack.Api.Storage;

namespace Zhengyan.LLamaStack.Tests;

public sealed class InfrastructureBehaviorTests
{
    private static readonly MethodInfo TryExtractToolCallsMethod =
        typeof(LLamaInferenceService).GetMethod("TryExtractToolCalls", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryExtractToolCalls method was not found.");

    private static readonly MethodInfo ShouldRetryToolProtocolMethod =
        typeof(LLamaInferenceService).GetMethod("ShouldRetryToolProtocol", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ShouldRetryToolProtocol method was not found.");

    private static readonly MethodInfo BuildToolCallGrammarMethod =
        typeof(LLamaInferenceService).GetMethod("BuildToolCallGrammar", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildToolCallGrammar method was not found.");

    [Fact]
    public async Task MemoryStore_PersistsAndUpdatesResponseTasks()
    {
        var store = new OpenAiMemoryStore();
        var task = CreateTask("task_memory");

        await store.AddResponseTaskAsync(task, CancellationToken.None);
        await store.UpdateResponseTaskAsync(task.Id, ResponseTaskStatus.Completed, "resp_done", cancellationToken: CancellationToken.None);

        var saved = await store.GetResponseTaskAsync(task.Id, CancellationToken.None);
        var list = await store.ListResponseTasksAsync(10, null, null, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(ResponseTaskStatus.Completed, saved.Status);
        Assert.Equal("resp_done", saved.ResultResponseId);
        Assert.Contains(list.Items, x => x.Id == task.Id);
    }

    [Fact]
    public async Task SqliteStore_PersistsAndUpdatesResponseTasks()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "llamastack-tests", Guid.NewGuid().ToString("N"), "store.db");
        var options = Options.Create(new LLamaStackOptions
        {
            Store = new LLamaStoreOptions
            {
                Provider = "Sqlite",
                SqlitePath = dbPath
            }
        });
        var store = new OpenAiSqliteStore(options);
        var task = CreateTask("task_sqlite");

        await store.AddResponseTaskAsync(task, CancellationToken.None);
        await store.UpdateResponseTaskAsync(task.Id, ResponseTaskStatus.Failed, errorMessage: "boom", cancellationToken: CancellationToken.None);

        var saved = await store.GetResponseTaskAsync(task.Id, CancellationToken.None);
        var list = await store.ListResponseTasksAsync(10, null, null, CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Equal(ResponseTaskStatus.Failed, saved.Status);
        Assert.Equal("boom", saved.ErrorMessage);
        Assert.NotNull(saved.CompletedAt);
        Assert.Contains(list.Items, x => x.Id == task.Id);
    }

    [Fact]
    public async Task ApiKeyMiddleware_HonorsConfiguredHeaderName()
    {
        var options = Options.Create(new LLamaStackOptions
        {
            Auth = new LLamaAuthOptions
            {
                Enabled = true,
                ApiKey = "secret",
                ApiKeyHeader = "X-Api-Key"
            }
        });
        var called = false;
        var middleware = new OpenApiKeyMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, options);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "secret";

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RequestMapper_BlocksLoopbackRemoteMediaUrls()
    {
        using var content = JsonDocument.Parse("""
            [
              {
                "type": "image_url",
                "image_url": {
                  "url": "http://127.0.0.1/private.png"
                }
              }
            ]
            """);
        var mapper = new OpenAiRequestMapper(
            new StubHttpClientFactory(),
            Options.Create(new LLamaStackOptions { AllowRemoteMedia = true }));
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = content.RootElement.Clone()
                }
            ]
        };

        var exception = await Assert.ThrowsAsync<OpenAiProtocolException>(
            () => mapper.FromChatAsync(request, CancellationToken.None));

        Assert.Equal("remote_media_host_not_allowed", exception.Code);
    }

    [Fact]
    public async Task RequestMapper_ParsesSpecificToolChoice()
    {
        using var toolChoice = JsonDocument.Parse("""{"type":"function","function":{"name":"lookup_weather"}}""");
        var mapper = new OpenAiRequestMapper(
            new StubHttpClientFactory(),
            Options.Create(new LLamaStackOptions()));
        var request = new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonDocument.Parse("\"weather?\"").RootElement.Clone()
                }
            ],
            ToolChoice = toolChoice.RootElement.Clone()
        };

        var inferenceRequest = await mapper.FromChatAsync(request, CancellationToken.None);

        Assert.Equal(InferenceToolChoiceMode.Function, inferenceRequest.ToolChoiceMode);
        Assert.Equal("lookup_weather", inferenceRequest.ToolChoiceName);
    }

    [Fact]
    public void ToolCallExtraction_ReturnsOnlyToolsDeclaredByTheRequest()
    {
        var request = CreateToolInferenceRequest(
            "lookup_weather",
            parallelToolCalls: true);
        var generated = """
            {"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"calculator","arguments":"{\"expression\":\"6 * 7\"}"}},
              {"id":"call_2","type":"function","function":{"name":"lookup_weather","arguments":"{\"city\":\"Shanghai\"}"}}
            ]}
            """;

        var toolCalls = ExtractToolCalls(generated, request, out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("lookup_weather", call.Function.Name);
    }

    [Fact]
    public void ToolCallExtraction_RespectsToolChoiceNone()
    {
        var request = CreateToolInferenceRequest("lookup_weather");
        request.ToolChoiceMode = InferenceToolChoiceMode.None;

        var toolCalls = ExtractToolCalls(
            """{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup_weather","arguments":"{\"city\":\"Shanghai\"}"}}]}""",
            request,
            out var cleanText);

        Assert.Empty(toolCalls);
        Assert.Contains("tool_calls", cleanText);
    }

    [Fact]
    public void ToolCallExtraction_RespectsParallelToolCallsFalse()
    {
        var request = CreateToolInferenceRequest(
            "lookup_weather",
            "lookup_time",
            parallelToolCalls: false);

        var toolCalls = ExtractToolCalls(
            """
            {"tool_calls":[
              {"id":"call_1","type":"function","function":{"name":"lookup_weather","arguments":"{\"city\":\"Shanghai\"}"}},
              {"id":"call_2","type":"function","function":{"name":"lookup_time","arguments":"{\"timezone\":\"Asia/Shanghai\"}"}}
            ]}
            """,
            request,
            out _);

        var call = Assert.Single(toolCalls);
        Assert.Equal("lookup_weather", call.Function.Name);
    }

    [Fact]
    public void ToolCallExtraction_ParsesDwToolCallTextProtocol()
    {
        var request = CreateToolInferenceRequest("skill_list");

        var toolCalls = ExtractToolCalls(
            """<dw_tool_call>{"name":"skill_list","arguments":{"includeContent":true}}</dw_tool_call>""",
            request,
            out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("skill_list", call.Function.Name);
        Assert.Equal("""{"includeContent":true}""", call.Function.Arguments);
    }

    [Fact]
    public void ToolCallExtraction_IgnoresThinkJsonAndParsesLaterToolCall()
    {
        var request = CreateToolInferenceRequest("lookup_weather");

        var toolCalls = ExtractToolCalls(
            """
            <think>{"not_a_tool":true}</think>
            {"tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup_weather","arguments":"{\"city\":\"Fuzhou\"}"}}]}
            """,
            request,
            out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("lookup_weather", call.Function.Name);
        Assert.Equal("""{"city":"Fuzhou"}""", call.Function.Arguments);
    }

    [Fact]
    public void ToolCallExtraction_NormalizesObjectArguments()
    {
        var request = CreateToolInferenceRequest("lookup_weather");

        var toolCalls = ExtractToolCalls(
            """{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup_weather","arguments":{"city":"Fuzhou"}}}]}""",
            request,
            out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("lookup_weather", call.Function.Name);
        Assert.Equal("""{"city":"Fuzhou"}""", call.Function.Arguments);
    }

    [Fact]
    public void ToolCallExtraction_DoesNotDropDeclaredToolWhenArgumentsMissSchema()
    {
        var request = new InferenceRequest
        {
            Tools = [CreateRequiredFunctionTool("lookup_weather", "city")]
        };

        var toolCalls = ExtractToolCalls(
            """{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"lookup_weather","arguments":{}}}]}""",
            request,
            out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("lookup_weather", call.Function.Name);
        Assert.Equal("{}", call.Function.Arguments);
    }

    [Fact]
    public void ToolProtocolRetry_RetriesBareMarkupNonAnswer()
    {
        var request = CreateToolInferenceRequest("lookup_weather");

        Assert.True(ShouldRetryToolProtocol(request, "<think>"));
        Assert.True(ShouldRetryToolProtocol(request, "<reasoning></reasoning>"));
    }

    [Fact]
    public void ToolProtocolRetry_DoesNotRetryNormalAutoAnswer()
    {
        var request = CreateToolInferenceRequest("lookup_weather");

        Assert.False(ShouldRetryToolProtocol(request, "I can help with that."));
    }

    [Fact]
    public void ToolCallGrammar_CanBeParsedByLLamaSharp()
    {
        var request = CreateToolInferenceRequest("lookup_weather", "skill_list");

        var grammar = new Grammar(BuildToolCallGrammar(request), "root");

        Assert.NotNull(grammar);
    }

    private static ResponseTaskInfo CreateTask(string id)
    {
        return new ResponseTaskInfo
        {
            Id = id,
            Type = "compact",
            SourceResponseId = "resp_source",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    private static InferenceRequest CreateToolInferenceRequest(
        string firstToolName,
        string? secondToolName = null,
        bool? parallelToolCalls = null)
    {
        var tools = new List<OpenAiTool>
        {
            CreateFunctionTool(firstToolName)
        };

        if (!string.IsNullOrWhiteSpace(secondToolName))
        {
            tools.Add(CreateFunctionTool(secondToolName));
        }

        return new InferenceRequest
        {
            Tools = tools,
            ParallelToolCalls = parallelToolCalls
        };
    }

    private static OpenAiTool CreateFunctionTool(string name)
    {
        using var parameters = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {},
              "required": []
            }
            """);

        return new OpenAiTool
        {
            Type = "function",
            Function = new OpenAiFunction
            {
                Name = name,
                Parameters = parameters.RootElement.Clone()
            }
        };
    }

    private static OpenAiTool CreateRequiredFunctionTool(string name, params string[] required)
    {
        using var parameters = JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "{{required[0]}}": { "type": "string" }
              },
              "required": [{{string.Join(",", required.Select(x => $"\"{x}\""))}}]
            }
            """);

        return new OpenAiTool
        {
            Type = "function",
            Function = new OpenAiFunction
            {
                Name = name,
                Parameters = parameters.RootElement.Clone()
            }
        };
    }

    private static IReadOnlyList<OpenAiToolCall> ExtractToolCalls(
        string generated,
        InferenceRequest request,
        out string cleanText)
    {
        object?[] parameters = [generated, request, null];
        var result = (IReadOnlyList<OpenAiToolCall>)TryExtractToolCallsMethod.Invoke(null, parameters)!;
        cleanText = (string)parameters[2]!;
        return result;
    }

    private static bool ShouldRetryToolProtocol(InferenceRequest request, string cleanText)
    {
        object?[] parameters = [request, cleanText];
        return (bool)ShouldRetryToolProtocolMethod.Invoke(null, parameters)!;
    }

    private static string BuildToolCallGrammar(InferenceRequest request)
    {
        object?[] parameters = [request];
        return (string)BuildToolCallGrammarMethod.Invoke(null, parameters)!;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StubHttpMessageHandler())
            {
                BaseAddress = new Uri("http://example.test")
            };
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
