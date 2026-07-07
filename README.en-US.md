# Zhengyan.LLamaStack

English | [Simplified Chinese](README.md)

`Zhengyan.LLamaStack` is a local OpenAI-compatible inference stack for GGUF models. It is built with .NET 10, ASP.NET Core Minimal API, and LLamaSharp 0.27.0. The service loads local GGUF chat, vision, audio-capable multimodal, and embedding models, then exposes OpenAI-style HTTP endpoints for existing SDKs and debugging clients.

The current codebase covers Chat Completions, Responses, Embeddings, tokenization, SSE streaming, multi-model routing, lazy and explicit model load/unload, per-model request queues, runtime pool resizing, tool-call protocol extraction, JSON-schema structured outputs, guarded multimodal input loading, persistent stores, API-key auth, CORS, and an Avalonia desktop debug client.

## Table of Contents

- [Features](#features)
- [Project Logic](#project-logic)
- [Project Structure](#project-structure)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Model Preparation](#model-preparation)
- [Running the Service](#running-the-service)
- [API Examples](#api-examples)
- [Desktop Debug Client](#desktop-debug-client)
- [Deployment](#deployment)
- [GPU Backends](#gpu-backends)
- [OpenAI Compatibility Roadmap](#openai-compatibility-roadmap)
- [Development Commands](#development-commands)
- [References](#references)

## Features

- .NET 10 ASP.NET Core Minimal API service backed by LLamaSharp 0.27.0.
- OpenAI-style JSON, using snake_case property names and omitting null values.
- Multi-model registry through `LLamaStack:Models[]`, with legacy `ModelId` / `ModelPath` fallback.
- Independent embedding model registry through `LLamaStack:EmbeddingModels[]`.
- Lazy model loading by default, with optional startup warmup and explicit load/unload endpoints.
- Model capability declarations exposed from `/v1/models` and checked before inference.
- Per-model FIFO request queue plus an inner pool of `LLamaContext` / `InteractiveExecutor` instances that share one `LLamaWeights`.
- Runtime model pool resize through `POST /v1/models/{modelId}/resize`, with estimated VRAM budget checks.
- SSE streaming for Chat Completions and Responses. Requests with tools are buffered and emitted as final SSE output so stream and non-stream shapes stay aligned.
- Tool calling is protocol-only. The server injects declared `tools` / legacy `functions`, parses model-emitted JSON, and returns OpenAI-compatible `tool_calls` / `function_call` output for the client to execute.
- `tool_choice: none`, specific function choices, and `parallel_tool_calls: false` are enforced while extracting tool calls.
- Structured output support through JSON Schema to GBNF constrained decoding where possible, plus strict post-generation validation.
- Multimodal request parsing for text, image, and audio input blocks. Media may come from data URLs, raw base64, guarded remote URLs, or explicitly enabled local file paths.
- Chat and Responses management endpoints backed by Memory, SQLite, PostgreSQL, or Redis stores.
- Responses features include `previous_response_id`, in-memory `conversation` continuation, `background` execution, cancellation, token counting, and compact tasks.
- Optional API key authentication and CORS configuration.
- Logs redact prompts, message content, media URLs/data, embeddings, generated text, and API keys.
- Avalonia desktop client for local manual debugging.

### Endpoint Summary

| Endpoint | Purpose |
| --- | --- |
| `GET /` | Service descriptor. |
| `GET /health` | Simple health alias. |
| `GET /v1/health` | OpenAI-style health details with model load state and uptime. |
| `GET /v1/models` | Model list, load state, paths, capabilities, embedding dimensions. |
| `POST /v1/models/{modelId}/load` | Load a model at runtime. |
| `POST /v1/models/{modelId}/unload` | Unload a model at runtime. |
| `POST /v1/models/{modelId}/resize` | Resize the loaded model pool. |
| `GET /v1/queue/{entryId}` | Inspect queue entry state. |
| `POST /v1/chat/completions` | Chat Completions inference. |
| `POST /chat/completions` | Compatibility alias for Chat Completions. |
| `GET /v1/chat/completions` | List stored Chat Completions. |
| `GET /v1/chat/completions/{completionId}` | Retrieve a stored Chat Completion. |
| `POST /v1/chat/completions/{completionId}` | Update stored Chat Completion metadata. |
| `DELETE /v1/chat/completions/{completionId}` | Delete a stored Chat Completion. |
| `GET /v1/chat/completions/{completionId}/messages` | List stored Chat Completion messages. |
| `POST /v1/chat/completions/{completionId}/cancel` | Cancel an executing non-streaming Chat Completion when tracked. |
| `POST /v1/responses` | Responses API inference. |
| `POST /responses` | Compatibility alias for Responses. |
| `GET /v1/responses` | List stored Responses. |
| `GET /v1/responses/{responseId}` | Retrieve a stored Response. |
| `POST /v1/responses/{responseId}` | Update stored Response metadata. |
| `DELETE /v1/responses/{responseId}` | Delete a stored Response. |
| `POST /v1/responses/{responseId}/cancel` | Cancel an executing or stored Response. |
| `GET /v1/responses/{responseId}/input_items` | List stored Response input items. |
| `POST /v1/responses/{responseId}/count_tokens` | Count tokens for a stored Response. |
| `POST /v1/responses/input_tokens` | Estimate input tokens for a Responses request. |
| `POST /v1/responses/compact` | Schedule a model-driven compact task for a stored Response. |
| `GET /v1/responses/tasks/{taskId}` | Inspect compact task state. |
| `POST /v1/embeddings` | Generate embeddings. |
| `POST /v1/tokenize` | Tokenize text with a configured model. |
| `POST /v1/detokenize` | Detokenize token IDs with a configured model. |

## Project Logic

The service startup path is in `src/Zhengyan.LLamaStack.Api/Program.cs`. It binds the `LLamaStack` options section, configures JSON naming, registers the store provider, request mapper, inference service, queue manager, conversation store, cancellation tracker, background response worker, compact scheduler, warmup hosted service, exception handler, API-key middleware, and endpoint routes.

Request flow:

1. `OpenAiCompatibleEndpoints` receives an OpenAI-style request and logs a redacted shape.
2. `OpenAiRequestMapper` converts Chat Completions or Responses payloads into `InferenceRequest`, including content arrays, media blocks, tools, tool choice, JSON mode, sampling options, metadata, and compatibility warnings.
3. `LLamaInferenceService.ValidateRequest` checks model existence, declared capabilities, model path, optional `mmproj` path, streaming, tools, JSON mode, and media capability requirements.
4. `ModelRequestQueue` gives each model FIFO admission. Response headers include `X-Queue-Position` and `X-Queue-Entry-Id`.
5. `LLamaInferenceService` lazily loads the selected runtime, acquires one isolated context/executor instance from the pool, builds a prompt, optionally attaches media, applies sampler and grammar settings, streams or completes generation, extracts tool-call JSON, validates strict JSON schema output, counts tokens, and releases the instance.
6. `OpenAiResponseFactory` converts the internal completion or stored object into OpenAI-compatible response JSON or SSE events. Store-backed endpoints persist and retrieve compact local state.

The API does not execute tools server-side. A client must execute the returned function call and send the result back as a tool message or Responses `function_call_output`.

## Project Structure

```text
.
|-- README.md
|-- README.en-US.md
|-- Zhengyan.LLamaStack.slnx
|-- src/
|   |-- Zhengyan.LLamaStack.Api/
|   |   |-- Endpoints/        # HTTP route handlers
|   |   |-- Inference/        # LLamaSharp runtime, queues, background tasks
|   |   |-- Infrastructure/   # API key middleware and OpenAI errors
|   |   |-- OpenAi/           # Request mapper, response factory, API contracts
|   |   |-- Options/          # LLamaStack configuration model
|   |   |-- Storage/          # Memory, SQLite, PostgreSQL, Redis stores
|   |   |-- Program.cs
|   |   `-- appsettings.json
|   |-- Zhengyan.OpenAIModels/ # Shared OpenAI-compatible DTO library
|   `-- Zhengyan.ChatUI.Desktop/ # Avalonia desktop debug client
`-- tests/
    `-- Zhengyan.LLamaStack.Tests/ # xUnit behavior tests
```

## Requirements

- Windows, Linux, or macOS.
- .NET SDK 10.0 or later for the API and tests.
- A GGUF model file for text inference.
- A matching `mmproj` GGUF file when image or audio input is needed.
- For the checked-in API project, the current backend package is `LLamaSharp.Backend.Cuda12`. Replace it if your runtime target needs CPU-only, Vulkan, or another backend.

Check the installed .NET version:

```powershell
dotnet --info
```

## Quick Start

1. Restore and build:

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
```

2. Configure a model path. You can create or edit `src/Zhengyan.LLamaStack.Api/appsettings.Development.json`:

```json
{
  "LLamaStack": {
    "DefaultModel": "local-gguf",
    "Models": [
      {
        "Id": "local-gguf",
        "ModelPath": "D:\\models\\your-model.gguf"
      }
    ]
  }
}
```

Legacy single-model configuration is still accepted:

```json
{
  "LLamaStack": {
    "ModelId": "local-gguf",
    "ModelPath": "D:\\models\\your-model.gguf"
  }
}
```

Environment variable example:

```powershell
$env:LLamaStack__DefaultModel = "local-gguf"
$env:LLamaStack__Models__0__Id = "local-gguf"
$env:LLamaStack__Models__0__ModelPath = "D:\models\your-model.gguf"
```

3. Start the API:

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

Default development URL:

```text
http://localhost:5062
```

4. Check health:

Windows PowerShell:

```powershell
Invoke-RestMethod http://localhost:5062/health
```

Linux curl:

```bash
curl -s http://localhost:5062/health
```

## Configuration

The main configuration section is `LLamaStack`. `Models[]` is the recommended shape; `ModelId` / `ModelPath` is retained for compatibility. Model-specific values inherit from top-level defaults when omitted.

```json
{
  "LLamaStack": {
    "DefaultModel": "local-gguf",
    "ModelId": "local-gguf",
    "ModelPath": "",
    "MmprojPath": "",
    "ContextSize": 4096,
    "GpuLayerCount": 0,
    "Threads": null,
    "BatchThreads": null,
    "BatchSize": 512,
    "UBatchSize": 512,
    "UseMemoryMap": true,
    "UseMemoryLock": false,
    "FlashAttention": null,
    "UseGpuForMtmd": false,
    "LoadModelOnStartup": false,
    "MaxVramBytes": 0,
    "DefaultMaxTokens": 512,
    "DefaultTemperature": 0.7,
    "DefaultTopP": 0.95,
    "DefaultTopK": 40,
    "AntiPrompts": [ "<|im_end|>", "</s>" ],
    "AllowRemoteMedia": true,
    "AllowLocalMediaPaths": false,
    "MaxMediaBytes": 33554432,
    "Store": {
      "Provider": "Memory",
      "SqlitePath": "data/llamastack.db",
      "ConnectionString": null
    },
    "Auth": {
      "Enabled": false,
      "ApiKey": null,
      "ApiKeyHeader": "Authorization"
    },
    "Cors": {
      "Enabled": false,
      "AllowedOrigins": [],
      "AllowedHeaders": [],
      "AllowedMethods": []
    },
    "Models": [
      {
        "Id": "local-gguf",
        "OwnedBy": "local",
        "ModelPath": "D:\\models\\model.gguf",
        "MmprojPath": "",
        "ContextSize": 4096,
        "GpuLayerCount": 0,
        "MaxConcurrency": 1,
        "Capabilities": {
          "ChatCompletions": true,
          "Responses": true,
          "TextInput": true,
          "ImageInput": false,
          "AudioInput": false,
          "ToolCalling": true,
          "Streaming": true,
          "JsonMode": true,
          "Embeddings": false
        }
      }
    ],
    "EmbeddingModels": [
      {
        "Id": "bge-m3",
        "ModelPath": "D:\\models\\bge-m3.gguf",
        "Dimensions": 1024,
        "MaxConcurrency": 1
      }
    ]
  }
}
```

| Key | Description |
| --- | --- |
| `DefaultModel` | Default chat/Responses model when a request omits `model`. |
| `ModelId` / `ModelPath` | Legacy single-model ID and GGUF path. |
| `MmprojPath` | Legacy/default multimodal projection model path. |
| `ContextSize` | Context window size. |
| `GpuLayerCount` | Number of layers to offload. `0` means no layer offload. |
| `Threads`, `BatchThreads` | LLamaSharp thread settings. `null` lets LLamaSharp choose. |
| `BatchSize`, `UBatchSize` | Prompt and physical batch sizes. |
| `UseMemoryMap`, `UseMemoryLock` | Model memory loading behavior. |
| `FlashAttention` | Optional Flash Attention setting. |
| `UseGpuForMtmd` | Whether MTMD/mmproj runs on GPU. |
| `LoadModelOnStartup` | Load configured models during startup instead of lazy loading. |
| `MaxVramBytes` | Estimated VRAM budget. `0` disables the budget check. |
| `DefaultMaxTokens`, `DefaultTemperature`, `DefaultTopP`, `DefaultTopK` | Default generation settings. |
| `AntiPrompts` | LLamaSharp anti-prompts used as stop strings. |
| `AllowRemoteMedia` | Allows remote image/audio URLs with host guards, no redirects, and byte limits. |
| `AllowLocalMediaPaths` | Allows local file paths in request bodies. Keep disabled unless needed. |
| `MaxMediaBytes` | Maximum bytes per media input. |
| `Store.Provider` | `Memory`, `Sqlite`, `Postgres`, or `Redis`. |
| `Store.SqlitePath` | SQLite database path. |
| `Store.ConnectionString` | PostgreSQL or Redis connection string. |
| `Auth.Enabled`, `Auth.ApiKey`, `Auth.ApiKeyHeader` | Optional API-key authentication. The middleware accepts either a raw key or `Bearer <key>`. |
| `Cors` | Optional allowed origins, headers, and methods. Empty lists mean allow any when CORS is enabled. |
| `Models[].Id` | Public model ID. |
| `Models[].OwnedBy` | `owned_by` returned by `/v1/models`. |
| `Models[].ModelPath` | GGUF path for this model. |
| `Models[].MmprojPath` | mmproj path for this model. |
| `Models[].MaxConcurrency` | Number of runtime context/executor instances for this model. |
| `Models[].Capabilities` | Capability declaration used for `/v1/models` and request validation. |
| `EmbeddingModels[]` | Dedicated embedding model registry. Embedding settings include `Dimensions`, GPU layers, threads, batch size, memory options, and `MaxConcurrency`. |

## Model Preparation

LLamaSharp consumes GGUF files. Download pre-converted GGUF models from a model hub or convert/quantize with the llama.cpp toolchain.

Quantized variants such as `Q4_K_M`, `Q5_K_M`, and `Q6_K` reduce memory usage. Multimodal models need both the text GGUF and the matching `mmproj` GGUF file.

Model files under `./models/` are gitignored.

## Running the Service

Development run:

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

Specify the listen URL:

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
```

## API Examples

Every testable interface below includes both Windows PowerShell and Linux curl commands. For examples that need a dynamic ID, first run the create request; on Linux, either copy the returned `id` manually into the variable or use a JSON helper such as `jq`. If API key authentication is enabled, add `-Headers @{ Authorization = "Bearer <key>" }` to PowerShell requests and `-H "Authorization: Bearer <key>"` to curl requests.

### Health Check

Windows PowerShell:

```powershell
Invoke-RestMethod http://localhost:5062/health
Invoke-RestMethod http://localhost:5062/v1/health
```

Linux curl:

```bash
curl -s http://localhost:5062/health
curl -s http://localhost:5062/v1/health
```

### List Models

Windows PowerShell:

```powershell
Invoke-RestMethod http://localhost:5062/v1/models
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/models
```

The response includes `loaded`, model paths, `capabilities`, and embedding dimensions when available.

### Load and Unload a Model

Windows PowerShell:

```powershell
Invoke-RestMethod http://localhost:5062/v1/models/local-gguf/load -Method Post
Invoke-RestMethod http://localhost:5062/v1/models/local-gguf/unload -Method Post
```

Linux curl:

```bash
curl -s -X POST http://localhost:5062/v1/models/local-gguf/load
curl -s -X POST http://localhost:5062/v1/models/local-gguf/unload
```

### Chat Completions

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  messages = @(
    @{ role = "system"; content = "You are a concise assistant." },
    @{ role = "user"; content = "Say hello in Chinese." }
  )
  max_tokens = 64
} | ConvertTo-Json -Depth 10

Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "messages": [
      { "role": "system", "content": "You are a concise assistant." },
      { "role": "user", "content": "Say hello in Chinese." }
    ],
    "max_tokens": 64
  }'
```

### Chat Completions Streaming

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  stream = $true
  stream_options = @{ include_usage = $true }
  messages = @(
    @{ role = "user"; content = "Write a short haiku about local inference." }
  )
} | ConvertTo-Json -Depth 10

Invoke-WebRequest http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -N -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "stream": true,
    "stream_options": { "include_usage": true },
    "messages": [
      { "role": "user", "content": "Write a short haiku about local inference." }
    ]
  }'
```

### Responses API

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  input = "Write one sentence about local GGUF inference."
  max_output_tokens = 64
} | ConvertTo-Json -Depth 10

Invoke-RestMethod http://localhost:5062/v1/responses `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "input": "Write one sentence about local GGUF inference.",
    "max_output_tokens": 64
  }'
```

### Responses Streaming

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  stream = $true
  stream_options = @{ include_usage = $true }
  input = "Stream a short local inference answer."
} | ConvertTo-Json -Depth 10

Invoke-WebRequest http://localhost:5062/v1/responses `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -N -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "stream": true,
    "stream_options": { "include_usage": true },
    "input": "Stream a short local inference answer."
  }'
```

### Embeddings

Windows PowerShell:

```powershell
$body = @{
  model = "bge-m3"
  input = "Hello world"
  dimensions = 512
} | ConvertTo-Json -Depth 10

Invoke-RestMethod http://localhost:5062/v1/embeddings `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/embeddings \
  -H "Content-Type: application/json" \
  -d '{
    "model": "bge-m3",
    "input": "Hello world",
    "dimensions": 512
  }'
```

If a dedicated embedding model is not registered, the endpoint can fall back to a configured chat model and create a one-shot embedder from that model.

### Tokenize and Detokenize

Windows PowerShell:

```powershell
$tokenizeBody = @{
  model = "local-gguf"
  input = "Hello local inference"
} | ConvertTo-Json

$tokens = Invoke-RestMethod http://localhost:5062/v1/tokenize `
  -Method Post `
  -ContentType "application/json" `
  -Body $tokenizeBody

$detokenizeBody = @{
  model = "local-gguf"
  tokens = @($tokens.data | ForEach-Object { $_.token })
} | ConvertTo-Json

Invoke-RestMethod http://localhost:5062/v1/detokenize `
  -Method Post `
  -ContentType "application/json" `
  -Body $detokenizeBody
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/tokenize \
  -H "Content-Type: application/json" \
  -d '{ "model": "local-gguf", "input": "Hello local inference" }'

curl -s http://localhost:5062/v1/detokenize \
  -H "Content-Type: application/json" \
  -d '{ "model": "local-gguf", "tokens": [1, 2, 3] }'
```

### Dynamic Pool Resize

Windows PowerShell:

```powershell
$body = @{ max_concurrency = 4 } | ConvertTo-Json

Invoke-RestMethod http://localhost:5062/v1/models/local-gguf/resize `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/models/local-gguf/resize \
  -H "Content-Type: application/json" \
  -d '{ "max_concurrency": 4 }'
```

The response includes the new concurrency and estimated model memory.

### Queue Status

First send an inference request. The response headers include `X-Queue-Entry-Id`. Queue entries exist only while the request is queued or executing; if the request has already completed, the lookup may return not found.

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  messages = @(@{ role = "user"; content = "Write a slow answer." })
  max_tokens = 512
} | ConvertTo-Json -Depth 10

$result = Invoke-WebRequest http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body

$entryId = $result.Headers["X-Queue-Entry-Id"]
Invoke-RestMethod "http://localhost:5062/v1/queue/$entryId"
```

Linux curl:

```bash
curl -i -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "messages": [{ "role": "user", "content": "Write a slow answer." }],
    "max_tokens": 512
  }'

QUEUE_ENTRY_ID=replace_with_x_queue_entry_id
curl -s "http://localhost:5062/v1/queue/$QUEUE_ENTRY_ID"
```

### Request Cancellation

Windows PowerShell:

```powershell
Invoke-RestMethod "http://localhost:5062/v1/chat/completions/chatcmpl_xxx/cancel" -Method Post
Invoke-RestMethod "http://localhost:5062/v1/responses/resp_xxx/cancel" -Method Post
```

Linux curl:

```bash
curl -s -X POST http://localhost:5062/v1/chat/completions/chatcmpl_xxx/cancel
curl -s -X POST http://localhost:5062/v1/responses/resp_xxx/cancel
```

Cancellation works for tracked non-streaming and background executions. A finished or unknown Chat Completion cancel request returns `cancelled = false`; unknown Responses return a not-found error unless a stored response exists.

### Management Endpoints

Chat Completions are stored only when `store = true`.

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  store = $true
  metadata = @{ app = "demo" }
  messages = @(@{ role = "user"; content = "Say hello." })
} | ConvertTo-Json -Depth 10

$chat = Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body

Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)"
Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)/messages"
Invoke-RestMethod http://localhost:5062/v1/chat/completions

$update = @{ metadata = @{ app = "demo-updated" } } | ConvertTo-Json -Depth 10
Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)" `
  -Method Post `
  -ContentType "application/json" `
  -Body $update

Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)" -Method Delete
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "store": true,
    "metadata": { "app": "demo" },
    "messages": [{ "role": "user", "content": "Say hello." }]
  }'

