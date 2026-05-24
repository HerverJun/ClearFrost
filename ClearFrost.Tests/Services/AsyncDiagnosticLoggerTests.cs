using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class AsyncDiagnosticLoggerTests
{
    [Fact]
    public void Dispose_会刷新已入队的日志()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostAsyncLoggerTests", Guid.NewGuid().ToString("N"));
        string logPath = Path.Combine(tempDir, "diag.log");

        try
        {
            var logger = new AsyncDiagnosticLogger(logPath, capacity: 16);

            logger.Enqueue("line-1").Should().BeTrue();
            logger.Enqueue("line-2").Should().BeTrue();
            logger.Dispose();

            File.ReadAllText(logPath).Should().Contain("line-1").And.Contain("line-2");
            logger.Enqueue("line-3").Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void 写入短暂失败后_后台线程继续处理后续日志()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostAsyncLoggerTests", Guid.NewGuid().ToString("N"));
        string logPath = Path.Combine(tempDir, "diag.log");

        try
        {
            Directory.CreateDirectory(tempDir);
            using var blocker = new FileStream(logPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var logger = new AsyncDiagnosticLogger(logPath, capacity: 16);

            logger.Enqueue("while-locked").Should().BeTrue();
            SpinWait.SpinUntil(() => logger.FailedCount > 0, TimeSpan.FromSeconds(3)).Should().BeTrue();

            blocker.Dispose();
            logger.Enqueue("after-unlock").Should().BeTrue();
            logger.Dispose();

            File.ReadAllText(logPath).Should().Contain("after-unlock");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
