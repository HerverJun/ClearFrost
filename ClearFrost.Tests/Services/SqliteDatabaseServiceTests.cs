using System.Reflection;
using ClearFrost.Core.Inspection;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ClearFrost.Tests.Services;

public class SqliteDatabaseServiceTests
{
    [Fact]
    public void LegacyMigration_旧数据库记录会导入到运行时数据库()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string runtimeDbPath = Path.Combine(tempDir, "runtime", "detection.db");
            string legacyDbPath = Path.Combine(tempDir, "legacy", "detection.db");
            CreateDatabaseWithRows(legacyDbPath, "2026-04-08 10:00:00", "2026-04-08 10:01:00");

            InvokeMigration(new[] { legacyDbPath }, runtimeDbPath);

            CountRows(runtimeDbPath).Should().Be(2);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LegacyMigration_会合并多个旧库且避免重复导入()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string runtimeDbPath = Path.Combine(tempDir, "runtime", "detection.db");
            string legacyAppDbPath = Path.Combine(tempDir, "legacy-app", "detection.db");
            string legacySharedDbPath = Path.Combine(tempDir, "legacy-shared", "detection.db");

            CreateDatabaseWithRows(runtimeDbPath, "2026-04-08 09:00:00");
            CreateDatabaseWithRows(legacyAppDbPath, "2026-04-08 08:00:00", "2026-04-08 09:00:00");
            CreateDatabaseWithRows(legacySharedDbPath, "2026-04-08 09:00:00", "2026-04-08 10:00:00");

            InvokeMigration(new[] { legacySharedDbPath, legacyAppDbPath }, runtimeDbPath);

            CountRows(runtimeDbPath).Should().Be(3);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InitializeAsync_旧表只追加追溯列()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string dbPath = Path.Combine(tempDir, "runtime", "detection.db");
            CreateDatabaseWithRows(dbPath, "2026-04-08 10:00:00");

            using var service = new SqliteDatabaseService(dbPath);
            await service.InitializeAsync();

            GetColumns(dbPath).Should().Contain(new[]
            {
                "InspectionId",
                "TraceStatus",
                "ImagePath",
                "ErrorStage",
                "ErrorCode",
                "ErrorMessage",
                "TotalMs",
                "CaptureMs",
                "PlcWriteMs",
                "UsedModelName",
                "ProductBarcode",
                "BarcodeReadSucceeded",
                "BarcodeError"
            });

            CountRows(dbPath).Should().Be(1);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SaveDetectionRecordAsync_会保存追溯字段()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string dbPath = Path.Combine(tempDir, "runtime", "detection.db");
            using var service = new SqliteDatabaseService(dbPath);
            await service.InitializeAsync();

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 4, 29, 15, 30, 12),
                IsQualified = false,
                InspectionId = "CF-20260429-153012000-MANUAL-000001",
                TriggerSource = "手动",
                TraceStatus = TraceStatus.Partial,
                ProductBarcode = "SN-20260504-0001",
                BarcodeReadSucceeded = true,
                BarcodeError = "",
                ImagePath = @"C:\Trace\FAIL_CF-20260429-153012000-MANUAL-000001.jpg",
                ErrorStage = "Capture",
                ErrorCode = "CaptureFrameFailed",
                ErrorMessage = "相机拍照失败",
                CaptureMs = 12,
                PlcWriteMs = 3,
                UsedModelName = "model-a",
                WasFallback = true,
                TargetLabel = "screw",
                ExpectedCount = 4,
                ActualCount = 0,
                InferenceMs = 0,
                ModelName = "model-a",
                CameraId = "cam-01",
                ResultJson = "{}"
            });

            List<DetectionRecord> records = await service.GetRecordsAsync(limit: 10);

            records.Should().ContainSingle();
            DetectionRecord record = records[0];
            record.InspectionId.Should().Be("CF-20260429-153012000-MANUAL-000001");
            record.TraceStatus.Should().Be(TraceStatus.Partial);
            record.ProductBarcode.Should().Be("SN-20260504-0001");
            record.BarcodeReadSucceeded.Should().BeTrue();
            record.BarcodeError.Should().BeEmpty();
            record.ErrorCode.Should().Be("CaptureFrameFailed");
            record.ImagePath.Should().Contain("FAIL_CF-20260429");
            record.CaptureMs.Should().Be(12);
            record.PlcWriteMs.Should().Be(3);
            record.UsedModelName.Should().Be("model-a");
            record.WasFallback.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static void InvokeMigration(IEnumerable<string> sourcePaths, string runtimeDbPath)
    {
        MethodInfo? method = typeof(SqliteDatabaseService).GetMethod(
            "TryMigrateLegacyDatabases",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        method!.Invoke(null, new object[] { sourcePaths, runtimeDbPath });
    }

    private static void CreateDatabaseWithRows(string dbPath, params string[] timestamps)
    {
        string directory = Path.GetDirectoryName(dbPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
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
            ";
            command.ExecuteNonQuery();
        }

        foreach (string timestamp in timestamps)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = @"
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
                VALUES
                (
                    $timestamp,
                    1,
                    'screw',
                    4,
                    4,
                    12,
                    'test-model',
                    'cam-01',
                    '{}'
                );
            ";
            insertCommand.Parameters.AddWithValue("$timestamp", timestamp);
            insertCommand.ExecuteNonQuery();
        }
    }

    private static int CountRows(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DetectionRecords;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static HashSet<string> GetColumns(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(DetectionRecords);";
        using var reader = command.ExecuteReader();

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostTests",
            nameof(SqliteDatabaseServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(path, true);
        }
    }
}
