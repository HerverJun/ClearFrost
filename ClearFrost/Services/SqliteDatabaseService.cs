using System;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ClearFrost.Interfaces;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    /// <summary>
    /// SQLite 数据库服务实现
    /// </summary>
    public class SqliteDatabaseService : IDatabaseService
    {
        private const int BusyTimeoutMs = 5000;

        private readonly string _connectionString;
        private readonly string _dbPath;
        private bool _initialized = false;
        private bool _disposed = false;

        public SqliteDatabaseService(string? dbPath = null)
        {
            // 默认数据库路径：运行时可写目录/Data/detection.db
            if (string.IsNullOrEmpty(dbPath))
            {
                _dbPath = RuntimePaths.DatabasePath;
                TryMigrateLegacyDatabases(
                    new[]
                    {
                        RuntimePaths.LegacySharedDatabasePath,
                        RuntimePaths.LegacyDatabasePath
                    },
                    _dbPath);
            }
            else
            {
                _dbPath = dbPath;
            }

            string directory = Path.GetDirectoryName(_dbPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connectionString = $"Data Source={_dbPath};Cache=Shared;Pooling=True;Default Timeout={BusyTimeoutMs / 1000}";
            Debug.WriteLine($"[SqliteDatabaseService] Database path: {_dbPath}");
        }

        private static void TryMigrateLegacyDatabases(IEnumerable<string> sourcePaths, string runtimeDbPath)
        {
            try
            {
                string[] legacySources = sourcePaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(path => Path.GetFullPath(path))
                    .Where(path => !PathsEqual(path, runtimeDbPath) && File.Exists(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (legacySources.Length == 0)
                {
                    return;
                }

                EnsureDatabaseDirectory(runtimeDbPath);

                using var connection = OpenDatabase(runtimeDbPath);
                EnsureSchema(connection);

                foreach (string legacyPath in legacySources)
                {
                    if (!TryImportFromLegacyDatabase(connection, legacyPath))
                    {
                        continue;
                    }

                    Debug.WriteLine($"[SqliteDatabaseService] Migrated legacy records from {legacyPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Legacy migration skipped: {ex.Message}");
            }
        }

        private static bool TryImportFromLegacyDatabase(SqliteConnection destinationConnection, string legacyDbPath)
        {
            const string alias = "legacy_db";

            try
            {
                using (var attachCommand = destinationConnection.CreateCommand())
                {
                    attachCommand.CommandText = $"ATTACH DATABASE $sourcePath AS {alias};";
                    attachCommand.Parameters.AddWithValue("$sourcePath", legacyDbPath);
                    attachCommand.ExecuteNonQuery();
                }

                if (!HasDetectionRecordsTable(destinationConnection, alias))
                {
                    return false;
                }

                using var importCommand = destinationConnection.CreateCommand();
                importCommand.CommandText = $@"
                    INSERT INTO DetectionRecords
                    (
                        Timestamp,
                        IsQualified,
                        TargetLabel,
                        ExpectedCount,
                        ActualCount,
                        InferenceMs,
                        ModelName,
                        CameraId,
                        ResultJson
                    )
                    SELECT
                        Timestamp,
                        IsQualified,
                        TargetLabel,
                        ExpectedCount,
                        ActualCount,
                        InferenceMs,
                        ModelName,
                        CameraId,
                        ResultJson
                    FROM {alias}.DetectionRecords
                    EXCEPT
                    SELECT
                        Timestamp,
                        IsQualified,
                        TargetLabel,
                        ExpectedCount,
                        ActualCount,
                        InferenceMs,
                        ModelName,
                        CameraId,
                        ResultJson
                    FROM DetectionRecords;
                ";

                importCommand.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Failed to import legacy database {legacyDbPath}: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    using var detachCommand = destinationConnection.CreateCommand();
                    detachCommand.CommandText = $"DETACH DATABASE {alias};";
                    detachCommand.ExecuteNonQuery();
                }
                catch
                {
                    // 忽略 detach 失败，避免影响主流程
                }
            }
        }

        private static bool HasDetectionRecordsTable(SqliteConnection connection, string databaseAlias)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                SELECT 1
                FROM {databaseAlias}.sqlite_master
                WHERE type = 'table' AND name = 'DetectionRecords'
                LIMIT 1;
            ";

            return command.ExecuteScalar() != null;
        }

        private static SqliteConnection OpenDatabase(string dbPath)
        {
            var connection = new SqliteConnection($"Data Source={dbPath};Cache=Shared;Pooling=True;Default Timeout={BusyTimeoutMs / 1000}");
            connection.Open();
            ConfigureConnection(connection);
            return connection;
        }

        private async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await ConfigureConnectionAsync(connection);
            return connection;
        }

        private static void ConfigureConnection(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                PRAGMA busy_timeout = {BusyTimeoutMs};
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            command.ExecuteNonQuery();
        }

        private static async Task ConfigureConnectionAsync(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $@"
                PRAGMA busy_timeout = {BusyTimeoutMs};
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            await command.ExecuteNonQueryAsync();
        }

        private static void EnsureDatabaseDirectory(string dbPath)
        {
            string directory = Path.GetDirectoryName(dbPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS DetectionRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    IsQualified INTEGER NOT NULL,
                    TargetLabel TEXT,
                    ExpectedCount INTEGER,
                    ActualCount INTEGER,
                    InferenceMs INTEGER,
                    ModelName TEXT,
                    CameraId TEXT,
                    ResultJson TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_timestamp ON DetectionRecords(Timestamp);
                CREATE INDEX IF NOT EXISTS idx_qualified ON DetectionRecords(IsQualified);
            ";
            command.ExecuteNonQuery();
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                using var connection = await OpenConnectionAsync();

                EnsureSchema(connection);

                _initialized = true;
                Debug.WriteLine("[SqliteDatabaseService] Database initialized");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Init error: {ex.Message}");
                throw;
            }
        }

        public async Task SaveDetectionRecordAsync(DetectionRecord record)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                using var connection = await OpenConnectionAsync();

                string insertSql = @"
                    INSERT INTO DetectionRecords 
                    (Timestamp, IsQualified, TargetLabel, ExpectedCount, ActualCount, InferenceMs, ModelName, CameraId, ResultJson)
                    VALUES (@Timestamp, @IsQualified, @TargetLabel, @ExpectedCount, @ActualCount, @InferenceMs, @ModelName, @CameraId, @ResultJson)
                ";

                using var command = new SqliteCommand(insertSql, connection);
                command.Parameters.AddWithValue("@Timestamp", record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                command.Parameters.AddWithValue("@IsQualified", record.IsQualified ? 1 : 0);
                command.Parameters.AddWithValue("@TargetLabel", record.TargetLabel ?? "");
                command.Parameters.AddWithValue("@ExpectedCount", record.ExpectedCount);
                command.Parameters.AddWithValue("@ActualCount", record.ActualCount);
                command.Parameters.AddWithValue("@InferenceMs", record.InferenceMs);
                command.Parameters.AddWithValue("@ModelName", record.ModelName ?? "");
                command.Parameters.AddWithValue("@CameraId", record.CameraId ?? "");
                command.Parameters.AddWithValue("@ResultJson", record.ResultJson ?? "");

                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Save error: {ex.Message}");
                Trace.TraceError($"[SqliteDatabaseService] Save error: {ex}");
                throw;
            }
        }

        public async Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
        {
            if (!_initialized) await InitializeAsync();

            var records = new List<DetectionRecord>();

            try
            {
                using var connection = await OpenConnectionAsync();

                var conditions = new List<string>();
                if (startDate.HasValue)
                    conditions.Add("Timestamp >= @StartDate");
                if (endDate.HasValue)
                    conditions.Add("Timestamp <= @EndDate");
                if (isQualified.HasValue)
                    conditions.Add("IsQualified = @IsQualified");

                string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
                string querySql = $"SELECT * FROM DetectionRecords {whereClause} ORDER BY Timestamp DESC, Id DESC LIMIT @Limit";

                using var command = new SqliteCommand(querySql, connection);
                command.Parameters.AddWithValue("@Limit", limit);

                if (startDate.HasValue)
                    command.Parameters.AddWithValue("@StartDate", startDate.Value.ToString("yyyy-MM-dd 00:00:00.000"));
                if (endDate.HasValue)
                    command.Parameters.AddWithValue("@EndDate", endDate.Value.ToString("yyyy-MM-dd 23:59:59.999"));
                if (isQualified.HasValue)
                    command.Parameters.AddWithValue("@IsQualified", isQualified.Value ? 1 : 0);

                using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    records.Add(new DetectionRecord
                    {
                        Id = reader.GetInt64(0),
                        Timestamp = DateTime.Parse(reader.GetString(1)),
                        IsQualified = reader.GetInt32(2) == 1,
                        TargetLabel = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        ExpectedCount = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                        ActualCount = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                        InferenceMs = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        ModelName = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        CameraId = reader.IsDBNull(8) ? "" : reader.GetString(8),
                        ResultJson = reader.IsDBNull(9) ? "" : reader.GetString(9)
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Query error: {ex.Message}");
            }

            return records;
        }

        public async Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                using var connection = await OpenConnectionAsync();

                string dateStr = date.ToString("yyyy-MM-dd");
                string sql = @"
                    SELECT 
                        COUNT(*) as Total,
                        SUM(CASE WHEN IsQualified = 1 THEN 1 ELSE 0 END) as Pass,
                        SUM(CASE WHEN IsQualified = 0 THEN 1 ELSE 0 END) as Fail
                    FROM DetectionRecords 
                    WHERE Timestamp LIKE @DatePattern
                ";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@DatePattern", dateStr + "%");

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    int total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    int pass = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    int fail = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    return (total, pass, fail);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Statistics error: {ex.Message}");
            }

            return (0, 0, 0);
        }

        public async Task<int> CleanupOldRecordsAsync(int daysToKeep)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                using var connection = await OpenConnectionAsync();

                string cutoffDate = DateTime.Now.AddDays(-daysToKeep).ToString("yyyy-MM-dd 00:00:00.000");
                string sql = "DELETE FROM DetectionRecords WHERE Timestamp < @CutoffDate";

                using var command = new SqliteCommand(sql, connection);
                command.Parameters.AddWithValue("@CutoffDate", cutoffDate);

                int deleted = await command.ExecuteNonQueryAsync();
                Debug.WriteLine($"[SqliteDatabaseService] Cleaned up {deleted} old records");
                return deleted;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Cleanup error: {ex.Message}");
                return 0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Debug.WriteLine("[SqliteDatabaseService] Disposed");
        }
    }
}
