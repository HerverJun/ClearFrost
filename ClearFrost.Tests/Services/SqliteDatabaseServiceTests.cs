using System.Reflection;
using ClearFrost.Core.Inspection;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace ClearFrost.Tests.Services;

[Collection(global::ClearFrost.Tests.TestCollections.SqliteGlobalPool)]
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
    public void LegacyMigration_保留旧库图片路径并兼容缺列()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string runtimeDbPath = Path.Combine(tempDir, "runtime", "detection.db");
            string legacyDbPath = Path.Combine(tempDir, "legacy", "detection.db");
            CreateDatabaseWithImagePathRow(
                legacyDbPath,
                "2026-05-04 14:24:41.681",
                @"C:\GreeVisionData\Images\Unqualified\2026年05月04日\14\FAIL_CF-20260504-142441681.jpg",
                @"C:\GreeVisionData\Images\Unqualified\2026年05月04日\14\RENDER_FAIL_CF-20260504-142441681.jpg");

            InvokeMigration(new[] { legacyDbPath }, runtimeDbPath);

            GetImagePaths(runtimeDbPath).Should().ContainSingle().Which.Should().Be((
                @"C:\GreeVisionData\Images\Unqualified\2026年05月04日\14\FAIL_CF-20260504-142441681.jpg",
                @"C:\GreeVisionData\Images\Unqualified\2026年05月04日\14\RENDER_FAIL_CF-20260504-142441681.jpg"));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LegacyMigration_跳过链接旧库来源且不导入外部记录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedLegacyDbPath = string.Empty;

        try
        {
            string runtimeDbPath = Path.Combine(tempDir, "runtime", "detection.db");
            string safeLegacyDbPath = Path.Combine(tempDir, "safe-legacy", "detection.db");
            string externalDbPath = Path.Combine(externalDir, "external.db");
            CreateDatabaseWithRows(safeLegacyDbPath);
            CreateDatabaseWithRows(externalDbPath, "2026-04-08 11:00:00");

            linkedLegacyDbPath = Path.Combine(tempDir, "legacy", "detection.db");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedLegacyDbPath)!);
            if (!TryCreateFileSymbolicLink(linkedLegacyDbPath, externalDbPath))
            {
                return;
            }

            InvokeMigration(new[] { safeLegacyDbPath, linkedLegacyDbPath }, runtimeDbPath);

            CountRows(runtimeDbPath).Should().Be(0);
            CountRows(externalDbPath).Should().Be(1);
        }
        finally
        {
            TryDeleteFileLink(linkedLegacyDbPath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void Constructor_拒绝链接数据库文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDbPath = string.Empty;

        try
        {
            linkedDbPath = Path.Combine(tempDir, "runtime", "detection.db");
            Directory.CreateDirectory(Path.GetDirectoryName(linkedDbPath)!);
            string externalDbPath = Path.Combine(externalDir, "external.db");
            File.WriteAllText(externalDbPath, "external database");
            if (!TryCreateFileSymbolicLink(linkedDbPath, externalDbPath))
            {
                return;
            }

            Action act = () => _ = new SqliteDatabaseService(linkedDbPath);

            act.Should().Throw<IOException>().WithMessage("*检测数据库文件*链接文件*");
            File.ReadAllText(externalDbPath).Should().Be("external database");
        }
        finally
        {
            TryDeleteFileLink(linkedDbPath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void Constructor_拒绝链接数据库目录且不写入外部目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDirectory = string.Empty;

        try
        {
            linkedDirectory = Path.Combine(tempDir, "runtime");
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDir))
            {
                return;
            }

            string dbPath = Path.Combine(linkedDirectory, "detection.db");

            Action act = () => _ = new SqliteDatabaseService(dbPath);

            act.Should().Throw<IOException>().WithMessage("*检测数据库目录*链接目录*");
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedDirectory);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
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
                "Barcode",
                "BarcodeReadSucceeded",
                "BarcodeError",
                "PlcTriggerSeq",
                "QueueStatus",
                "TraceImagePath",
                "RecipeVersion"
            });

            CountRows(dbPath).Should().Be(1);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task InitializeAsync_极旧表缺少检测列_补齐后仍可保存()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string dbPath = Path.Combine(tempDir, "runtime", "detection.db");
            CreateMinimalDatabaseWithRows(dbPath, "2026-04-08 10:00:00");

            using var service = new SqliteDatabaseService(dbPath);
            await service.InitializeAsync();

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 24, 41),
                IsQualified = false,
                InspectionId = "CF-20260504-142441681-TEST-000001",
                ImagePath = @"C:\GreeVisionData\Images\Unqualified\2026年05月04日\14\FAIL_CF-20260504-142441681.jpg",
                TargetLabel = "screw",
                ExpectedCount = 4,
                ActualCount = 0,
                InferenceMs = 18,
                ModelName = "model-new",
                CameraId = "cam-01",
                ResultJson = "{}"
            });

            List<DetectionRecord> records = await service.GetRecordsAsync(limit: 10);

            records.Should().HaveCount(2);
            records[0].InspectionId.Should().Be("CF-20260504-142441681-TEST-000001");
            records[0].TargetLabel.Should().Be("screw");
            records[0].ImagePath.Should().Contain("FAIL_CF-20260504");
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
                TriggerSeq = 17,
                PlcTriggerSeq = 17,
                TraceStatus = TraceStatus.Partial,
                QueueStatus = "{\"TraceStatus\":\"Partial\"}",
                ProductBarcode = "SN-20260504-0001",
                Barcode = "SN-20260504-0001",
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
                RecipeVersion = "20260429153012000",
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
            record.Barcode.Should().Be("SN-20260504-0001");
            record.PlcTriggerSeq.Should().Be(17);
            record.QueueStatus.Should().Contain("Partial");
            record.BarcodeReadSucceeded.Should().BeTrue();
            record.BarcodeError.Should().BeEmpty();
            record.ErrorCode.Should().Be("CaptureFrameFailed");
            record.ImagePath.Should().Contain("FAIL_CF-20260429");
            record.TraceImagePath.Should().Contain("FAIL_CF-20260429");
            record.RecipeVersion.Should().Be("20260429153012000");
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

    [Fact]
    public async Task GetTraceRecordsAsync_按条码和时间范围过滤并优先返回最新记录()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string dbPath = Path.Combine(tempDir, "runtime", "detection.db");
            using var service = new SqliteDatabaseService(dbPath);
            await service.InitializeAsync();

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 0, 0),
                IsQualified = false,
                InspectionId = "CF-20260504-140000-AAA",
                ProductBarcode = "SN-001",
                ModelVersion = "v1",
                ModelName = "model-a",
                CameraId = "cam-01",
                ImagePath = @"C:\Trace\FAIL_1.jpg",
                RenderedImagePath = @"C:\Trace\Rendered\FAIL_1_rendered.jpg"
            });

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 30, 0),
                IsQualified = false,
                InspectionId = "CF-20260504-143000-BBB",
                ProductBarcode = "SN-002",
                ModelVersion = "v2",
                ModelName = "model-b",
                CameraId = "cam-02",
                ErrorStage = "Barcode",
                ErrorCode = "NoBarcode",
                ErrorMessage = "PLC 条码为空",
                ImagePath = @"C:\Trace\FAIL_2.jpg",
                RenderedImagePath = @"C:\Trace\Rendered\FAIL_2_rendered.jpg"
            });

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 45, 0),
                IsQualified = true,
                InspectionId = "CF-20260504-144500-CCC",
                ProductBarcode = "SN-002",
                ModelVersion = "v2",
                ModelName = "model-b",
                CameraId = "cam-02",
                ImagePath = @"C:\Trace\PASS_3.jpg",
                RenderedImagePath = @"C:\Trace\Rendered\PASS_3_rendered.jpg"
            });

            List<DetectionTraceRecord> records = await service.GetTraceRecordsAsync(new DetectionTraceQuery
            {
                ProductBarcode = "SN-002",
                InspectionId = "CF-20260504-143000-BBB",
                IsQualified = false,
                StartTime = new DateTime(2026, 5, 4, 14, 0, 0),
                EndTime = new DateTime(2026, 5, 4, 14, 59, 59, 999),
                Limit = 300
            });

            records.Should().HaveCount(1);
            records[0].InspectionId.Should().Be("CF-20260504-143000-BBB");
            records[0].ProductBarcode.Should().Be("SN-002");
            records[0].IsQualified.Should().BeFalse();
            records[0].ErrorStage.Should().Be("Barcode");
            records[0].ErrorCode.Should().Be("NoBarcode");
            records[0].ErrorMessage.Should().Be("PLC 条码为空");

            List<DetectionTraceRecord> topRecords = await service.GetTraceRecordsAsync(new DetectionTraceQuery
            {
                ProductBarcode = "SN-002",
                Limit = 2
            });

            topRecords.Should().HaveCount(2);
            topRecords[0].InspectionId.Should().Be("CF-20260504-144500-CCC");
            topRecords[1].InspectionId.Should().Be("CF-20260504-143000-BBB");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GetReplayRecordsAsync_支持条码模型和配方版本筛选()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string dbPath = Path.Combine(tempDir, "runtime", "detection.db");
            using var service = new SqliteDatabaseService(dbPath);
            await service.InitializeAsync();

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 1, 8, 0, 0),
                IsQualified = false,
                InspectionId = "CF-REPLAY-001",
                ProductBarcode = "BC-001",
                Barcode = "BC-001",
                ModelName = "main.onnx",
                ModelVersion = "v1",
                RecipeVersion = "r1",
                ImagePath = Path.Combine(tempDir, "a.jpg")
            });
            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 1, 9, 0, 0),
                IsQualified = true,
                InspectionId = "CF-REPLAY-002",
                ProductBarcode = "BC-002",
                Barcode = "BC-002",
                ModelName = "main.onnx",
                ModelVersion = "v2",
                RecipeVersion = "r2",
                ImagePath = Path.Combine(tempDir, "b.jpg")
            });

            List<DetectionRecord> records = await service.GetReplayRecordsAsync(new DetectionReplayQuery
            {
                StartTime = new DateTime(2026, 5, 1, 0, 0, 0),
                EndTime = new DateTime(2026, 5, 1, 23, 59, 59),
                ProductOrBarcode = "BC-001",
                IsQualified = false,
                ModelName = "main.onnx",
                ModelVersion = "v1",
                RecipeVersion = "r1",
                Limit = 20
            });

            records.Should().ContainSingle();
            records[0].InspectionId.Should().Be("CF-REPLAY-001");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task GetTraceRecordPageAsync_按游标分页返回下一页且不重复()
    {
        string tempDir = CreateTempDirectory();

        try
        {
            string dbPath = Path.Combine(tempDir, "runtime", "detection.db");
            using var service = new SqliteDatabaseService(dbPath);
            await service.InitializeAsync();

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 30, 0),
                IsQualified = false,
                InspectionId = "CF-20260504-143000-AAA",
                ProductBarcode = "SN-001",
                ModelVersion = "v1",
                ModelName = "model-a",
                CameraId = "cam-01",
                ImagePath = @"C:\Trace\FAIL_A.jpg",
                RenderedImagePath = @"C:\Trace\Rendered\FAIL_A_rendered.jpg"
            });

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 30, 0),
                IsQualified = false,
                InspectionId = "CF-20260504-143000-BBB",
                ProductBarcode = "SN-002",
                ModelVersion = "v1",
                ModelName = "model-a",
                CameraId = "cam-01",
                ImagePath = @"C:\Trace\FAIL_B.jpg",
                RenderedImagePath = @"C:\Trace\Rendered\FAIL_B_rendered.jpg"
            });

            await service.SaveDetectionRecordAsync(new DetectionRecord
            {
                Timestamp = new DateTime(2026, 5, 4, 14, 20, 0),
                IsQualified = false,
                InspectionId = "CF-20260504-142000-CCC",
                ProductBarcode = "SN-003",
                ModelVersion = "v1",
                ModelName = "model-a",
                CameraId = "cam-01",
                ImagePath = @"C:\Trace\FAIL_C.jpg",
                RenderedImagePath = @"C:\Trace\Rendered\FAIL_C_rendered.jpg"
            });

            DetectionTracePage firstPage = await service.GetTraceRecordPageAsync(new DetectionTraceQuery
            {
                IsQualified = false,
                Limit = 1
            });

            firstPage.PageSize.Should().Be(1);
            firstPage.HasMore.Should().BeTrue();
            firstPage.Records.Should().ContainSingle();
            firstPage.Records[0].InspectionId.Should().Be("CF-20260504-143000-BBB");
            firstPage.NextCursorTimestamp.Should().Be("2026-05-04 14:30:00.000");
            firstPage.NextCursorId.Should().BeGreaterThan(0);

            DetectionTracePage secondPage = await service.GetTraceRecordPageAsync(new DetectionTraceQuery
            {
                IsQualified = false,
                Limit = 1,
                AfterTimestamp = firstPage.NextCursorTimestamp,
                AfterId = firstPage.NextCursorId
            });

            secondPage.HasMore.Should().BeTrue();
            secondPage.Records.Should().ContainSingle();
            secondPage.Records[0].InspectionId.Should().Be("CF-20260504-143000-AAA");
            secondPage.Records[0].InspectionId.Should().NotBe(firstPage.Records[0].InspectionId);

            DetectionTracePage thirdPage = await service.GetTraceRecordPageAsync(new DetectionTraceQuery
            {
                IsQualified = false,
                Limit = 1,
                AfterTimestamp = secondPage.NextCursorTimestamp,
                AfterId = secondPage.NextCursorId
            });

            thirdPage.HasMore.Should().BeFalse();
            thirdPage.Records.Should().ContainSingle();
            thirdPage.Records[0].InspectionId.Should().Be("CF-20260504-142000-CCC");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static void CreateMinimalDatabaseWithRows(string dbPath, params string[] timestamps)
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
                    IsQualified INTEGER NOT NULL
                );
            ";
            command.ExecuteNonQuery();
        }

        foreach (string timestamp in timestamps)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = @"
                INSERT INTO DetectionRecords (Timestamp, IsQualified)
                VALUES ($timestamp, 1);
            ";
            insertCommand.Parameters.AddWithValue("$timestamp", timestamp);
            insertCommand.ExecuteNonQuery();
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

    private static void CreateDatabaseWithImagePathRow(
        string dbPath,
        string timestamp,
        string imagePath,
        string renderedImagePath)
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
                    ImagePath TEXT,
                    RenderedImagePath TEXT
                );
            ";
            command.ExecuteNonQuery();
        }

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = @"
            INSERT INTO DetectionRecords
            (
                Timestamp,
                IsQualified,
                ImagePath,
                RenderedImagePath
            )
            VALUES
            (
                $timestamp,
                0,
                $imagePath,
                $renderedImagePath
            );
        ";
        insertCommand.Parameters.AddWithValue("$timestamp", timestamp);
        insertCommand.Parameters.AddWithValue("$imagePath", imagePath);
        insertCommand.Parameters.AddWithValue("$renderedImagePath", renderedImagePath);
        insertCommand.ExecuteNonQuery();
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

    private static List<(string ImagePath, string RenderedImagePath)> GetImagePaths(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT ImagePath, RenderedImagePath FROM DetectionRecords ORDER BY Timestamp;";
        using var reader = command.ExecuteReader();

        var paths = new List<(string ImagePath, string RenderedImagePath)>();
        while (reader.Read())
        {
            paths.Add((reader.GetString(0), reader.GetString(1)));
        }

        return paths;
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

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = Directory.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteFileLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new FileInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void TryDeleteDirectoryLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            SqliteConnection.ClearAllPools();
            var info = new DirectoryInfo(path);
            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
                return;
            }

            Directory.Delete(path, true);
        }
    }
}
