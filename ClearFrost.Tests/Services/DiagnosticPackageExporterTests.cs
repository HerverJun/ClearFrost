using System.IO.Compression;
using ClearFrost.Config;
using ClearFrost.Core.Recipes;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class DiagnosticPackageExporterTests
{
    [Fact]
    public async Task ExportAsync_导出诊断包且不包含模型或大图()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logsDir = Path.Combine(tempDir, "Logs");
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(Path.Combine(logsDir, "Outbox"));
            await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "hello log");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "Outbox", "audit.json"), "operator-secret");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "model.onnx"), "model bytes");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "image.jpg"), "image bytes");

            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                LogsDirectory = logsDir,
                AppConfig = new AppConfig { StoragePath = tempDir, CurrentOperatorId = "operator-secret" },
                Recipe = new Recipe { RecipeId = "default", Version = "v1" },
                StartupDiagnostics = new StartupDiagnosticReport
                {
                    Items = new[]
                    {
                        new StartupDiagnosticItem
                        {
                            Name = "Storage directory",
                            Status = StartupDiagnosticStatus.Pass,
                            Message = "Writable."
                        }
                    }
                },
                HealthSnapshot = new HealthSnapshot
                {
                    HealthLevel = HealthLevel.Ok,
                    CameraStatus = "Grabbing",
                    PlcStatus = "Connected:Fake",
                    ModelStatus = "Loaded:model-a:CPU",
                    LastInspectionId = "CF-1",
                    RecentInspectionTimings = new[]
                    {
                        new RecentInspectionTimingSnapshot
                        {
                            InspectionId = "CF-1",
                            TotalMs = 42,
                            CaptureMs = 5,
                            InferenceMs = 31
                        }
                    },
                    RecentErrors = new[]
                    {
                        new HealthError
                        {
                            Source = "PLC",
                            Message = "写入失败",
                            InspectionId = "CF-1"
                        }
                    }
                },
                RecentRecords = new List<DetectionRecord>
                {
                    new DetectionRecord
                    {
                        InspectionId = "CF-1",
                        ModelName = "model-a",
                        ProductBarcode = "barcode-secret",
                        ImagePath = Path.Combine(tempDir, "Images", "raw.jpg")
                    }
                }
            });

            File.Exists(zipPath).Should().BeTrue();

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Select(e => e.FullName).Should().Contain(new[]
            {
                "config.sanitized.json",
                "recipe.json",
                "startup_diagnostics.json",
                "health.json",
                "field_diagnostics.json",
                "recent_inspection_timings.json",
                "recent_errors.json",
                "model_probe_summary.json",
                "queue_status.json",
                "recent_records.json",
                "logs/app.log"
            });
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
            zip.Entries.Select(e => e.FullName).Should().NotContain("logs/Outbox/audit.json");

            string configJson = ReadEntry(zip, "config.sanitized.json");
            string recordsJson = ReadEntry(zip, "recent_records.json");
            string timingsJson = ReadEntry(zip, "recent_inspection_timings.json");
            string startupJson = ReadEntry(zip, "startup_diagnostics.json");
            configJson.Should().NotContain("operator-secret");
            configJson.Should().NotContain(tempDir);
            recordsJson.Should().NotContain("barcode-secret");
            recordsJson.Should().NotContain("raw.jpg");
            timingsJson.Should().Contain("CF-1");
            timingsJson.Should().Contain("CaptureMs");
            startupJson.Should().Contain("Storage directory");

        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(DiagnosticPackageExporterTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ReadEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
