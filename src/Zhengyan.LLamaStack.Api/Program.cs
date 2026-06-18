using System.Text.Json;
using System.Text.Json.Serialization;
using Zhengyan.LLamaStack.Api.Endpoints;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Options;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LLamaStackOptions>(builder.Configuration.GetSection(LLamaStackOptions.SectionName));
builder.Services.AddHttpClient(OpenAiRequestMapper.MediaHttpClientName);
builder.Services.AddSingleton<OpenAiRequestMapper>();
builder.Services.AddSingleton<OpenAiMemoryStore>();
builder.Services.AddSingleton<LLamaInferenceService>();
builder.Services.AddHostedService<LLamaWarmupHostedService>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    ConfigureJson(options.SerializerOptions);
});

var app = builder.Build();

OpenAiJson.Configure = ConfigureJson;
app.MapOpenAiCompatibleEndpoints();

app.Run();

static void ConfigureJson(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.WriteIndented = false;
}
