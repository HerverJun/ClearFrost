using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class AlarmCenterServiceTests
{
    [Fact]
    public void Evaluate_维护建议生成活动告警并持久化()
    {
        string tempDir = CreateTempDirectory();
        DateTimeOffset now = new(2026, 5, 27, 8, 0, 0, TimeSpan.Zero);

        try
        {
            var service = new AlarmCenterService(tempDir, () => now);

            AlarmSnapshot snapshot = service.Evaluate(new HealthSnapshot
            {
                HealthLevel = HealthLevel.Critical,
                MaintenanceAdvices = new[]
                {
                    new MaintenanceAdvice
                    {
                        Level = HealthLevel.Critical,
                        Source = "Storage",
                        Message = "图像目录不可写",
                        Action = "检查磁盘权限"
                    }
                }
            });

            snapshot.ActiveCount.Should().Be(1);
            snapshot.UnacknowledgedCount.Should().Be(1);
            snapshot.HighestSeverity.Should().Be(AlarmSeverity.Critical);
            snapshot.ActiveAlarms[0].Source.Should().Be("Storage");
            File.Exists(service.AlarmPath).Should().BeTrue();

            var reloaded = new AlarmCenterService(tempDir, () => now.AddMinutes(1));
            reloaded.GetSnapshot().ActiveCount.Should().Be(1);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Acknowledge_记录操作员并降低未确认数量()
    {
        string tempDir = CreateTempDirectory();
        DateTimeOffset now = new(2026, 5, 27, 9, 0, 0, TimeSpan.Zero);

        try
        {
            var service = new AlarmCenterService(tempDir, () => now);
            AlarmRecord alarm = service.Evaluate(CreateErrorSnapshot(now)).ActiveAlarms.Single();

            now = now.AddMinutes(1);
            AlarmRecord acknowledged = service.Acknowledge(alarm.AlarmId, new OperatorSession
            {
                OperatorName = "张工",
                Role = "Engineer",
                ShiftName = "白班",
                SignedInAt = now
            });

            acknowledged.IsAcknowledged.Should().BeTrue();
            acknowledged.AcknowledgedBy.Should().Be("张工");
            service.GetSnapshot().UnacknowledgedCount.Should().Be(0);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Evaluate_错误窗口过期后自动清除活动告警()
    {
        string tempDir = CreateTempDirectory();
        DateTimeOffset now = new(2026, 5, 27, 10, 0, 0, TimeSpan.Zero);

        try
        {
            var service = new AlarmCenterService(tempDir, () => now);
            service.Evaluate(CreateErrorSnapshot(now)).ActiveCount.Should().Be(1);

            now = now.AddMinutes(31);
            AlarmSnapshot cleared = service.Evaluate(CreateErrorSnapshot(now.AddMinutes(-31)));

            cleared.ActiveCount.Should().Be(0);
            cleared.RecentAlarms.Should().ContainSingle(a => a.State == AlarmState.Cleared);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Evaluate_同一告警重复出现时复用活动告警编号()
    {
        string tempDir = CreateTempDirectory();
        DateTimeOffset now = new(2026, 5, 27, 11, 0, 0, TimeSpan.Zero);

        try
        {
            var service = new AlarmCenterService(tempDir, () => now);
            AlarmRecord first = service.Evaluate(CreateErrorSnapshot(now)).ActiveAlarms.Single();

            now = now.AddMinutes(1);
            AlarmRecord second = service.Evaluate(CreateErrorSnapshot(now)).ActiveAlarms.Single();

            second.AlarmId.Should().Be(first.AlarmId);
            second.OccurrenceCount.Should().BeGreaterThan(first.OccurrenceCount);
            second.LastSeenAt.Should().Be(now);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static HealthSnapshot CreateErrorSnapshot(DateTimeOffset timestamp)
    {
        return new HealthSnapshot
        {
            HealthLevel = HealthLevel.Warning,
            RecentErrors = new[]
            {
                new HealthError
                {
                    Timestamp = timestamp,
                    Source = "PLC",
                    Message = "PLC写入失败",
                    InspectionId = "CF-001"
                }
            }
        };
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostAlarmCenterTests", Guid.NewGuid().ToString("N"));
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
