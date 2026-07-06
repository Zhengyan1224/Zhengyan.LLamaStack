# Zhengyan.LLamaStack

English | [Simplified Chinese](README.md)

`Zhengyan.LLamaStack` is a local large language model inference service built with **.NET 10** and **LLamaSharp**. It runs GGUF models locally and exposes an OpenAI-compatible HTTP API so existing OpenAI SDKs, clients, and tools can gradually move to a local inference runtime.

The current version covers `chat/completions`, `responses`, `embeddings`, SSE streaming, multi-model registration, `model` routing, tool-call execution, structured outputs, embedding vector extraction, concurrent inference (queuing/dynamic pool size/cancellation/VRAM protection), multimodal input parsing, and persistent storage. See [OpenAI Compatibility Roadmap](#openai-compatibility-roadmap).

## Table of Contents

- [Features](#features)
- [Project Structure](#project-structure)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Model Preparation](#model-preparation)
- [Running the Service](#running-the-service)
- [API Examples](#api-examples)
- [Deployment](#deployment)
- [GPU Backends](#gpu-backends)
- [OpenAI Compatibility Roadmap](#openai-compatibility-roadmap)
- [Development Commands](#development-commands)
- [References](#references)

## Features

- Built on `.NET 10` and `ASP.NET Core Minimal API`.
- Runs GGUF models through `LLamaSharp 0.27.0`.
- Uses `LLamaSharp.Backend.Cpu` by default and can be switched to CUDA, Vulkan, or other backends.
- Supports multi-model registration, a default model, and request routing by the `model` field.
- `/v1/models` returns model load state and capability declarations.
- Exposes OpenAI-compatible endpoints:
  - `GET /v1/models`
  - `POST /v1/chat/completions`
  - `POST /v1/responses`
  - `POST /v1/embeddings`
  - `POST /v1/tokenize`
  - `POST /v1/detokenize`
  - `GET /v1/health`
  - `POST /v1/models/{model_id}/load`
  - `POST /v1/models/{model_id}/unload`
  - `GET /v1/queue/{entry_id}`
  - `GET /v1/chat/completions`
  - `GET /v1/chat/completions/{completion_id}`
  - `POST /v1/chat/completions/{completion_id}`
  - `DELETE /v1/chat/completions/{completion_id}`
  - `GET /v1/chat/completions/{completion_id}/messages`
  - `GET /v1/responses`
  - `GET /v1/responses/{response_id}`
  - `POST /v1/responses/{response_id}`
  - `DELETE /v1/responses/{response_id}`
  - `POST /v1/responses/{response_id}/cancel`
  - `GET /v1/responses/{response_id}/input_items`
  - `POST /v1/responses/{response_id}/count_tokens`
  - `POST /v1/responses/input_tokens`
  - `POST /v1/responses/compact`
  - `GET /v1/responses/tasks/{taskId}`
  - `POST /v1/chat/completions/{completionId}/cancel`
  - `POST /v1/models/{modelId}/resize`
  - `POST /chat/completions`
  - `POST /responses`
  - `GET /health`
- Supports OpenAI-style input:
  - plain text messages
  - Chat Completions content arrays
  - Responses API `input`
  - `image_url` / `input_image`
  - `input_audio`
- Supports media loading from data URLs, guarded remote URLs, and optionally local file paths.
- Supports SSE streaming. Tool-enabled streaming is buffered and then emitted as SSE with the same response shape as non-streaming calls.
- Supports Chat/Responses storage (Memory/SQLite/PostgreSQL/Redis) with full management endpoints.
- Parses `tools` and legacy `functions` and returns model-emitted calls in OpenAI-compatible response fields.
- Tool calls are protocol-only: the server tells the client which function to call and with what JSON arguments; clients execute tools and send results back.
- Converts model-emitted tool-call JSON into OpenAI-compatible `tool_calls` / `function_call` response fields.
- Supports optional `mmproj` / MTMD multimodal projection models.
- Per-model configurable concurrency (`MaxConcurrency`), shared weights, isolated context/executor instances; runtime dynamic pool resizing, request queuing, real-time cancellation, and VRAM protection.
- Independent embedding model registration and vector extraction with `Dimensions` truncation support.
- Structured outputs: JSON Schema 鈫?GBNF Grammar constrained decoding + strict mode validation.
- Past-Responses management (`previous_response_id` context continuation, `conversation` sessions, `compact` compression, `background` execution).
- API Key authentication and CORS configuration.

## Project Structure

```text
.
|-- README.md
|-- README.en-US.md
|-- Zhengyan.LLamaStack.slnx
|-- src/
|   |-- Zhengyan.LLamaStack.Api/
|   |   |-- Endpoints/
|   |   |-- Inference/
|   |   |-- Infrastructure/
|   |   |-- OpenAi/
|   |   |-- Options/
|   |   |-- Storage/
|   |   |-- Program.cs
|   |   `-- appsettings.json
|   |-- Zhengyan.OpenAIModels/
|   `-- Zhengyan.ChatUI.Desktop/
`-- tests/
    `-- Zhengyan.LLamaStack.Tests/
```

## Requirements

- Windows, Linux, or macOS.
- .NET SDK 10.0 or later.
- A GGUF large language model file.
- A compatible `mmproj` GGUF file for multimodal input.

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

2. Configure the model path. You can edit `src/Zhengyan.LLamaStack.Api/appsettings.Development.json`:

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

Legacy single-model configuration is still supported:

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

3. Start the service:

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

The default development URL is:

```text
http://localhost:5062
```

4. Check health:

```powershell
Invoke-RestMethod http://localhost:5062/health
```

## Configuration

The configuration section is `LLamaStack`. The recommended configuration style is `Models`, which registers one or more models. The legacy `ModelId` / `ModelPath` configuration remains compatible.

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
    "DefaultMaxTokens": 512,
    "DefaultTemperature": 0.7,
    "DefaultTopP": 0.95,
    "DefaultTopK": 40,
    "AntiPrompts": [ "<|im_end|>", "</s>" ],
    "AllowRemoteMedia": true,
    "AllowLocalMediaPaths": false,
    "MaxMediaBytes": 33554432,
    "MaxVramBytes": 0,
    "Models": [
      {
        "Id": "local-gguf",
        "OwnedBy": "local",
        "ModelPath": "D:\\models\\model.gguf",
        "MmprojPath": "",
        "ContextSize": 4096,
        "GpuLayerCount": 0,
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
      },
      {
        "Id": "vision-gguf",
        "OwnedBy": "local",
        "ModelPath": "D:\\models\\vision-model.gguf",
        "MmprojPath": "D:\\models\\mmproj.gguf",
        "Capabilities": {
          "ImageInput": true,
          "AudioInput": true
        }
      }
    ]
  }
}
```

| Key | Description |
| --- | --- |
| `DefaultModel` | Default model ID used when the request omits `model`. |
| `ModelId` | Legacy single-model model ID; still auto-registers as the default model. |
| `ModelPath` | Legacy single-model GGUF path. Inference endpoints return an OpenAI-style `503` error when it is missing. |
| `MmprojPath` | Legacy single-model multimodal projection model path. |
| `ContextSize` | Default context window size. |
| `GpuLayerCount` | Default number of layers to offload to GPU. Use `0` for CPU mode. |
| `Threads` | Default inference thread count. `null` uses LLamaSharp defaults. |
| `BatchThreads` | Default batch thread count. |
| `BatchSize` | Default prompt batch size. |
| `UBatchSize` | Default physical batch size. |
| `UseMemoryMap` | Whether to load the model with memory mapping. |
| `UseMemoryLock` | Whether to lock model memory. |
| `FlashAttention` | Whether to enable Flash Attention. |
| `UseGpuForMtmd` | Whether MTMD/mmproj should use GPU. |
| `LoadModelOnStartup` | Load the model during service startup. The default is lazy loading. |
| `DefaultMaxTokens` | Default generation token limit when the request does not set one. |
| `DefaultTemperature` | Default temperature. |
| `DefaultTopP` | Default top_p. |
| `DefaultTopK` | Default top_k. |
| `AntiPrompts` | LLamaSharp anti-prompts used to stop generation. |
| `AllowRemoteMedia` | Allow remote image/audio URLs. Remote URLs are blocked for localhost/private/link-local/CGNAT targets, redirects are disabled, and downloads are limited by `MaxMediaBytes`. |
| `AllowLocalMediaPaths` | Allow request bodies to reference local media paths. Keep disabled in production unless needed. |
| `MaxMediaBytes` | Maximum bytes per media input. |
| `MaxVramBytes` | VRAM budget limit in bytes. `0` means unlimited. Checked before model loading and pool resize. |
| `Models` | Multi-model registry. Requests are routed by the `model` field. |
| `Models[].Id` | Public model ID. |
| `Models[].OwnedBy` | Owner field returned by `/v1/models`. |
| `Models[].ModelPath` | GGUF file path for that model. |
| `Models[].MmprojPath` | mmproj file path for that model. |
| `Models[].Capabilities` | Model capability declaration used by `/v1/models` and request preflight validation. |
| `MaxConcurrency` | Number of concurrent inference instances per model (default `1`). LLamaWeights are shared; LLamaContext/InteractiveExecutor are isolated. Can be changed at runtime via `POST /v1/models/{id}/resize`. |
| `EmbeddingModels` | Independent embedding model registry (see details below). |
| `EmbeddingModels[].Id` | Embedding model ID. |
| `EmbeddingModels[].ModelPath` | GGUF file path for the embedding model. |
| `EmbeddingModels[].Dimensions` | Output embedding vector dimension (optional if the model declares it natively). |

Inference settings omitted from `Models[]` inherit from the top-level defaults. Embedding models support `GpuLayerCount`, `Threads`, `BatchSize`, `MaxConcurrency`, etc.

## Model Preparation

LLamaSharp uses GGUF models. You can download pre-converted GGUF files from model hubs such as Hugging Face, or convert and quantize models yourself with the llama.cpp toolchain.

Quantized models such as `Q4_K_M`, `Q5_K_M`, or `Q6_K` are recommended to reduce memory usage. Multimodal models require both the text model GGUF and the matching `mmproj` GGUF.

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

### List Models

```powershell
Invoke-RestMethod http://localhost:5062/v1/models
```

The response includes `loaded` and `capabilities`, allowing clients to inspect model state and supported features.

### Chat Completions

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

### Chat Completions Streaming

```powershell
$body = @{
  model = "local-gguf"
  stream = $true
  messages = @(
    @{ role = "user"; content = "Write a short haiku about local inference." }
  )
} | ConvertTo-Json -Depth 10

Invoke-WebRequest http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

### Responses API

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

### Embeddings

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

You can also use a Chat model for embeddings without registering a dedicated embedding model. The service will create a one-shot embedder from the chat model's weights.

### Dynamic Pool Resize

`POST /v1/models/{modelId}/resize` adjusts concurrency without unloading:

```powershell
$body = @{ max_concurrency = 4 } | ConvertTo-Json
Invoke-RestMethod http://localhost:5062/v1/models/local-gguf/resize `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

The response includes the new concurrency count and estimated memory usage.

### Request Cancellation

Non-streaming Chat Completions can be cancelled via `POST /v1/chat/completions/{completionId}/cancel`:

```powershell
$body = @{
  model = "local-gguf"
  messages = @(@{ role = "user"; content = "Write a very long story..." })
} | ConvertTo-Json -Depth 10

$chat = Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post -ContentType "application/json" -Body $body

# Cancel
Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)/cancel" -Method Post
```

`POST /v1/responses/{responseId}/cancel` works the same way.

### Management Endpoints

Chat Completions are stored only when the request explicitly sets `store = $true`:

```powershell
$body = @{
  model = "local-gguf"
  store = $true
  metadata = @{ app = "demo" }
  messages = @(
    @{ role = "user"; content = "Say hello." }
  )
} | ConvertTo-Json -Depth 10

$chat = Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post `
  -ContentType "application/json" `
  -Body $body

Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)"
Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)/messages"
Invoke-RestMethod http://localhost:5062/v1/chat/completions
```

Responses are stored in local memory by default. Set `store = $false` to skip storage and make the response unavailable to later management calls:

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
```

The management layer supports multiple storage backends via `LLamaStack:Store:Provider`:
- `Memory` (default, in-process, lost on restart)
- `Sqlite` 鈥?set `LLamaStack:Store:SqlitePath`
- `Postgres` 鈥?set `LLamaStack:Store:ConnectionString`
- `Redis` 鈥?set `LLamaStack:Store:ConnectionString`

### Tool Calling

Streaming requests that include tools are buffered and then emitted as SSE final output, so `stream: true` and `stream: false` keep the same response shape.

Tool calling is protocol-only. The service injects request-provided `tools` / legacy `functions` into the model prompt, parses model-emitted tool-call JSON, and returns standard OpenAI-compatible `tool_calls` / `function_call` output. The client is responsible for executing the tool and sending the tool result back in a follow-up request.

The server only accepts tool calls for functions declared in the current request. `tool_choice: "none"` suppresses tool-call extraction, a specific function choice restricts calls to that function, and `parallel_tool_calls: false` returns at most one call.

Request body structure for the `calculator` tool:

```json
{
  "model": "qwen3.5-0.8b",
  "messages": [
    {
      "role": "user",
      "content": "Calculate 15 * 37"
    }
  ],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "calculator",
        "description": "Perform arithmetic calculations",
        "parameters": {
          "type": "object",
          "properties": {
            "expression": { "type": "string", "description": "Math expression to evaluate" }
          },
          "required": [ "expression" ]
        }
      }
    }
  ]
}
```

#### Chat Completions with Tool Calling

Linux:

```bash
curl -s http://localhost:5062/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "qwen3.5-0.8b",
    "messages": [{"role": "user", "content": "Tell me what time it is now."}],
    "tools": [{
      "type": "function",
      "function": {
        "name": "current_time",
        "description": "Get the current time for a timezone",
        "parameters": {
          "type": "object",
          "properties": {
            "timezone": { "type": "string", "description": "Timezone (e.g. Asia/Shanghai)" }
          }
        }
      }
    }]
  }' | jq .
```

Windows (CMD / PowerShell with `curl.exe`, save the JSON as `body.json` first):

```json
{
  "model": "qwen3.5-0.8b",
  "messages": [{"role": "user", "content": "Calculate 15 * 37"}],
  "tools": [{
    "type": "function",
    "function": {
      "name": "calculator",
      "description": "Perform arithmetic calculations",
      "parameters": {
        "type": "object",
        "properties": {
          "expression": { "type": "string", "description": "Math expression" }
        },
        "required": ["expression"]
      }
    }
  }]
}
```

```powershell
curl.exe -s http://localhost:5062/v1/chat/completions -H "Content-Type: application/json" -d "@body.json"
```

#### Responses with Tool Calling

Linux:

```bash
curl -s http://localhost:5062/v1/responses \
  -H "Content-Type: application/json" \
  -d '{
    "model": "qwen3.5-0.8b",
    "input": "What time is it in Tokyo?",
    "tools": [{
      "type": "function",
      "function": {
        "name": "current_time",
        "description": "Get the current time for a timezone",
        "parameters": {
          "type": "object",
          "properties": {
            "timezone": { "type": "string", "description": "Timezone (e.g. Asia/Tokyo)" }
          }
        }
      }
    }]
  }' | jq .
```

Windows (CMD / PowerShell with `curl.exe`):

```json
{
  "model": "qwen3.5-0.8b",
  "input": "Calculate (42 + 7) * 3",
  "tools": [{
    "type": "function",
    "function": {
      "name": "calculator",
      "description": "Perform arithmetic calculations",
      "parameters": {
        "type": "object",
        "properties": {
          "expression": { "type": "string", "description": "Math expression" }
        },
        "required": ["expression"]
      }
    }
  }]
}
```

```powershell
curl.exe -s http://localhost:5062/v1/responses -H "Content-Type: application/json" -d "@body.json"
```

### Multimodal Input

```json
{
  "model": "vision-gguf",
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "Describe this image." },
        { "type": "image_url", "image_url": { "url": "data:image/png;base64,..." } }
      ]
    }
  ],
  "max_tokens": 256
}
```

Multimodal models require `Models[].MmprojPath` and declared `ImageInput` or `AudioInput` capabilities.

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

The project does not yet include `Microsoft.Extensions.Hosting.WindowsServices`. To deploy as a Windows Service, the planned steps are:

1. Add the Windows Service hosting package.
2. Enable `UseWindowsService()` in `Program.cs`.
3. Register the service with `sc.exe create` or PowerShell.

### Linux systemd

Example service file:

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

This repository does not yet include a Dockerfile. Recommended future images:

- CPU image based on the .NET ASP.NET Runtime 10 image.
- CUDA image based on an NVIDIA CUDA runtime image with the LLamaSharp CUDA backend.
- Model files mounted through volumes instead of being copied into the image.

## GPU Backends

The project uses the CPU backend by default:

```xml
<PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.27.0" />
```

Choose a LLamaSharp backend package for your target environment, for example:

- `LLamaSharp.Backend.Cuda12`
- `LLamaSharp.Backend.Vulkan`
- macOS can use Metal support included in the CPU package, depending on LLamaSharp's official guidance.

Example:

```powershell
dotnet remove src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Cpu
dotnet add src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Cuda12 --version 0.27.0
```

Then configure:

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
| `GET /v1/models` | Multi-model list, load status, capability declarations, and embedding dimensions implemented. |
| `POST /v1/chat/completions` | Basic non-streaming and streaming inference implemented. |
| `POST /v1/responses` | Basic non-streaming and streaming inference implemented. |
| `POST /v1/embeddings` | Implemented via LLamaEmbedder; independent embedding model registration and `Dimensions` truncation supported. |
| `POST /v1/tokenize` / `POST /v1/detokenize` | Tokenization and detokenization implemented. |
| `GET /v1/health` | Health check with model load status. |
| `POST /v1/models/{model_id}/load` / `unload` | Runtime model hot load and unload implemented. |
| `POST /v1/models/{model_id}/resize` | Runtime dynamic pool resize with VRAM budget check implemented. |
| `GET /v1/queue/{entry_id}` | Queue status lookup implemented. |
| OpenAI-style error payloads | Implemented for main service errors. |
| Text message/content parsing | Implemented. |
| Image and audio input parsing | Implemented at request level; requires `MmprojPath`. |
| `tools` / `functions` request parsing | Implemented. |
| Tool-call protocol | `tools` / legacy `functions` are parsed; model-emitted calls are returned as OpenAI-compatible `tool_calls` / `function_call` output for the client to execute. `tool_choice` and `parallel_tool_calls` are enforced during extraction. |
| Strict JSON validation | Strict schema validation failures return an OpenAI-format error instead of silently accepting invalid model output. |
| `response_format` / JSON mode | Fully implemented: JSON Schema 鈫?GBNF Grammar constrained decoding + strict mode (`ValidateJsonOutput`) recursive validation. |
| usage token counts | Fully implemented: streaming includes real-time token counts, non-streaming provides exact counts. |
| Multi-model registry and `model` routing | Implemented, with legacy single-model configuration compatibility. |
| Model capability declarations | Implemented for `/v1/models` and request preflight validation. |
| Chat protocol fields | Accepts `metadata`, `user`, `store`, `service_tier`, `parallel_tool_calls`, and `stream_options.include_usage`; unsupported `logprobs` / `logit_bias` return compatibility warnings. |
| Chat management endpoints | Implemented (Memory/SQLite/PostgreSQL/Redis backends): list, retrieve, update metadata, delete, messages. |
| Responses protocol fields | Accepts `previous_response_id`, `conversation`, `background`, `reasoning`, `metadata`, `truncation`, `include`, `store`, and `parallel_tool_calls`; `previous_response_id` continues context from store. |
| Responses management endpoints | Implemented (Memory/SQLite/PostgreSQL/Redis backends): list, retrieve, update metadata, delete, cancel, input_items, input_tokens, count_tokens, compact (with TASK state machine), tasks query. |
| Response background execution | Implemented via `background: true` + `Channel`-based background queue. |
| Conversation management | `ConversationStore` implemented; `conversation` field auto-resolves `previous_response_id`. |
| API key authentication | Optional Bearer token middleware implemented (`LLamaStack:Auth`). |
| CORS | Optional cross-origin configuration implemented (`LLamaStack:Cors`). |
| Embedding model registration | Via `LLamaStack:EmbeddingModels[]` config; supports `Dimensions`, `MaxConcurrency`, GPU layers, etc. |
| Concurrent inference | Two-tier concurrency control: `ModelRequestQueue` (FIFO) 鈫?`ModelRuntime` (SemaphoreSlim pool). Dynamic pool resize, request queuing, real-time cancellation (`ResponseExecutionTracker`), and VRAM protection (`MaxVramBytes`). |
| Storage backends | Supports Memory, SQLite, PostgreSQL, and Redis providers. |

### Missing for Full OpenAI Protocol Coverage

| Area | Gap | Plan |
| --- | --- | --- |
| Chat Completions | `logprobs`, `top_logprobs`, and `logit_bias` do not have real sampler support (requires custom token loop). | Verify LLamaSharp logits/logprobs exposure; extend the sampling pipeline. |
| Multimodal output | Only text output is supported. Image/audio output items are not supported. | Extend response output item models and integrate image/audio generation backends. |
| Audio API | `/v1/audio/transcriptions`, `/v1/audio/translations`, and `/v1/audio/speech` are not implemented. | Integrate Whisper/Sherpa-ONNX/TTS backends and expose OpenAI-compatible payloads. |
| Images API | `/v1/images/generations`, `/v1/images/edits`, and `/v1/images/variations` are not implemented. | Add local image generation adapters, such as Stable Diffusion or ComfyUI. |
| Moderations API | `/v1/moderations` is not implemented. | Add a local safety classification model or rules engine. |
| Files / Uploads | `/v1/files` and `/v1/uploads` resources are not implemented. | Add file storage, validation, lifecycle management, and access control. |
| Vector Stores | vector stores, file batches, and search endpoints are not implemented. | Add a vector database abstraction for HNSW, SQLite vec, Qdrant, Milvus, or similar backends. |
| Batch API | `/v1/batches` is not implemented. | Add an async job queue, batch request parser, status lookup, and output files. |
| Fine-tuning | fine-tuning jobs, checkpoints, and events are not implemented. | Treat as a long-term goal; prioritize LoRA/QLoRA orchestration instead of direct large-model training. |
| Realtime API | WebSocket/WebRTC realtime protocols are not implemented. | Plan a separate realtime host for bidirectional audio, incremental transcription, and low-latency output. |
| Legacy Assistants API | assistants, threads, runs, and run steps are not implemented. | Implement only if compatibility demand is strong; prioritize Responses API first. |
| Authentication and rate limits | Optional API key middleware implemented (`LLamaStack:Auth`); rate limits, organization/project not implemented. | Add rate limiting, request auditing, and tenant metadata. |
| Model management | Multi-model registration, default model, `model` routing, capability declarations, runtime hot load/unload, and dynamic pool resize implemented. | Add runtime configuration refresh, model health checks, and automatic capability detection. |
| Observability | Metrics, tracing, and structured audit logs are missing. | Add OpenTelemetry, Prometheus metrics, request IDs, token/s, and latency metrics. |
| SDK compatibility tests | No automated OpenAI SDK compatibility matrix exists yet. | Build end-to-end tests with official OpenAI .NET, Python, and JavaScript SDKs. |

### Suggested Iteration Order

1. 鉁?Completed multi-model registration, `model` routing, and model capability declarations.
2. 鉁?Completed Chat Completions and Responses protocol field parsing, degradation, and echoing.
3. 鉁?Completed Chat/Response store and management endpoints (with durable backends).
4. 鉁?Completed durable store, background task state machine, real cancellation, and model-driven compact.
5. 鉁?Completed structured outputs (JSON Schema 鈫?GBNF Grammar + strict validation).
6. 鉁?Completed Embedding API (independent registration, dimension declaration, vector extraction).
7. 鉁?Completed concurrent inference enhancements (dynamic pool resize, queuing, cancellation, VRAM protection).
8. 鉁?Completed tool-calling enhancements (registry, hot-load, timeout, permissions, output validation).
9. Add Files / Uploads and Vector Stores.
10. Add Audio, Images, Moderations, and other independent capabilities.
11. Add rate limiting, metrics, and production deployment scaffolding.
12. Build an OpenAI SDK compatibility test matrix, then evaluate Realtime API and fine-tuning orchestration.

## Development Commands

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
```

## References

- LLamaSharp: https://github.com/SciSharp/LLamaSharp
- LLamaSharp NuGet: https://www.nuget.org/packages/LLamaSharp
- OpenAI API Reference: https://platform.openai.com/docs/api-reference
