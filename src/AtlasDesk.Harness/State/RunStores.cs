using Microsoft.Data.Sqlite;

namespace AtlasDesk.Harness.State;

public interface IRunStore
{
    Task<RunState?> LoadAsync(string runId, CancellationToken ct = default);
    Task SaveAsync(RunState state, CancellationToken ct = default);
}

/// <summary>Stores serialized copies, so callers never share mutable state with the store.</summary>
public sealed class InMemoryRunStore : IRunStore
{
    private readonly Dictionary<string, string> _runs = new();
    private readonly object _gate = new();

    public Task<RunState?> LoadAsync(string runId, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_runs.TryGetValue(runId, out var json) ? RunState.FromJson(json) : null);
    }

    public Task SaveAsync(RunState state, CancellationToken ct = default)
    {
        lock (_gate) _runs[state.RunId] = state.ToJson();
        return Task.CompletedTask;
    }
}

/// <summary>Durable store: survives process restarts. One short-lived connection per operation.</summary>
public sealed class SqliteRunStore : IRunStore
{
    private readonly string _connectionString;

    public SqliteRunStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                state_json TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    public async Task<RunState?> LoadAsync(string runId, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT state_json FROM runs WHERE run_id = $id";
        cmd.Parameters.AddWithValue("$id", runId);
        var json = await cmd.ExecuteScalarAsync(ct) as string;
        return json is null ? null : RunState.FromJson(json);
    }

    public async Task SaveAsync(RunState state, CancellationToken ct = default)
    {
        await using var c = Open();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO runs (run_id, status, updated_utc, state_json) VALUES ($id, $status, $now, $json)
            ON CONFLICT(run_id) DO UPDATE SET status = $status, updated_utc = $now, state_json = $json;
            """;
        cmd.Parameters.AddWithValue("$id", state.RunId);
        cmd.Parameters.AddWithValue("$status", state.Status.ToCode());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$json", state.ToJson());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
