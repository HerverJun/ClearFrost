using ClearFrost.Core.Security;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Security;

public class ProductionAuthorizationServiceTests
{
    [Theory]
    [InlineData(ProductionRole.Operator, ProductionOperation.RunInspection, true)]
    [InlineData(ProductionRole.Operator, ProductionOperation.ManualRelease, false)]
    [InlineData(ProductionRole.ShiftLead, ProductionOperation.ManualRelease, true)]
    [InlineData(ProductionRole.ShiftLead, ProductionOperation.EngineeringChange, false)]
    [InlineData(ProductionRole.Engineer, ProductionOperation.EngineeringChange, true)]
    public void Authorize_按角色等级判定生产操作(
        ProductionRole role,
        ProductionOperation operation,
        bool expected)
    {
        bool actual = ProductionAuthorizationService.Authorize(role, operation, out string denialReason);

        actual.Should().Be(expected);
        if (expected)
        {
            denialReason.Should().BeEmpty();
        }
        else
        {
            denialReason.Should().NotBeNullOrWhiteSpace();
        }
    }
}
