using ClearFrost.Config;
using FluentAssertions;

namespace ClearFrost.Tests.Config;

public class RuntimeConfigurationChangeClassifierTests
{
    [Theory]
    [InlineData(nameof(AppConfig.CurrentOperatorId), true)]
    [InlineData(nameof(AppConfig.CurrentOperatorRole), false)]
    [InlineData(nameof(AppConfig.TriggerSource), false)]
    public void ShouldIgnoreForSystemConfigChange_只允许操作员标识绕过系统配置门禁(
        string propertyName,
        bool expected)
    {
        bool actual = RuntimeConfigurationChangeClassifier.ShouldIgnoreForSystemConfigChange(propertyName);

        actual.Should().Be(expected);
    }
}
