# AGENTS.md

## Project

.NET 10 ASP.NET Core Minimal API that serves an OpenAI-compatible endpoint backed by GGUF models via LLamaSharp 0.27.0.

## Solution layout

```
Zhengyan.LLamaStack.slnx           # new .slnx format (not .sln)
src/Zhengyan.LLamaStack.Api/        # main API project (net10.0)
src/Zhengyan.OpenAIModels/          # shared DTO library (net8.0;net9.0)
src/Zhengyan.ChatUI.Desktop/        # Avalonia desktop debug client (net10.0)
tests/Zhengyan.LLamaStack.Tests/    # xUnit behavior tests (net10.0)
```

## Commands

```bash
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src/Zhengyan.LLamaStack.Api
```

- No lint, format, codegen, or CI in this repo.
- GPU backend: default is `LLamaSharp.Backend.Cuda12`. Switch to Vulkan by replacing that package with `LLamaSharp.Backend.Vulkan` 0.27.0.
- Tests are in a single file (`InfrastructureBehaviorTests.cs`), test private static methods via reflection.

## Config

- All settings under the `LLamaStack` section in `appsettings.json` / `appsettings.Development.json`.
- Default dev URLs: `http://localhost:5062`, `https://localhost:7095`.
- Storage providers: Memory (default), SQLite, PostgreSQL, Redis — selected via `Store:Provider` in config.
- Serialization: `System.Text.Json` with `SnakeCaseLower` naming, nulls omitted, no indentation.

## Architecture notes

- **Entry point**: `src/Zhengyan.LLamaStack.Api/Program.cs` — registers services, CORS, API key middleware, maps OpenAI-compatible endpoints.
- **Routing**: `OpenAiCompatibleEndpoints.cs` via `MapOpenAiCompatibleEndpoints()` extension.
- **Inference**: `LLamaInferenceService` wraps LLamaSharp; supports streaming (SSE), tool calling, structured outputs (JSON Schema → GBNF grammar).
- **Tool calling is protocol-only** — the server parses model-generated JSON into OpenAI `tool_calls` structs; the client must execute the actual tool.
- **Admin endpoints** (`/admin/*`) for model management, listed in `OpenAiCompatibleEndpoints.cs`.

## Testing quirks

- Tests access private static methods of `LLamaInferenceService` via `BindingFlags.NonPublic | BindingFlags.Static` reflection.
- No integration tests; no external service dependencies for tests
.
