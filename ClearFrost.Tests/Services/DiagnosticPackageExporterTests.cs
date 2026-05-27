using System.IO.Compression;
using System.Text.Json;
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
            await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "hello log");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "model.onnx"), "model bytes");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "image.jpg"), "image bytes");
            using (var storage = new StorageService(tempDir))
            {
                storage.WriteAuditLog("Settings", "Save", "StoragePath=D:\\Data", success: true);
            }

            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                LogsDirectory = logsDir,
                AppConfig = new AppConfig { StoragePath = tempDir },
                Recipe = new Recipe { RecipeId = "default", Version = "v1" },
                HealthSnapshot = new HealthSnapshot { HealthLevel = HealthLevel.Ok },
                AlarmSnapshot = new AlarmSnapshot
                {
                    ActiveCount = 1,
                    ActiveAlarms = new[]
                    {
                        new AlarmRecord
                        {
                            AlarmId = "ALM-1",
                            Source = "Storage",
                            Message = "磁盘空间不足",
                            Severity = AlarmSeverity.Warning
                        }
                    }
                },
                RecentRecords = new List<DetectionRecord>
                {
                    new DetectionRecord { InspectionId = "CF-1", ModelName = "model-a" }
                }
            });

            File.Exists(zipPath).Should().BeTrue();

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Select(e => e.FullName).Should().Contain(new[]
            {
                "config.sanitized.json",
                "recipe.json",
                "health.json",
                "alarms.json",
                "recent_records.json",
                "audit_integrity_summary.json",
                "package_manifest.json",
                "logs/app.log"
            });
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));

            var auditSummary = JsonSerializer.Deserialize<DiagnosticAuditIntegritySummary>(
                ReadEntryText(zip, "audit_integrity_summary.json"));
            auditSummary.Should().NotBeNull();
            auditSummary!.ValidRecords.Should().Be(1);
            auditSummary.TamperedRecords.Should().Be(0);

            var manifest = JsonSerializer.Deserialize<DiagnosticPackageManifest>(
                ReadEntryText(zip, "package_manifest.json"));
            manifest.Should().NotBeNull();
            manifest!.Entries.Should().Contain(entry => entry.Path == "logs/app.log" && entry.Sha256.Length == 64);
            manifest.Entries.Should().Contain(entry => entry.Path == "audit_integrity_summary.json" && entry.SizeBytes > 0);

        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string ReadEntryText(ZipArchive zip, string entryName)
    {
        ZipArchiveEntry entry = zip.GetEntry(entryName) ?? throw new InvalidOperationException($"Entry not found: {entryName}");
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimStart('\uFEFF');
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(DiagnosticPackageExporterTests), Guid.NewGuid().ToString("N"));
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
