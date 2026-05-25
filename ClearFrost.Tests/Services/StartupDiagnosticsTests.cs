using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Hardware;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class StartupDiagnosticsTests
{
    [Fact]
    public void Run_空模型注册表会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig { StoragePath = tempDir };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "Model registry" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking);
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_Plc地址错误会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcProtocol = PlcProtocolType.Mitsubishi_MC_ASCII.ToString(),
                PlcTriggerAddress = "bad-address"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking);
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_Plc协议名非法会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcProtocol = "Mitsubishi_MC_ASCI"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking &&
                i.Details.Contains("PLC 协议无效"));
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_Plc驱动名非法会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcDriverProvider = "HaoCommunicaton"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking &&
                i.Details.Contains("PLC 驱动库"));
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_条码启用时会校验条码地址()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                BarcodeEnabled = true,
                BarcodeAddress = "bad-address"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_条码未启用时不因条码地址阻塞启动()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                BarcodeEnabled = false,
                BarcodeAddress = "bad-address"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Pass);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_McpX三菱非D区地址会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcProtocol = PlcProtocolType.Mitsubishi_MC_Binary.ToString(),
                PlcDriverProvider = "McpX",
                PlcTriggerAddress = "M100",
                PlcResultAddress = "D101"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_Hao三菱可接受常见非D区字地址()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcProtocol = PlcProtocolType.Mitsubishi_MC_Binary.ToString(),
                PlcDriverProvider = "HaoCommunication",
                PlcTriggerAddress = "M100",
                PlcResultAddress = "Y10"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Pass);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(StartupDiagnosticsTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
