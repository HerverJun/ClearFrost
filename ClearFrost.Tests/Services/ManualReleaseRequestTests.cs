using ClearFrost.Core.Security;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class ManualReleaseRequestTests
{
    [Fact]
    public void Parse_前端自报高权限_仍使用后端当前角色()
    {
        string json = """
        {
            "operatorId": "spoofed",
            "role": "Engineer",
            "reason": "现场确认需要放行",
            "confirmationToken": "CONFIRM_MANUAL_RELEASE"
        }
        """;

        ManualReleaseRequest request = ManualReleaseRequest.Parse(
            json,
            "backend-operator",
            ProductionRole.Operator);

        request.OperatorId.Should().Be("backend-operator");
        request.Role.Should().Be(ProductionRole.Operator);
        request.TryAuthorize(out string denialReason).Should().BeFalse();
        denialReason.Should().Contain("至少需要 班组长");
    }

    [Fact]
    public void TryAuthorize_班组长确认且记录原因_允许手动放行()
    {
        string json = """
        {
            "reason": "现场复核后需要人工放行",
            "confirmationToken": "CONFIRM_MANUAL_RELEASE"
        }
        """;

        ManualReleaseRequest request = ManualReleaseRequest.Parse(
            json,
            "lead-01",
            ProductionRole.ShiftLead);

        request.TryAuthorize(out string denialReason).Should().BeTrue();
        denialReason.Should().BeEmpty();
    }
}
