using System.Text.Json;
using ClearFrost.Models;
using FluentAssertions;

namespace ClearFrost.Tests.Models;

public class StatisticsModelPathSafetyTests
{
    [Fact]
    public void Load_拒绝链接统计文件且不加载外部内容()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string systemDir = Path.Combine(tempDir, "System");
            Directory.CreateDirectory(systemDir);

            string externalCurrent = Path.Combine(externalDir, "external-statistics.json");
            File.WriteAllText(
                externalCurrent,
                JsonSerializer.Serialize(new DetectionStatistics
                {
                    TotalCount = 99,
                    QualifiedCount = 90,
                    UnqualifiedCount = 9,
                    CurrentDate = "2026-07-05"
                }));
            if (!TryCreateFileSymbolicLink(Path.Combine(systemDir, "statistics.json"), externalCurrent))
            {
                return;
            }

            string externalHistory = Path.Combine(externalDir, "external-history.json");
            File.WriteAllText(
                externalHistory,
                JsonSerializer.Serialize(new StatisticsHistory
                {
                    Records =
                    [
                        new DailyStatisticsRecord
                        {
                            Date = "2026-07-04",
                            TotalCount = 99,
                            QualifiedCount = 90,
                            UnqualifiedCount = 9
                        }
                    ]
                }));
            if (!TryCreateFileSymbolicLink(Path.Combine(systemDir, "statistics_history.json"), externalHistory))
            {
                return;
            }

            DetectionStatistics stats = DetectionStatistics.Load(tempDir);
            StatisticsHistory history = StatisticsHistory.Load(tempDir);

            stats.TotalCount.Should().Be(0);
            stats.QualifiedCount.Should().Be(0);
            stats.UnqualifiedCount.Should().Be(0);
            history.Records.Should().BeEmpty();
            File.ReadAllText(externalCurrent).Should().Contain("99");
            File.ReadAllText(externalHistory).Should().Contain("2026-07-04");
        }
        finally
        {
            TryDeleteFileLink(Path.Combine(tempDir, "System", "statistics.json"));
            TryDeleteFileLink(Path.Combine(tempDir, "System", "statistics_history.json"));
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void SetSavePath_拒绝链接统计目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string systemLink = Path.Combine(tempDir, "System");
        try
        {
            if (!TryCreateDirectorySymbolicLink(systemLink, externalDir))
            {
                return;
            }

            var stats = new DetectionStatistics();
            var history = new StatisticsHistory();

            Action setCurrent = () => stats.SetSavePath(tempDir);
            Action setHistory = () => history.SetSavePath(tempDir);

            setCurrent.Should().Throw<IOException>().WithMessage("*今日统计目录*链接目录*");
            setHistory.Should().Throw<IOException>().WithMessage("*历史统计目录*链接目录*");
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(systemLink);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void Load_修正损坏的今日统计计数()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string systemDir = Path.Combine(tempDir, "System");
            Directory.CreateDirectory(systemDir);
            File.WriteAllText(
                Path.Combine(systemDir, "statistics.json"),
                """
                {
                  "TotalCount": 999,
                  "QualifiedCount": 8,
                  "UnqualifiedCount": -4,
                  "CurrentDate": "bad-date"
                }
                """);

            DetectionStatistics stats = DetectionStatistics.Load(tempDir);

            stats.QualifiedCount.Should().Be(8);
            stats.UnqualifiedCount.Should().Be(0);
            stats.TotalCount.Should().Be(8);
            stats.CurrentDate.Should().MatchRegex("^\\d{4}-\\d{2}-\\d{2}$");
            stats.QualifiedPercentage.Should().Be(100);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Load_修正损坏的历史统计记录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string systemDir = Path.Combine(tempDir, "System");
            Directory.CreateDirectory(systemDir);
            File.WriteAllText(
                Path.Combine(systemDir, "statistics_history.json"),
                """
                {
                  "Records": [
                    { "Date": "2026-07-08", "TotalCount": 50, "QualifiedCount": 3, "UnqualifiedCount": -1 },
                    { "Date": "", "TotalCount": 10, "QualifiedCount": 10, "UnqualifiedCount": 0 },
                    { "Date": null, "TotalCount": 10, "QualifiedCount": 10, "UnqualifiedCount": 0 },
                    { "Date": "bad-date", "TotalCount": 10, "QualifiedCount": 10, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-08", "TotalCount": -9, "QualifiedCount": 4, "UnqualifiedCount": 1 },
                    { "Date": "2026-07-01", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-02", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-03", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-04", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-05", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-06", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 },
                    { "Date": "2026-07-07", "TotalCount": 1, "QualifiedCount": 1, "UnqualifiedCount": 0 }
                  ]
                }
                """);

            StatisticsHistory history = StatisticsHistory.Load(tempDir);

            history.Records.Should().HaveCount(7);
            history.Records.Select(record => record.Date).Should().Equal(
                "2026-07-08",
                "2026-07-07",
                "2026-07-06",
                "2026-07-05",
                "2026-07-04",
                "2026-07-03",
                "2026-07-02");
            history.Records[0].TotalCount.Should().Be(5);
            history.Records[0].QualifiedCount.Should().Be(4);
            history.Records[0].UnqualifiedCount.Should().Be(1);
            history.Records.Should().NotContain(record => string.IsNullOrWhiteSpace(record.Date));
            history.Records.Should().NotContain(record => record.Date == "bad-date");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(StatisticsModelPathSafetyTests), Guid.NewGuid().ToString("N"));
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
        if (!Directory.Exists(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete();
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}
