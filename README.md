# Zhengyan.LLamaStack

[English](README.en-US.md) | 简体中文

`Zhengyan.LLamaStack` 是一个基于 .NET 10、ASP.NET Core Minimal API 和 LLamaSharp 的本地 GGUF 大语言模型服务。它面向本地推理场景，提供 OpenAI 兼容 HTTP API，便于现有 SDK、客户端和工具迁移到本地模型运行时。

## 项目结构

```text
.
|-- Zhengyan.LLamaStack.slnx
|-- src/
|   |-- Zhengyan.LLamaStack.Api/       # OpenAI 兼容 API 服务
|   |-- Zhengyan.OpenAIModels/         # 共享协议 DTO
|   `-- Zhengyan.ChatUI.Desktop/       # Avalonia 桌面调试客户端
`-- tests/
    `-- Zhengyan.LLamaStack.Tests/     # xUnit 单元测试
```

## 常用命令

```powershell
dotnet restore Zhengyan.LLamaStack.slnx
dotnet build Zhengyan.LLamaStack.slnx -v minimal
dotnet test Zhengyan.LLamaStack.slnx -v minimal
dotnet run --project src\Zhengyan.LLamaStack.Api\Zhengyan.LLamaStack.Api.csproj
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
```

API 默认开发地址：

```text
http://localhost:5062
```

桌面端默认连接：

```text
http://localhost:5062/v1
```

## 主要能力

- OpenAI 兼容接口：`/v1/chat/completions`、`/v1/responses`、`/v1/embeddings`、`/v1/models`、`/v1/tokenize`、`/v1/detokenize`。
- SSE 流式输出；带工具的流式请求会先缓冲生成结果，再以 SSE 输出与非流式一致的响应结构。
- 多模型注册、默认模型、按请求体 `model` 路由。
- Memory、SQLite、PostgreSQL、Redis 存储后端，包含 response task 状态管理。
- 工具调用采用协议透传模式：服务端解析 `tools`/legacy `functions`，把模型生成的工具名和 JSON 参数以 OpenAI 兼容的 `tool_calls` / `function_call` 返回，由客户端执行工具。
- JSON Schema 到 GBNF 的结构化输出约束；strict 校验失败会返回 OpenAI 风格错误。
- Embedding 模型独立注册，支持 `dimensions` 截断。
- 可选多模态输入解析：data URL、受保护的远程 URL、可选本地路径。
- API Key 认证和 CORS 配置。
- 动态模型池 resize、FIFO 队列、取消和 VRAM 预算检查。

## 配置示例

```json
{
  "LLamaStack": {
    "DefaultModel": "local-gguf",
    "Models": [
      {
        "Id": "local-gguf",
        "ModelPath": "D:\\models\\model.gguf",
        "ContextSize": 4096,
        "MaxConcurrency": 1
      }
    ],
    "Store": {
      "Provider": "Memory"
    },
    "Auth": {
      "Enabled": false,
      "ApiKey": null,
      "ApiKeyHeader": "Authorization"
    }
  }
}
```

环境变量使用 .NET 的双下划线分隔：

```powershell
$env:LLamaStack__Models__0__Id = "local-gguf"
$env:LLamaStack__Models__0__ModelPath = "D:\models\model.gguf"
```

## 安全和运行说明

- 远程媒体 URL 会阻止 localhost、私网、链路本地和 CGNAT 地址，禁用重定向，并按 `MaxMediaBytes` 限制读取。
- 默认不允许本地媒体路径；只有设置 `LLamaStack:AllowLocalMediaPaths=true` 才会读取本地文件。
- 日志会脱敏 prompt、message、媒体 URL/data、embedding 和生成文本。
- GPU 后端是包替换，不是配置开关：将 `LLamaSharp.Backend.Cpu` 替换为 CUDA/Vulkan 等后端包。
- GGUF 模型文件建议放在 `./models/`，该目录已 gitignore。
