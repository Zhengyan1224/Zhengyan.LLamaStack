using System.Data;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Options;

namespace Zhengyan.LLamaStack.Api.Storage;

public sealed class OpenAiPostgresStore : IOpenAiStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public OpenAiPostgresStore(IOptions<LLamaStackOptions> options)
    {
        _connectionString = options.Value.Store.ConnectionString
            ?? "Host=localhost;Database=llamastack;Username=postgres;Password=postgres";
    }

    public async Task AddChatCompletionAsync(
        string id,
        long created,
        InferenceRequest request,
        InferenceCompletion completion,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chat_completions
                (id, created, model, metadata_json, "user", service_tier, store, messages_json, output_text,
                 tool_calls_json, finish_reason, prompt_tokens, completion_tokens, compatibility_warnings_json)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9,
                 $10, $11, $12, $13, $14)
            ON CONFLICT (id) DO UPDATE SET
                output_text = EXCLUDED.output_text,
                tool_calls_json = EXCLUDED.tool_calls_json,
                finish_reason = EXCLUDED.finish_reason,
                prompt_tokens = EXCLUDED.prompt_tokens,
                completion_tokens = EXCLUDED.completion_tokens,
                compatibility_warnings_json = EXCLUDED.compatibility_warnings_json;
            """;

        command.Parameters.AddWithValue("$1", id);
        command.Parameters.AddWithValue("$2", created);
        command.Parameters.AddWithValue("$3", completion.Model);
        command.Parameters.AddWithValue("$4", (object?)SerializeNullable(completion.Metadata) ?? DBNull.Value);
        command.Parameters.AddWithValue("$5", (object?)completion.User ?? DBNull.Value);
        command.Parameters.AddWithValue("$6", (object?)completion.ServiceTier ?? DBNull.Value);
        command.Parameters.AddWithValue("$7", 1);
        command.Parameters.AddWithValue("$8", Serialize(request.Messages));
        command.Parameters.AddWithValue("$9", completion.Text);
        command.Parameters.AddWithValue("$10", Serialize(completion.ToolCalls));
        command.Parameters.AddWithValue("$11", completion.FinishReason);
        command.Parameters.AddWithValue("$12", completion.PromptTokens);
        command.Parameters.AddWithValue("$13", completion.CompletionTokens);
        command.Parameters.AddWithValue("$14", Serialize(completion.CompatibilityWarnings));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredListResult<StoredChatCompletion>> ListChatCompletionsAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var all = new List<StoredChatCompletion>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created, model, metadata_json, "user", service_tier, store, messages_json, output_text,
                   tool_calls_json, finish_reason, prompt_tokens, completion_tokens, compatibility_warnings_json
            FROM chat_completions
            ORDER BY created DESC, id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            all.Add(ReadChatCompletion(reader));
        }

        var result = OpenAiStoreHelpers.ApplyCursor(all, x => x.Id, x => x.Created, safeLimit, after, before);
        return new StoredListResult<StoredChatCompletion>(result.Items, result.HasMore);
    }

    public async Task<StoredChatCompletion?> GetChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created, model, metadata_json, "user", service_tier, store, messages_json, output_text,
                   tool_calls_json, finish_reason, prompt_tokens, completion_tokens, compatibility_warnings_json
            FROM chat_completions
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue("$1", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadChatCompletion(reader) : null;
    }

    public async Task<StoredChatCompletion?> UpdateChatCompletionMetadataAsync(
        string id,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_completions SET metadata_json = $1 WHERE id = $2;";
        command.Parameters.AddWithValue("$1", (object?)SerializeNullable(metadata) ?? DBNull.Value);
        command.Parameters.AddWithValue("$2", id);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetChatCompletionAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_completions WHERE id = $1;";
        command.Parameters.AddWithValue("$1", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task AddResponseAsync(
        string id,
        long createdAt,
        InferenceRequest request,
        InferenceCompletion completion,
        CancellationToken cancellationToken)
    {
        return AddResponseAsync(new StoredResponse
        {
            Id = id,
            CreatedAt = createdAt,
            Status = "completed",
            Model = completion.Model,
            Metadata = completion.Metadata,
            User = completion.User,
            ServiceTier = completion.ServiceTier,
            Store = request.Store ?? true,
            PreviousResponseId = request.PreviousResponseId,
            InputMessages = request.Messages,
            OutputText = completion.Text,
            ToolCalls = completion.ToolCalls,
            InputTokens = completion.PromptTokens,
            OutputTokens = completion.CompletionTokens,
            CompatibilityWarnings = completion.CompatibilityWarnings
        }, cancellationToken);
    }

    public async Task AddResponseAsync(StoredResponse response, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO responses
                (id, created_at, status, model, metadata_json, "user", service_tier, store, previous_response_id,
                 input_messages_json, output_text, tool_calls_json, input_tokens, output_tokens, compatibility_warnings_json)
            VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9,
                 $10, $11, $12, $13, $14, $15)
            ON CONFLICT (id) DO UPDATE SET
                status = EXCLUDED.status,
                output_text = EXCLUDED.output_text,
                tool_calls_json = EXCLUDED.tool_calls_json,
                input_tokens = EXCLUDED.input_tokens,
                output_tokens = EXCLUDED.output_tokens,
                compatibility_warnings_json = EXCLUDED.compatibility_warnings_json;
            """;

        command.Parameters.AddWithValue("$1", response.Id);
        command.Parameters.AddWithValue("$2", response.CreatedAt);
        command.Parameters.AddWithValue("$3", response.Status);
        command.Parameters.AddWithValue("$4", response.Model);
        command.Parameters.AddWithValue("$5", (object?)SerializeNullable(response.Metadata) ?? DBNull.Value);
        command.Parameters.AddWithValue("$6", (object?)response.User ?? DBNull.Value);
        command.Parameters.AddWithValue("$7", (object?)response.ServiceTier ?? DBNull.Value);
        command.Parameters.AddWithValue("$8", response.Store ? 1 : 0);
        command.Parameters.AddWithValue("$9", (object?)response.PreviousResponseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$10", Serialize(response.InputMessages));
        command.Parameters.AddWithValue("$11", response.OutputText);
        command.Parameters.AddWithValue("$12", Serialize(response.ToolCalls));
        command.Parameters.AddWithValue("$13", response.InputTokens);
        command.Parameters.AddWithValue("$14", response.OutputTokens);
        command.Parameters.AddWithValue("$15", Serialize(response.CompatibilityWarnings));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredListResult<StoredResponse>> ListResponsesAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var safeLimit = Math.Clamp(limit, 1, 100);
        var all = new List<StoredResponse>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, status, model, metadata_json, "user", service_tier, store, previous_response_id,
                   input_messages_json, output_text, tool_calls_json, input_tokens, output_tokens, compatibility_warnings_json
            FROM responses
            ORDER BY created_at DESC, id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            all.Add(ReadResponse(reader));
        }

        var result = OpenAiStoreHelpers.ApplyCursor(all, x => x.Id, x => x.CreatedAt, safeLimit, after, before);
        return new StoredListResult<StoredResponse>(result.Items, result.HasMore);
    }

    public async Task<StoredResponse?> GetResponseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, status, model, metadata_json, "user", service_tier, store, previous_response_id,
                   input_messages_json, output_text, tool_calls_json, input_tokens, output_tokens, compatibility_warnings_json
            FROM responses
            WHERE id = $1;
            """;
        command.Parameters.AddWithValue("$1", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader) : null;
    }

    public async Task<bool> DeleteResponseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM responses WHERE id = $1;";
        command.Parameters.AddWithValue("$1", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<StoredResponse?> CancelResponseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE responses SET status = 'cancelled' WHERE id = $1;";
        command.Parameters.AddWithValue("$1", id);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetResponseAsync(id, cancellationToken);
    }

    public async Task<StoredResponse?> UpdateResponseMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE responses SET metadata_json = $1 WHERE id = $2;";
        command.Parameters.AddWithValue("$1", (object?)SerializeNullable(metadata) ?? DBNull.Value);
        command.Parameters.AddWithValue("$2", id);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetResponseAsync(id, cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);

            await using var cmd1 = connection.CreateCommand();
            cmd1.CommandText =
                """
                CREATE TABLE IF NOT EXISTS chat_completions (
                    id TEXT PRIMARY KEY,
                    created BIGINT NOT NULL,
                    model TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    "user" TEXT NULL,
                    service_tier TEXT NULL,
                    store INTEGER NOT NULL,
                    messages_json TEXT NOT NULL,
                    output_text TEXT NOT NULL,
                    tool_calls_json TEXT NOT NULL,
                    finish_reason TEXT NOT NULL DEFAULT 'stop',
                    prompt_tokens INTEGER NOT NULL,
                    completion_tokens INTEGER NOT NULL,
                    compatibility_warnings_json TEXT NOT NULL
                );
                """;
            await cmd1.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd2 = connection.CreateCommand();
            cmd2.CommandText =
                """
                CREATE INDEX IF NOT EXISTS idx_chat_completions_created_id
                ON chat_completions (created DESC, id ASC);
                """;
            await cmd2.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd3 = connection.CreateCommand();
            cmd3.CommandText =
                """
                CREATE TABLE IF NOT EXISTS responses (
                    id TEXT PRIMARY KEY,
                    created_at BIGINT NOT NULL,
                    status TEXT NOT NULL,
                    model TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    "user" TEXT NULL,
                    service_tier TEXT NULL,
                    store INTEGER NOT NULL,
                    previous_response_id TEXT NULL,
                    input_messages_json TEXT NOT NULL,
                    output_text TEXT NOT NULL,
                    tool_calls_json TEXT NOT NULL,
                    input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    compatibility_warnings_json TEXT NOT NULL
                );
                """;
            await cmd3.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd4 = connection.CreateCommand();
            cmd4.CommandText =
                """
                CREATE INDEX IF NOT EXISTS idx_responses_created_id
                ON responses (created_at DESC, id ASC);
                """;
            await cmd4.ExecuteNonQueryAsync(cancellationToken);

            await using var cmd5 = connection.CreateCommand();
            cmd5.CommandText =
                """
                CREATE INDEX IF NOT EXISTS idx_responses_previous_response_id
                ON responses (previous_response_id);
                """;
            await cmd5.ExecuteNonQueryAsync(cancellationToken);

            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static StoredChatCompletion ReadChatCompletion(IDataRecord reader)
    {
        return new StoredChatCompletion
        {
            Id = reader.GetString(0),
            Created = reader.GetInt64(1),
            Model = reader.GetString(2),
            Metadata = DeserializeNullable<IReadOnlyDictionary<string, string>>(reader, 3),
            User = GetNullableString(reader, 4),
            ServiceTier = GetNullableString(reader, 5),
            Store = reader.GetInt32(6) == 1,
            Messages = Deserialize<IReadOnlyList<InferenceMessage>>(reader.GetString(7)),
            OutputText = reader.GetString(8),
            ToolCalls = Deserialize<IReadOnlyList<OpenAiToolCall>>(reader.GetString(9)),
            FinishReason = reader.GetString(10),
            PromptTokens = reader.GetInt32(11),
            CompletionTokens = reader.GetInt32(12),
            CompatibilityWarnings = Deserialize<IReadOnlyList<string>>(reader.GetString(13))
        };
    }

    private static StoredResponse ReadResponse(IDataRecord reader)
    {
        return new StoredResponse
        {
            Id = reader.GetString(0),
            CreatedAt = reader.GetInt64(1),
            Status = reader.GetString(2),
            Model = reader.GetString(3),
            Metadata = DeserializeNullable<IReadOnlyDictionary<string, string>>(reader, 4),
            User = GetNullableString(reader, 5),
            ServiceTier = GetNullableString(reader, 6),
            Store = reader.GetInt32(7) == 1,
            PreviousResponseId = GetNullableString(reader, 8),
            InputMessages = Deserialize<IReadOnlyList<InferenceMessage>>(reader.GetString(9)),
            OutputText = reader.GetString(10),
            ToolCalls = Deserialize<IReadOnlyList<OpenAiToolCall>>(reader.GetString(11)),
            InputTokens = reader.GetInt32(12),
            OutputTokens = reader.GetInt32(13),
            CompatibilityWarnings = Deserialize<IReadOnlyList<string>>(reader.GetString(14))
        };
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, OpenAiJson.CreateOptions());
    }

    private static string? SerializeNullable<T>(T? value)
    {
        return value is null ? null : Serialize(value);
    }

    private static T Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, OpenAiJson.CreateOptions())!;
    }

    private static T? DeserializeNullable<T>(IDataRecord reader, int index)
    {
        return reader.IsDBNull(index) ? default : Deserialize<T>(reader.GetString(index));
    }

    public Task AddResponseTaskAsync(ResponseTaskInfo task, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Response task management is not supported in Postgres store. Use Memory store instead.");
    }

    public Task<ResponseTaskInfo?> GetResponseTaskAsync(string id, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Response task management is not supported in Postgres store. Use Memory store instead.");
    }

    public Task UpdateResponseTaskAsync(string id, ResponseTaskStatus status, string? resultResponseId = null, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Response task management is not supported in Postgres store. Use Memory store instead.");
    }

    public Task<StoredListResult<ResponseTaskInfo>> ListResponseTasksAsync(int limit, string? after, string? before, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Response task management is not supported in Postgres store. Use Memory store instead.");
    }

    private static string? GetNullableString(IDataRecord reader, int index)
    {
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }
}
