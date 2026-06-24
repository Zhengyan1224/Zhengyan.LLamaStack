# Zhengyan.LLamaStack

[English](README.en-US.md) | 简体中文

`Zhengyan.LLamaStack` 是一个基于 **.NET 10** 和 **LLamaSharp** 的本地大语言模型推理服务。它面向 GGUF 模型，提供 OpenAI 兼容 HTTP API，便于现有 OpenAI SDK、客户端和工具逐步迁移到本地推理环境。

当前版本重点覆盖 `chat/completions`、`responses`、流式 SSE、多模型注册、按 `model` 路由、模型能力声明、工具调用协议解析、多模态输入解析和基础内存管理端点。完整 OpenAI 平台协议仍在持续开发中，详见 [OpenAI 协议兼容计划](#openai-协议兼容计划)。

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
- 支持 OpenAI 风格输入：
  - 纯文本消息
  - Chat Completions `content` 数组
  - Responses API `input`
  - `image_url` / `input_image`
  - `input_audio`
- 支持 data URL、远程 URL 和可选本地文件路径媒体读取。
- 支持 SSE 流式响应。
- 支持进程内 Chat/Responses 存储和基础管理接口；服务重启后内存数据会丢失。
- 支持 `tools` 和 legacy `functions` 请求字段解析。
- 支持将模型输出的工具调用 JSON 转换为 OpenAI 兼容 `tool_calls` / `function_call` 响应字段。
- 支持可选 `mmproj` / MTMD 多模态投影模型。
- 每模型支持可配并发数（`MaxConcurrency`），共享权重，独立上下文/执行器实例。

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
| `Models` | 多模型注册列表。配置后可按请求体 `model` 字段路由到不同 GGUF。 |
| `Models[].Id` | 对外暴露的模型 ID。 |
| `Models[].OwnedBy` | `/v1/models` 中返回的 owner 字段。 |
| `Models[].ModelPath` | 该模型的 GGUF 文件路径。 |
| `Models[].MmprojPath` | 该模型的 mmproj 文件路径。 |
| `Models[].Capabilities` | 模型能力声明，用于 `/v1/models` 返回和请求前置校验。 |
| `MaxConcurrency` | 每个模型的并发推理实例数（默认 `1`）。LLamaWeights 共享，LLamaContext/InteractiveExecutor 隔离。 |

`Models[]` 中未设置的推理参数会继承顶层默认配置。

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

### 管理接口

Chat Completions 只有在请求中显式设置 `store = $true` 时会写入本地内存存储：

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

当前管理接口使用进程内内存存储，适合开发和 SDK 兼容验证；生产部署需要后续替换为 SQLite、PostgreSQL、Redis 或其他持久化后端。

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
| `GET /v1/models` | 已实现多模型列表、加载状态和能力声明返回。 |
| `POST /v1/chat/completions` | 已实现非流式和流式基础推理。 |
| `POST /v1/responses` | 已实现非流式和流式基础推理。 |
| OpenAI 风格错误结构 | 已实现主要业务错误。 |
| 文本 message/content 解析 | 已实现。 |
| 图片和音频输入解析 | 已实现请求解析，需要配置 `MmprojPath`。 |
| `tools` / `functions` 请求解析 | 已实现。 |
| 工具调用响应字段 | 部分实现，依赖模型按约定输出 JSON。 |
| `response_format` / JSON mode | 部分实现，通过 prompt 约束。 |
| usage token 统计 | 部分实现，使用 LLamaSharp tokenizer 估算。 |
| 多模型注册和按 `model` 路由 | 已实现，保留旧版单模型配置兼容。 |
| 模型能力声明 | 已实现，用于 `/v1/models` 和请求前置校验。 |
| Chat 协议字段兼容 | 已接收 `metadata`、`user`、`store`、`service_tier`、`parallel_tool_calls`、`stream_options.include_usage`；不支持的 `logprobs` / `logit_bias` 会返回明确错误。 |
| Chat 管理接口 | 已实现进程内存版本：list、retrieve、update metadata、delete、messages。 |
| Responses 协议字段兼容 | 已接收 `previous_response_id`、`conversation`、`background`、`reasoning`、`metadata`、`truncation`、`include`、`store`、`parallel_tool_calls`；`previous_response_id` 可从本地内存 store 续写上下文。 |
| Responses 管理接口 | 已实现进程内存版本：list、retrieve、delete、cancel、input_items、input_tokens、count_tokens、compact。 |

### 距离完整 OpenAI 协议还缺的能力

| 模块 | 缺口 | 计划 |
| --- | --- | --- |
| Chat Completions | 已接收多数字段并支持 `stream_options.include_usage` 的基础 usage chunk；`logprobs`、`top_logprobs`、`logit_bias` 尚未实现真实采样支持，`parallel_tool_calls` 尚未执行工具。 | 研究 LLamaSharp 是否暴露 logits/logprobs；补充并行工具调用和更完整的流式 usage 统计。 |
| Chat 管理接口 | 已有内存版管理接口，但重启丢失；streaming Chat 响应暂不落库。 | 抽象存储接口并增加 SQLite/PostgreSQL/Redis 实现；保存 streaming 完整输出和分页游标。 |
| Responses API | 已有内存 store 和 `previous_response_id` 上下文续写；`conversation`、`background`、真实取消、截断策略仍是兼容降级。 | 设计持久化 response store、conversation state、后台执行队列、真实取消和上下文截断策略。 |
| Responses 管理接口 | retrieve/delete/cancel/input_items/token count/compact 已有基础内存版；`compact` 当前只生成上下文快照，不做模型总结压缩。 | 增加持久化、任务状态机、模型驱动 compact、精确 token count 和 SDK 兼容分页。 |
| Tool Calling | 已内置 `calculator` 和 `current_time` 两个工具的多轮执行循环；未知工具返回客户端，不执行。 | 增加工具注册表、工具热加载、并行工具调用、超时/权限控制、结构化输出校验和自定义工具接口。 |
| Structured Outputs | 未实现 JSON Schema 强约束解码。 | 接入 llama.cpp/LLamaSharp grammar 或 schema-to-grammar 转换；补充严格 JSON Schema 校验。 |
| 多模态输出 | 当前只支持文本输出，不支持图片、音频等输出。 | 根据 OpenAI 输出 item 类型扩展响应模型；后续集成图像/语音生成模块。 |
| Audio API | `/v1/audio/transcriptions`、`/v1/audio/translations`、`/v1/audio/speech` 未实现。 | 规划接入 Whisper/Sherpa-ONNX/TTS 后端，并提供 OpenAI 兼容格式。 |
| Images API | `/v1/images/generations`、`/v1/images/edits`、`/v1/images/variations` 未实现。 | 规划接入本地图像生成后端，例如 Stable Diffusion/ComfyUI 适配器。 |
| Embeddings API | `/v1/embeddings` 未实现。 | 使用 LLamaSharp embedding 模式或独立 embedding 模型实现向量输出。 |
| Moderations API | `/v1/moderations` 未实现。 | 增加本地安全分类模型或规则引擎。 |
| Files / Uploads | `/v1/files`、`/v1/uploads` 资源接口未实现。 | 增加文件存储、校验、生命周期管理和权限控制。 |
| Vector Stores | vector stores、file batches、search 等接口未实现。 | 增加向量数据库抽象，可接入 HNSW、SQLite vec、Qdrant、Milvus 等。 |
| Batch API | `/v1/batches` 未实现。 | 增加异步任务队列、批量请求解析、状态查询和结果文件。 |
| Fine-tuning | fine-tuning jobs、checkpoints、events 未实现。 | 作为长期计划，优先支持 LoRA/QLoRA 作业编排，而非直接训练大模型。 |
| Realtime API | WebSocket/WebRTC 实时协议未实现。 | 规划独立 realtime host，处理双向音频、增量转写、低延迟输出。 |
| Assistants legacy API | assistants、threads、runs、run steps 等 legacy 接口未实现。 | 视兼容需求决定是否实现；优先级低于 Responses API。 |
| 认证和限流 | 未实现 OpenAI 风格 Bearer key 校验、组织/project、rate limit。 | 增加 API key 配置、请求审计、限流、中间件和多租户字段。 |
| 模型管理 | 已支持多模型注册、默认模型、按 `model` 路由和能力声明；尚不支持运行时热加载/卸载。 | 增加模型热加载/卸载、运行时配置刷新、模型健康检查和能力自动探测。 |
| 并发推理 | 已实现每模型可配并发数（`MaxConcurrency`），共享 LLamaWeights，池化 LLamaContext/InteractiveExecutor。 | 后续增加动态池大小、排队、取消和显存保护。 |
| 可观测性 | 缺少 metrics、trace、结构化审计日志。 | 增加 OpenTelemetry、Prometheus metrics、请求 ID、token/s 延迟指标。 |
| SDK 兼容测试 | 尚未建立 OpenAI SDK 自动化兼容测试矩阵。 | 使用官方 OpenAI .NET/Python/JS SDK 构建端到端兼容测试。 |

### 建议迭代顺序

1. 已完成多模型注册、按 `model` 路由和模型能力声明。
2. 已完成 Chat Completions 和 Responses 的一批协议字段接收、降级和基础回显。
3. 已完成 response/chat 基础内存 store 和管理接口。
4. 增加持久化 store、后台任务状态机、真实取消和模型驱动 compact。
5. 实现工具执行循环和 structured outputs。
6. 实现 Embeddings API。
7. 增加 Files / Uploads 和 Vector Stores。
8. 增加 Audio、Images、Moderations 等独立能力。
9. 实现认证、限流、队列、metrics 和生产部署脚手架。
10. 建立 OpenAI SDK 兼容测试矩阵，并评估 Realtime API 和 fine-tuning 编排。

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
