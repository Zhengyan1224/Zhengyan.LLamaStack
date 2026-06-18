# Zhengyan.LLamaStack

English | [Simplified Chinese](README.md)

`Zhengyan.LLamaStack` is a local large language model inference service built with **.NET 10** and **LLamaSharp**. It runs GGUF models locally and exposes an OpenAI-compatible HTTP API so existing OpenAI SDKs, clients, and tools can gradually move to a local inference runtime.

The current version focuses on `chat/completions`, `responses`, SSE streaming, multi-model registration, `model` routing, model capability declarations, tool-call protocol parsing, multimodal input parsing, and basic in-memory management endpoints. Full OpenAI platform compatibility is still on the roadmap. See [OpenAI Compatibility Roadmap](#openai-compatibility-roadmap).

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
  - `GET /v1/chat/completions`
  - `GET /v1/chat/completions/{completion_id}`
  - `POST /v1/chat/completions/{completion_id}`
  - `DELETE /v1/chat/completions/{completion_id}`
  - `GET /v1/chat/completions/{completion_id}/messages`
  - `POST /v1/responses`
  - `GET /v1/responses`
  - `GET /v1/responses/{response_id}`
  - `DELETE /v1/responses/{response_id}`
  - `POST /v1/responses/{response_id}/cancel`
  - `GET /v1/responses/{response_id}/input_items`
  - `POST /v1/responses/{response_id}/count_tokens`
  - `POST /v1/responses/input_tokens`
  - `POST /v1/responses/compact`
  - `POST /chat/completions`
  - `POST /responses`
  - `GET /health`
- Supports OpenAI-style input:
  - plain text messages
  - Chat Completions content arrays
  - Responses API `input`
  - `image_url` / `input_image`
  - `input_audio`
- Supports media loading from data URLs, remote URLs, and optionally local file paths.
- Supports SSE streaming.
- Supports in-memory Chat/Responses storage and basic management endpoints; stored data is lost after service restart.
- Parses `tools` and legacy `functions`.
- Converts model-emitted tool-call JSON into OpenAI-compatible `tool_calls` / `function_call` response fields.
- Supports optional `mmproj` / MTMD multimodal projection models.

## Project Structure

```text
.
|-- README.md
|-- README.en-US.md
|-- Zhengyan.LLamaStack.slnx
`-- src/
    `-- Zhengyan.LLamaStack.Api/
        |-- Endpoints/
        |-- Inference/
        |-- Infrastructure/
        |-- OpenAi/
        |-- Options/
        |-- Program.cs
        |-- appsettings.json
        `-- Zhengyan.LLamaStack.Api.csproj
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
| `AllowRemoteMedia` | Allow remote image/audio URLs. |
| `AllowLocalMediaPaths` | Allow request bodies to reference local media paths. Keep disabled in production unless needed. |
| `MaxMediaBytes` | Maximum bytes per media input. |
| `Models` | Multi-model registry. Requests are routed by the `model` field. |
| `Models[].Id` | Public model ID. |
| `Models[].OwnedBy` | Owner field returned by `/v1/models`. |
| `Models[].ModelPath` | GGUF file path for that model. |
| `Models[].MmprojPath` | mmproj file path for that model. |
| `Models[].Capabilities` | Model capability declaration used by `/v1/models` and request preflight validation. |

Inference settings omitted from `Models[]` inherit from the top-level defaults.

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

### Management Endpoints

Chat Completions are stored in local memory only when the request explicitly sets `store = $true`:

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

The current management layer uses process memory. It is useful for development and SDK compatibility checks; production deployments should replace it with SQLite, PostgreSQL, Redis, or another durable backend.

### Tool Calling

```json
{
  "model": "local-gguf",
  "messages": [
    {
      "role": "user",
      "content": "What is the weather in Shanghai?"
    }
  ],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "get_weather",
        "description": "Get current weather for a city.",
        "parameters": {
          "type": "object",
          "properties": {
            "city": { "type": "string" }
          },
          "required": [ "city" ]
        }
      }
    }
  ]
}
```

The current version does not execute tools. It injects tool schemas into the prompt and converts model-generated tool-call JSON into OpenAI-compatible response fields. Real tool execution and tool-call loops are on the roadmap.

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
| `GET /v1/models` | Multi-model list, load status, and capability declarations implemented. |
| `POST /v1/chat/completions` | Basic non-streaming and streaming inference implemented. |
| `POST /v1/responses` | Basic non-streaming and streaming inference implemented. |
| OpenAI-style error payloads | Implemented for main service errors. |
| Text message/content parsing | Implemented. |
| Image and audio input parsing | Implemented at request level; requires `MmprojPath`. |
| `tools` / `functions` request parsing | Implemented. |
| Tool-call response fields | Partially implemented; depends on model-emitted JSON. |
| `response_format` / JSON mode | Partially implemented through prompt constraints. |
| usage token counts | Partially implemented with LLamaSharp tokenizer estimation. |
| Multi-model registry and `model` routing | Implemented, with legacy single-model configuration compatibility. |
| Model capability declarations | Implemented for `/v1/models` and request preflight validation. |
| Chat protocol fields | Accepts `metadata`, `user`, `store`, `service_tier`, `parallel_tool_calls`, and `stream_options.include_usage`; unsupported `logprobs` / `logit_bias` return explicit errors. |
| Chat management endpoints | Implemented in process memory: list, retrieve, update metadata, delete, and messages. |
| Responses protocol fields | Accepts `previous_response_id`, `conversation`, `background`, `reasoning`, `metadata`, `truncation`, `include`, `store`, and `parallel_tool_calls`; `previous_response_id` can continue from the local in-memory store. |
| Responses management endpoints | Implemented in process memory: list, retrieve, delete, cancel, input_items, input_tokens, count_tokens, and compact. |

### Missing for Full OpenAI Protocol Coverage

| Area | Gap | Plan |
| --- | --- | --- |
| Chat Completions | Most common fields are accepted and `stream_options.include_usage` emits a basic usage chunk; `logprobs`, `top_logprobs`, and `logit_bias` do not have real sampler support yet, and `parallel_tool_calls` does not execute tools. | Verify LLamaSharp logits/logprobs support; add parallel tool execution and more complete streaming usage accounting. |
| Chat management endpoints | Basic in-memory endpoints are implemented, but data is lost after restart; streaming Chat responses are not stored yet. | Add a storage abstraction with SQLite/PostgreSQL/Redis backends; persist streaming outputs and complete cursor pagination. |
| Responses API | An in-memory store and `previous_response_id` continuation are implemented; `conversation`, `background`, real cancellation, and truncation policy still degrade for compatibility. | Design a durable response store, conversation state, background job queue, real cancellation, and context truncation policy. |
| Responses management endpoints | retrieve/delete/cancel/input_items/token count/compact have basic in-memory implementations; `compact` currently creates a context snapshot and does not run model-based summarization. | Add durable storage, a task state machine, model-driven compact, exact token counts, and SDK-compatible pagination. |
| Tool calling | Current implementation only injects tools into prompts and parses JSON. It does not execute tools or run tool-call loops. | Add a tool registry, tool executors, multi-turn tool loops, timeout/permission controls, parallel calls, and structured output validation. |
| Structured Outputs | Strict JSON Schema constrained decoding is not implemented. | Integrate llama.cpp/LLamaSharp grammar support or schema-to-grammar conversion, plus strict JSON Schema validation. |
| Multimodal output | Only text output is supported. Image/audio output items are not supported. | Extend response output item models and integrate image/audio generation backends. |
| Audio API | `/v1/audio/transcriptions`, `/v1/audio/translations`, and `/v1/audio/speech` are not implemented. | Integrate Whisper/Sherpa-ONNX/TTS backends and expose OpenAI-compatible payloads. |
| Images API | `/v1/images/generations`, `/v1/images/edits`, and `/v1/images/variations` are not implemented. | Add local image generation adapters, such as Stable Diffusion or ComfyUI. |
| Embeddings API | `/v1/embeddings` is not implemented. | Use LLamaSharp embedding mode or dedicated embedding models. |
| Moderations API | `/v1/moderations` is not implemented. | Add a local safety classification model or rules engine. |
| Files / Uploads | `/v1/files` and `/v1/uploads` resources are not implemented. | Add file storage, validation, lifecycle management, and access control. |
| Vector Stores | vector stores, file batches, and search endpoints are not implemented. | Add a vector database abstraction for HNSW, SQLite vec, Qdrant, Milvus, or similar backends. |
| Batch API | `/v1/batches` is not implemented. | Add an async job queue, batch request parser, status lookup, and output files. |
| Fine-tuning | fine-tuning jobs, checkpoints, and events are not implemented. | Treat as a long-term goal; prioritize LoRA/QLoRA orchestration instead of direct large-model training. |
| Realtime API | WebSocket/WebRTC realtime protocols are not implemented. | Plan a separate realtime host for bidirectional audio, incremental transcription, and low-latency output. |
| Legacy Assistants API | assistants, threads, runs, and run steps are not implemented. | Implement only if compatibility demand is strong; prioritize Responses API first. |
| Authentication and rate limits | Bearer key validation, organization/project handling, and rate limits are not implemented. | Add API key configuration, request auditing, rate limiting middleware, and tenant metadata. |
| Model management | Multi-model registration, default model, `model` routing, and capability declarations are implemented; runtime hot load/unload is not implemented yet. | Add model hot load/unload, runtime configuration refresh, model health checks, and automatic capability detection. |
| Concurrent inference | Each loaded model currently uses one context and serializes inference. | Add a context pool, queueing, cancellation, timeouts, concurrency limits, and memory protection. |
| Observability | Metrics, tracing, and structured audit logs are missing. | Add OpenTelemetry, Prometheus metrics, request IDs, token/s, and latency metrics. |
| SDK compatibility tests | No automated OpenAI SDK compatibility matrix exists yet. | Build end-to-end tests with official OpenAI .NET, Python, and JavaScript SDKs. |

### Suggested Iteration Order

1. Completed multi-model registration, `model` routing, and model capability declarations.
2. Completed a first pass of Chat Completions and Responses protocol field parsing, degradation, and basic echoing.
3. Completed the basic in-memory response/chat store and management endpoints.
4. Add a durable store, background task state machine, real cancellation, and model-driven compact.
5. Implement tool execution loops and structured outputs.
6. Implement the Embeddings API.
7. Add Files / Uploads and Vector Stores.
8. Add Audio, Images, Moderations, and other independent capabilities.
9. Add authentication, rate limiting, queueing, metrics, and production deployment scaffolding.
10. Build an OpenAI SDK compatibility test matrix, then evaluate Realtime API and fine-tuning orchestration.

## Development Commands

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

## References

- LLamaSharp: https://github.com/SciSharp/LLamaSharp
- LLamaSharp NuGet: https://www.nuget.org/packages/LLamaSharp
- OpenAI API Reference: https://platform.openai.com/docs/api-reference
