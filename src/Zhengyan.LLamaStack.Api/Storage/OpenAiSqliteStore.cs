using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Zhengyan.LLamaStack.Api.Inference;
using Zhengyan.LLamaStack.Api.OpenAi;
using Zhengyan.LLamaStack.Api.Options;

namespace Zhengyan.LLamaStack.Api.Storage;

public sealed class OpenAiSqliteStore : IOpenAiStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private bool _schemaReady;

    public OpenAiSqliteStore(IOptions<LLamaStackOptions> options)
    {
        _connectionString = CreateConnectionString(options.Value.Store);
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
            INSERT OR REPLACE INTO chat_completions
                (id, created, model, metadata_json, user, service_tier, store, messages_json, output_text,
                 tool_calls_json, finish_reason, prompt_tokens, completion_tokens, compatibility_warnings_json)
            VALUES
                ($id, $created, $model, $metadata_json, $user, $service_tier, $store, $messages_json, $output_text,
                 $tool_calls_json, $finish_reason, $prompt_tokens, $completion_tokens, $compatibility_warnings_json);
            """;

        AddParameter(command, "$id", id);
        AddParameter(command, "$created", created);
        AddParameter(command, "$model", completion.Model);
        AddParameter(command, "$metadata_json", SerializeNullable(completion.Metadata));
        AddParameter(command, "$user", completion.User);
        AddParameter(command, "$service_tier", completion.ServiceTier);
        AddParameter(command, "$store", 1);
        AddParameter(command, "$messages_json", Serialize(request.Messages));
        AddParameter(command, "$output_text", completion.Text);
        AddParameter(command, "$tool_calls_json", Serialize(completion.ToolCalls));
        AddParameter(command, "$finish_reason", completion.FinishReason);
        AddParameter(command, "$prompt_tokens", completion.PromptTokens);
        AddParameter(command, "$completion_tokens", completion.CompletionTokens);
        AddParameter(command, "$compatibility_warnings_json", Serialize(completion.CompatibilityWarnings));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredListResult<StoredChatCompletion>> ListChatCompletionsAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var all = new List<StoredChatCompletion>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created, model, metadata_json, user, service_tier, store, messages_json, output_text,
                   tool_calls_json, finish_reason, prompt_tokens, completion_tokens, compatibility_warnings_json
            FROM chat_completions
            ORDER BY created DESC, id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            all.Add(ReadChatCompletion(reader));
        }

        var result = OpenAiStoreHelpers.ApplyCursor(all, x => x.Id, x => x.Created, limit, after, before);
        return new StoredListResult<StoredChatCompletion>(result.Items, result.HasMore);
    }

    public async Task<StoredChatCompletion?> GetChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created, model, metadata_json, user, service_tier, store, messages_json, output_text,
                   tool_calls_json, finish_reason, prompt_tokens, completion_tokens, compatibility_warnings_json
            FROM chat_completions
            WHERE id = $id;
            """;
        AddParameter(command, "$id", id);
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
        command.CommandText = "UPDATE chat_completions SET metadata_json = $metadata_json WHERE id = $id;";
        AddParameter(command, "$id", id);
        AddParameter(command, "$metadata_json", SerializeNullable(metadata));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetChatCompletionAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteChatCompletionAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_completions WHERE id = $id;";
        AddParameter(command, "$id", id);
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
            INSERT OR REPLACE INTO responses
                (id, created_at, status, model, metadata_json, user, service_tier, store, previous_response_id,
                 input_messages_json, output_text, tool_calls_json, input_tokens, output_tokens, compatibility_warnings_json)
            VALUES
                ($id, $created_at, $status, $model, $metadata_json, $user, $service_tier, $store, $previous_response_id,
                 $input_messages_json, $output_text, $tool_calls_json, $input_tokens, $output_tokens, $compatibility_warnings_json);
            """;

        AddParameter(command, "$id", response.Id);
        AddParameter(command, "$created_at", response.CreatedAt);
        AddParameter(command, "$status", response.Status);
        AddParameter(command, "$model", response.Model);
        AddParameter(command, "$metadata_json", SerializeNullable(response.Metadata));
        AddParameter(command, "$user", response.User);
        AddParameter(command, "$service_tier", response.ServiceTier);
        AddParameter(command, "$store", response.Store ? 1 : 0);
        AddParameter(command, "$previous_response_id", response.PreviousResponseId);
        AddParameter(command, "$input_messages_json", Serialize(response.InputMessages));
        AddParameter(command, "$output_text", response.OutputText);
        AddParameter(command, "$tool_calls_json", Serialize(response.ToolCalls));
        AddParameter(command, "$input_tokens", response.InputTokens);
        AddParameter(command, "$output_tokens", response.OutputTokens);
        AddParameter(command, "$compatibility_warnings_json", Serialize(response.CompatibilityWarnings));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredListResult<StoredResponse>> ListResponsesAsync(
        int limit,
        string? after,
        string? before,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var all = new List<StoredResponse>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, status, model, metadata_json, user, service_tier, store, previous_response_id,
                   input_messages_json, output_text, tool_calls_json, input_tokens, output_tokens, compatibility_warnings_json
            FROM responses
            ORDER BY created_at DESC, id ASC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            all.Add(ReadResponse(reader));
        }

        var result = OpenAiStoreHelpers.ApplyCursor(all, x => x.Id, x => x.CreatedAt, limit, after, before);
        return new StoredListResult<StoredResponse>(result.Items, result.HasMore);
    }

    public async Task<StoredResponse?> GetResponseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, created_at, status, model, metadata_json, user, service_tier, store, previous_response_id,
                   input_messages_json, output_text, tool_calls_json, input_tokens, output_tokens, compatibility_warnings_json
            FROM responses
            WHERE id = $id;
            """;
        AddParameter(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponse(reader) : null;
    }

    public async Task<bool> DeleteResponseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM responses WHERE id = $id;";
        AddParameter(command, "$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<StoredResponse?> CancelResponseAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE responses SET status = 'cancelled' WHERE id = $id;";
        AddParameter(command, "$id", id);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetResponseAsync(id, cancellationToken);
    }

    public async Task<StoredResponse?> UpdateResponseMetadataAsync(string id, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE responses SET metadata_json = $metadata_json WHERE id = $id;";
        AddParameter(command, "$id", id);
        AddParameter(command, "$metadata_json", SerializeNullable(metadata));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetResponseAsync(id, cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
            await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE TABLE IF NOT EXISTS chat_completions (
                    id TEXT PRIMARY KEY,
                    created INTEGER NOT NULL,
                    model TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    user TEXT NULL,
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
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE INDEX IF NOT EXISTS idx_chat_completions_created_id
                ON chat_completions (created DESC, id ASC);
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE TABLE IF NOT EXISTS responses (
                    id TEXT PRIMARY KEY,
                    created_at INTEGER NOT NULL,
                    status TEXT NOT NULL,
                    model TEXT NOT NULL,
                    metadata_json TEXT NULL,
                    user TEXT NULL,
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
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE INDEX IF NOT EXISTS idx_responses_created_id
                ON responses (created_at DESC, id ASC);
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE INDEX IF NOT EXISTS idx_responses_previous_response_id
                ON responses (previous_response_id);
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE TABLE IF NOT EXISTS response_tasks (
                    id TEXT PRIMARY KEY,
                    type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    source_response_id TEXT NULL,
                    result_response_id TEXT NULL,
                    error_message TEXT NULL,
                    created_at INTEGER NOT NULL,
                    completed_at INTEGER NULL
                );
                """,
                cancellationToken);
            await ExecuteNonQueryAsync(connection,
                """
                CREATE INDEX IF NOT EXISTS idx_response_tasks_created_id
                ON response_tasks (created_at DESC, id ASC);
                """,
                cancellationToken);

            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            Store = reader.GetInt64(6) == 1,
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
            Store = reader.GetInt64(7) == 1,
            PreviousResponseId = GetNullableString(reader, 8),
            InputMessages = Deserialize<IReadOnlyList<InferenceMessage>>(reader.GetString(9)),
            OutputText = reader.GetString(10),
            ToolCalls = Deserialize<IReadOnlyList<OpenAiToolCall>>(reader.GetString(11)),
            InputTokens = reader.GetInt32(12),
            OutputTokens = reader.GetInt32(13),
            CompatibilityWarnings = Deserialize<IReadOnlyList<string>>(reader.GetString(14))
        };
    }

    private static string CreateConnectionString(LLamaStoreOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return options.ConnectionString;
        }

        var path = string.IsNullOrWhiteSpace(options.SqlitePath) ? "data/llamastack.db" : options.SqlitePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        return builder.ToString();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
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

    public async Task AddResponseTaskAsync(ResponseTaskInfo task, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR REPLACE INTO response_tasks
                (id, type, status, source_response_id, result_response_id, error_message, created_at, completed_at)
            VALUES
                ($id, $type, $status, $source_response_id, $result_response_id, $error_message, $created_at, $completed_at);
            """;
        AddParameter(command, "$id", task.Id);
        AddParameter(command, "$type", task.Type);
        AddParameter(command, "$status", SerializeStatus(task.Status));
        AddParameter(command, "$source_response_id", task.SourceResponseId);
        AddParameter(command, "$result_response_id", task.ResultResponseId);
        AddParameter(command, "$error_message", task.ErrorMessage);
        AddParameter(command, "$created_at", task.CreatedAt);
        AddParameter(command, "$completed_at", task.CompletedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ResponseTaskInfo?> GetResponseTaskAsync(string id, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, type, status, source_response_id, result_response_id, error_message, created_at, completed_at
            FROM response_tasks
            WHERE id = $id;
            """;
        AddParameter(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResponseTask(reader) : null;
    }

    public async Task UpdateResponseTaskAsync(string id, ResponseTaskStatus status, string? resultResponseId = null, string? errorMessage = null, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE response_tasks
            SET status = $status,
                result_response_id = COALESCE($result_response_id, result_response_id),
                error_message = COALESCE($error_message, error_message),
                completed_at = COALESCE($completed_at, completed_at)
            WHERE id = $id;
            """;
        AddParameter(command, "$id", id);
        AddParameter(command, "$status", SerializeStatus(status));
        AddParameter(command, "$result_response_id", resultResponseId);
        AddParameter(command, "$error_message", errorMessage);
        AddParameter(command, "$completed_at", status is ResponseTaskStatus.Completed or ResponseTaskStatus.Failed
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            : null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredListResult<ResponseTaskInfo>> ListResponseTasksAsync(int limit, string? after, string? before, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var all = new List<ResponseTaskInfo>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, type, status, source_response_id, result_response_id, error_message, created_at, completed_at
            FROM response_tasks
            ORDER BY created_at DESC, id ASC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            all.Add(ReadResponseTask(reader));
        }

        var result = OpenAiStoreHelpers.ApplyCursor(all, x => x.Id, x => x.CreatedAt, limit, after, before);
        return new StoredListResult<ResponseTaskInfo>(result.Items, result.HasMore);
    }

    private static string? GetNullableString(IDataRecord reader, int index)
    {
        return reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static ResponseTaskInfo ReadResponseTask(IDataRecord reader)
    {
        return new ResponseTaskInfo
        {
            Id = reader.GetString(0),
            Type = reader.GetString(1),
            Status = DeserializeStatus(reader.GetString(2)),
            SourceResponseId = GetNullableString(reader, 3),
            ResultResponseId = GetNullableString(reader, 4),
            ErrorMessage = GetNullableString(reader, 5),
            CreatedAt = reader.GetInt64(6),
            CompletedAt = reader.IsDBNull(7) ? null : reader.GetInt64(7)
        };
    }

    private static string SerializeStatus(ResponseTaskStatus status)
    {
        return status switch
        {
            ResponseTaskStatus.Running => "running",
            ResponseTaskStatus.Completed => "completed",
            ResponseTaskStatus.Failed => "failed",
            _ => "pending"
        };
    }

    private static ResponseTaskStatus DeserializeStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "running" => ResponseTaskStatus.Running,
            "completed" => ResponseTaskStatus.Completed,
            "failed" => ResponseTaskStatus.Failed,
            _ => ResponseTaskStatus.Pending
        };
    }
}
