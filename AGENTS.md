# AGENTS.md — Zhengyan.LLamaStack

## Project

Single-project .NET 10 ASP.NET Core Minimal API for local LLM inference via GGUF models (LLamaSharp 0.27.0). No test projects, no CI, no Dockerfile.

## Commands

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj -c Release -o publish\Zhengyan.LLamaStack.Api
```

- Solution file uses `.slnx` format (not `.sln`).
- No lint, format, typecheck, or codegen commands exist.

## Architecture

- **Single project** at `src/Zhengyan.LLamaStack.Api/` (no shared libs, no tests).
- **Directories**: `Endpoints/` (route handlers), `Inference/` (LLamaSharp wrapper), `OpenAi/` (protocol types/mapping), `Options/` (config models), `Storage/` (IOpenAiStore interface + Memory/Sqlite implementations), `Infrastructure/` (error types).
- **Entrypoint**: `Program.cs` — registers DI (LLamaInferenceService singleton, OpenAiMemoryStore singleton, OpenAiRequestMapper singleton, LLamaWarmupHostedService, ToolExecutor, IAgentTool built-ins), calls `app.MapOpenAiCompatibleEndpoints()`.
- **JSON**: `SnakeCaseLower` naming, `WhenWritingNull` ignore, no indentation. Set globally in `Program.cs` via `ConfigureJson`.
- **Storage**: `IOpenAiStore` interface. Default is `OpenAiMemoryStore` (in-process, lost on restart). Can switch to `OpenAiSqliteStore` by setting `LLamaStack:Store:Provider` to `"Sqlite"`.
- **Models**: Registered via `LLamaStack:Models[]` array. Legacy single-model config (`ModelId`/`ModelPath`) auto-registers a fallback entry. Both paths merge in `GetModelRegistrations()`.
- **Inference**: Each model uses one `LLamaContext` / `InteractiveExecutor`. Per-model `SemaphoreSlim` serializes inference (no concurrent requests per model).
- **Model loading**: Lazy by default (`LoadModelOnStartup: false`). Loaded on first request via `EnsureLoadedAsync`.
- **Endpoint prefix**: `/v1/` on all OpenAI-compatible routes. Non-prefixed `/chat/completions` and `/responses` also mapped.
- **Infrastructure**: Global exception handler (`OpenAiExceptionHandler`) returns OpenAI-format errors for unhandled exceptions. CORS and API key auth (`LLamaStack:Auth`) are configurable.

## Quirks

- Dev server runs on **http://localhost:5062** by default (launchSettings.json).
- GPU backend is a **package swap**, not a config toggle: remove `LLamaSharp.Backend.Cpu`, add `LLamaSharp.Backend.Cuda12` (or Vulkan).
- `AntiPrompts` default: `["<|im_end|>", "</s>"]`.
- SSE streaming writes raw `data: ...\n\n` lines to `HttpContext.Response`. `data: [DONE]` terminates. `stream_options.include_usage` now reports real output token counts.
- Chat completions are stored only when `store: true` in request body. Responses are stored by default unless `store: false`.
- `previous_response_id` resolves from the local store and prepends prior messages for context continuation.
- Tool calling now **executes known tools** (built-in: `calculator`, `current_time`) in a multi-turn loop via `IAgentTool`/`ToolExecutor`. Unknown tools are returned to the client as-is.
- `logprobs`, `top_logprobs`, and `logit_bias` are accepted with compatibility warnings instead of rejecting.
- `POST /v1/responses/{responseId}` updates response metadata (mirrors `POST /v1/chat/completions/{completionId}`).
- Chat `n` parameter generates multiple choices (each run independently).
- API key auth: set `LLamaStack:Auth:Enabled=true` and `LLamaStack:Auth:ApiKey=<key>`. CORS: set `LLamaStack:Cors:Enabled=true`.
- Configuration env var convention: `LLamaStack__Models__0__ModelPath` (.NET `__` separator).
- GGUF model files in `./models/` are gitignored. Paths set in `appsettings.Development.json`.
