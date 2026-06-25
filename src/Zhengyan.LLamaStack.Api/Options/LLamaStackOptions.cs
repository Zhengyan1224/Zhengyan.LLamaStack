namespace Zhengyan.LLamaStack.Api.Options;

public sealed class LLamaStackOptions
{
    public const string SectionName = "LLamaStack";

    public string? DefaultModel { get; set; }

    public string ModelId { get; set; } = "local-gguf";

    public string? ModelPath { get; set; }

    public string? MmprojPath { get; set; }

    public uint? ContextSize { get; set; } = 4096;

    public int GpuLayerCount { get; set; }

    public int? Threads { get; set; }

    public int? BatchThreads { get; set; }

    public uint? BatchSize { get; set; }

    public uint? UBatchSize { get; set; }

    public bool UseMemoryMap { get; set; } = true;

    public bool UseMemoryLock { get; set; }

    public bool? FlashAttention { get; set; }

    public bool UseGpuForMtmd { get; set; }

    public bool LoadModelOnStartup { get; set; }

    public long MaxVramBytes { get; set; }

    public int DefaultMaxTokens { get; set; } = 512;

    public float DefaultTemperature { get; set; } = 0.7f;

    public float DefaultTopP { get; set; } = 0.95f;

    public int DefaultTopK { get; set; } = 40;

    public IReadOnlyList<string> AntiPrompts { get; set; } = ["<|im_end|>", "</s>"];

    public bool AllowRemoteMedia { get; set; } = true;

    public bool AllowLocalMediaPaths { get; set; }

    public long MaxMediaBytes { get; set; } = 32 * 1024 * 1024;

    public LLamaModelCapabilitiesOptions? Capabilities { get; set; }

    public int? MaxConcurrency { get; set; }

    public LLamaStoreOptions Store { get; set; } = new();

    public List<LLamaModelOptions> Models { get; set; } = [];

    public List<LLamaEmbeddingModelOptions> EmbeddingModels { get; set; } = [];

    public LLamaAuthOptions Auth { get; set; } = new();

    public LLamaCorsOptions Cors { get; set; } = new();

    public IReadOnlyList<LLamaEmbeddingModelRuntimeOptions> GetEmbeddingModelRegistrations()
    {
        var result = new List<LLamaEmbeddingModelRuntimeOptions>();
        foreach (var model in EmbeddingModels)
        {
            if (string.IsNullOrWhiteSpace(model.Id)) continue;
            result.Add(CreateEmbeddingRuntimeOptions(model));
        }
        return result;
    }

    private LLamaEmbeddingModelRuntimeOptions CreateEmbeddingRuntimeOptions(LLamaEmbeddingModelOptions model)
    {
        return new LLamaEmbeddingModelRuntimeOptions
        {
            Id = model.Id,
            ModelPath = model.ModelPath,
            Dimensions = model.Dimensions ?? 0,
            GpuLayerCount = model.GpuLayerCount ?? GpuLayerCount,
            Threads = model.Threads ?? Threads,
            BatchThreads = model.BatchThreads ?? BatchThreads,
            BatchSize = model.BatchSize ?? BatchSize ?? 512,
            UseMemoryMap = model.UseMemoryMap ?? UseMemoryMap,
            UseMemoryLock = model.UseMemoryLock ?? UseMemoryLock,
            MaxConcurrency = Math.Max(1, model.MaxConcurrency ?? MaxConcurrency ?? 1)
        };
    }

    public IReadOnlyList<LLamaModelRuntimeOptions> GetModelRegistrations()
    {
        var result = new List<LLamaModelRuntimeOptions>();
        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                continue;
            }

