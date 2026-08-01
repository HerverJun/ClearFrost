using ClearFrost.Models;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class StatisticsServiceTests
{
    [Fact]
    public void GetStatisticsData_返回防御性副本()
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
                  "TotalCount": 2,
                  "QualifiedCount": 1,
                  "UnqualifiedCount": 1,
                  "CurrentDate": "2026-07-07"
                }
                """);
            File.WriteAllText(
                Path.Combine(systemDir, "statistics_history.json"),
                """
                {
                  "Records": [
                    { "Date": "2026-07-06", "TotalCount": 3, "QualifiedCount": 2, "UnqualifiedCount": 1 }
                  ]
                }
                """);

            using var service = new StatisticsService(tempDir);

            var (history, stats) = service.GetStatisticsData();
            stats.TotalCount = 999;
            stats.QualifiedCount = 999;
            stats.CurrentDate = "2099-01-01";
            history.Records.Clear();
            history.Records.Add(new DailyStatisticsRecord
            {
                Date = "2099-01-01",
                TotalCount = 999,
                QualifiedCount = 999,
                UnqualifiedCount = 0
            });

            var (freshHistory, freshStats) = service.GetStatisticsData();

            freshStats.TotalCount.Should().Be(2);
            freshStats.QualifiedCount.Should().Be(1);
            freshStats.UnqualifiedCount.Should().Be(1);
            freshStats.CurrentDate.Should().Be("2026-07-07");
            freshHistory.Records.Should().ContainSingle(record => record.Date == "2026-07-06");
            freshHistory.Records.Should().NotContain(record => record.Date == "2099-01-01");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(StatisticsServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
