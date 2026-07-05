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
    public async Task CollectAsync_数据库链接原图_拒绝采集外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string? linkPath = null;
        try
        {
            string dbPath = Path.Combine(tempDir, "detection.db");
            string externalImage = Path.Combine(externalDir, "external-source.jpg");
            linkPath = Path.Combine(tempDir, "linked-source.jpg");
            File.WriteAllBytes(externalImage, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            if (!TryCreateFileSymbolicLink(linkPath, externalImage))
            {
                return;
            }

            CreateDatabaseWithSingleRecord(
                dbPath,
                DateTime.Now,
                isQualified: false,
                imagePath: linkPath,
                renderedPath: null,
                inspectionId: "INS-LINKED-SOURCE");
            var service = new DatasetCollectionService(dbPath, storagePath);

            var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("图片文件");
            File.Exists(externalImage).Should().BeTrue();
            Directory.Exists(Path.Combine(storagePath, "DatasetCollections")).Should().BeFalse();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkPath) && File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
            DeleteDirectory(storagePath);
        }
    }

    [Fact]
    public async Task CollectAsync_直接扫描链接小时目录_拒绝采集外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string? linkHourDir = null;
        try
        {
            string dbPath = CreateEmptyDatabase(tempDir);
            DateTime timestamp = DateTime.Now.Date.AddHours(15).AddMinutes(10).AddSeconds(11);
            string dateDir = Path.Combine(
                storagePath,
                "Images",
                "Unqualified",
                timestamp.ToString("yyyy年MM月dd日"));
            linkHourDir = Path.Combine(dateDir, timestamp.ToString("HH"));
            Directory.CreateDirectory(dateDir);
            File.WriteAllBytes(
                Path.Combine(externalDir, $"FAIL_{timestamp:HHmmss}123.jpg"),
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            if (!TryCreateDirectorySymbolicLink(linkHourDir, externalDir))
            {
                return;
            }

            var service = new DatasetCollectionService(dbPath, storagePath);

            var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("未找到任何检测记录或图片文件");
            Directory.Exists(Path.Combine(storagePath, "DatasetCollections")).Should().BeFalse();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkHourDir))
            {
                TryDeleteDirectoryLink(linkHourDir);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
            DeleteDirectory(storagePath);
        }
    }

    [Fact]
    public async Task CollectAsync_拒绝链接DatasetCollections输出根目录且不写外部目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string? outputRootLink = null;
        try
        {
            string dbPath = Path.Combine(tempDir, "detection.db");
            string sourceImage = Path.Combine(tempDir, "source.jpg");
            File.WriteAllBytes(sourceImage, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            CreateDatabaseWithSingleRecord(
                dbPath,
                DateTime.Now,
                isQualified: false,
                imagePath: sourceImage,
                renderedPath: null,
                inspectionId: "INS-LINKED-OUTPUT");

            outputRootLink = Path.Combine(storagePath, "DatasetCollections");
            if (!TryCreateDirectorySymbolicLink(outputRootLink, externalDir))
            {
                return;
            }

            var service = new DatasetCollectionService(dbPath, storagePath);

            var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("输出目录不安全");
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(outputRootLink))
            {
                TryDeleteDirectoryLink(outputRootLink);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
            DeleteDirectory(storagePath);
        }
    }

    [Fact]
    public async Task CollectAsync_复制失败清理遇到链接子目录时保留输出目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string? linkedOutputChild = null;
        try
        {
            string probeTarget = Path.Combine(tempDir, "probe-target");
            string probeLink = Path.Combine(tempDir, "probe-link");
            Directory.CreateDirectory(probeTarget);
            if (!TryCreateDirectorySymbolicLink(probeLink, probeTarget))
            {
                return;
            }

            TryDeleteDirectoryLink(probeLink);
            DeleteDirectory(probeTarget);

            string dbPath = Path.Combine(tempDir, "detection.db");
            string sourceImage = Path.Combine(tempDir, "source.jpg");
            string externalFile = Path.Combine(externalDir, "external.txt");
            File.WriteAllBytes(sourceImage, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
            File.WriteAllText(externalFile, "external");
            CreateDatabaseWithSingleRecord(
                dbPath,
                DateTime.Now,
                isQualified: false,
                imagePath: sourceImage,
                renderedPath: null,
                inspectionId: "INS-CLEANUP-LINKED-OUTPUT");

            var service = new DatasetCollectionService(
                dbPath,
                storagePath,
                copyFile: (_, destPath, _) =>
                {
                    string destDir = Path.GetDirectoryName(destPath) ??
                                     throw new InvalidOperationException("dest directory missing");
                    linkedOutputChild ??= Path.Combine(destDir, "linked-external");
                    if (!Directory.Exists(linkedOutputChild))
                    {
                        TryCreateDirectorySymbolicLink(linkedOutputChild, externalDir).Should().BeTrue();
                    }

                    throw new IOException("disk full");
                });

            var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("未成功复制任何图片");
            File.Exists(externalFile).Should().BeTrue();
            linkedOutputChild.Should().NotBeNull();
            Directory.Exists(linkedOutputChild!).Should().BeTrue();
            Directory.GetDirectories(Path.Combine(storagePath, "DatasetCollections"))
                .Should()
                .ContainSingle();
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(linkedOutputChild))
            {
                TryDeleteDirectoryLink(linkedOutputChild);
            }

            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
            DeleteDirectory(storagePath);
        }
    }

    [Fact]
    public async Task CollectAsync_直接扫描时忽略Rendered子目录()
    {
        string tempDir = CreateTempDirectory();
        string dbPath = CreateEmptyDatabase(tempDir);
        string storagePath = CreateTempDirectory();
        DateTime timestamp = DateTime.Now.Date.AddHours(16).AddMinutes(11).AddSeconds(12);
        string imageDir = Path.Combine(
            storagePath,
            "Images",
            "Unqualified",
            timestamp.ToString("yyyy年MM月dd日"),
            timestamp.ToString("HH"));
        string renderedDir = Path.Combine(imageDir, "Rendered");
        Directory.CreateDirectory(renderedDir);
        File.WriteAllBytes(
            Path.Combine(imageDir, $"FAIL_{timestamp:HHmmss}123.jpg"),
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        File.WriteAllBytes(
            Path.Combine(renderedDir, $"FAIL_{timestamp:HHmmss}123_rendered.jpg"),
            new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 });
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(1);
        Directory.GetFiles(Path.Combine(result.OutputDirectory, "Fail"))
            .Should()
            .ContainSingle()
            .Which.Should().EndWith(".jpg").And.NotContain("rendered");
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
    public async Task CollectAsync_部分路径失效_增量标准目录补回()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = CreateEmptyDatabase(tempDir);
        DateTime timestamp = DateTime.Now.Date.AddHours(11).AddMinutes(12).AddSeconds(13);
        string validPassPath = Path.Combine(tempDir, "valid-pass.jpg");
        File.WriteAllBytes(validPassPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

        string inspectionId = "INS-PARTIAL-FALLBACK";
        string fallbackDir = Path.Combine(
            storagePath,
            "Images",
            "Unqualified",
            timestamp.ToString("yyyy年MM月dd日"),
            timestamp.ToString("HH"));
        Directory.CreateDirectory(fallbackDir);
        string fallbackFailPath = Path.Combine(fallbackDir, $"FAIL_{inspectionId}.jpg");
        File.WriteAllBytes(fallbackFailPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 });

        InsertDetectionRecord(dbPath, timestamp, isQualified: true, validPassPath, renderedPath: null, "INS-VALID-PASS");
        InsertDetectionRecord(
            dbPath,
            timestamp,
            isQualified: false,
            imagePath: Path.Combine(tempDir, "missing-fail.jpg"),
            renderedPath: null,
            inspectionId);
        var progressMessages = new List<string>();
        var progress = new RecordingProgress(progressMessages);
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 2, failRatio: 0.5, progress: progress);

        result.Success.Should().BeTrue();
        result.PassCopied.Should().Be(1);
        result.FailCopied.Should().Be(1);
        File.Exists(Path.Combine(result.OutputDirectory, "Pass", Path.GetFileName(validPassPath))).Should().BeTrue();
        File.Exists(Path.Combine(result.OutputDirectory, "Fail", Path.GetFileName(fallbackFailPath))).Should().BeTrue();
        progressMessages.Should().Contain(message => message.Contains("部分图片路径失效"));
    }

    [Fact]
    public async Task CollectAsync_复制阶段全部失败_返回失败并清理空目录()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = CreateEmptyDatabase(tempDir);
        DateTime timestamp = DateTime.Now.Date.AddHours(12).AddMinutes(1);
        string passPath = Path.Combine(tempDir, "pass-source.jpg");
        string failPath = Path.Combine(tempDir, "fail-source.jpg");
        File.WriteAllBytes(passPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        File.WriteAllBytes(failPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 });
        InsertDetectionRecord(dbPath, timestamp, isQualified: true, passPath, renderedPath: null, "INS-COPY-PASS");
        InsertDetectionRecord(dbPath, timestamp, isQualified: false, failPath, renderedPath: null, "INS-COPY-FAIL");
        var service = new DatasetCollectionService(
            dbPath,
            storagePath,
            copyFile: (_, _, _) => throw new IOException("disk full"));

        var result = await service.CollectAsync(maxDays: 15, totalCount: 2, failRatio: 0.5);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("未成功复制任何图片");
        result.Message.Should().Contain("disk full");
        string collectionRoot = Path.Combine(storagePath, "DatasetCollections");
        Directory.Exists(collectionRoot).Should().BeTrue();
        Directory.GetDirectories(collectionRoot).Should().BeEmpty();
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
    public async Task CollectAsync_原图和渲染图都存在_优先复制原图()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = Path.Combine(tempDir, "detection.db");
        DateTime timestamp = DateTime.Now;
        string originalPath = Path.Combine(tempDir, "original.jpg");
        string renderedPath = Path.Combine(tempDir, "rendered.jpg");
        File.WriteAllBytes(originalPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });
        File.WriteAllBytes(renderedPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 });
        CreateDatabaseWithSingleRecord(dbPath, timestamp, isQualified: false, originalPath, renderedPath, "INS-BOTH-PATHS");
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

        result.Success.Should().BeTrue();
        result.FailCopied.Should().Be(1);
        File.Exists(Path.Combine(result.OutputDirectory, "Fail", "original.jpg")).Should().BeTrue();
        File.Exists(Path.Combine(result.OutputDirectory, "Fail", "rendered.jpg")).Should().BeFalse();
    }

    [Fact]
    public async Task CollectAsync_原图缺失但渲染图存在_不会复制渲染图()
    {
        string tempDir = CreateTempDirectory();
        string storagePath = CreateTempDirectory();
        string dbPath = Path.Combine(tempDir, "detection.db");
        DateTime timestamp = DateTime.Now;
        string missingOriginalPath = Path.Combine(tempDir, "missing-original.jpg");
        string renderedPath = Path.Combine(tempDir, "rendered.jpg");
        File.WriteAllBytes(renderedPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 });
        CreateDatabaseWithSingleRecord(dbPath, timestamp, isQualified: false, missingOriginalPath, renderedPath, "INS-ONLY-RENDER");
        var service = new DatasetCollectionService(dbPath, storagePath);

        var result = await service.CollectAsync(maxDays: 15, totalCount: 1, failRatio: 1.0);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("图片文件在磁盘上已不存在");
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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                SqliteConnection.ClearAllPools();
                System.Threading.Thread.Sleep(50);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        }
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

    private static void InsertDetectionRecord(
        string dbPath,
        DateTime timestamp,
        bool isQualified,
        string? imagePath,
        string? renderedPath,
        string inspectionId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

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

    private static void TryDeleteDirectoryLink(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // 测试清理失败不应覆盖主体断言。
        }
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        private readonly List<string> _messages;

        public RecordingProgress(List<string> messages)
        {
            _messages = messages;
        }

        public void Report(string value)
        {
            _messages.Add(value);
        }
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
