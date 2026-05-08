using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClearFrost.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClearFrost.Tests.Services;

public class DatasetCollectionServiceTests
{
    [Fact]
    public async Task CollectAsync_数据库不存在_返回失败()
    {
        string fakeDb = Path.Combine(CreateTempDirectory(), "nonexistent.db");
        string storagePath = CreateTempDirectory();
        var service = new DatasetCollectionService(fakeDb, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 10);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("数据库不存在");
    }

    [Fact]
    public async Task CollectAsync_无记录_返回失败()
    {
        string tempDir = CreateTempDirectory();
        string dbPath = CreateEmptyDatabase(tempDir);
        string storagePath = CreateTempDirectory();
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 10);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("未找到任何检测记录");
    }

    [Fact]
    public async Task CollectAsync_无数据库记录但目录有图片_直接扫描收集()
    {
        string tempDir = CreateTempDirectory();
        string dbPath = CreateEmptyDatabase(tempDir);
        string storagePath = CreateTempDirectory();
        DateTime timestamp = DateTime.Now.Date.AddHours(15).AddMinutes(10).AddSeconds(11);
        string imageDir = Path.Combine(
            storagePath,
            "Images",
            "Unqualified",
            timestamp.ToString("yyyy年MM月dd日"),
            timestamp.ToString("HH"));
        Directory.CreateDirectory(imageDir);
        File.WriteAllBytes(
            Path.Combine(imageDir, $"FAIL_{timestamp:HHmmss}123.png"),
            new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(1);
        Directory.GetFiles(Path.Combine(result.OutputDirectory, "Fail")).Should().ContainSingle();
    }

    [Fact]
    public async Task CollectAsync_有足够记录_正确收集并复制()
    {
        string tempDir = CreateTempDirectory();
        string dbPath = CreateDatabaseWithRecords(tempDir, count: 200);
        string storagePath = CreateTempDirectory();
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 20, failRatio: 0.7);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(14);
        result.PassCopied.Should().Be(6);
        Directory.Exists(result.OutputDirectory).Should().BeTrue();
        Directory.GetFiles(Path.Combine(result.OutputDirectory, "Fail")).Length.Should().Be(14);
        Directory.GetFiles(Path.Combine(result.OutputDirectory, "Pass")).Length.Should().Be(6);
    }

    [Fact]
    public async Task CollectAsync_图片文件缺失_跳过并继续()
    {
        string tempDir = CreateTempDirectory();
        string dbPath = CreateDatabaseWithRecords(tempDir, count: 100, createImageFiles: false);
        string storagePath = CreateTempDirectory();
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 20);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("图片文件在磁盘上已不存在");
    }

    [Fact]
    public async Task CollectAsync_不合格不足_自动调整比例()
    {
        string tempDir = CreateTempDirectory();
        // 只生成合格图片
        string dbPath = CreateDatabaseWithRecords(tempDir, count: 50, failRatio: 0.0);
        string storagePath = CreateTempDirectory();
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 30, failRatio: 0.7);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(0);
        result.PassCopied.Should().Be(30);
    }

    [Fact]
    public async Task CollectAsync_分散到多天的记录_按天分摊均匀()
    {
        string tempDir = CreateTempDirectory();
        string dbPath = Path.Combine(tempDir, "detection.db");
        string imageDir = Path.Combine(tempDir, "images");
        Directory.CreateDirectory(imageDir);

        // 构造 5 天、每天 4 小时的记录，共 20 条不合格
        var records = new List<(DateTime timestamp, bool isQualified, string model)>();
        for (int day = 0; day < 5; day++)
        {
            for (int hour = 0; hour < 4; hour++)
            {
                records.Add((DateTime.Now.AddDays(-day).AddHours(-hour), false, $"model-{day % 2}"));
                records.Add((DateTime.Now.AddDays(-day).AddHours(-hour), true, $"model-{day % 2}"));
            }
        }

        CreateDatabaseWithRecords(dbPath, imageDir, records);
        string storagePath = CreateTempDirectory();
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 10, failRatio: 0.5);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(5);
        result.PassCopied.Should().Be(5);
    }

    [Fact]
    public async Task CollectAsync_路径字段为空_按标准目录InspectionId匹配()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = Path.Combine(tempDir, "detection.db");
        DateTime timestamp = DateTime.Now.Date.AddHours(10).AddMinutes(20).AddSeconds(30);
        string inspectionId = "INS-NULL-PATH";
        string imageDir = Path.Combine(
            storagePath,
            "Images",
            "Qualified",
            timestamp.ToString("yyyy年MM月dd日"),
            timestamp.ToString("HH"));
        Directory.CreateDirectory(imageDir);
        File.WriteAllBytes(Path.Combine(imageDir, $"PASS_{inspectionId}.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        CreateDatabaseWithSingleRecord(dbPath, timestamp, isQualified: true, imagePath: null, renderedPath: null, inspectionId);
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 0.0);

        result.Success.Should().BeTrue();
        result.PassCopied.Should().Be(1);
        Directory.GetFiles(Path.Combine(result.OutputDirectory, "Pass")).Length.Should().Be(1);
    }

    [Fact]
    public async Task CollectAsync_渲染图缺失_回退复制原图()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = Path.Combine(tempDir, "detection.db");
        DateTime timestamp = DateTime.Now;
        string originalPath = Path.Combine(tempDir, "original.jpg");
        string missingRenderedPath = Path.Combine(tempDir, "missing-rendered.jpg");
        File.WriteAllBytes(originalPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        CreateDatabaseWithSingleRecord(dbPath, timestamp, isQualified: false, originalPath, missingRenderedPath, "INS-STALE-RENDER");
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(1);
        File.Exists(Path.Combine(result.OutputDirectory, "Fail", "original.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task CollectAsync_路径字段为空_按标准目录文件名时间匹配()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = Path.Combine(tempDir, "detection.db");
        DateTime timestamp = DateTime.Now.Date.AddHours(14).AddMinutes(30).AddSeconds(22);
        string imageDir = Path.Combine(
            storagePath,
            "Images",
            "Unqualified",
            timestamp.ToString("yyyy年MM月dd日"),
            timestamp.ToString("HH"));
        Directory.CreateDirectory(imageDir);
        File.WriteAllBytes(Path.Combine(imageDir, $"FAIL_{timestamp:HHmmss}123.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        CreateDatabaseWithSingleRecord(dbPath, timestamp, isQualified: false, imagePath: null, renderedPath: null, inspectionId: "");
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(1);
        Directory.GetFiles(Path.Combine(result.OutputDirectory, "Fail")).Length.Should().Be(1);
    }

    #region Helpers

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostTests",
            nameof(DatasetCollectionServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateEmptyDatabase(string tempDir)
    {
        string dbPath = Path.Combine(tempDir, "detection.db");
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS DetectionRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                IsQualified INTEGER NOT NULL,
                ImagePath TEXT,
                RenderedImagePath TEXT,
                ModelName TEXT,
                RecipeId TEXT,
                InspectionId TEXT
            );
        ";
        command.ExecuteNonQuery();
        return dbPath;
    }

    private static string CreateDatabaseWithRecords(string tempDir, int count, bool createImageFiles = true, double failRatio = 0.5)
    {
        string dbPath = Path.Combine(tempDir, "detection.db");
        string imageDir = Path.Combine(tempDir, "images");
        if (createImageFiles)
        {
            Directory.CreateDirectory(imageDir);
        }

        var records = new List<(DateTime timestamp, bool isQualified, string model)>();
        var random = new Random(42);
        for (int i = 0; i < count; i++)
        {
            // 分散到 10 天内不同小时
            int dayOffset = i % 10;
            int hourOffset = i % 24;
            var timestamp = DateTime.Now.AddDays(-dayOffset).AddHours(-hourOffset).AddMinutes(-(i % 60));
            bool isFail = random.NextDouble() < failRatio;
            records.Add((timestamp, !isFail, $"model-{i % 3}"));
        }

        CreateDatabaseWithRecords(dbPath, createImageFiles ? imageDir : null, records);
        return dbPath;
    }

    private static void CreateDatabaseWithSingleRecord(
        string dbPath,
        DateTime timestamp,
        bool isQualified,
        string? imagePath,
        string? renderedPath,
        string inspectionId)
    {
        string directory = Path.GetDirectoryName(dbPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var createCommand = connection.CreateCommand();
        createCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS DetectionRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                IsQualified INTEGER NOT NULL,
                ImagePath TEXT,
                RenderedImagePath TEXT,
                ModelName TEXT,
                RecipeId TEXT,
                InspectionId TEXT
            );
        ";
        createCommand.ExecuteNonQuery();

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = @"
            INSERT INTO DetectionRecords
            (Timestamp, IsQualified, ImagePath, RenderedImagePath, ModelName, RecipeId, InspectionId)
            VALUES ($timestamp, $isQualified, $imagePath, $renderedPath, $modelName, $recipeId, $inspectionId);
        ";
        insertCommand.Parameters.AddWithValue("$timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        insertCommand.Parameters.AddWithValue("$isQualified", isQualified ? 1 : 0);
        insertCommand.Parameters.AddWithValue("$imagePath", (object?)imagePath ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$renderedPath", (object?)renderedPath ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("$modelName", "model-test");
        insertCommand.Parameters.AddWithValue("$recipeId", "recipe-test");
        insertCommand.Parameters.AddWithValue("$inspectionId", inspectionId);
        insertCommand.ExecuteNonQuery();
    }

    private static void CreateDatabaseWithRecords(string dbPath, string? imageDir, List<(DateTime timestamp, bool isQualified, string model)> records)
    {
        string directory = Path.GetDirectoryName(dbPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var createCommand = connection.CreateCommand();
        createCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS DetectionRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                IsQualified INTEGER NOT NULL,
                ImagePath TEXT,
                RenderedImagePath TEXT,
                ModelName TEXT,
                RecipeId TEXT,
                InspectionId TEXT
            );
        ";
        createCommand.ExecuteNonQuery();

        int index = 0;
        foreach (var (timestamp, isQualified, model) in records)
        {
            string imagePath = "";
            if (!string.IsNullOrEmpty(imageDir))
            {
                string fileName = $"{(isQualified ? "PASS" : "FAIL")}_{index:D4}.jpg";
                imagePath = Path.Combine(imageDir, fileName);
                File.WriteAllBytes(imagePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }); // 伪 JPEG 头
            }

            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = @"
                INSERT INTO DetectionRecords
                (Timestamp, IsQualified, ImagePath, RenderedImagePath, ModelName, RecipeId, InspectionId)
                VALUES ($timestamp, $isQualified, $imagePath, $renderedPath, $modelName, $recipeId, $inspectionId);
            ";
            insertCommand.Parameters.AddWithValue("$timestamp", timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            insertCommand.Parameters.AddWithValue("$isQualified", isQualified ? 1 : 0);
            insertCommand.Parameters.AddWithValue("$imagePath", imagePath);
            insertCommand.Parameters.AddWithValue("$renderedPath", "");
            insertCommand.Parameters.AddWithValue("$modelName", model);
            insertCommand.Parameters.AddWithValue("$recipeId", $"recipe-{index % 2}");
            insertCommand.Parameters.AddWithValue("$inspectionId", $"INS-{index:D6}");
            insertCommand.ExecuteNonQuery();
            index++;
        }
    }

    #endregion
}
