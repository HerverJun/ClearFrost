using ClearFrost.Hardware;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class TriggerSourceRuntimeCoordinatorTests
{
    [Fact]
    public async Task RestartAfterConfigurationChangeAsync_未运行时不重启触发源()
    {
        int stopCount = 0;
        int startCount = 0;
        string? logMessage = null;

        bool result = await TriggerSourceRuntimeCoordinator.RestartAfterConfigurationChangeAsync(
            isProductionRunning: false,
            stopTriggerSourcesAsync: () =>
            {
                stopCount++;
                return Task.CompletedTask;
            },
            startTriggerSourceAsync: () =>
            {
                startCount++;
                return Task.FromResult(true);
            },
            logAsync: message =>
            {
                logMessage = message;
                return Task.CompletedTask;
            },
            reason: "测试配置变更");

        result.Should().BeTrue();
        stopCount.Should().Be(0);
        startCount.Should().Be(0);
        logMessage.Should().Contain("当前未在生产运行");
    }

    [Fact]
    public async Task RestartAfterConfigurationChangeAsync_运行中先停止再启动触发源()
    {
        List<string> calls = new();

        bool result = await TriggerSourceRuntimeCoordinator.RestartAfterConfigurationChangeAsync(
            isProductionRunning: true,
            stopTriggerSourcesAsync: () =>
            {
                calls.Add("stop");
                return Task.CompletedTask;
            },
            startTriggerSourceAsync: () =>
            {
                calls.Add("start");
                return Task.FromResult(true);
            });

        result.Should().BeTrue();
        calls.Should().Equal("stop", "start");
    }

    [Theory]
    [InlineData(TriggerSource.PLC, true)]
    [InlineData(TriggerSource.SerialPhotoelectric, false)]
    [InlineData(TriggerSource.Manual, false)]
    public void CanWriteManualRelease_仅Plc触发源允许写放行(TriggerSource triggerSource, bool expected)
    {
        bool actual = TriggerSourceRuntimeCoordinator.CanWriteManualRelease(triggerSource);

        actual.Should().Be(expected);
    }

    [Theory]
    [InlineData(true, true, 3, 3, false)]
    [InlineData(false, false, 3, 3, false)]
    [InlineData(false, true, 4, 3, false)]
    [InlineData(false, true, 3, 3, true)]
    public void IsProductionStartCurrent_同时满足运行态与代次才有效(
        bool isShutdownInProgress,
        bool isProductionRunning,
        int currentGeneration,
        int startGeneration,
        bool expected)
    {
        bool actual = TriggerSourceRuntimeCoordinator.IsProductionStartCurrent(
            isShutdownInProgress,
            isProductionRunning,
            currentGeneration,
            startGeneration);

        actual.Should().Be(expected);
    }
}