CHAT_ID=chatcmpl_xxx
curl -s "http://localhost:5062/v1/chat/completions/$CHAT_ID"
curl -s "http://localhost:5062/v1/chat/completions/$CHAT_ID/messages"
curl -s http://localhost:5062/v1/chat/completions
curl -s -X POST "http://localhost:5062/v1/chat/completions/$CHAT_ID" \
  -H "Content-Type: application/json" \
  -d '{ "metadata": { "app": "demo-updated" } }'
curl -s -X DELETE "http://localhost:5062/v1/chat/completions/$CHAT_ID"
```

Responses are stored by default. Set `store = false` to skip persistence.

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  input = "Write one sentence about local state."
} | ConvertTo-Json -Depth 10

$response = Invoke-RestMethod http://localhost:5062/v1/responses `
  -Method Post `
  -ContentType "application/json" `
  -Body $body

Invoke-RestMethod "http://localhost:5062/v1/responses/$($response.id)"
Invoke-RestMethod "http://localhost:5062/v1/responses/$($response.id)/input_items"
Invoke-RestMethod "http://localhost:5062/v1/responses/$($response.id)/count_tokens" -Method Post

$next = @{
  model = "local-gguf"
  previous_response_id = $response.id
  input = "Continue from the previous response."
} | ConvertTo-Json -Depth 10

