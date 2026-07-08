# Zhengyan.LLamaStack — Agent Guide

## Project

.NET 10 ASP.NET Core Minimal API + LLamaSharp 0.27.0. Local GGUF model inference exposing OpenAI-compatible HTTP endpoints.

## Solution layout

```
Zhengyan.LLamaStack.slnx          # new .slnx format
src/
  Zhengyan.LLamaStack.Api/        # Web API (net10.0)
  Zhengyan.OpenAIModels/          # Shared DTO library (net8.0;net9.0)
  Zhengyan.ChatUI.Desktop/        # Avalonia debug client (net10.0)
tests/
  Zhengyan.LLamaStack.Tests/      # xUnit tests (net10.0, references API project)
```

## Commands (run from repo root)

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj -c Release -o publish\Zhengyan.LLamaStack.Api
```

No lint, format, codegen, or CI scripts exist.

## Architecture notes

### Entrypoint
`src/Zhengyan.LLamaStack.Api/Program.cs:11` — configures DI, CORS, middleware, routes. All endpoints registered via `MapOpenAiCompatibleEndpoints()`. Returns snake_case JSON, nulls omitted.

### Request flow
1. `OpenAiCompatibleEndpoints.cs` → receives HTTP
2. `OpenAiRequestMapper.cs` → converts OpenAI JSON → `InferenceRequest`
3. `LLamaInferenceService.cs` → validates, loads runtime, acquires pool context, generates, extracts tool calls
4. `OpenAiResponseFactory.cs` → serializes response / SSE events

### Key constraints
- Tool calling is **protocol-only**: server parses model-generated function-call JSON; client must execute. No server-side tool runtime.
- Backend package (`LLamaSharp.Backend.Cuda12`) is a **compile-time** choice in the API csproj. Swap NuGet refs to change (e.g. CPU-only, Vulkan).
- `appsettings.Development.json` is gitignored. Create it for local model paths.
- Default dev URL: `http://localhost:5062`

### Tests
- Single test file: `tests/Zhengyan.LLamaStack.Tests/InfrastructureBehaviorTests.cs`
- Tests private static methods via reflection (refers to many `LLamaInferenceService` methods by `BindingFlags.NonPublic | BindingFlags.Static`)
- Uses xUnit (`[Fact]`), no moq/fluentassertions dependency — plain assertion only
- SQLite tests create temp DB files at `Path.GetTempPath()`
- No integration or end-to-end tests exist

### Configuration
Main section: `LLamaStack:`. Models registered via `Models[]` (multi-model) or legacy `ModelId`/`ModelPath`. Storage: `Memory` (default), `Sqlite`, `Postgres`, `Redis`.

### Shared models library
`Zhengyan.OpenAIModels` targets `net8.0;net9.0`. Contains OpenAI-compatible DTOs with `JsonSerializerOptions` set to snake_case + null-omit.

### Known gaps (from README roadmap)
- No `/v1/audio/*`, `/v1/images/*`, `/v1/files/*`, `/v1/batches`, no logprobs, no vector stores
- No CI, no Dockerfile
- No logprob generation