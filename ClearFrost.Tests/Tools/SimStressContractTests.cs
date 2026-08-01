using FluentAssertions;

namespace ClearFrost.Tests.Tools;

public class SimStressContractTests
{
    [Fact]
    public void StressOptions_Parse_支持时长循环并发和输出()
    {
        string output = Path.Combine(Path.GetTempPath(), "clearfrost-simstress", "acceptance.md");

        var options = global::StressOptions.Parse(new[]
        {
            "--duration-minutes", "60",
            "--cycles", "120",
            "--parallel", "4",
            "--output", output
        });

        options.DurationMinutes.Should().Be(60);
        options.Cycles.Should().Be(120);
        options.Parallelism.Should().Be(4);
        options.OutputPath.Should().Be(output);
        options.MarkdownReportPath.Should().EndWith("acceptance.md");
        options.JsonReportPath.Should().EndWith("acceptance.json");
    }

    [Fact]
    public void StressOptions_Parse_仅指定时长时不强制默认循环上限()
    {
        var options = global::StressOptions.Parse(new[]
        {
            "--duration-minutes", "0.01",
            "--parallel", "2"
        });

        options.DurationMinutes.Should().Be(0.01);
        options.Cycles.Should().Be(0);
        options.HasDurationLimit.Should().BeTrue();
        options.HasCycleLimit.Should().BeFalse();
    }

    [Fact]
    public async Task StressRunner_RunAsync_报告包含验收关键字段()
    {
        string output = Path.Combine(Path.GetTempPath(), "clearfrost-simstress", $"report-{Guid.NewGuid():N}.md");
        var options = global::StressOptions.Parse(new[]
        {
            "--cycles", "12",
            "--parallel", "3",
            "--failure-rate", "0",
            "--output", output
        });

        var report = await new global::StressRunner(options).RunAsync();
        string markdown = global::StressMarkdownReport.Build(report);

        report.TotalCycles.Should().Be(12);
        report.Parallelism.Should().Be(3);
        report.AverageMs.Should().BeGreaterThan(0);
        report.P95Ms.Should().BeGreaterThanOrEqualTo(0);
        report.P99Ms.Should().BeGreaterThanOrEqualTo(report.P95Ms);
        report.FailedCount.Should().Be(0);
        report.QueueBacklog.Should().BeGreaterThan(0);
        report.MemoryDeltaMb.Should().NotBe(double.NaN);
        report.MarkdownReportPath.Should().EndWith(".md");
        report.JsonReportPath.Should().EndWith(".json");

        markdown.Should().Contain("平均耗时");
        markdown.Should().Contain("P95");
        markdown.Should().Contain("P99");
        markdown.Should().Contain("失败数");
        markdown.Should().Contain("队列 backlog");
        markdown.Should().Contain("内存变化");
    }
}