Invoke-RestMethod http://localhost:5062/v1/responses `
  -Method Post `
  -ContentType "application/json" `
  -Body $next

$update = @{ metadata = @{ app = "responses-demo" } } | ConvertTo-Json -Depth 10
Invoke-RestMethod "http://localhost:5062/v1/responses/$($response.id)" `
  -Method Post `
  -ContentType "application/json" `
  -Body $update

Invoke-RestMethod "http://localhost:5062/v1/responses/$($response.id)" -Method Delete
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "input": "Write one sentence about local state."
  }'

RESPONSE_ID=resp_xxx
curl -s "http://localhost:5062/v1/responses/$RESPONSE_ID"
curl -s "http://localhost:5062/v1/responses/$RESPONSE_ID/input_items"
curl -s -X POST "http://localhost:5062/v1/responses/$RESPONSE_ID/count_tokens"
curl -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"local-gguf\",
    \"previous_response_id\": \"$RESPONSE_ID\",
    \"input\": \"Continue from the previous response.\"
  }"
curl -s -X POST "http://localhost:5062/v1/responses/$RESPONSE_ID" \
  -H "Content-Type: application/json" \
  -d '{ "metadata": { "app": "responses-demo" } }'
curl -s -X DELETE "http://localhost:5062/v1/responses/$RESPONSE_ID"
```

