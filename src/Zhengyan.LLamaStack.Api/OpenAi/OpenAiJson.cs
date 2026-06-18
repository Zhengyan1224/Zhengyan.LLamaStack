using System.Text.Json;

namespace Zhengyan.LLamaStack.Api.OpenAi;

public static class OpenAiJson
{
    public static Action<JsonSerializerOptions>? Configure { get; set; }

    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure?.Invoke(options);
        return options;
    }
}
