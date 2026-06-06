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
            await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "hello log");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "model.onnx"), "model bytes");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "image.jpg"), "image bytes");

            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                LogsDirectory = logsDir,
                AppConfig = new AppConfig { StoragePath = tempDir },
                Recipe = new Recipe { RecipeId = "default", Version = "v1" },
                HealthSnapshot = new HealthSnapshot { HealthLevel = HealthLevel.Ok },
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
                "recent_records.json",
                "native_dependencies.txt",
                "logs/app.log"
            });
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));

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

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
