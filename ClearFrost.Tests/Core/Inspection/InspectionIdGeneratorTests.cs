using ClearFrost.Core.Inspection;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Inspection;

public class InspectionIdGeneratorTests
{
    [Fact]
    public void Next_同一毫秒生成唯一编号()
    {
        var timestamp = new DateTimeOffset(2026, 4, 29, 15, 30, 12, 123, TimeSpan.Zero);

        string first = InspectionIdGenerator.Next("PLC半自动", timestamp);
        string second = InspectionIdGenerator.Next("PLC半自动", timestamp);

        first.Should().StartWith("CF-20260429-153012123-PLC-");
        second.Should().StartWith("CF-20260429-153012123-PLC-");
        second.Should().NotBe(first);
    }

    [Fact]
    public void Next_手动触发映射为Manual()
    {
        var timestamp = new DateTimeOffset(2026, 4, 29, 15, 30, 12, 0, TimeSpan.Zero);

        string inspectionId = InspectionIdGenerator.Next("手动", timestamp);

        inspectionId.Should().StartWith("CF-20260429-153012000-MANUAL-");
    }
}