Store providers:

| Provider | Configuration |
| --- | --- |
| `Memory` | Default in-process store, lost on restart. |
| `Sqlite` | Set `LLamaStack:Store:SqlitePath`. |
| `Postgres` | Set `LLamaStack:Store:ConnectionString`. |
| `Redis` | Set `LLamaStack:Store:ConnectionString`. |

### Background Responses and Compact Tasks

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  background = $true
  input = "Write a longer answer in the background."
} | ConvertTo-Json -Depth 10

$response = Invoke-RestMethod http://localhost:5062/v1/responses `
  -Method Post `
  -ContentType "application/json" `
  -Body $body

Invoke-RestMethod "http://localhost:5062/v1/responses/$($response.id)"

$compactBody = @{ response_id = $response.id } | ConvertTo-Json
$task = Invoke-RestMethod http://localhost:5062/v1/responses/compact `
  -Method Post `
  -ContentType "application/json" `
  -Body $compactBody

Invoke-RestMethod "http://localhost:5062/v1/responses/tasks/$($task.id)"
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "background": true,
    "input": "Write a longer answer in the background."
  }'

RESPONSE_ID=resp_xxx
curl -s "http://localhost:5062/v1/responses/$RESPONSE_ID"
curl -s http://localhost:5062/v1/responses/compact \
  -H "Content-Type: application/json" \
  -d "{ \"response_id\": \"$RESPONSE_ID\" }"

