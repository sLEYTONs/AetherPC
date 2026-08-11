using System.Text.Json;
using AetherPC.Core.Abstractions;
using AetherPC.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AetherPC.Infrastructure.Data;

public sealed class SqliteStore : IHistoryStore, IAppSettingsStore, IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<SqliteStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteStore(ILogger<SqliteStore> logger)
    {
        _logger = logger;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AetherPC");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "aetherpc.db");
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                detail_json TEXT NOT NULL,
                rollback_json TEXT,
                created_at TEXT NOT NULL,
                user_name TEXT NOT NULL,
                can_rollback INTEGER NOT NULL,
                rolled_back INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS profile (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS benchmarks (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                score REAL NOT NULL,
                unit TEXT NOT NULL,
                details TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', '1');
            """;
        cmd.ExecuteNonQuery();

        // Migración suave: claves de título para re-localizar historial
        TryAlter(conn, "ALTER TABLE history ADD COLUMN title_key TEXT NOT NULL DEFAULT ''");
        TryAlter(conn, "ALTER TABLE history ADD COLUMN title_args TEXT NOT NULL DEFAULT '[]'");
    }

    private static void TryAlter(SqliteConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            /* columna ya existe */
        }
    }

    public async Task<Guid> AddAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO history(id, kind, title, detail_json, rollback_json, created_at, user_name, can_rollback, rolled_back, title_key, title_args)
                VALUES ($id, $kind, $title, $detail, $rollback, $created, $user, $can, $rolled, $titleKey, $titleArgs)
                """;
            cmd.Parameters.AddWithValue("$id", entry.Id.ToString());
            cmd.Parameters.AddWithValue("$kind", entry.Kind);
            cmd.Parameters.AddWithValue("$title", entry.Title);
            cmd.Parameters.AddWithValue("$detail", entry.DetailJson);
            cmd.Parameters.AddWithValue("$rollback", (object?)entry.RollbackJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", entry.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$user", entry.UserName);
            cmd.Parameters.AddWithValue("$can", entry.CanRollback ? 1 : 0);
            cmd.Parameters.AddWithValue("$rolled", entry.RolledBack ? 1 : 0);
            cmd.Parameters.AddWithValue("$titleKey", entry.TitleKey ?? "");
            cmd.Parameters.AddWithValue("$titleArgs", JsonSerializer.Serialize(entry.TitleArgs ?? Array.Empty<string>()));
            await cmd.ExecuteNonQueryAsync(ct);
            return entry.Id;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<HistoryEntry>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, kind, title, detail_json, rollback_json, created_at, user_name, can_rollback, rolled_back, title_key, title_args FROM history ORDER BY created_at DESC LIMIT $take";
            cmd.Parameters.AddWithValue("$take", take);
            var list = new List<HistoryEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(ReadHistory(reader));
            }
            return list;
        }
        finally { _gate.Release(); }
    }

    public async Task<HistoryEntry?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, kind, title, detail_json, rollback_json, created_at, user_name, can_rollback, rolled_back, title_key, title_args FROM history WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return ReadHistory(reader);
            return null;
        }
        finally { _gate.Release(); }
    }

    public async Task MarkRolledBackAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE history SET rolled_back=1, can_rollback=0 WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<UserProfile> LoadProfileAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM profile WHERE id=1";
            var json = (string?)await cmd.ExecuteScalarAsync(ct);
            if (string.IsNullOrWhiteSpace(json))
                return new UserProfile();
            return JsonSerializer.Deserialize<UserProfile>(json) ?? new UserProfile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo cargar el perfil");
            return new UserProfile();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveProfileAsync(UserProfile profile, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO profile(id, json) VALUES (1, $json) ON CONFLICT(id) DO UPDATE SET json=$json";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveBenchmarkAsync(BenchmarkResult result, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO benchmarks(id, kind, score, unit, details, created_at) VALUES ($id,$kind,$score,$unit,$details,$created)";
            cmd.Parameters.AddWithValue("$id", result.Id.ToString());
            cmd.Parameters.AddWithValue("$kind", result.Kind);
            cmd.Parameters.AddWithValue("$score", result.Score);
            cmd.Parameters.AddWithValue("$unit", result.Unit);
            cmd.Parameters.AddWithValue("$details", result.Details);
            cmd.Parameters.AddWithValue("$created", result.CreatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteBenchmarkAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM benchmarks WHERE id=$id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<BenchmarkResult>> ListBenchmarksAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, kind, score, unit, details, created_at FROM benchmarks ORDER BY created_at DESC LIMIT 100";
            var list = new List<BenchmarkResult>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new BenchmarkResult
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Kind = reader.GetString(1),
                    Score = reader.GetDouble(2),
                    Unit = reader.GetString(3),
                    Details = reader.GetString(4),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(5))
                });
            }
            return list;
        }
        finally { _gate.Release(); }
    }

    private static HistoryEntry ReadHistory(SqliteDataReader reader)
    {
        var titleArgs = Array.Empty<string>();
        var titleKey = "";
        try
        {
            if (reader.FieldCount > 9 && !reader.IsDBNull(9))
                titleKey = reader.GetString(9);
            if (reader.FieldCount > 10 && !reader.IsDBNull(10))
                titleArgs = JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? Array.Empty<string>();
        }
        catch { /* esquema antiguo */ }

        return new HistoryEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            Kind = reader.GetString(1),
            Title = reader.GetString(2),
            DetailJson = reader.GetString(3),
            RollbackJson = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(5)),
            UserName = reader.GetString(6),
            CanRollback = reader.GetInt32(7) == 1,
            RolledBack = reader.GetInt32(8) == 1,
            TitleKey = titleKey,
            TitleArgs = titleArgs
        };
    }

    public void Dispose() => _gate.Dispose();
}
