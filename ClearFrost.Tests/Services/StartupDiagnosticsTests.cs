using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;
using System.Drawing;

namespace ClearFrost.Tests.Services;

public class StartupDiagnosticsTests
{
    [Fact]
    public void Run_空模型注册表只产生非阻塞警告()
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
                i.Status == StartupDiagnosticStatus.Warning &&
                !i.IsBlocking);
            report.IsReady.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_模型包阻塞错误只产生非阻塞警告()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string packageRoot = Path.Combine(tempDir, "models");
            string packageDir = Path.Combine(packageRoot, "pkg-blocked");
            Directory.CreateDirectory(packageDir);
            File.WriteAllBytes(Path.Combine(packageDir, "model.onnx"), new byte[] { 1, 2, 3 });
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = "pkg-blocked",
                    Version = "1",
                    ModelHash = "bad-hash",
                    Labels = new List<string> { "screw" }
                }));

            var registry = new ModelRegistry();
            registry.Scan(new ModelRegistryScanOptions
            {
                PackageDirectory = packageRoot,
                StrictPackageMode = true,
                Warmup = (_, _) => true
            });
            registry.HasBlockingErrors.Should().BeTrue();

            var config = new AppConfig { StoragePath = tempDir };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                registry);

            report.Items.Should().Contain(i =>
                i.Name == "Model registry" &&
                i.Status == StartupDiagnosticStatus.Warning &&
                !i.IsBlocking &&
                i.Details.Contains("pkg-blocked"));
            report.IsReady.Should().BeTrue();
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
    public void Run_串口光电触发时跳过Plc地址校验()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                TriggerSource = TriggerSource.SerialPhotoelectric,
                SerialPhotoelectricPortName = "COM1",
                PlcProtocol = "Mitsubishi_MC_ASCI",
                PlcTriggerAddress = "bad-address",
                PlcResultAddress = "bad-result-address",
                BarcodeEnabled = true,
                BarcodeAddress = "bad-barcode-address"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "PLC address config" &&
                i.Status == StartupDiagnosticStatus.Pass &&
                !i.IsBlocking &&
                i.Details == TriggerSource.SerialPhotoelectric.ToString());
            report.IsReady.Should().BeTrue();
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
    public void Run_配置存储路径不可用时按运行存储目录诊断()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = @"Z:\ClearFrost_Unavailable_Test_Path"
            };
            using var storage = new FakeStorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry());

            report.Items.Should().Contain(i =>
                i.Name == "Storage directory" &&
                i.Status == StartupDiagnosticStatus.Pass &&
                i.Details.Contains(tempDir, StringComparison.OrdinalIgnoreCase));
            report.IsReady.Should().BeTrue();
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

    [Fact]
    public void Run_Plc端口非法会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcPort = 70000
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                CreateBareModelRegistry(tempDir));

            report.Items.Should().Contain(i =>
                i.Name == "Trigger source config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking &&
                i.Details.Contains("PLC 端口"));
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_串口光电启用但端口为空会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                TriggerSource = TriggerSource.SerialPhotoelectric,
                SerialPhotoelectricPortName = ""
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                CreateBareModelRegistry(tempDir));

            report.Items.Should().Contain(i =>
                i.Name == "Trigger source config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking &&
                i.Details.Contains("COM"));
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_视觉参数越界会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                Confidence = 1.5f,
                IouThreshold = -0.1f,
                TargetCount = -1
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                CreateBareModelRegistry(tempDir));

            report.Items.Should().Contain(i =>
                i.Name == "Vision parameter config" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking &&
                i.Details.Contains("Confidence") &&
                i.Details.Contains("IouThreshold") &&
                i.Details.Contains("TargetCount"));
            report.IsReady.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_Plc写回重试参数越界会产生非阻塞告警()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                PlcWriteRetryCount = 9,
                PlcWriteRetryIntervalMs = -1
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                CreateBareModelRegistry(tempDir));

            report.Items.Should().Contain(i =>
                i.Name == "Vision parameter config" &&
                i.Status == StartupDiagnosticStatus.Warning &&
                !i.IsBlocking &&
                i.Details.Contains("PlcWriteRetryCount") &&
                i.Details.Contains("PlcWriteRetryIntervalMs"));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Run_当前模型配置不存在会产生非阻塞告警()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                CurrentModelFileName = "missing.onnx"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                CreateBareModelRegistry(tempDir));

            report.Items.Should().Contain(i =>
                i.Name == "Configured model" &&
                i.Status == StartupDiagnosticStatus.Warning &&
                !i.IsBlocking &&
                i.Details.Contains("missing.onnx"));
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

    private static ModelRegistry CreateBareModelRegistry(string tempDir)
    {
        string onnxDir = Path.Combine(tempDir, "ONNX");
        Directory.CreateDirectory(onnxDir);
        File.WriteAllBytes(Path.Combine(onnxDir, "model.onnx"), new byte[] { 1, 2, 3 });

        var registry = new ModelRegistry();
        registry.Scan(new ModelRegistryScanOptions { OnnxDirectory = onnxDir });
        return registry;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }

    private sealed class FakeStorageService : IStorageService
    {
        private readonly string _basePath;

        public FakeStorageService(string basePath)
        {
            _basePath = basePath;
        }

        public string ImageBasePath => Path.Combine(_basePath, "Images");
        public string LogBasePath => Path.Combine(_basePath, "Logs");
        public string SystemPath => Path.Combine(_basePath, "System");

        public void SaveDetectionImage(Bitmap bitmap, bool isQualified) { }
        public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified) { }
        public void WriteDetectionLog(string content, bool isQualified) { }
        public void WriteStartupLog(string action, string? serialNumber = null) { }
        public void WriteErrorLog(string message) { }
        public void WriteAuditLog(string category, string action, string detail, bool success = true) { }
        public void CleanOldData(int retainDays) { }
        public double GetDiskFreeSpaceGb() => 10;
        public double PerformEmergencyCleanup() => 10;
        public void EnsureDirectoriesExist() { }
        public void Dispose() { }
    }
}