TASK_ID=task_xxx
curl -s "http://localhost:5062/v1/responses/tasks/$TASK_ID"
```

### Tool Calling

Tool calling is a protocol bridge, not a server-side tool runtime. The client supplies tool definitions, the model emits a tool-call JSON object, the server returns OpenAI-compatible tool-call fields, and the client executes the function.

The service recognizes common model outputs including:

```json
{"name":"calculator","arguments":{"expression":"15 * 37"}}
```

```json
{
  "tool_calls": [
    {
      "id": "call_1",
      "type": "function",
      "function": {
        "name": "calculator",
        "arguments": "{\"expression\":\"15 * 37\"}"
      }
    }
  ]
}
```

Chat Completions tool calling.

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  messages = @(@{ role = "user"; content = "Calculate 15 * 37" })
  tools = @(
    @{
      type = "function"
      function = @{
        name = "calculator"
        description = "Perform arithmetic calculations"
        parameters = @{
          type = "object"
          properties = @{
            expression = @{ type = "string" }
          }
          required = @("expression")
        }
      }
    }
  )
} | ConvertTo-Json -Depth 20

Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "messages": [{ "role": "user", "content": "Calculate 15 * 37" }],
    "tools": [{
      "type": "function",
      "function": {
        "name": "calculator",
        "description": "Perform arithmetic calculations",
        "parameters": {
          "type": "object",
          "properties": {
            "expression": { "type": "string" }
          },
          "required": [ "expression" ]
        }
      }
    }]
  }'
```

