# Zhengyan.LLamaStack

[English](README.en-US.md) | 简体中文

`Zhengyan.LLamaStack` 是一个基于 **.NET 10** 和 **LLamaSharp** 的本地大语言模型推理服务。它面向 GGUF 模型，提供 OpenAI 兼容 HTTP API，便于现有 OpenAI SDK、客户端和工具逐步迁移到本地推理环境。

当前版本覆盖 `chat/completions`、`responses`、`embeddings`、流式 SSE、多模型注册、按 `model` 路由、工具调用执行、结构化输出、Embedding 向量提取、并发推理（排队/动态池/取消/显存保护）、多模态输入解析和持久化存储。详见 [OpenAI 协议兼容计划](#openai-协议兼容计划)。

## 目录

- [特性](#特性)
- [项目结构](#项目结构)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [配置说明](#配置说明)
- [模型准备](#模型准备)
- [运行服务](#运行服务)
- [API 使用示例](#api-使用示例)
- [部署](#部署)
- [GPU 后端](#gpu-后端)
- [OpenAI 协议兼容计划](#openai-协议兼容计划)
- [开发命令](#开发命令)
- [参考资料](#参考资料)

## 特性

- 基于 `.NET 10` 和 `ASP.NET Core Minimal API`。
- 使用 `LLamaSharp 0.27.0` 运行 GGUF 模型。
- 默认集成 `LLamaSharp.Backend.Cpu`，可替换为 CUDA、Vulkan 等后端。
- 支持多模型注册、默认模型和按请求体 `model` 字段路由。
- `/v1/models` 返回模型加载状态和能力声明。
- 提供 OpenAI 兼容接口：
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
- 支持 OpenAI 风格输入：
  - 纯文本消息
  - Chat Completions `content` 数组
  - Responses API `input`
  - `image_url` / `input_image`
  - `input_audio`
- 支持 data URL、远程 URL 和可选本地文件路径媒体读取。
- 支持 SSE 流式响应。
- 支持 Chat/Responses 存储（内存/SQLite/PostgreSQL/Redis）和完整管理接口。
- 支持 `tools` 和 legacy `functions` 请求字段解析与执行。
- 支持内置工具（`calculator`、`current_time`）的多轮自动执行，支持工具注册表、热加载、超时和权限控制。
- 支持将模型输出的工具调用 JSON 转换为 OpenAI 兼容 `tool_calls` / `function_call` 响应字段。
- 支持可选 `mmproj` / MTMD 多模态投影模型。
- 每模型支持可配并发数（`MaxConcurrency`），共享权重，独立上下文/执行器实例；支持运行时动态调整池大小、请求排队、实时取消和显存保护。
- 支持 Embedding 模型独立注册和向量提取，支持 `Dimensions` 截断。
- 支持结构化输出：JSON Schema → GBNF Grammar 约束解码 + 严格模式校验。
- 支持过去响应管理（`previous_response_id` 上下文续写、`conversation` 会话、`compact` 压缩、`background` 后台执行）。
- 支持 API Key 认证和 CORS 跨域配置。

## 项目结构

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

## 环境要求

- Windows、Linux 或 macOS。
- .NET SDK 10.0 或更高版本。
- 一个 GGUF 格式的大语言模型文件。
- 如需多模态输入，需要兼容的 `mmproj` GGUF 文件。

检查 .NET 版本：

```powershell
dotnet --info
```

## 快速开始

1. 还原并构建项目：

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
```

2. 配置模型路径。可以修改 `src/Zhengyan.LLamaStack.Api/appsettings.Development.json`：

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

也可以继续使用旧版单模型配置：

```json
{
  "LLamaStack": {
    "ModelId": "local-gguf",
    "ModelPath": "D:\\models\\your-model.gguf"
  }
}
```

环境变量示例：

```powershell
$env:LLamaStack__DefaultModel = "local-gguf"
$env:LLamaStack__Models__0__Id = "local-gguf"
$env:LLamaStack__Models__0__ModelPath = "D:\models\your-model.gguf"
```

3. 启动服务：

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

默认开发端口是：

```text
http://localhost:5062
```

4. 检查健康状态：

```powershell
Invoke-RestMethod http://localhost:5062/health
```

## 配置说明

配置节名称为 `LLamaStack`。推荐使用 `Models` 注册一个或多个模型；旧版 `ModelId` / `ModelPath` 仍然保留兼容。

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

| 配置项 | 说明 |
| --- | --- |
| `DefaultModel` | 请求未传 `model` 时使用的默认模型 ID。 |
| `ModelId` | 旧版单模型配置的模型 ID；仍会自动注册为默认模型。 |
| `ModelPath` | 旧版单模型 GGUF 路径；未配置时推理接口返回 OpenAI 风格 `503` 错误。 |
| `MmprojPath` | 旧版单模型多模态投影模型路径。 |
| `ContextSize` | 默认上下文窗口大小。 |
| `GpuLayerCount` | 默认卸载到 GPU 的层数。CPU 模式保持 `0`。 |
| `Threads` | 默认推理线程数，`null` 表示使用 LLamaSharp 默认值。 |
| `BatchThreads` | 默认 batch 线程数。 |
| `BatchSize` | 默认 prompt batch size。 |
| `UBatchSize` | 默认 physical batch size。 |
| `UseMemoryMap` | 是否使用 memory map 加载模型。 |
| `UseMemoryLock` | 是否锁定模型内存。 |
| `FlashAttention` | 是否启用 Flash Attention。 |
| `UseGpuForMtmd` | MTMD/mmproj 是否使用 GPU。 |
| `LoadModelOnStartup` | 是否在服务启动时加载模型。默认懒加载。 |
| `DefaultMaxTokens` | 请求未设置 token 上限时使用的默认生成 token 数。 |
| `DefaultTemperature` | 默认 temperature。 |
| `DefaultTopP` | 默认 top_p。 |
| `DefaultTopK` | 默认 top_k。 |
| `AntiPrompts` | LLamaSharp 停止生成的 anti-prompt 列表。 |
| `AllowRemoteMedia` | 是否允许读取远程图片/音频 URL。 |
| `AllowLocalMediaPaths` | 是否允许请求体引用本地媒体路径。生产环境建议关闭。 |
| `MaxMediaBytes` | 单个媒体输入最大字节数。 |
| `MaxVramBytes` | 显存预算上限（字节），`0` 表示不限制。模型加载和池扩容时检查。 |
| `Models` | 多模型注册列表。配置后可按请求体 `model` 字段路由到不同 GGUF。 |
| `Models[].Id` | 对外暴露的模型 ID。 |
| `Models[].OwnedBy` | `/v1/models` 中返回的 owner 字段。 |
| `Models[].ModelPath` | 该模型的 GGUF 文件路径。 |
| `Models[].MmprojPath` | 该模型的 mmproj 文件路径。 |
| `Models[].Capabilities` | 模型能力声明，用于 `/v1/models` 返回和请求前置校验。 |
| `MaxConcurrency` | 每个模型的并发推理实例数（默认 `1`）。LLamaWeights 共享，LLamaContext/InteractiveExecutor 隔离。可通过 `POST /v1/models/{id}/resize` 运行时调整。 |
| `EmbeddingModels` | Embedding 模型独立注册列表（详见下方说明）。 |
| `EmbeddingModels[].Id` | Embedding 模型 ID。 |
| `EmbeddingModels[].ModelPath` | Embedding 模型的 GGUF 文件路径。 |
| `EmbeddingModels[].Dimensions` | 输出向量维度（当模型直接返回该维度时可选）。 |

`Models[]` 中未设置的推理参数会继承顶层默认配置。Embedding 模型支持 `GpuLayerCount`、`Threads`、`BatchSize`、`MaxConcurrency` 等参数。

## 模型准备

LLamaSharp 使用 GGUF 模型。你可以从 Hugging Face 等模型仓库下载已转换的 GGUF 文件，或使用 llama.cpp 工具链自行转换和量化。

推荐选择量化模型，例如 `Q4_K_M`、`Q5_K_M`、`Q6_K` 等，以降低内存占用。多模态模型需要同时准备文本模型 GGUF 和匹配的 `mmproj` GGUF。

## 运行服务

开发运行：

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

指定监听地址：

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
```

## API 使用示例

### 列出模型

```powershell
Invoke-RestMethod http://localhost:5062/v1/models
```

返回内容包含 `loaded` 和 `capabilities`，便于客户端判断模型能力。

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

你也可以使用 Chat 模型做向量提取（无需独立注册 Embedding 模型），服务会自动使用其权重创建 Embedder。

### 动态池调整

`POST /v1/models/{modelId}/resize` 可以在不卸载模型的情况下调整并发实例数：

```powershell
$body = @{ max_concurrency = 4 } | ConvertTo-Json
Invoke-RestMethod http://localhost:5062/v1/models/local-gguf/resize `
  -Method Post `
  -ContentType "application/json" `
  -Body $body
```

返回信息包含新的并发数和内存估算。

### 请求取消

Chat Completions 非流式请求可通过 `POST /v1/chat/completions/{completionId}/cancel` 取消：

```powershell
$body = @{
  model = "local-gguf"
  messages = @(@{ role = "user"; content = "Write a very long story..." })
} | ConvertTo-Json -Depth 10

$chat = Invoke-RestMethod http://localhost:5062/v1/chat/completions `
  -Method Post -ContentType "application/json" -Body $body

# 取消
Invoke-RestMethod "http://localhost:5062/v1/chat/completions/$($chat.id)/cancel" -Method Post
```

`POST /v1/responses/{responseId}/cancel` 同理。

### 管理接口

Chat Completions 只有在请求中显式设置 `store = $true` 时会写入存储：

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

Responses 默认会写入本地内存存储；如果请求中设置 `store = $false`，则不会被后续管理接口检索：

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

管理接口支持多种存储后端，通过 `LLamaStack:Store:Provider` 配置：
- `Memory`（默认，进程内，重启丢失）
- `Sqlite` — 设置 `LLamaStack:Store:SqlitePath`
- `Postgres` — 设置 `LLamaStack:Store:ConnectionString`
- `Redis` — 设置 `LLamaStack:Store:ConnectionString`

### 工具调用

服务内置了 `calculator`（计算器）和 `current_time`（当前时间）两个工具，它们会在 Chat Completions 和 Responses 的多轮调用循环中自动执行。未知工具会被原样返回给客户端，不会执行。

以下是 `calculator` 工具的请求体结构：

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

#### Chat Completions 工具调用

Linux：

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

Windows (CMD / PowerShell with `curl.exe`，先将 JSON 保存到 `body.json`)：

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

#### Responses 工具调用

Linux：

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

Windows (CMD / PowerShell with `curl.exe`)：

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

### 多模态输入

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

多模态模型需要配置 `Models[].MmprojPath`，并声明 `ImageInput` 或 `AudioInput` 能力。

## 部署

### 发布

```powershell
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj `
  -c Release `
  -o publish\Zhengyan.LLamaStack.Api
```

### 运行发布产物

```powershell
$env:LLamaStack__DefaultModel = "local-gguf"
$env:LLamaStack__Models__0__Id = "local-gguf"
$env:LLamaStack__Models__0__ModelPath = "D:\models\model.gguf"
dotnet publish\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.dll --urls http://0.0.0.0:5062
```

### Windows Service

当前项目尚未加入 `Microsoft.Extensions.Hosting.WindowsServices`。如果需要作为 Windows Service 部署，计划步骤是：

1. 添加 Windows Service hosting 包。
2. 在 `Program.cs` 启用 `UseWindowsService()`。
3. 使用 `sc.exe create` 或 PowerShell 注册服务。

### Linux systemd

示例 service 文件：

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

当前仓库尚未提供 Dockerfile。建议后续增加：

- CPU 镜像：基于 .NET ASP.NET Runtime 10。
- CUDA 镜像：基于 NVIDIA CUDA runtime，并切换 LLamaSharp CUDA 后端包。
- 模型目录通过 volume 挂载，不打包进镜像。

## GPU 后端

当前项目默认使用 CPU 后端：

```xml
<PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.27.0" />
```

如果要使用 GPU，请按运行环境选择 LLamaSharp 后端包，例如：

- `LLamaSharp.Backend.Cuda12`
- `LLamaSharp.Backend.Vulkan`
- macOS 可使用 CPU 包中包含的 Metal 支持，具体以 LLamaSharp 官方说明为准。

示例：

```powershell
dotnet remove src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Cpu
dotnet add src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Cuda12 --version 0.27.0
```

然后配置：

```json
{
  "LLamaStack": {
    "GpuLayerCount": 35
  }
}
```

## OpenAI 协议兼容计划

### 当前已实现

| 能力 | 状态 |
| --- | --- |
| `GET /v1/models` | 已实现多模型列表、加载状态、能力声明和 embedding 维度返回。 |
| `POST /v1/chat/completions` | 已实现非流式和流式基础推理。 |
| `POST /v1/responses` | 已实现非流式和流式基础推理。 |
| `POST /v1/embeddings` | 已实现，使用 LLamaEmbedder 提取向量，支持独立 embedding 模型注册和 `Dimensions` 截断。 |
| `POST /v1/tokenize` / `POST /v1/detokenize` | 已实现分词和去分词。 |
| `GET /v1/health` | 已实现健康检查和模型加载状态。 |
| `POST /v1/models/{model_id}/load` / `unload` | 已实现运行时热加载和卸载。 |
| `POST /v1/models/{model_id}/resize` | 已实现运行时动态调整并发池大小，含显存预算检查。 |
| `GET /v1/queue/{entry_id}` | 已实现队列状态查询。 |
| OpenAI 风格错误结构 | 已实现主要业务错误。 |
| 文本 message/content 解析 | 已实现。 |
| 图片和音频输入解析 | 已实现请求解析，需要配置 `MmprojPath`。 |
| `tools` / `functions` 请求解析 | 已实现。 |
| 工具调用执行 | 已实现 `calculator`、`current_time` 内建工具的多轮执行循环，支持 `parallel_tool_calls` 并行执行；支持工具注册表（`IToolRegistry`）、热加载、超时、权限控制、输出校验。 |
| `response_format` / JSON mode | 完整实现：JSON Schema → GBNF Grammar 约束解码 + 严格模式（`ValidateJsonOutput`）递归校验。 |
| usage token 统计 | 完整实现：流式 response_format 包含实时 token 统计，非流式精确计数。 |
| 多模型注册和按 `model` 路由 | 已实现，保留旧版单模型配置兼容。 |
| 模型能力声明 | 已实现，用于 `/v1/models` 和请求前置校验。 |
| Chat 协议字段兼容 | 已接收 `metadata`、`user`、`store`、`service_tier`、`parallel_tool_calls`、`stream_options.include_usage`；不支持的 `logprobs` / `logit_bias` 会返回兼容性警告。 |
| Chat 管理接口 | 已实现（Memory/SQLite/PostgreSQL/Redis 后端）：list、retrieve、update metadata、delete、messages。 |
| Responses 协议字段兼容 | 已接收 `previous_response_id`、`conversation`、`background`、`reasoning`、`metadata`、`truncation`、`include`、`store`、`parallel_tool_calls`；`previous_response_id` 可从存储续写上下文。 |
| Responses 管理接口 | 已实现（Memory/SQLite/PostgreSQL/Redis 后端）：list、retrieve、update metadata、delete、cancel、input_items、input_tokens、count_tokens、compact（含 TASK 状态机）、tasks 查询。 |
| Response 后台执行 | 已实现 `background: true` + `Channel` 后台队列。 |
| 会话（Conversation）管理 | 已实现 `ConversationStore`，支持 `conversation` 字段自动解析 `previous_response_id`。 |
| API key 认证 | 已实现可选 Bearer token 校验中间件 (`LLamaStack:Auth`)。 |
| CORS | 已实现可选跨域配置 (`LLamaStack:Cors`)。 |
| Embedding 模型独立注册 | 通过 `LLamaStack:EmbeddingModels[]` 配置，支持 `Dimensions`、`MaxConcurrency`、GPU 等参数。 |
| 并发推理 | 已实现两阶段并发控制：`ModelRequestQueue`（FIFO 排队）→ `ModelRuntime`（SemaphoreSlim 池）。支持动态池大小调整、请求排队、实时取消（`ResponseExecutionTracker`）和显存保护（`MaxVramBytes`）。 |
| 存储后端 | 支持 Memory、SQLite、PostgreSQL、Redis 四种 Provider。 |

### 距离完整 OpenAI 协议还缺的能力

| 模块 | 缺口 | 计划 |
| --- | --- | --- |
| Chat Completions | `logprobs`、`top_logprobs`、`logit_bias` 尚未实现真实采样支持（需自定义 token 循环）。 | 研究 LLamaSharp 是否暴露 logits/logprobs；扩展采样管道。 |
| 多模态输出 | 当前只支持文本输出，不支持图片、音频等输出。 | 根据 OpenAI 输出 item 类型扩展响应模型；后续集成图像/语音生成模块。 |
| Audio API | `/v1/audio/transcriptions`、`/v1/audio/translations`、`/v1/audio/speech` 未实现。 | 规划接入 Whisper/Sherpa-ONNX/TTS 后端，并提供 OpenAI 兼容格式。 |
| Images API | `/v1/images/generations`、`/v1/images/edits`、`/v1/images/variations` 未实现。 | 规划接入本地图像生成后端，例如 Stable Diffusion/ComfyUI 适配器。 |
| Moderations API | `/v1/moderations` 未实现。 | 增加本地安全分类模型或规则引擎。 |
| Files / Uploads | `/v1/files`、`/v1/uploads` 资源接口未实现。 | 增加文件存储、校验、生命周期管理和权限控制。 |
| Vector Stores | vector stores、file batches、search 等接口未实现。 | 增加向量数据库抽象，可接入 HNSW、SQLite vec、Qdrant、Milvus 等。 |
| Batch API | `/v1/batches` 未实现。 | 增加异步任务队列、批量请求解析、状态查询和结果文件。 |
| Fine-tuning | fine-tuning jobs、checkpoints、events 未实现。 | 作为长期计划，优先支持 LoRA/QLoRA 作业编排，而非直接训练大模型。 |
| Realtime API | WebSocket/WebRTC 实时协议未实现。 | 规划独立 realtime host，处理双向音频、增量转写、低延迟输出。 |
| Assistants legacy API | assistants、threads、runs、run steps 等 legacy 接口未实现。 | 视兼容需求决定是否实现；优先级低于 Responses API。 |
| 认证和限流 | 已实现可选 API key 校验中间件 (`LLamaStack:Auth`)；rate limit、组织/project 未实现。 | 增加限流、请求审计、多租户字段。 |
| 模型管理 | 已支持多模型注册、默认模型、按 `model` 路由、能力声明、运行时热加载/卸载、动态池大小调整。 | 增加运行时配置刷新、模型健康检查和能力自动探测。 |
| 可观测性 | 缺少 metrics、trace、结构化审计日志。 | 增加 OpenTelemetry、Prometheus metrics、请求 ID、token/s 延迟指标。 |
| SDK 兼容测试 | 尚未建立 OpenAI SDK 自动化兼容测试矩阵。 | 使用官方 OpenAI .NET/Python/JS SDK 构建端到端兼容测试。 |

### 建议迭代顺序

1. ✅ 已完成多模型注册、按 `model` 路由和模型能力声明。
2. ✅ 已完成 Chat Completions 和 Responses 的协议字段接收、降级和基础回显。
3. ✅ 已完成 Chat/Response store 和管理接口（含持久化后端）。
4. ✅ 已完成持久化 store、后台任务状态机、真实取消和模型驱动 compact。
5. ✅ 已完成 structured outputs（JSON Schema → GBNF Grammar + 严格校验）。
6. ✅ 已完成 Embedding API（独立模型注册、维度声明、向量提取）。
7. ✅ 已完成并发推理增强（动态池调整、排队、取消、显存保护）。
8. ✅ 已完成工具调用增强（注册表、热加载、超时、权限、输出校验）。
9. 增加 Files / Uploads 和 Vector Stores。
10. 增加 Audio、Images、Moderations 等独立能力。
11. 实现限流、metrics 和生产部署脚手架。
12. 建立 OpenAI SDK 兼容测试矩阵，并评估 Realtime API 和 fine-tuning 编排。

## 开发命令

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

## 参考资料

- LLamaSharp: https://github.com/SciSharp/LLamaSharp
- LLamaSharp NuGet: https://www.nuget.org/packages/LLamaSharp
- OpenAI API Reference: https://platform.openai.com/docs/api-reference
