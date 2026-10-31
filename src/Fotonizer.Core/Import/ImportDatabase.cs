using System.Globalization;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using Microsoft.Data.Sqlite;

namespace Fotonizer.Core.Import;

/// <summary>
/// SQLite-backed library index. Used for duplicate detection, upload queue state and stats.
/// </summary>
public sealed class ImportDatabase : IDisposable
{
    private readonly string _connectionString;

    public string FilePath { get; }

    public ImportDatabase(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.DefaultDatabasePath;
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS files (
                sha256 TEXT PRIMARY KEY,
                quick_hash TEXT NOT NULL,
                source_path TEXT,
                dest_path TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                captured_at TEXT,
                imported_at TEXT NOT NULL,
                upload_state INTEGER NOT NULL DEFAULT 0,
                upload_attempts INTEGER NOT NULL DEFAULT 0,
                upload_error TEXT,
                google_id TEXT,
                uploaded_at TEXT,
                burst_group TEXT,
                score REAL,
                is_burst_pick INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_files_quick ON files(quick_hash);
            CREATE INDEX IF NOT EXISTS idx_files_upload ON files(upload_state, upload_attempts);

            CREATE TABLE IF NOT EXISTS seen_sources (
                source_key TEXT PRIMARY KEY,
                seen_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS skipped_files (
                quick_hash TEXT PRIMARY KEY,
                reason TEXT,
                seen_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static string? Iso(DateTime? value) =>
        value?.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(object? value)
    {
        if (value is null or DBNull) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces, out var dt)
            ? DateTime.SpecifyKind(dt, DateTimeKind.Unspecified)
            : null;
    }

    private static int ScalarInt(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public bool ContainsSha(string sha256)
    {
        using var conn = Open();
        return ScalarInt(conn, "SELECT COUNT(1) FROM files WHERE sha256=@sha", ("@sha", sha256)) > 0;
    }

    public bool ContainsQuickHash(string quickHash)
    {
        using var conn = Open();
        return ScalarInt(conn, "SELECT COUNT(1) FROM files WHERE quick_hash=@q", ("@q", quickHash)) > 0;
    }

    public bool IsSkippedHash(string quickHash)
    {
        if (string.IsNullOrWhiteSpace(quickHash)) return false;
        using var conn = Open();
        return ScalarInt(conn, "SELECT COUNT(1) FROM skipped_files WHERE quick_hash=@q", ("@q", quickHash)) > 0;
    }

    /// <summary>Remembers a file that was deliberately not imported (e.g. a burst loser).</summary>
    public void MarkSkippedHash(string quickHash, string reason)
    {
        if (string.IsNullOrWhiteSpace(quickHash)) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO skipped_files(quick_hash, reason, seen_at) VALUES(@q, @reason, @at);
            """;
        cmd.Parameters.AddWithValue("@q", quickHash);
        cmd.Parameters.AddWithValue("@reason", Truncate(reason, 200));
        cmd.Parameters.AddWithValue("@at", Iso(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Inserts a record. Returns false when the same SHA-256 is already known.</summary>
    public bool AddFile(ImportedFileRecord record)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO files
              (sha256, quick_hash, source_path, dest_path, file_name, size, captured_at, imported_at,
               upload_state, upload_attempts, upload_error, google_id, uploaded_at, burst_group, score, is_burst_pick)
            VALUES
              (@sha, @quick, @source, @dest, @name, @size, @captured, @imported,
               @state, @attempts, @error, @google, @uploaded, @burst, @score, @pick);
            """;
        cmd.Parameters.AddWithValue("@sha", record.Sha256);
        cmd.Parameters.AddWithValue("@quick", record.QuickHash);
        cmd.Parameters.AddWithValue("@source", (object?)record.SourcePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dest", record.DestPath);
        cmd.Parameters.AddWithValue("@name", record.FileName);
        cmd.Parameters.AddWithValue("@size", record.Size);
        cmd.Parameters.AddWithValue("@captured", (object?)Iso(record.CapturedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@imported", Iso(record.ImportedAt) ?? DateTime.Now.ToString("o"));
        cmd.Parameters.AddWithValue("@state", (int)record.UploadState);
        cmd.Parameters.AddWithValue("@attempts", record.UploadAttempts);
        cmd.Parameters.AddWithValue("@error", (object?)record.UploadError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@google", (object?)record.GoogleMediaItemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@uploaded", (object?)Iso(record.UploadedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@burst", (object?)record.BurstGroup ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@score", (object?)record.Score ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pick", record.IsBurstPick ? 1 : 0);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<ImportedFileRecord> GetPendingUploads(int limit, int retryLimit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sha256, quick_hash, source_path, dest_path, file_name, size, captured_at, imported_at,
                   upload_state, upload_attempts, upload_error, google_id, uploaded_at, burst_group, score, is_burst_pick
            FROM files
            WHERE upload_state = 0 AND upload_attempts < @retry
            ORDER BY size ASC, imported_at
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@retry", retryLimit);
        cmd.Parameters.AddWithValue("@limit", limit);

        var result = new List<ImportedFileRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ImportedFileRecord
            {
                Sha256 = reader.GetString(0),
                QuickHash = reader.GetString(1),
                SourcePath = reader.IsDBNull(2) ? null : reader.GetString(2),
                DestPath = reader.GetString(3),
                FileName = reader.GetString(4),
                Size = reader.GetInt64(5),
                CapturedAt = ParseDate(reader.GetValue(6)) ?? DateTime.MinValue,
                ImportedAt = ParseDate(reader.GetValue(7)) ?? DateTime.MinValue,
                UploadState = (UploadState)reader.GetInt32(8),
                UploadAttempts = reader.GetInt32(9),
                UploadError = reader.IsDBNull(10) ? null : reader.GetString(10),
                GoogleMediaItemId = reader.IsDBNull(11) ? null : reader.GetString(11),
                UploadedAt = ParseDate(reader.GetValue(12)),
                BurstGroup = reader.IsDBNull(13) ? null : reader.GetString(13),
                Score = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                IsBurstPick = reader.GetInt32(15) != 0
            });
        }

        return result;
    }

    public void MarkUploaded(string sha256, string googleMediaItemId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE files
            SET upload_state=1, google_id=@google, uploaded_at=@at, upload_error=NULL
            WHERE sha256=@sha;
            """;
        cmd.Parameters.AddWithValue("@google", googleMediaItemId);
        cmd.Parameters.AddWithValue("@at", Iso(DateTime.Now));
        cmd.Parameters.AddWithValue("@sha", sha256);
        cmd.ExecuteNonQuery();
    }

    public void MarkUploadAttemptFailed(string sha256, string error, int retryLimit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE files
            SET upload_attempts = upload_attempts + 1,
                upload_error = @error,
                upload_state = CASE WHEN upload_attempts + 1 >= @retry THEN 2 ELSE 0 END
            WHERE sha256=@sha;
            """;
        cmd.Parameters.AddWithValue("@error", Truncate(error, 500));
        cmd.Parameters.AddWithValue("@retry", retryLimit);
        cmd.Parameters.AddWithValue("@sha", sha256);
        cmd.ExecuteNonQuery();
    }

    public void MarkUploadSkipped(string sha256, string reason)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET upload_state=3, upload_error=@reason WHERE sha256=@sha;";
        cmd.Parameters.AddWithValue("@reason", Truncate(reason, 500));
        cmd.Parameters.AddWithValue("@sha", sha256);
        cmd.ExecuteNonQuery();
    }

    public void ResetFailedUploads()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE files SET upload_state=0, upload_attempts=0 WHERE upload_state=2;";
        cmd.ExecuteNonQuery();
    }

    public bool IsSourceSeen(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return false;
        using var conn = Open();
        return ScalarInt(conn, "SELECT COUNT(1) FROM seen_sources WHERE source_key=@k", ("@k", sourceKey)) > 0;
    }

    public void MarkSourceSeen(string sourceKey)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) return;
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO seen_sources(source_key, seen_at) VALUES(@k, @at);";
        cmd.Parameters.AddWithValue("@k", sourceKey);
        cmd.Parameters.AddWithValue("@at", Iso(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    public ImportStats GetStats()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(1),
                   COALESCE(SUM(size), 0),
                   COALESCE(SUM(CASE WHEN upload_state=1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN upload_state=0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN upload_state=2 THEN 1 ELSE 0 END), 0),
                   MAX(imported_at)
            FROM files;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return new ImportStats(0, 0, 0, 0, 0, null);
        return new ImportStats(
            reader.GetInt32(0),
            reader.GetInt64(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            ParseDate(reader.GetValue(5)));
    }

    public string? GetMeta(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=@k;";
        cmd.Parameters.AddWithValue("@k", key);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public void SetMeta(string key, string? value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES(@k, @v) ON CONFLICT(key) DO UPDATE SET value=@v;";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void RecordError(string message)
    {
        AppLog.Shared.Warn(message);
        SetMeta("last_error", $"{DateTime.Now:O} {message}");
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max];

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
        }
    }
}
