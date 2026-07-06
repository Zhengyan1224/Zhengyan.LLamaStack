using System.Net;
using System.Reflection;
using System.Text.Json;
using LLama.Common;
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

    private static readonly MethodInfo BuildToolResultNonAnswerFallbackMethod =
        typeof(LLamaInferenceService).GetMethod("BuildToolResultNonAnswerFallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildToolResultNonAnswerFallback method was not found.");

    private static readonly MethodInfo IsInvalidToolProtocolRetryOutputMethod =
        typeof(LLamaInferenceService).GetMethod("IsInvalidToolProtocolRetryOutput", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("IsInvalidToolProtocolRetryOutput method was not found.");

    private static readonly MethodInfo BuildInvalidToolCallFallbackMethod =
        typeof(LLamaInferenceService).GetMethod("BuildInvalidToolCallFallback", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("BuildInvalidToolCallFallback method was not found.");

    private static readonly MethodInfo CreateInferenceParamsMethod =
        typeof(LLamaInferenceService).GetMethod("CreateInferenceParams", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("CreateInferenceParams method was not found.");

    private static readonly MethodInfo ShouldAutoTruncateMethod =
        typeof(LLamaInferenceService).GetMethod("ShouldAutoTruncate", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("ShouldAutoTruncate method was not found.");

    private static readonly MethodInfo PromptAlreadyContainsToolDefinitionsMethod =
        typeof(LLamaInferenceService).GetMethod("PromptAlreadyContainsToolDefinitions", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("PromptAlreadyContainsToolDefinitions method was not found.");

    private static readonly MethodInfo BuildToolInstructionMethod =
        typeof(LLamaInferenceService).GetMethod(
            "BuildToolInstruction",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(InferenceRequest), typeof(bool)],
            modifiers: null)
        ?? throw new InvalidOperationException("BuildToolInstruction method was not found.");

    private static readonly MethodInfo AddToolProtocolRetryNudgeMethod =
        typeof(LLamaInferenceService).GetMethod("AddToolProtocolRetryNudge", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AddToolProtocolRetryNudge method was not found.");

    private static readonly MethodInfo AddToolProtocolRepairNudgeMethod =
        typeof(LLamaInferenceService).GetMethod("AddToolProtocolRepairNudge", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("AddToolProtocolRepairNudge method was not found.");

    private static readonly MethodInfo TryBuildToolProtocolRecoveryCallMethod =
        typeof(LLamaInferenceService).GetMethod("TryBuildToolProtocolRecoveryCall", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("TryBuildToolProtocolRecoveryCall method was not found.");

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
    public void ToolCallExtraction_ParsesCommonToolCallTags()
    {
        var request = CreateToolInferenceRequest("skill_run_command");

        var toolCalls = ExtractToolCalls(
            """
            <tool_call>
            {"name":"skill_run_command","arguments":{"command":"echo hello"}}
            </tool_call>
            """,
            request,
            out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("skill_run_command", call.Function.Name);
        Assert.Equal("""{"command":"echo hello"}""", call.Function.Arguments);
    }

    [Fact]
    public void ToolCallExtraction_ParsesTaggedToolCallParametersAlias()
    {
        var request = CreateToolInferenceRequest("skill_run_command");

        var toolCalls = ExtractToolCalls(
            """<tool_call>{"name":"skill_run_command","parameters":{"command":"echo hello"}}</tool_call>""",
            request,
            out var cleanText);

        Assert.Empty(cleanText);
        var call = Assert.Single(toolCalls);
        Assert.Equal("skill_run_command", call.Function.Name);
        Assert.Equal("""{"command":"echo hello"}""", call.Function.Arguments);
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
    public void ToolProtocolRetry_RetriesNewUserRequestAfterHistoricalToolResult()
    {
        var request = CreateToolInferenceRequest("lookup_weather");
        request.Messages =
        [
            new InferenceMessage { Role = "user", Content = "lookup weather" },
            new InferenceMessage { Role = "tool", Content = """{"ok":true}""", ToolCallId = "call_1" },
            new InferenceMessage { Role = "user", Content = "lookup news" }
        ];

        Assert.True(ShouldRetryToolProtocol(request, "<think>"));
    }

    [Fact]
    public void ToolProtocolRetry_DoesNotRetryWhenCurrentTurnIsToolResult()
    {
        var request = CreateToolInferenceRequest("lookup_weather");
        request.Messages =
        [
            new InferenceMessage { Role = "user", Content = "lookup weather" },
            new InferenceMessage { Role = "tool", Content = """{"ok":true}""", ToolCallId = "call_1" }
        ];

        Assert.False(ShouldRetryToolProtocol(request, "<think>"));
    }

    [Fact]
    public void ToolProtocolRetryNudge_IncludesRealToolNames()
    {
        var request = CreateToolInferenceRequest("lookup_weather", "skill_list");

        var retry = AddToolProtocolRetryNudge(request);
        var nudge = retry.Messages[retry.Messages.Count - 1];

        Assert.True(retry.ForceToolCallJson);
        Assert.Equal(0, retry.Temperature);
        Assert.Contains("lookup_weather", nudge.Content);
        Assert.Contains("skill_list", nudge.Content);
        Assert.Contains("""{"name":"tool_name","arguments":{}}""", nudge.Content);
        Assert.DoesNotContain("tool_calls", nudge.Content);
    }

    [Fact]
    public void ToolProtocolRetryNudge_PrunesPreviousFailureHistory()
    {
        var request = CreateToolInferenceRequest("skill_run_command");
        request.Messages =
        [
            new InferenceMessage { Role = "system", Content = "system prompt" },
            new InferenceMessage { Role = "user", Content = "hello" },
            new InferenceMessage { Role = "assistant", Content = "hello response" },
            new InferenceMessage { Role = "user", Content = "lookup weather" },
            new InferenceMessage { Role = "assistant", Content = "Model did not generate a valid tool call.\n{" },
            new InferenceMessage { Role = "user", Content = "check this computer memory" },
            new InferenceMessage { Role = "user", Content = "The previous assistant message was not a valid final answer or a valid tool call. Continue the same user request." }
        ];

        var retry = AddToolProtocolRetryNudge(request);

        Assert.Equal(3, retry.Messages.Count);
        Assert.Equal("system", retry.Messages[0].Role);
        Assert.Contains("tool-call planner", retry.Messages[0].Content);
        Assert.DoesNotContain("system prompt", retry.Messages[0].Content);
        Assert.Equal("user", retry.Messages[1].Role);
        Assert.Equal("check this computer memory", retry.Messages[1].Content);
        Assert.StartsWith("Retry the same request by calling", retry.Messages[2].Content);
        Assert.DoesNotContain(retry.Messages, message => message.Content.Contains("Model did not generate", StringComparison.Ordinal));
        Assert.DoesNotContain(retry.Messages, message => message.Content.Contains("lookup weather", StringComparison.Ordinal));
        Assert.DoesNotContain(retry.Messages, message => message.Content.Contains("previous assistant message", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolProtocolRepairNudge_DisablesGrammarAndUsesRealToolNames()
    {
        var request = CreateToolInferenceRequest("lookup_weather", "skill_list");

        var repair = AddToolProtocolRepairNudge(request, "{\"");
        var nudge = repair.Messages[repair.Messages.Count - 1];

        Assert.False(repair.ForceToolCallJson);
        Assert.Equal(0, repair.Temperature);
        Assert.Contains("lookup_weather", nudge.Content);
        Assert.Contains("skill_list", nudge.Content);
        Assert.Contains("""{"name":"lookup_weather","arguments":{}}""", nudge.Content);
        Assert.DoesNotContain("tool_calls", nudge.Content);
    }

    [Fact]
    public void ToolProtocolRepairNudge_DropsPreviousRetryPrompt()
    {
        var request = CreateToolInferenceRequest("skill_run_command");
        request.Messages =
        [
            new InferenceMessage { Role = "system", Content = "system prompt" },
            new InferenceMessage { Role = "user", Content = "check this computer memory" }
        ];

        var retry = AddToolProtocolRetryNudge(request);
        var repair = AddToolProtocolRepairNudge(retry, "{");

        Assert.Equal(3, repair.Messages.Count);
        Assert.Contains("tool-call planner", repair.Messages[0].Content);
        Assert.DoesNotContain("system prompt", repair.Messages[0].Content);
        Assert.Equal("check this computer memory", repair.Messages[1].Content);
        Assert.StartsWith("The previous tool-call JSON was invalid.", repair.Messages[2].Content);
        Assert.DoesNotContain(repair.Messages, message => message.Content.StartsWith("Retry the same request", StringComparison.Ordinal));
    }

    [Fact]
    public void ToolCallGrammar_CanBeParsedByLLamaSharp()
    {
        var request = CreateToolInferenceRequest("lookup_weather", "skill_list");
        var grammarText = BuildToolCallGrammar(request);

        Assert.DoesNotContain("\"\\{\"", grammarText);
        Assert.DoesNotContain("\"\\}\"", grammarText);
        Assert.DoesNotContain("\"\\[\"", grammarText);
        Assert.DoesNotContain("\"\\]\"", grammarText);
        Assert.DoesNotContain("\"tool_calls\"", grammarText);
        Assert.Contains("\"\\\"name\\\"\"", grammarText);
        Assert.Contains("\"\\\"arguments\\\"\"", grammarText);

        var grammar = new Grammar(grammarText, "root");

        Assert.NotNull(grammar);
    }

    [Fact]
    public void ToolProtocolRetry_TreatsPartialJsonAsInvalid()
    {
        Assert.True(IsInvalidToolProtocolRetryOutput("{"));
        Assert.True(IsInvalidToolProtocolRetryOutput("""{"tool_calls":["""));
        Assert.False(IsInvalidToolProtocolRetryOutput("I cannot call a tool."));

        var fallback = BuildInvalidToolCallFallback("{");

        Assert.Contains("模型没有生成有效的工具调用", fallback);
        Assert.NotEqual("{", fallback.Trim());
    }

    [Fact]
    public void ToolProtocolRecovery_UsesRegisteredSkillListWhenAvailable()
    {
        var request = CreateToolInferenceRequest("lookup_weather", "skill_list");

        var calls = TryBuildToolProtocolRecoveryCall(request);

        var call = Assert.Single(calls);
        Assert.Equal("skill_list", call.Function.Name);
        Assert.Equal("{}", call.Function.Arguments);
    }

    [Fact]
    public void ToolProtocolRecovery_DoesNotGuessPureAutoCustomTool()
    {
        var request = CreateToolInferenceRequest("lookup_weather");

        var calls = TryBuildToolProtocolRecoveryCall(request);

        Assert.Empty(calls);
    }

    [Fact]
    public void ToolProtocolRecovery_HonorsSpecificFunctionChoice()
    {
        var request = CreateToolInferenceRequest("lookup_weather");
        request.ToolChoiceMode = InferenceToolChoiceMode.Function;
        request.ToolChoiceName = "lookup_weather";

        var calls = TryBuildToolProtocolRecoveryCall(request);

        var call = Assert.Single(calls);
        Assert.Equal("lookup_weather", call.Function.Name);
        Assert.Equal("{}", call.Function.Arguments);
    }

    [Fact]
    public void ToolResultFallback_ReportsFailedToolResult()
    {
        var request = new InferenceRequest
        {
            Messages =
            [
                new InferenceMessage
                {
                    Role = "tool",
                    Content = """{"ok":false,"command":"curl -s 'https://wttr.in/Fuzhou'","exitCode":3,"stdout":"","stderr":""}""",
                    ToolCallId = "call_1"
                }
            ]
        };

        var fallback = BuildToolResultNonAnswerFallback(request);

        Assert.Contains("工具调用失败", fallback);
        Assert.Contains("退出码：3", fallback);
        Assert.DoesNotContain("<think>", fallback);
    }

    [Fact]
    public void ToolResultFallback_DoesNotTreatNonCommandJsonAsFailedCommand()
    {
        var request = new InferenceRequest
        {
            Messages =
            [
                new InferenceMessage
                {
                    Role = "tool",
                    Content = """{"skills":[{"name":"system_info","description":"Read system information."}]}""",
                    ToolCallId = "call_1"
                }
            ]
        };

        var fallback = BuildToolResultNonAnswerFallback(request);

        Assert.Contains("system_info", fallback);
        Assert.DoesNotContain("stdout/stderr", fallback);
    }

    [Fact]
    public void InferenceParams_UsesManualOverflowManagement()
    {
        var model = new LLamaModelRuntimeOptions
        {
            DefaultMaxTokens = 256
        };

        var parameters = CreateInferenceParams(model, new InferenceRequest());

        Assert.Equal(ContextOverflowStrategy.ThrowException, parameters.OverflowStrategy);
        Assert.Equal(256, parameters.MaxTokens);
    }

    [Fact]
    public void InferenceParams_LimitsMaxTokensToRemainingContext()
    {
        var model = new LLamaModelRuntimeOptions
        {
            ContextSize = 4096,
            DefaultMaxTokens = 512
        };

        var parameters = CreateInferenceParams(model, new InferenceRequest(), promptTokens: 4000);

        Assert.Equal(64, parameters.MaxTokens);
    }

    [Fact]
    public void InferenceParams_RejectsPromptThatLeavesNoCompletionBudget()
    {
        var model = new LLamaModelRuntimeOptions
        {
            ContextSize = 4096,
            DefaultMaxTokens = 512
        };

        var exception = Assert.Throws<TargetInvocationException>(() =>
            CreateInferenceParams(model, new InferenceRequest(), promptTokens: 4050));
        var protocol = Assert.IsType<OpenAiProtocolException>(exception.InnerException);

        Assert.Equal("context_length_exceeded", protocol.Code);
    }

    [Fact]
    public void AutoTruncation_DefaultsOnUnlessExplicitlyDisabled()
    {
        Assert.True(ShouldAutoTruncate(new InferenceRequest()));
        Assert.True(ShouldAutoTruncate(new InferenceRequest { Truncation = "auto" }));
        Assert.False(ShouldAutoTruncate(new InferenceRequest { Truncation = "disabled" }));
    }

    [Fact]
    public void ToolInstruction_DoesNotDuplicateAlreadyProvidedToolDefinitions()
    {
        var request = CreateToolInferenceRequest("memory_search", "skill_list");
        request.Messages =
        [
            new InferenceMessage
            {
                Role = "system",
                Content = """
                    Available tools are provided as JSON:
                    [{"type":"function","function":{"name":"memory_search","parameters":{}}},{"type":"function","function":{"name":"skill_list","parameters":{}}}]
                    """
            }
        ];

        Assert.True(PromptAlreadyContainsToolDefinitions(request));

        var instruction = BuildToolInstruction(request, includeToolDefinitions: false);

        Assert.DoesNotContain("[{\"type\":\"function\"", instruction);
        Assert.Contains("already provided", instruction);
        Assert.Contains("""{"name":"tool_name","arguments":{"arg":"value"}}""", instruction);
    }

    [Fact]
    public void ToolInstruction_DoesNotTreatTextCatalogAsJsonToolDefinitions()
    {
        var request = CreateToolInferenceRequest("memory_search", "skill_list");
        request.Messages =
        [
            new InferenceMessage
            {
                Role = "system",
                Content = "\u53ef\u7528\u5de5\u5177:\n" +
                    "- memory_search: Search long-term memory files.\n" +
                    "- skill_list: List project skills.\n" +
                    "\u5de5\u5177\u8c03\u7528: respond with tool_calls JSON."
            }
        ];

        Assert.False(PromptAlreadyContainsToolDefinitions(request));
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

    private static string BuildToolResultNonAnswerFallback(InferenceRequest request)
    {
        object?[] parameters = [request];
        return (string)BuildToolResultNonAnswerFallbackMethod.Invoke(null, parameters)!;
    }

    private static bool IsInvalidToolProtocolRetryOutput(string cleanText)
    {
        object?[] parameters = [cleanText];
        return (bool)IsInvalidToolProtocolRetryOutputMethod.Invoke(null, parameters)!;
    }

    private static string BuildInvalidToolCallFallback(string invalidOutput)
    {
        object?[] parameters = [invalidOutput];
        return (string)BuildInvalidToolCallFallbackMethod.Invoke(null, parameters)!;
    }

    private static InferenceParams CreateInferenceParams(LLamaModelRuntimeOptions model, InferenceRequest request, int? promptTokens = null)
    {
        object?[] parameters = [model, request, promptTokens];
        return (InferenceParams)CreateInferenceParamsMethod.Invoke(null, parameters)!;
    }

    private static bool ShouldAutoTruncate(InferenceRequest request)
    {
        object?[] parameters = [request];
        return (bool)ShouldAutoTruncateMethod.Invoke(null, parameters)!;
    }

    private static bool PromptAlreadyContainsToolDefinitions(InferenceRequest request)
    {
        object?[] parameters = [request];
        return (bool)PromptAlreadyContainsToolDefinitionsMethod.Invoke(null, parameters)!;
    }

    private static string BuildToolInstruction(InferenceRequest request, bool includeToolDefinitions)
    {
        object?[] parameters = [request, includeToolDefinitions];
        return (string)BuildToolInstructionMethod.Invoke(null, parameters)!;
    }

    private static InferenceRequest AddToolProtocolRetryNudge(InferenceRequest request)
    {
        object?[] parameters = [request];
        return (InferenceRequest)AddToolProtocolRetryNudgeMethod.Invoke(null, parameters)!;
    }

    private static InferenceRequest AddToolProtocolRepairNudge(InferenceRequest request, string invalidOutput)
    {
        object?[] parameters = [request, invalidOutput];
        return (InferenceRequest)AddToolProtocolRepairNudgeMethod.Invoke(null, parameters)!;
    }

    private static IReadOnlyList<OpenAiToolCall> TryBuildToolProtocolRecoveryCall(InferenceRequest request)
    {
        object?[] parameters = [request];
        return (IReadOnlyList<OpenAiToolCall>)TryBuildToolProtocolRecoveryCallMethod.Invoke(null, parameters)!;
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