            result.Add(CreateRuntimeOptions(model));
        }

        if (result.Count == 0 || !string.IsNullOrWhiteSpace(ModelPath))
        {
            var legacyId = string.IsNullOrWhiteSpace(ModelId) ? "local-gguf" : ModelId;
            if (!result.Any(x => string.Equals(x.Id, legacyId, StringComparison.OrdinalIgnoreCase)))
            {
                result.Insert(0, CreateRuntimeOptions(new LLamaModelOptions
                {
                    Id = legacyId,
                    ModelPath = ModelPath,
                    MmprojPath = MmprojPath,
                    ContextSize = ContextSize,
                    GpuLayerCount = GpuLayerCount,
                    Threads = Threads,
                    BatchThreads = BatchThreads,
                    BatchSize = BatchSize,
                    UBatchSize = UBatchSize,
                    UseMemoryMap = UseMemoryMap,
                    UseMemoryLock = UseMemoryLock,
                    FlashAttention = FlashAttention,
                    UseGpuForMtmd = UseGpuForMtmd,
                    LoadModelOnStartup = LoadModelOnStartup,
                    DefaultMaxTokens = DefaultMaxTokens,
                    DefaultTemperature = DefaultTemperature,
                    DefaultTopP = DefaultTopP,
                    DefaultTopK = DefaultTopK,
                    AntiPrompts = AntiPrompts,
                    Capabilities = Capabilities
                }));
            }
        }

        if (result.Count == 0)
        {
            result.Add(CreateRuntimeOptions(new LLamaModelOptions { Id = ModelId }));
        }

        return result;
    }

    private LLamaModelRuntimeOptions CreateRuntimeOptions(LLamaModelOptions model)
    {
        var mmprojPath = model.MmprojPath ?? MmprojPath;
        var capabilities = CreateCapabilities(model.Capabilities ?? Capabilities, !string.IsNullOrWhiteSpace(mmprojPath));

        return new LLamaModelRuntimeOptions
        {
            Id = model.Id,
            OwnedBy = string.IsNullOrWhiteSpace(model.OwnedBy) ? "local" : model.OwnedBy,
            Created = model.Created ?? 0,
            ModelPath = model.ModelPath ?? ModelPath,
            MmprojPath = mmprojPath,
            ContextSize = model.ContextSize ?? ContextSize,
            GpuLayerCount = model.GpuLayerCount ?? GpuLayerCount,
            Threads = model.Threads ?? Threads,
            BatchThreads = model.BatchThreads ?? BatchThreads,
            BatchSize = model.BatchSize ?? BatchSize,
            UBatchSize = model.UBatchSize ?? UBatchSize,
            UseMemoryMap = model.UseMemoryMap ?? UseMemoryMap,
            UseMemoryLock = model.UseMemoryLock ?? UseMemoryLock,
            FlashAttention = model.FlashAttention ?? FlashAttention,
            UseGpuForMtmd = model.UseGpuForMtmd ?? UseGpuForMtmd,
            LoadModelOnStartup = model.LoadModelOnStartup ?? LoadModelOnStartup,
            DefaultMaxTokens = model.DefaultMaxTokens ?? DefaultMaxTokens,
            DefaultTemperature = model.DefaultTemperature ?? DefaultTemperature,
            DefaultTopP = model.DefaultTopP ?? DefaultTopP,
            DefaultTopK = model.DefaultTopK ?? DefaultTopK,
            AntiPrompts = model.AntiPrompts ?? AntiPrompts,
            Capabilities = capabilities,
            MaxConcurrency = Math.Max(1, model.MaxConcurrency ?? MaxConcurrency ?? 1)
        };
    }

    private static LLamaModelCapabilities CreateCapabilities(LLamaModelCapabilitiesOptions? configured, bool hasMmproj)
    {
        return new LLamaModelCapabilities
        {
            ChatCompletions = configured?.ChatCompletions ?? true,
            Responses = configured?.Responses ?? true,
            TextInput = configured?.TextInput ?? true,
            ImageInput = configured?.ImageInput ?? hasMmproj,
            AudioInput = configured?.AudioInput ?? hasMmproj,
            ToolCalling = configured?.ToolCalling ?? true,
            Streaming = configured?.Streaming ?? true,
            JsonMode = configured?.JsonMode ?? true,
            Embeddings = configured?.Embeddings ?? false
        };
    }
}

public sealed class LLamaStoreOptions
{
    public string Provider { get; set; } = "Memory";

    public string SqlitePath { get; set; } = "data/llamastack.db";

    public string? ConnectionString { get; set; }
}

public sealed class LLamaModelOptions
{
    public string Id { get; set; } = string.Empty;

    public string OwnedBy { get; set; } = "local";

    public long? Created { get; set; }

    public string? ModelPath { get; set; }

    public string? MmprojPath { get; set; }

    public uint? ContextSize { get; set; }

    public int? GpuLayerCount { get; set; }

    public int? Threads { get; set; }

    public int? BatchThreads { get; set; }

    public uint? BatchSize { get; set; }

