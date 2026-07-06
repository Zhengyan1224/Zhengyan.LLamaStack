using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Zhengyan.LLamaStack.Api.Endpoints;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.Infrastructure;
using Zhengyan.LLamaStack.Api.Options;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<LLamaStackOptions>(builder.Configuration.GetSection(LLamaStackOptions.SectionName));
builder.Services.AddCors();
builder.Services.AddHttpClient(OpenAiRequestMapper.MediaHttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false
});
builder.Services.AddSingleton<OpenAiRequestMapper>();
builder.Services.AddSingleton<IOpenAiStore>(sp =>
{
    var options = sp.GetRequiredService<IOptions<LLamaStackOptions>>();
    return options.Value.Store.Provider.ToLowerInvariant() switch
    {
        "sqlite" => new OpenAiSqliteStore(options),
        "postgres" => new OpenAiPostgresStore(options),
        "redis" => new OpenAiRedisStore(options),
        _ => new OpenAiMemoryStore()
    };
});
builder.Services.AddSingleton<ModelQueueManager>();
builder.Services.AddSingleton<LLamaInferenceService>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<ResponseExecutionTracker>();
builder.Services.AddSingleton<IResponseCompactScheduler, ResponseCompactScheduler>();
builder.Services.AddHostedService<ResponseCompactScheduler>(sp => (ResponseCompactScheduler)sp.GetRequiredService<IResponseCompactScheduler>());
builder.Services.AddSingleton<ResponseBackgroundService>();
builder.Services.AddHostedService<ResponseBackgroundService>(sp => sp.GetRequiredService<ResponseBackgroundService>());
builder.Services.AddHostedService<LLamaWarmupHostedService>();
builder.Services.AddExceptionHandler<OpenAiExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    ConfigureJson(options.SerializerOptions);
});

var app = builder.Build();

app.UseExceptionHandler();

var corsOptions = app.Services.GetRequiredService<IOptions<LLamaStackOptions>>().Value.Cors;
if (corsOptions.Enabled)
{
    app.UseCors(policy =>
    {
        if (corsOptions.AllowedOrigins.Count > 0)
        {
            policy.WithOrigins([.. corsOptions.AllowedOrigins]);
        }
        else
        {
            policy.AllowAnyOrigin();
        }

        if (corsOptions.AllowedHeaders.Count > 0)
        {
            policy.WithHeaders([.. corsOptions.AllowedHeaders]);
        }
        else
        {
            policy.AllowAnyHeader();
        }

        if (corsOptions.AllowedMethods.Count > 0)
        {
            policy.WithMethods([.. corsOptions.AllowedMethods]);
        }
        else
        {
            policy.AllowAnyMethod();
        }
    });
}

app.UseMiddleware<OpenApiKeyMiddleware>();

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
