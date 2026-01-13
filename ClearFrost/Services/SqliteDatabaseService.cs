using System.IO;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    /// <summary>
    /// SQLite 数据库服务实现
    /// </summary>
    public class SqliteDatabaseService : IDatabaseService
    {
        private readonly string _connectionString;
        private readonly string _dbPath;
        private bool _initialized = false;
        private bool _disposed = false;

        public SqliteDatabaseService(string? dbPath = null)
        {
            // 默认数据库路径：程序目录/Data/detection.db
            _dbPath = dbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "detection.db");

            // 确保目录存在
            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _connectionString = $"Data Source={_dbPath}";
            Debug.WriteLine($"[SqliteDatabaseService] Database path: {_dbPath}");
        }

        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                // 创建检测记录表
                string createTableSql = @"
                    CREATE TABLE IF NOT EXISTS detection_records (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Timestamp DATETIME NOT NULL,
                        IsQualified INTEGER NOT NULL,
                        TargetLabel TEXT,
                        ExpectedCount INTEGER,
                        ActualCount INTEGER,
                        InferenceMs INTEGER,
                        ModelName TEXT,
                        CameraId TEXT,
                        ResultJson TEXT,
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                    );
                    
                    CREATE INDEX IF NOT EXISTS idx_timestamp ON detection_records(Timestamp);
                    CREATE INDEX IF NOT EXISTS idx_qualified ON detection_records(IsQualified);
                ";

                using var command = new SqliteCommand(createTableSql, connection);
                await command.ExecuteNonQueryAsync();

                _initialized = true;
                Debug.WriteLine("[SqliteDatabaseService] Database initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] Initialize error: {ex.Message}");
                throw;
            }
        }

        public async Task<long> SaveDetectionRecordAsync(DetectionRecord record)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string insertSql = @"
                    INSERT INTO detection_records 
                    (Timestamp, IsQualified, TargetLabel, ExpectedCount, ActualCount, InferenceMs, ModelName, CameraId, ResultJson)
                    VALUES 
                    (@Timestamp, @IsQualified, @TargetLabel, @ExpectedCount, @ActualCount, @InferenceMs, @ModelName, @CameraId, @ResultJson);
                    SELECT last_insert_rowid();
                ";

                using var command = new SqliteCommand(insertSql, connection);
                command.Parameters.AddWithValue("@Timestamp", record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
                command.Parameters.AddWithValue("@IsQualified", record.IsQualified ? 1 : 0);
                command.Parameters.AddWithValue("@TargetLabel", record.TargetLabel ?? "");
                command.Parameters.AddWithValue("@ExpectedCount", record.ExpectedCount);
                command.Parameters.AddWithValue("@ActualCount", record.ActualCount);
                command.Parameters.AddWithValue("@InferenceMs", record.InferenceMs);
                command.Parameters.AddWithValue("@ModelName", record.ModelName ?? "");
                command.Parameters.AddWithValue("@CameraId", record.CameraId ?? "");
                command.Parameters.AddWithValue("@ResultJson", record.ResultJson ?? "");

                var result = await command.ExecuteScalarAsync();
                long id = Convert.ToInt64(result);

                Debug.WriteLine($"[SqliteDatabaseService] Saved record ID: {id}");
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] SaveDetectionRecord error: {ex.Message}");
                throw;
            }
        }

        public async Task<List<DetectionRecord>> QueryRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
        {
            if (!_initialized) await InitializeAsync();

            var records = new List<DetectionRecord>();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var conditions = new List<string>();
                if (startDate.HasValue) conditions.Add("Timestamp >= @StartDate");
                if (endDate.HasValue) conditions.Add("Timestamp <= @EndDate");
                if (isQualified.HasValue) conditions.Add("IsQualified = @IsQualified");

                string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
                string querySql = $"SELECT * FROM detection_records {whereClause} ORDER BY Timestamp DESC LIMIT @Limit";

                using var command = new SqliteCommand(querySql, connection);
                if (startDate.HasValue) command.Parameters.AddWithValue("@StartDate", startDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                if (endDate.HasValue) command.Parameters.AddWithValue("@EndDate", endDate.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                if (isQualified.HasValue) command.Parameters.AddWithValue("@IsQualified", isQualified.Value ? 1 : 0);
                command.Parameters.AddWithValue("@Limit", limit);

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
                Debug.WriteLine($"[SqliteDatabaseService] QueryRecords error: {ex.Message}");
            }

            return records;
        }

        public async Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string dateStr = date.ToString("yyyy-MM-dd");
                string querySql = @"
                    SELECT 
                        COUNT(*) as total,
                        SUM(CASE WHEN IsQualified = 1 THEN 1 ELSE 0 END) as pass,
                        SUM(CASE WHEN IsQualified = 0 THEN 1 ELSE 0 END) as fail
                    FROM detection_records 
                    WHERE date(Timestamp) = @Date
                ";

                using var command = new SqliteCommand(querySql, connection);
                command.Parameters.AddWithValue("@Date", dateStr);

                using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    int total = reader.GetInt32(0);
                    int pass = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    int fail = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    return (total, pass, fail);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SqliteDatabaseService] GetStatistics error: {ex.Message}");
            }

            return (0, 0, 0);
        }

        public async Task<int> CleanupOldRecordsAsync(int daysToKeep)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                string deleteSql = "DELETE FROM detection_records WHERE Timestamp < @CutoffDate";
                using var command = new SqliteCommand(deleteSql, connection);
                command.Parameters.AddWithValue("@CutoffDate", DateTime.Now.AddDays(-daysToKeep).ToString("yyyy-MM-dd HH:mm:ss"));

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

