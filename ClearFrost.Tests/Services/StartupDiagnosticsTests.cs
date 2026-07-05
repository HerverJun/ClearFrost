using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Yolo;
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
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = false
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

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

            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = false
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                registry,
                PassGate);

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
                RequireApprovedModelsForProduction = false,
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
                RequireApprovedModelsForProduction = false,
                PlcProtocol = "Mitsubishi_MC_ASCI"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

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
                RequireApprovedModelsForProduction = false,
                TriggerSource = TriggerSource.SerialPhotoelectric,
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
                new ModelRegistry(),
                PassGate);

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
    public void Run_审批开启但Gate缺失会阻塞Ready()
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
                i.Name == "Replay evidence gate" &&
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
    public void Run_Plc驱动名非法会产生阻塞失败()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = false,
                PlcDriverProvider = "HaoCommunicaton"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

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
                RequireApprovedModelsForProduction = false,
                BarcodeEnabled = true,
                BarcodeAddress = "bad-address"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

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
                RequireApprovedModelsForProduction = false,
                BarcodeEnabled = false,
                BarcodeAddress = "bad-address"
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

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
                StoragePath = @"Z:\ClearFrost_Unavailable_Test_Path",
                RequireApprovedModelsForProduction = false
            };
            using var storage = new FakeStorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

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
    public void Run_拒绝链接存储目录且不写入外部目标()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedStoragePath = string.Empty;
        try
        {
            linkedStoragePath = Path.Combine(tempDir, "linked-storage");
            if (!TryCreateDirectorySymbolicLink(linkedStoragePath, externalDir))
            {
                return;
            }

            var config = new AppConfig
            {
                StoragePath = linkedStoragePath,
                RequireApprovedModelsForProduction = false
            };
            using var storage = new FakeStorageService(linkedStoragePath);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

            report.Items.Should().Contain(i =>
                i.Name == "Storage directory" &&
                i.Status == StartupDiagnosticStatus.Fail &&
                i.IsBlocking &&
                i.Details.Contains("linked", StringComparison.OrdinalIgnoreCase));
            report.IsReady.Should().BeFalse();
            Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedStoragePath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void Run_会检查关键证据目录可写性()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = false
            };
            using var storage = new StorageService(tempDir);

            StartupDiagnosticReport report = new StartupDiagnostics().Run(
                config,
                storage,
                new ModelRegistry(),
                PassGate);

            report.Items.Should().Contain(i =>
                i.Name == "System evidence directory" &&
                i.Status == StartupDiagnosticStatus.Pass &&
                i.IsBlocking &&
                i.Details.Contains(storage.SystemPath, StringComparison.OrdinalIgnoreCase));
            report.Items.Should().Contain(i =>
                i.Name == "Audit outbox directory" &&
                i.Status == StartupDiagnosticStatus.Pass &&
                i.IsBlocking &&
                i.Details.Contains(Path.Combine(storage.LogBasePath, "Outbox"), StringComparison.OrdinalIgnoreCase));
            report.Items.Should().Contain(i =>
                i.Name == "Diagnostic package directory" &&
                i.Status == StartupDiagnosticStatus.Pass &&
                !i.IsBlocking &&
                i.Details.Contains(Path.Combine(storage.LogBasePath, "Diagnostics"), StringComparison.OrdinalIgnoreCase));
            report.Items.Should().Contain(i =>
                i.Name == "Handoff report directory" &&
                i.Status == StartupDiagnosticStatus.Pass &&
                !i.IsBlocking &&
                i.Details.Contains(Path.Combine(storage.LogBasePath, "HandoffReports"), StringComparison.OrdinalIgnoreCase));
            Directory.Exists(Path.Combine(storage.LogBasePath, "Outbox")).Should().BeTrue();
            Directory.Exists(Path.Combine(storage.LogBasePath, "Diagnostics")).Should().BeTrue();
            Directory.Exists(Path.Combine(storage.LogBasePath, "HandoffReports")).Should().BeTrue();
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
                RequireApprovedModelsForProduction = false,
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
                RequireApprovedModelsForProduction = false,
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

    private static ProductionModelReadinessResult PassGate(
        ModelRole role,
        ModelRegistryEntry entry,
        ProductionModelReference reference)
    {
        return ProductionModelReadinessResult.Ok();
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = Directory.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
                return;
            }

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
        public string BaseStoragePath => _basePath;

        public void SaveDetectionImage(Bitmap bitmap, bool isQualified) { }
        public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified) { }
        public void WriteDetectionLog(string content, bool isQualified) { }
        public void WriteStartupLog(string action, string? serialNumber = null) { }
        public void WriteErrorLog(string message) { }
        public void CleanOldData(int retainDays) { }
        public double GetDiskFreeSpaceGb() => 10;
        public double PerformEmergencyCleanup() => 10;
        public void EnsureDirectoriesExist() { }
        public void UpdateStoragePath(string storagePath) { }
        public void Dispose() { }
    }
}