    public uint? UBatchSize { get; set; }

    public bool? UseMemoryMap { get; set; }

    public bool? UseMemoryLock { get; set; }

    public bool? FlashAttention { get; set; }

    public bool? UseGpuForMtmd { get; set; }

    public bool? LoadModelOnStartup { get; set; }

    public int? DefaultMaxTokens { get; set; }

    public float? DefaultTemperature { get; set; }

    public float? DefaultTopP { get; set; }

    public int? DefaultTopK { get; set; }

    public IReadOnlyList<string>? AntiPrompts { get; set; }

    public LLamaModelCapabilitiesOptions? Capabilities { get; set; }

    public int? MaxConcurrency { get; set; }
}

public sealed class LLamaModelCapabilitiesOptions
{
    public bool? ChatCompletions { get; set; }

    public bool? Responses { get; set; }

    public bool? TextInput { get; set; }

    public bool? ImageInput { get; set; }

    public bool? AudioInput { get; set; }

    public bool? ToolCalling { get; set; }

    public bool? Streaming { get; set; }

    public bool? JsonMode { get; set; }

    public bool? Embeddings { get; set; }
}

public sealed class LLamaModelCapabilities
{
    public bool ChatCompletions { get; init; } = true;

    public bool Responses { get; init; } = true;

    public bool TextInput { get; init; } = true;

    public bool ImageInput { get; init; }

    public bool AudioInput { get; init; }

    public bool ToolCalling { get; init; } = true;

    public bool Streaming { get; init; } = true;

    public bool JsonMode { get; init; } = true;

    public bool Embeddings { get; init; }
}

public sealed class LLamaModelRuntimeOptions
{
    public string Id { get; init; } = string.Empty;

    public string OwnedBy { get; init; } = "local";

    public long Created { get; init; }

    public string? ModelPath { get; init; }

    public string? MmprojPath { get; init; }

    public uint? ContextSize { get; init; }

    public int GpuLayerCount { get; init; }

    public int? Threads { get; init; }

    public int? BatchThreads { get; init; }

    public uint? BatchSize { get; init; }

    public uint? UBatchSize { get; init; }

    public bool UseMemoryMap { get; init; } = true;

    public bool UseMemoryLock { get; init; }

    public bool? FlashAttention { get; init; }

    public bool UseGpuForMtmd { get; init; }

    public bool LoadModelOnStartup { get; init; }

    public int DefaultMaxTokens { get; init; } = 512;

    public float DefaultTemperature { get; init; } = 0.7f;

    public float DefaultTopP { get; init; } = 0.95f;

    public int DefaultTopK { get; init; } = 40;

    public IReadOnlyList<string> AntiPrompts { get; init; } = ["<|im_end|>", "</s>"];

    public LLamaModelCapabilities Capabilities { get; init; } = new();

    public int MaxConcurrency { get; init; } = 1;
}

public sealed class LLamaAuthOptions
{
    public bool Enabled { get; set; }

    public string? ApiKey { get; set; }

    public string? ApiKeyHeader { get; set; } = "Authorization";
}

public sealed class LLamaCorsOptions
{
    public bool Enabled { get; set; }

    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];

    public IReadOnlyList<string> AllowedHeaders { get; set; } = [];

    public IReadOnlyList<string> AllowedMethods { get; set; } = [];
}

public sealed class LLamaEmbeddingModelOptions
{
    public string Id { get; set; } = string.Empty;

    public string? ModelPath { get; set; }

    public int? Dimensions { get; set; }

    public int? GpuLayerCount { get; set; }

    public int? Threads { get; set; }

    public int? BatchThreads { get; set; }

    public uint? BatchSize { get; set; }

    public bool? UseMemoryMap { get; set; }

    public bool? UseMemoryLock { get; set; }

    public int? MaxConcurrency { get; set; }
}

public sealed class LLamaEmbeddingModelRuntimeOptions
{
    public string Id { get; init; } = string.Empty;

    public string? ModelPath { get; init; }

    public int Dimensions { get; init; }

    public int GpuLayerCount { get; init; }

    public int? Threads { get; init; }

    public int? BatchThreads { get; init; }

    public uint BatchSize { get; init; } = 512;

    public bool UseMemoryMap { get; init; } = true;

    public bool UseMemoryLock { get; init; }

    public int MaxConcurrency { get; init; } = 1;
}
