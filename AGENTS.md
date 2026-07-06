# AGENTS.md - Zhengyan.LLamaStack

## Project

Local OpenAI-compatible LLM stack for GGUF models. The repository now contains:

- `src/Zhengyan.LLamaStack.Api/` - .NET 10 ASP.NET Core Minimal API backed by LLamaSharp 0.27.0.
- `src/Zhengyan.OpenAIModels/` - shared OpenAI-compatible request/response DTOs.
- `src/Zhengyan.ChatUI.Desktop/` - Avalonia desktop client for local debugging.
- `tests/Zhengyan.LLamaStack.Tests/` - xUnit tests for storage, tool execution, auth, and request mapping behavior.

No CI or Dockerfile exists yet. The solution file uses `.slnx`, not `.sln`.

## Commands

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj -c Release -o publish\Zhengyan.LLamaStack.Api
```

There are no separate lint, format, or codegen commands.

## Architecture

- Entrypoint: `src/Zhengyan.LLamaStack.Api/Program.cs`.
- API routes: `Endpoints/OpenAiCompatibleEndpoints.cs`.
- Inference: `Inference/LLamaInferenceService.cs`.
- Protocol mapping: `OpenAi/OpenAiRequestMapper.cs` and `OpenAi/OpenAiResponseFactory.cs`.
- Configuration: `Options/LLamaStackOptions.cs`.
- Storage: `Storage/IOpenAiStore.cs` with Memory, SQLite, PostgreSQL, and Redis implementations.
- Infrastructure: API key middleware and OpenAI-format exception handling.

## Runtime Behavior

- Default dev URL: `http://localhost:5062`.
- API prefix: `/v1/`; compatibility aliases `/chat/completions` and `/responses` also exist.
- JSON uses snake_case lower naming and omits null values.
- Default storage provider is Memory. Set `LLamaStack:Store:Provider` to `Sqlite`, `Postgres`, or `Redis` for persistent stores.
- Chat models are configured through `LLamaStack:Models[]`; legacy `ModelId`/`ModelPath` still registers a fallback model.
- Embedding models are configured through `LLamaStack:EmbeddingModels[]`.
- Model loading is lazy by default unless `LoadModelOnStartup` is true.
- Tool calling is protocol-only: request `tools`/legacy `functions` are injected into the prompt, and model-emitted tool-call JSON is returned as OpenAI-compatible `tool_calls` / `function_call` output for the client to execute.
- The API does not execute tools server-side. `tool_choice: none`, specific function choices, and `parallel_tool_calls: false` are enforced when parsing model-emitted tool calls.
- Streaming requests that include tools are buffered and emitted as SSE final output to keep stream/non-stream response shape aligned.
- Strict JSON schema mode returns an OpenAI-format error if the generated output does not validate.
- Remote media URLs are blocked for localhost/private/link-local/CGNAT targets, redirects are disabled, and media is read with a configured byte limit.
- CORS and API key auth are configurable through `LLamaStack:Cors` and `LLamaStack:Auth`. `Auth:ApiKeyHeader` is honored.

## Concurrency

- Outer layer: `ModelRequestQueue` provides FIFO gating per model.
- Inner layer: each loaded model has a pool of `LLamaContext`/`InteractiveExecutor` instances sharing one `LLamaWeights`.
- `POST /v1/models/{id}/resize` changes pool size with VRAM checks. Resize does not replace the live semaphore; shrink waits for idle instances before removing them.
- `ResponseExecutionTracker` supports cancellation through linked `CancellationTokenSource`.

## Notes

- GPU backend is a package swap, not a config toggle: replace `LLamaSharp.Backend.Cpu` with CUDA/Vulkan backend packages.
- GGUF model files under `./models/` are gitignored.
- API logs redact prompt, message content, media URL/data, embeddings, and generated text payloads.
- Persistent stores include response task management for compact/background task state.