Responses tool calling.

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  input = "Calculate (42 + 7) * 3"
  tools = @(
    @{
      type = "function"
      function = @{
        name = "calculator"
        description = "Perform arithmetic calculations"
        parameters = @{
          type = "object"
          properties = @{
            expression = @{ type = "string" }
          }
          required = @("expression")
        }
      }
    }
  )
} | ConvertTo-Json -Depth 20

Invoke-RestMethod http://localhost:5062/v1/responses `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "input": "Calculate (42 + 7) * 3",
    "tools": [{
      "type": "function",
      "function": {
        "name": "calculator",
        "description": "Perform arithmetic calculations",
        "parameters": {
          "type": "object",
          "properties": {
            "expression": { "type": "string" }
          },
          "required": [ "expression" ]
        }
      }
    }]
  }'
```

### Structured Outputs

Chat Completions use `response_format`; Responses use `text.format`. When `type` is `json_schema`, the service attempts grammar-constrained decoding and, in `strict` mode, validates the generated JSON before returning success.

Windows PowerShell:

```powershell
$body = @{
  model = "local-gguf"
  messages = @(@{ role = "user"; content = "Return a city record for Shanghai." })
  response_format = @{
    type = "json_schema"
    json_schema = @{
      name = "city"
      strict = $true
      schema = @{
        type = "object"
        properties = @{
          name = @{ type = "string" }
          country = @{ type = "string" }
        }
        required = @("name", "country")
      }
    }
  }
} | ConvertTo-Json -Depth 20

Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "local-gguf",
    "messages": [
      { "role": "user", "content": "Return a city record for Shanghai." }
    ],
    "response_format": {
      "type": "json_schema",
      "json_schema": {
        "name": "city",
        "strict": true,
        "schema": {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "country": { "type": "string" }
          },
          "required": [ "name", "country" ]
        }
      }
    }
  }'
