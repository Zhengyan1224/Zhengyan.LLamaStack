# Zhengyan.ChatUI.Desktop

Avalonia desktop client for manually testing `Zhengyan.LLamaStack.Api`.

## Run

```powershell
dotnet run --project src\Zhengyan.ChatUI.Desktop\Zhengyan.ChatUI.Desktop.csproj
```

Default server endpoint:

```text
http://localhost:5062/v1
```

## Features

- Configure server endpoint, API key, model, max tokens, temperature, and top_p.
- Load models from `GET /v1/models`.
- Switch between `/v1/chat/completions` and `/v1/responses`.
- Display SSE streaming output.
- Display thinking/reasoning text when it is present in streamed payloads.
- Show additional response properties as formatted JSON.
- Add image URL or local image attachments.
- Persist local UI settings.

## Settings File

Settings are stored at:

```text
%LocalAppData%\Zhengyan.ChatUI.Desktop\settings.json
```

Stored fields:

```text
ServerEndpoint
ApiKey
SelectedModel
MaxCompletionTokens
Temperature
TopP
UseResponsesApi
```

## Notes

- The client expects an OpenAI-compatible `/v1` endpoint.
- Local images are converted to data URLs before sending.
- The desktop client is a debugging tool; production auth, rate limiting, and request auditing belong on the API service.
