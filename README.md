# Zhengyan.LLamaStack

[English](README.en-US.md) | 简体中文

`Zhengyan.LLamaStack` 是一个面向 GGUF 模型的本地 OpenAI 兼容推理栈。项目基于 .NET 10、ASP.NET Core Minimal API 和 LLamaSharp 0.27.0 构建，可以加载本地 GGUF 聊天模型、视觉/音频多模态模型和 Embedding 模型，并向现有 SDK 与调试客户端暴露 OpenAI 风格 HTTP 接口。

当前代码已经覆盖 Chat Completions、Responses、Embeddings、分词、SSE 流式输出、多模型路由、懒加载和显式加载/卸载、按模型排队、运行时池大小调整、工具调用协议解析、JSON Schema 结构化输出、受保护的多模态输入读取、持久化存储、API Key 认证、CORS，以及 Avalonia 桌面调试客户端。

## 目录

- [主要能力](#主要能力)
- [项目逻辑](#项目逻辑)
- [项目结构](#项目结构)
- [运行要求](#运行要求)
- [快速开始](#快速开始)
- [配置](#配置)
- [模型准备](#模型准备)
- [运行服务](#运行服务)
- [API 示例](#api-示例)
- [桌面调试客户端](#桌面调试客户端)
- [部署](#部署)
- [GPU 后端](#gpu-后端)
- [OpenAI 兼容路线图](#openai-兼容路线图)
- [开发命令](#开发命令)
- [参考](#参考)

## 主要能力

- .NET 10 ASP.NET Core Minimal API 服务，底层使用 LLamaSharp 0.27.0。
- OpenAI 风格 JSON，属性使用 snake_case，并忽略 null 值。
- 通过 `LLamaStack:Models[]` 注册多模型，同时保留 `ModelId` / `ModelPath` 旧式回退配置。
- 通过 `LLamaStack:EmbeddingModels[]` 独立注册 Embedding 模型。
- 默认懒加载模型，也支持启动时预热和运行时显式加载/卸载。
- `/v1/models` 暴露模型能力声明，推理前也会按能力做校验。
- 每个模型有外层 FIFO 请求队列，内层是共享 `LLamaWeights` 的多个独立 `LLamaContext` / `InteractiveExecutor` 实例。
- 通过 `POST /v1/models/{modelId}/resize` 运行时调整模型池，并做估算 VRAM 预算检查。
- Chat Completions 和 Responses 支持 SSE 流式输出。带工具的流式请求会先缓冲，再输出最终 SSE 事件，以保持流式和非流式响应形状一致。
- 工具调用是协议透传，不是服务端工具运行时。服务端注入请求声明的 `tools` / legacy `functions`，解析模型生成的 JSON，并返回 OpenAI 兼容的 `tool_calls` / `function_call`，由客户端执行工具。
- 解析工具调用时会执行 `tool_choice: none`、指定函数选择和 `parallel_tool_calls: false`。
- 支持结构化输出：尽量把 JSON Schema 转换为 GBNF 做约束解码，并在 strict 模式下做生成后校验。
- 支持文本、图片和音频输入块。媒体可以来自 data URL、原始 base64、受保护的远程 URL，或显式开启后的本地文件路径。
- Chat 和 Responses 管理端点支持 Memory、SQLite、PostgreSQL、Redis 存储。
- Responses 支持 `previous_response_id`、内存型 `conversation` 续接、`background` 后台执行、取消、token 统计和 compact 任务。
- 可选 API Key 认证和 CORS 配置。
- 日志会脱敏 prompt、message 内容、媒体 URL/data、embedding、生成文本和 API Key。
- 提供 Avalonia 桌面客户端用于本地手动调试。

### 端点概览

| 端点 | 用途 |
| --- | --- |
| `GET /` | 服务描述。 |
| `GET /health` | 简单健康检查别名。 |
| `GET /v1/health` | OpenAI 风格健康信息，包含模型加载状态和 uptime。 |
| `GET /v1/models` | 模型列表、加载状态、路径、能力和 embedding 维度。 |
| `POST /v1/models/{modelId}/load` | 运行时加载模型。 |
| `POST /v1/models/{modelId}/unload` | 运行时卸载模型。 |
| `POST /v1/models/{modelId}/resize` | 调整已加载模型池大小。 |
| `GET /v1/queue/{entryId}` | 查询队列项状态。 |
| `POST /v1/chat/completions` | Chat Completions 推理。 |
| `POST /chat/completions` | Chat Completions 兼容别名。 |
| `GET /v1/chat/completions` | 列出已存储的 Chat Completions。 |
| `GET /v1/chat/completions/{completionId}` | 获取已存储的 Chat Completion。 |
| `POST /v1/chat/completions/{completionId}` | 更新已存储 Chat Completion 的 metadata。 |
| `DELETE /v1/chat/completions/{completionId}` | 删除已存储的 Chat Completion。 |
| `GET /v1/chat/completions/{completionId}/messages` | 列出已存储 Chat Completion 的消息。 |
| `POST /v1/chat/completions/{completionId}/cancel` | 取消正在跟踪的非流式 Chat Completion。 |
| `POST /v1/responses` | Responses API 推理。 |
| `POST /responses` | Responses 兼容别名。 |
| `GET /v1/responses` | 列出已存储的 Responses。 |
| `GET /v1/responses/{responseId}` | 获取已存储的 Response。 |
| `POST /v1/responses/{responseId}` | 更新已存储 Response 的 metadata。 |
| `DELETE /v1/responses/{responseId}` | 删除已存储的 Response。 |
| `POST /v1/responses/{responseId}/cancel` | 取消正在执行或已存储的 Response。 |
| `GET /v1/responses/{responseId}/input_items` | 列出已存储 Response 的输入项。 |
| `POST /v1/responses/{responseId}/count_tokens` | 统计已存储 Response 的 token。 |
| `POST /v1/responses/input_tokens` | 估算 Responses 请求的输入 token。 |
| `POST /v1/responses/compact` | 为已存储 Response 调度模型驱动的 compact 任务。 |
| `GET /v1/responses/tasks/{taskId}` | 查询 compact 任务状态。 |
| `POST /v1/embeddings` | 生成 embeddings。 |
| `POST /v1/tokenize` | 使用配置模型分词。 |
| `POST /v1/detokenize` | 使用配置模型把 token ID 还原为文本。 |

## 项目逻辑

服务启动入口在 `src/Zhengyan.LLamaStack.Api/Program.cs`。启动时会绑定 `LLamaStack` 配置、设置 JSON 命名策略、注册存储提供者、请求映射器、推理服务、队列管理器、conversation store、取消跟踪器、后台 Response worker、compact scheduler、模型预热服务、异常处理器、API Key 中间件和路由。

请求流转：

1. `OpenAiCompatibleEndpoints` 接收 OpenAI 风格请求，并记录脱敏后的请求形状。
2. `OpenAiRequestMapper` 把 Chat Completions 或 Responses payload 转成 `InferenceRequest`，包括内容数组、媒体块、工具、tool choice、JSON mode、采样参数、metadata 和兼容性 warning。
3. `LLamaInferenceService.ValidateRequest` 校验模型存在性、声明能力、模型路径、可选 `mmproj` 路径、streaming、tools、JSON mode 和媒体能力。
4. `ModelRequestQueue` 为每个模型做 FIFO 准入控制。响应头会包含 `X-Queue-Position` 和 `X-Queue-Entry-Id`。
5. `LLamaInferenceService` 懒加载目标 runtime，从池中取一个独立 context/executor，构建 prompt，可选附加媒体，设置 sampler 和 grammar，执行流式或非流式生成，解析工具调用 JSON，校验 strict JSON schema 输出，统计 token，并释放实例。
6. `OpenAiResponseFactory` 把内部 completion 或已存储对象转换成 OpenAI 兼容 JSON 或 SSE 事件。带 store 的端点负责持久化和读取紧凑的本地状态。

API 不会在服务端执行工具。客户端必须执行返回的 function call，并把工具结果作为 tool message 或 Responses `function_call_output` 再发回服务。

## 项目结构

```text
.
|-- README.md
|-- README.en-US.md
|-- Zhengyan.LLamaStack.slnx
|-- src/
|   |-- Zhengyan.LLamaStack.Api/
|   |   |-- Endpoints/        # HTTP 路由处理
|   |   |-- Inference/        # LLamaSharp runtime、队列、后台任务
|   |   |-- Infrastructure/   # API Key 中间件和 OpenAI 错误
|   |   |-- OpenAi/           # 请求映射、响应工厂、API contracts
|   |   |-- Options/          # LLamaStack 配置模型
|   |   |-- Storage/          # Memory、SQLite、PostgreSQL、Redis 存储
|   |   |-- Program.cs
|   |   `-- appsettings.json
|   |-- Zhengyan.OpenAIModels/ # 共享 OpenAI 兼容 DTO 库
|   `-- Zhengyan.ChatUI.Desktop/ # Avalonia 桌面调试客户端
`-- tests/
    `-- Zhengyan.LLamaStack.Tests/ # xUnit 行为测试
```

## 运行要求

- Windows、Linux 或 macOS。
- API 和测试需要 .NET SDK 10.0 或更新版本。
- 文本推理需要 GGUF 模型文件。
- 图片或音频输入需要匹配的 `mmproj` GGUF 文件。
- 当前 API 项目引用的是 `LLamaSharp.Backend.Cuda12`。如果目标运行环境需要 CPU-only、Vulkan 或其他后端，请替换后端包。

检查 .NET 版本：

```powershell
dotnet --info
```

## 快速开始

1. 还原并构建：

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
```

2. 配置模型路径。可以创建或编辑 `src/Zhengyan.LLamaStack.Api/appsettings.Development.json`：

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

仍然支持旧式单模型配置：

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

3. 启动 API：

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

默认开发地址：

```text
http://localhost:5062
```

4. 检查健康状态：

Windows PowerShell:

```powershell
Invoke-RestMethod http://localhost:5062/health
```

Linux curl:

```bash
curl -s http://localhost:5062/health
```

## 配置

主配置节是 `LLamaStack`。推荐使用 `Models[]`；`ModelId` / `ModelPath` 保留用于兼容。模型项中省略的推理参数会继承顶层默认值。

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

| 配置项 | 说明 |
| --- | --- |
| `DefaultModel` | 请求省略 `model` 时使用的默认 chat/Responses 模型。 |
| `ModelId` / `ModelPath` | 旧式单模型 ID 和 GGUF 路径。 |
| `MmprojPath` | 旧式或默认多模态投影模型路径。 |
| `ContextSize` | 上下文窗口大小。 |
| `GpuLayerCount` | 要 offload 的层数。`0` 表示不做层 offload。 |
| `Threads`, `BatchThreads` | LLamaSharp 线程设置。`null` 表示交给 LLamaSharp 默认值。 |
| `BatchSize`, `UBatchSize` | prompt batch 和 physical batch 大小。 |
| `UseMemoryMap`, `UseMemoryLock` | 模型内存加载行为。 |
| `FlashAttention` | 可选 Flash Attention 设置。 |
| `UseGpuForMtmd` | MTMD/mmproj 是否使用 GPU。 |
| `LoadModelOnStartup` | 启动时加载模型，而不是懒加载。 |
| `MaxVramBytes` | 估算 VRAM 预算。`0` 表示关闭预算检查。 |
| `DefaultMaxTokens`, `DefaultTemperature`, `DefaultTopP`, `DefaultTopK` | 默认生成参数。 |
| `AntiPrompts` | 作为停止串使用的 LLamaSharp anti-prompts。 |
| `AllowRemoteMedia` | 允许远程图片/音频 URL，并启用主机保护、禁用重定向和大小限制。 |
| `AllowLocalMediaPaths` | 允许请求体引用本地文件路径。除非确有需要，否则应保持关闭。 |
| `MaxMediaBytes` | 单个媒体输入的最大字节数。 |
| `MaxImageDimension` | 图片缩放阈值（像素）。`-1` 时禁用缩放，改用请求中的 `detail` 参数；`> 0` 时强制缩放到该像素数以内。 |
| `EnableThinking` | 是否启用思考/推理模式（默认 `true`）。开启后模型输出的 `<think>`...`</think>` 内容会被提取为 `reasoning_content`，不混入最终文本。 |
| `ThinkingStartTag` | 思考内容起始标记（默认 `<think>`）。见下方"思考/推理模式"了解各模型常用值。 |
| `ThinkingEndTag` | 思考内容结束标记（默认 `</think>`）。见下方"思考/推理模式"了解各模型常用值。 |
| `Store.Provider` | `Memory`、`Sqlite`、`Postgres` 或 `Redis`。 |
| `Store.SqlitePath` | SQLite 数据库路径。 |
| `Store.ConnectionString` | PostgreSQL 或 Redis 连接字符串。 |
| `Auth.Enabled`, `Auth.ApiKey`, `Auth.ApiKeyHeader` | 可选 API Key 认证。中间件接受原始 key 或 `Bearer <key>`。 |
| `Cors` | 可选 allowed origins、headers、methods。启用 CORS 且列表为空时表示允许任意值。 |
| `Models[].Id` | 对外模型 ID。 |
| `Models[].OwnedBy` | `/v1/models` 返回的 `owned_by`。 |
| `Models[].ModelPath` | 该模型的 GGUF 路径。 |
| `Models[].MmprojPath` | 该模型的 mmproj 路径。 |
| `Models[].MaxConcurrency` | 该模型的 runtime context/executor 实例数量。 |
| `Models[].Capabilities` | 用于 `/v1/models` 和请求校验的能力声明。 |
| `Models[].EnableThinking` | 覆盖该模型的 thinking 开关。不设置则继承顶层 `EnableThinking`。 |
| `Models[].ThinkingStartTag` | 覆盖该模型的思考起始标记。不设置则继承顶层 `ThinkingStartTag`。 |
| `Models[].ThinkingEndTag` | 覆盖该模型的思考结束标记。不设置则继承顶层 `ThinkingEndTag`。 |
| `EmbeddingModels[]` | 独立 Embedding 模型注册。Embedding 设置包含 `Dimensions`、GPU layers、线程、batch、内存选项和 `MaxConcurrency`。 |

## 模型准备

LLamaSharp 使用 GGUF 文件。可以从模型平台下载已转换的 GGUF，也可以用 llama.cpp 工具链自行转换和量化。

建议使用 `Q4_K_M`、`Q5_K_M`、`Q6_K` 等量化版本降低内存占用。多模态模型需要文本 GGUF 和匹配的 `mmproj` GGUF。

`./models/` 下的模型文件已被 gitignore。

## 运行服务

开发运行：

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
```

指定监听地址：

```powershell
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
```

## API 示例

下面每个测试接口都给出两套命令：Windows PowerShell 和 Linux curl。需要动态 ID 的示例会先发起创建请求；Linux 示例中如果没有安装 `jq`，也可以直接从上一条响应里复制 `id` 并填入变量。启用 API Key 认证时，PowerShell 请求加 `-Headers @{ Authorization = "Bearer <key>" }`，curl 请求加 `-H "Authorization: Bearer <key>"`。

### 健康检查

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

### 列出模型

Windows PowerShell:

```powershell
Invoke-RestMethod http://localhost:5062/v1/models
```

Linux curl:

```bash
curl -s http://localhost:5062/v1/models
```

响应包含 `loaded`、模型路径、`capabilities`，以及可用时的 embedding 维度。

### 加载和卸载模型

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

### Chat Completions 流式输出

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

### Responses 流式输出

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

如果没有注册专用 Embedding 模型，该端点可以回退到已配置的 chat 模型，并从该模型创建一次性 embedder。

### Tokenize 和 Detokenize

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

### 动态调整模型池

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

响应会包含新的并发数和估算模型内存。

### 队列状态

先发起一个推理请求，响应头会返回 `X-Queue-Entry-Id`。队列项只在请求排队或执行期间存在；如果请求已经完成，查询可能返回 not found。

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

### 请求取消

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

取消适用于已跟踪的非流式执行和后台执行。已完成或未知的 Chat Completion 取消请求会返回 `cancelled = false`；未知 Responses 在没有存储对象时返回 not-found 错误。

### 管理端点

Chat Completions 只有在 `store = true` 时才会存储。

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

Responses 默认会存储。设置 `store = false` 可以跳过持久化。

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

存储提供者：

| Provider | 配置 |
| --- | --- |
| `Memory` | 默认进程内存储，重启后丢失。 |
| `Sqlite` | 设置 `LLamaStack:Store:SqlitePath`。 |
| `Postgres` | 设置 `LLamaStack:Store:ConnectionString`。 |
| `Redis` | 设置 `LLamaStack:Store:ConnectionString`。 |

### 后台 Responses 和 Compact 任务

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

### 工具调用

工具调用是协议桥接，不是服务端工具运行时。客户端提供工具定义，模型输出工具调用 JSON，服务端返回 OpenAI 兼容工具调用字段，然后客户端执行函数。

服务端可识别常见模型输出，例如：

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

Chat Completions 工具调用。

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

Responses 工具调用。

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

### 结构化输出

Chat Completions 使用 `response_format`；Responses 使用 `text.format`。当 `type` 为 `json_schema` 时，服务会尝试 grammar 约束解码，并在 `strict` 模式下先校验生成 JSON 再返回成功。

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

### 多模态输入

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

多模态模型需要 `Models[].MmprojPath`，并声明 `ImageInput` 或 `AudioInput` 能力。远程媒体 URL 会阻止 localhost、私网、链路本地和 CGNAT 目标；重定向会被禁用；下载大小受 `MaxMediaBytes` 限制。

图片输入支持服务端缩放，通过 `LLamaStack:MaxImageDimension` 或请求的 `detail` 字段控制：

- `MaxImageDimension >= 0`：最长边超过阈值时将图片等比缩放。
- `MaxImageDimension = -1`（默认）：使用请求的 `detail` 参数：
  - `"auto"` 或未传：不缩放。
  - `"low"`：缩放到 512×512 以内。
  - `"high"`：最短边缩到 768px；如最长边超过 2048px 再缩到 2048px。
- 缩放使用 SkiaSharp 解码并重编码为 JPEG（质量 85），失败时自动回退到原始 bytes。

### 思考/推理模式

服务端支持从模型生成文本中提取思考（reasoning）内容。开启后（默认开启），模型输出中的 `ThinkingStartTag`...`ThinkingEndTag` 内容会被分离为 `reasoning_content`，不会混入最终输出文本。

**Chat Completions 流式响应**中，思考部分以 `delta.reasoning_content` 字段发送：

```json
{
  "choices": [{
    "index": 0,
    "delta": { "reasoning_content": "Let me think about this..." }
  }]
}
```

**Responses API 流式响应**中，思考部分以 `type: "response.reasoning_text.delta"` 事件发送：

```json
{
  "type": "response.reasoning_text.delta",
  "delta": "Let me think about this..."
}
```

**非流式 Chat Completions** 中，思考内容以 `message.reasoning_content` 字段返回；**非流式 Responses** 中，思考内容以 `type: "reasoning"` 的输出项返回。

可通过配置关闭思考提取：

```json
{
  "LLamaStack": {
    "EnableThinking": false
  }
}
```

或为单个模型关闭：

```json
{
  "LLamaStack": {
    "Models": [
      {
        "Id": "my-model",
        "EnableThinking": false
      }
    ]
  }
}
```

#### 常见模型的思考标记

不同的模型使用不同的标记来标识思考内容：

| 模型 | ThinkingStartTag | ThinkingEndTag |
|---|---|---|
| DeepSeek R1 / DeepSeek V3 | `<think>` | `</think>` |
| QwQ-32B (Qwen) | `<think>` | `</think>` |
| Ministral 3 | `[THINK]` | `[/THINK]` |
| Gemma 4 | `<\|channel>thought` | `<channel\|>` |
| Cohere 2 (Command R) | `<\|START_THINKING\|>` | `<\|END_THINKING\|>` |

对于非标准标记的模型，在 `Models[]` 中配置即可：

```json
{
  "LLamaStack": {
    "Models": [
      {
        "Id": "ministral",
        "ThinkingStartTag": "[THINK]",
        "ThinkingEndTag": "[/THINK]"
      }
    ]
  }
}
```

思考提取不会影响 token 计数 —— 思考 token 仍然会计入 `completion_tokens`。

## 桌面调试客户端

运行 Avalonia 客户端：

```powershell
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
```

默认端点：

```text
http://localhost:5062/v1
```

客户端可以加载 `/v1/models`、在 Chat Completions 和 Responses 间切换、显示流式输出、展示 reasoning/additional response 字段、添加图片 URL 或本地图片，并把本地 UI 设置保存在 `%LocalAppData%\Zhengyan.ChatUI.Desktop\settings.json`。

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

仓库目前没有 Windows Service hosting。未来如果要作为 Windows Service 部署，需要添加 `Microsoft.Extensions.Hosting.WindowsServices`，在 `Program.cs` 调用 `UseWindowsService()`，并用 `sc.exe` 或 PowerShell 注册发布后的应用。

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

仓库目前没有 Dockerfile。未来镜像应通过 volume 挂载模型文件，并针对 CPU、CUDA 或 Vulkan 准备不同后端基础镜像。

## GPU 后端

LLamaSharp 后端由 NuGet 包引用决定，不是运行时配置开关。当前仓库引用的是：

```xml
<PackageReference Include="LLamaSharp.Backend.Cuda12" Version="0.27.0" />
```

支持 offload 的后端可以通过 `GpuLayerCount` 控制 offload 层数。如果要使用 CPU-only、Vulkan 或其他目标，需要替换 `src/Zhengyan.LLamaStack.Api/Zhengyan.LLamaStack.Api.csproj` 中的后端包。

示例：

```powershell
dotnet remove src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Cuda12
dotnet add src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj package LLamaSharp.Backend.Vulkan --version 0.27.0
```

然后配置 offload：

```json
{
  "LLamaStack": {
    "GpuLayerCount": 35
  }
}
```

## OpenAI 兼容路线图

### 已实现

| 能力 | 状态 |
| --- | --- |
| Models | `/v1/models`、加载状态、能力、embedding 维度、显式加载/卸载。 |
| Chat Completions | 非流式、流式、`n`、采样字段、stop strings、store opt-in、管理端点。 |
| Responses | 非流式、流式、默认存储、管理端点、`previous_response_id`、`conversation`、`background`、compact tasks。 |
| Embeddings | 专用 embedding 模型和 chat 模型回退，支持 `dimensions` 截断。 |
| Tokenization | `/v1/tokenize` 和 `/v1/detokenize`。 |
| Errors | 主要协议失败返回 OpenAI 风格错误 envelope。 |
| Tool calls | `tools` 和 legacy `functions` 解析、prompt 注入、工具调用 JSON 提取、tool choice 执行、parallel call 限制。 |
| Structured outputs | JSON mode、尽可能 JSON Schema 到 GBNF、strict schema 校验。 |
| Multimodal input | 请求级图片/音频解析，支持 data URL、base64、受保护远程 URL 和可选本地路径。 |
| Concurrency | 每模型 FIFO 队列、context 池、动态 resize、取消跟踪、估算 VRAM 检查。 |
| Storage | Memory、SQLite、PostgreSQL、Redis。 |
| Security | 可选 API Key 认证、可配置 header、CORS、日志脱敏、受保护媒体加载。 |
| Desktop client | Avalonia 调试 UI，支持 streaming、模型加载、图片附件、Chat/Responses 模式。 |

### 缺失或部分支持

| 领域 | 缺口 | 可能的下一步 |
| --- | --- | --- |
| Chat log probabilities | `logprobs` 和 `top_logprobs` 会被接受并返回兼容性 warning，但不会产生真实 logprob 输出。 | 围绕 logits/logprob 访问扩展生成流程。 |
| 多模态输出 | 当前只返回文本输出和 function-call item。 | 增加图片/音频输出模型类型与生成后端。 |
| Audio API | 未实现 `/v1/audio/transcriptions`、`/v1/audio/translations`、`/v1/audio/speech`。 | 增加 Whisper/Sherpa-ONNX/TTS 适配。 |
| Images API | 未实现 `/v1/images/generations`、edits、variations。 | 增加 Stable Diffusion 或 ComfyUI 适配。 |
| Moderations API | 未实现 `/v1/moderations`。 | 增加本地分类模型或规则引擎。 |
| Files 和 Uploads | 未实现 `/v1/files` 和 `/v1/uploads`。 | 增加文件存储、校验、生命周期和访问控制。 |
| Vector Stores | 未实现 vector stores、file batches 和 search endpoints。 | 增加 HNSW、SQLite vec、Qdrant、Milvus 等后端抽象。 |
| Batch API | 未实现 `/v1/batches`。 | 增加异步任务队列、batch parser、状态查询和输出文件。 |
| Fine-tuning | 未实现 fine-tuning jobs、checkpoints 和 events。 | 作为长期 LoRA/QLoRA 编排方向。 |
| Realtime API | 未实现 WebSocket/WebRTC realtime 协议。 | 为双向音频和低延迟输出构建独立 realtime host。 |
| Legacy Assistants API | 未实现 assistants、threads、runs、run steps。 | 仅在兼容性需求强时实现。 |
| 生产控制 | 缺少 rate limits、organization/project、metrics、tracing 和 audit logs。 | 增加限流和 OpenTelemetry/Prometheus 支持。 |
| SDK 兼容测试 | 尚无自动化 OpenAI SDK 兼容矩阵。 | 增加官方 .NET、Python、JavaScript SDK 的端到端测试。 |

### 建议迭代顺序

1. 增加 Files/Uploads 和 Vector Stores，因为它们能解锁检索类工作流。
2. 增加 rate limiting、metrics、request IDs、token/s 和 latency 可观测性。
3. 增加官方 OpenAI SDK 兼容测试矩阵。
4. 以独立适配器形式增加 Audio、Images 和 Moderations。
5. 在核心 HTTP 面稳定后，再评估 Realtime API 和 fine-tuning 编排。

## 开发命令

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj --urls http://0.0.0.0:5062
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
dotnet publish src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj -c Release -o publish\Zhengyan.LLamaStack.Api
```

仓库目前没有单独的 lint、format、codegen、CI 或 Docker 命令。

## 参考

- LLamaSharp: https://github.com/SciSharp/LLamaSharp
- LLamaSharp NuGet: https://www.nuget.org/packages/LLamaSharp
- OpenAI API Reference: https://platform.openai.com/docs/api-reference