```

### Multimodal Input

Windows PowerShell:

```powershell
$body = @{
  model = "vision-gguf"
  messages = @(
    @{
      role = "user"
      content = @(
        @{ type = "text"; text = "Describe this image." },
        @{ type = "image_url"; image_url = @{ url = "data:image/png;base64,..." } }
      )
    }
  )
  max_tokens = 256
} | ConvertTo-Json -Depth 20

Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "vision-gguf",
    "messages": [{
      "role": "user",
      "content": [
        { "type": "text", "text": "Describe this image." },
        { "type": "image_url", "image_url": { "url": "data:image/png;base64,..." } }
      ]
    }],
    "max_tokens": 256
  }'
```

Multimodal models need `Models[].MmprojPath` and declared `ImageInput` or `AudioInput` capability. Remote media URLs block localhost, private, link-local, and CGNAT targets; redirects are disabled; downloads are capped by `MaxMediaBytes`.

## Desktop Debug Client

Run the Avalonia client:

```powershell
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
```

Default endpoint:

```text
http://localhost:5062/v1
```

The client can load `/v1/models`, switch between Chat Completions and Responses, stream output, show reasoning/additional response fields, attach image URLs or local images, and persist local UI settings in `%LocalAppData%\Zhengyan.ChatUI.Desktop\settings.json`.

## Deployment

### Publish

```powershell
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj `
  -c Release `
  -o publish\Zhengyan.LLamaStack.Api
```

### Run Published Output

```powershell
$env:LLamaStack__DefaultModel = "local-gguf"
$env:LLamaStack__Models__0__Id = "local-gguf"
$env:LLamaStack__Models__0__ModelPath = "D:\models\model.gguf"
dotnet publish\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.dll --urls http://0.0.0.0:5062
```

### Windows Service

The repository does not currently include Windows Service hosting. A future service deployment would add `Microsoft.Extensions.Hosting.WindowsServices`, call `UseWindowsService()` in `Program.cs`, and register the published app with `sc.exe` or PowerShell.

### Linux systemd

```ini
[Unit]
Description=Zhengyan LLamaStack API
After=network.target

[Service]
WorkingDirectory=/opt/zhengyan-llamastack
ExecStart=/usr/bin/dotnet /opt/zhengyan-llamastack/Zhengyan.LLamaStack.Api.dll --urls http://0.0.0.0:5062
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=LLamaStack__DefaultModel=local-gguf
Environment=LLamaStack__Models__0__Id=local-gguf
Environment=LLamaStack__Models__0__ModelPath=/models/model.gguf

