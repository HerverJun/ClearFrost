using System;
using System.IO;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class OperatorSessionServiceTests
{
    [Fact]
    public void SignIn_保存并重新加载操作员会话()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string systemPath = Path.Combine(tempDir, "System");
            var signedInAt = new DateTimeOffset(2026, 5, 4, 23, 10, 0, TimeSpan.Zero);
            var now = signedInAt.AddHours(1);
            var service = new OperatorSessionService(systemPath, TimeSpan.FromHours(12), () => now);

            OperatorSession session = service.SignIn(
                "OP-1001",
                "Engineer",
                "夜班",
                signedInAt);

            session.IsSignedIn.Should().BeTrue();
            session.OperatorName.Should().Be("OP-1001");
            session.Role.Should().Be("Engineer");
            session.ShiftName.Should().Be("夜班");
            File.Exists(service.SessionPath).Should().BeTrue();

            var reloaded = new OperatorSessionService(systemPath, TimeSpan.FromHours(12), () => now);
            reloaded.Current.OperatorName.Should().Be("OP-1001");
            reloaded.Current.Role.Should().Be("Engineer");
            reloaded.Current.ShiftName.Should().Be("夜班");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void 重新加载_超过有效期的持久会话_自动回到未登录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string systemPath = Path.Combine(tempDir, "System");
            var signedInAt = new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);
            var service = new OperatorSessionService(systemPath, TimeSpan.FromHours(12), () => signedInAt);
            service.SignIn("OP-EXPIRED", "Engineer", "A班", signedInAt);

            var reloaded = new OperatorSessionService(
                systemPath,
                TimeSpan.FromHours(12),
                () => signedInAt.AddHours(13));

            reloaded.Current.IsSignedIn.Should().BeFalse();
            reloaded.Current.OperatorName.Should().Be(OperatorSession.DefaultOperatorName);
            reloaded.Current.ShiftName.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Current_运行中超过有效期_自动退出并保存默认会话()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            DateTimeOffset now = new DateTimeOffset(2026, 5, 4, 8, 0, 0, TimeSpan.Zero);
            string systemPath = Path.Combine(tempDir, "System");
            var service = new OperatorSessionService(systemPath, TimeSpan.FromHours(12), () => now);
            service.SignIn("OP-RUNNING", "Operator", "A班", now);

            now = now.AddHours(13);
            OperatorSession expired = service.Current;

            expired.IsSignedIn.Should().BeFalse();
            var reloaded = new OperatorSessionService(systemPath, TimeSpan.FromHours(12), () => now);
            reloaded.Current.IsSignedIn.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void SignIn_未指定班次时按登录时间解析班次()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            DateTime signInTime = new DateTime(2026, 5, 4, 1, 0, 0);
            var signedInAt = new DateTimeOffset(signInTime, TimeZoneInfo.Local.GetUtcOffset(signInTime));
            var service = new OperatorSessionService(
                Path.Combine(tempDir, "System"),
                TimeSpan.FromHours(12),
                () => signedInAt.AddHours(1));
            service.SignIn(
                "OP-2001",
                shiftName: "",
                signedInAt: signedInAt);

            OperatorSession snapshot = service.SnapshotFor(new DateTime(2026, 5, 4, 9, 0, 0));

            snapshot.OperatorName.Should().Be("OP-2001");
            snapshot.ShiftName.Should().Be("C班");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void SignIn_操作员为空_抛出异常()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var service = new OperatorSessionService(Path.Combine(tempDir, "System"));

            Action act = () => service.SignIn(" ");

            act.Should().Throw<ArgumentException>().WithMessage("*操作员不能为空*");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("工程师", "Engineer")]
    [InlineData("管理员", "Administrator")]
    [InlineData("tech", "Technician")]
    public void SignIn_角色别名_归一化为标准角色(string inputRole, string expectedRole)
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var service = new OperatorSessionService(Path.Combine(tempDir, "System"));

            OperatorSession session = service.SignIn("OP-3001", inputRole);

            session.Role.Should().Be(expectedRole);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostOperatorSessionTests", Guid.NewGuid().ToString("N"));
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
