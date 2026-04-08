using System.Reflection;
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