[Install]
WantedBy=multi-user.target
```

### Docker

No Dockerfile exists yet. Future images should keep model files mounted as volumes and use backend-specific base images for CPU, CUDA, or Vulkan.

## GPU Backends

LLamaSharp backend selection is controlled by NuGet package references, not by a runtime config switch. This checkout currently references:

```xml
<PackageReference Include="LLamaSharp.Backend.Cuda12" Version="0.27.0" />
```

Use `GpuLayerCount` to control layer offload for a backend that supports it. For CPU-only, Vulkan, or another target, replace the backend package in `src/Zhengyan.LLamaStack.Api/Zhengyan.LLamaStack.Api.csproj`.

Example:

```powershell
dotnet remove src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Cuda12
dotnet add src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Vulkan --version 0.27.0
```

Then configure offload:

```json
{
  "LLamaStack": {
    "GpuLayerCount": 35
  }
}
```

## OpenAI Compatibility Roadmap

### Implemented

| Capability | Status |
| --- | --- |
| Models | `/v1/models`, load state, capabilities, embedding dimensions, explicit load/unload. |
| Chat Completions | Non-streaming, streaming, `n`, sampling fields, stop strings, store opt-in, management endpoints. |
| Responses | Non-streaming, streaming, stored by default, management endpoints, `previous_response_id`, `conversation`, `background`, compact tasks. |
| Embeddings | Dedicated embedding models plus chat-model fallback, `dimensions` truncation. |
| Tokenization | `/v1/tokenize` and `/v1/detokenize`. |
| Errors | OpenAI-style error envelopes for main protocol failures. |
| Tool calls | `tools` and legacy `functions` parsing, prompt injection, tool-call JSON extraction, tool choice enforcement, parallel-call limiting. |
| Structured outputs | JSON mode, JSON Schema to GBNF where possible, strict schema validation. |
| Multimodal input | Request-level image/audio parsing with data URL, base64, guarded remote URL, and optional local path support. |
| Concurrency | Per-model FIFO queue, pooled contexts, dynamic resize, cancellation tracker, estimated VRAM checks. |
| Storage | Memory, SQLite, PostgreSQL, and Redis providers. |
| Security | Optional API-key auth, configurable header, CORS, redacted logs, guarded media loading. |
| Desktop client | Avalonia debug UI with streaming, model loading, image attachments, Chat/Responses mode. |

### Missing or Partial

| Area | Gap | Possible next step |
| --- | --- | --- |
| Chat log probabilities | `logprobs` and `top_logprobs` are accepted with compatibility warnings but no real logprob output is produced. | Extend generation around logits/logprob access. |
| Multimodal output | Only text output and function-call items are returned. | Add image/audio output model types and generation backends. |
| Audio API | `/v1/audio/transcriptions`, `/v1/audio/translations`, and `/v1/audio/speech` are not implemented. | Add Whisper/Sherpa-ONNX/TTS adapters. |
| Images API | `/v1/images/generations`, edits, and variations are not implemented. | Add Stable Diffusion or ComfyUI adapters. |
| Moderations API | `/v1/moderations` is not implemented. | Add local classifier or rules engine. |
| Files and Uploads | `/v1/files` and `/v1/uploads` are not implemented. | Add file storage, validation, lifecycle, and access control. |
| Vector Stores | Vector stores, file batches, and search endpoints are not implemented. | Add HNSW, SQLite vec, Qdrant, Milvus, or similar backend abstraction. |
| Batch API | `/v1/batches` is not implemented. | Add async job queue, batch parser, status lookup, and output files. |
| Fine-tuning | Fine-tuning jobs, checkpoints, and events are not implemented. | Treat as long-term LoRA/QLoRA orchestration. |
| Realtime API | WebSocket/WebRTC realtime protocols are not implemented. | Build a separate realtime host for bidirectional audio and low-latency output. |
| Legacy Assistants API | Assistants, threads, runs, and run steps are not implemented. | Implement only if compatibility demand is strong. |
| Production controls | Rate limits, organization/project scoping, metrics, tracing, and audit logs are missing. | Add rate limiting and OpenTelemetry/Prometheus support. |
| SDK compatibility | No automated OpenAI SDK compatibility matrix exists yet. | Add end-to-end tests for official .NET, Python, and JavaScript SDKs. |

### Suggested Iteration Order

1. Add Files/Uploads and Vector Stores, because they unblock retrieval workflows.
2. Add rate limiting, metrics, request IDs, token/s, and latency observability.
3. Add SDK compatibility tests across official OpenAI SDKs.
4. Add Audio, Images, and Moderations as independent adapters.
5. Revisit Realtime API and fine-tuning orchestration after the core HTTP surface is stable.

## Development Commands

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj -c Release -o publish\Zhengyan.LLamaStack.Api
```

There are no separate lint, format, code generation, CI, or Docker commands in this repository.

## References

- LLamaSharp: https://github.com/SciSharp/LLamaSharp
- LLamaSharp NuGet: https://www.nuget.org/packages/LLamaSharp
- OpenAI API Reference: https://platform.openai.com/docs/api-reference
