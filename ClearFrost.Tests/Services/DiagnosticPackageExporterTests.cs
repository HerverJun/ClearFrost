using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Yolo;
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
                OperationAuditChainVerification = new OperationAuditChainVerificationResult
                {
                    TotalRecords = 2,
                    VerifiedRecords = 1,
                    LastRecordSha256 = new string('a', 64),
                    Findings = new[]
                    {
                        new OperationAuditChainFinding
                        {
                            FilePath = Path.Combine(logsDir, "Outbox", "operation-audit-20260705.ndjson"),
                            LineNumber = 7,
                            Severity = "Blocking",
                            ErrorCode = "AuditRecordHashMismatch",
                            Message = "审计记录自身哈希不匹配",
                            ExpectedRecordSha256 = new string('b', 64),
                            ActualRecordSha256 = new string('c', 64)
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
                "diagnostic_manifest.json",
                "diagnostic_index.json",
                "field_report.md",
                "config.sanitized.json",
                "recipe.json",
                "recipe_summary.json",
                "startup_diagnostics.json",
                "startup_blockers.json",
                "health.json",
                "field_diagnostics.json",
                "recent_inspection_timings.json",
                "recent_errors.json",
                "maintenance_advice.json",
                "operation_audit_chain.json",
                "runtime_model_slots.json",
                "model_probe_summary.json",
                "model_registry_diagnostics.json",
                "queue_status.json",
                "recent_records.json",
                "logs/app.log"
            });
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
            zip.Entries.Select(e => e.FullName).Should().NotContain(e => e.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
            zip.Entries.Select(e => e.FullName).Should().NotContain("logs/Outbox/audit.json");

            using JsonDocument index = JsonDocument.Parse(ReadEntry(zip, "diagnostic_index.json"));
            index.RootElement.GetProperty("HashAlgorithm").GetString().Should().Be("SHA-256");
            List<JsonElement> indexEntries = index.RootElement
                .GetProperty("Entries")
                .EnumerateArray()
                .Select(entry => entry.Clone())
                .ToList();
            index.RootElement.GetProperty("EntryCount").GetInt32().Should().Be(indexEntries.Count);
            indexEntries.Should().Contain(entry => entry.GetProperty("EntryName").GetString() == "field_report.md");
            indexEntries.Should().Contain(entry => entry.GetProperty("EntryName").GetString() == "operation_audit_chain.json");
            indexEntries.Should().Contain(entry => entry.GetProperty("EntryName").GetString() == "logs/app.log");
            indexEntries.Should().NotContain(entry => entry.GetProperty("EntryName").GetString() == "diagnostic_index.json");

            long indexedBytes = 0;
            foreach (JsonElement indexedEntry in indexEntries)
            {
                string entryName = indexedEntry.GetProperty("EntryName").GetString() ?? string.Empty;
                byte[] bytes = ReadEntryBytes(zip, entryName);
                indexedEntry.GetProperty("LengthBytes").GetInt64().Should().Be(bytes.LongLength);
                indexedEntry.GetProperty("Sha256").GetString().Should().Be(ComputeSha256(bytes));
                indexedBytes += bytes.LongLength;
            }

            index.RootElement.GetProperty("TotalUncompressedBytes").GetInt64().Should().Be(indexedBytes);

            string configJson = ReadEntry(zip, "config.sanitized.json");
            string recordsJson = ReadEntry(zip, "recent_records.json");
            string timingsJson = ReadEntry(zip, "recent_inspection_timings.json");
            string startupJson = ReadEntry(zip, "startup_diagnostics.json");
            string report = ReadEntry(zip, "field_report.md");
            using JsonDocument manifest = JsonDocument.Parse(ReadEntry(zip, "diagnostic_manifest.json"));
            using JsonDocument auditChain = JsonDocument.Parse(ReadEntry(zip, "operation_audit_chain.json"));
            using JsonDocument fieldDiagnostics = JsonDocument.Parse(ReadEntry(zip, "field_diagnostics.json"));
            configJson.Should().NotContain("operator-secret");
            configJson.Should().NotContain(tempDir);
            recordsJson.Should().NotContain("barcode-secret");
            recordsJson.Should().NotContain("raw.jpg");
            auditChain.RootElement.GetProperty("Status").GetString().Should().Be("Blocking");
            auditChain.RootElement.GetProperty("TotalRecords").GetInt32().Should().Be(2);
            auditChain.RootElement.GetProperty("VerifiedRecords").GetInt32().Should().Be(1);
            auditChain.RootElement.GetProperty("FindingCount").GetInt32().Should().Be(1);
            auditChain.RootElement.GetProperty("Findings")[0].GetProperty("AuditFileName").GetString()
                .Should().Be("operation-audit-20260705.ndjson");
            fieldDiagnostics.RootElement.GetProperty("AuditChain").GetProperty("Status").GetString().Should().Be("Blocking");
            fieldDiagnostics.RootElement.GetProperty("AuditChain").GetProperty("FindingCount").GetInt32().Should().Be(1);
            ReadEntry(zip, "operation_audit_chain.json").Should().NotContain(tempDir);
            manifest.RootElement.GetProperty("AuditChainStatus").GetString().Should().Be("Blocking");
            manifest.RootElement.GetProperty("AuditChainFindingCount").GetInt32().Should().Be(1);
            report.Should().NotContain("operator-secret");
            report.Should().NotContain("barcode-secret");
            report.Should().NotContain(tempDir);
            report.Should().Contain("ClearFrost 现场诊断报告");
            report.Should().Contain("维护建议");
            report.Should().Contain("操作审计链");
            report.Should().Contain("AuditRecordHashMismatch");
            timingsJson.Should().Contain("CF-1");
            timingsJson.Should().Contain("CaptureMs");
            startupJson.Should().Contain("Storage directory");

        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_日志目录链接不会收集目录外文件()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logsDir = Path.Combine(tempDir, "Logs");
            string externalDir = Path.Combine(tempDir, "External");
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(externalDir);

            await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "normal log");
            await File.WriteAllTextAsync(Path.Combine(externalDir, "secret.log"), "external secret");

            var escapedFile = new FileInfo(Path.Combine(logsDir, "..", "External", "secret.log"));
            DiagnosticPackageExporter.IsSafeLogFileForPackage(logsDir, escapedFile).Should().BeFalse();

            string linkDir = Path.Combine(logsDir, "LinkedExternal");
            bool linkCreated = TryCreateDirectorySymbolicLink(linkDir, externalDir);
            if (linkCreated)
            {
                var linkedFile = new FileInfo(Path.Combine(linkDir, "secret.log"));
                DiagnosticPackageExporter.IsSafeLogFileForPackage(logsDir, linkedFile).Should().BeFalse();
            }

            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                LogsDirectory = logsDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Select(entry => entry.FullName).Should().Contain("logs/app.log");
            zip.Entries.Select(entry => entry.FullName).Should().NotContain("logs/LinkedExternal/secret.log");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_拒绝链接祖先下输出目录且不创建外部子目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalDir = Path.Combine(tempDir, "ExternalOutput");
            string linkDir = Path.Combine(tempDir, "LinkedOutput");
            Directory.CreateDirectory(externalDir);
            if (!TryCreateDirectorySymbolicLink(linkDir, externalDir))
            {
                return;
            }

            string outputDir = Path.Combine(linkDir, "nested");
            var exporter = new DiagnosticPackageExporter();

            Func<Task> act = () => exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*链接目录*");
            Directory.Exists(Path.Combine(externalDir, "nested")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task FieldHandoffExportAsync_拒绝链接祖先下输出目录且不创建外部子目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string externalDir = Path.Combine(tempDir, "ExternalHandoff");
            string linkDir = Path.Combine(tempDir, "LinkedHandoff");
            Directory.CreateDirectory(externalDir);
            if (!TryCreateDirectorySymbolicLink(linkDir, externalDir))
            {
                return;
            }

            var exporter = new FieldHandoffReportExporter();

            Func<Task> act = () => exporter.ExportAsync(new FieldHandoffReportRequest
            {
                OutputDirectory = Path.Combine(linkDir, "nested"),
                FieldDiagnostics = new FieldDiagnosticsSnapshot()
            });

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*链接目录*");
            Directory.Exists(Path.Combine(externalDir, "nested")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_运行时模型槽位按路径匹配注册表并导出配方摘要()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            string barePath = Path.Combine(tempDir, "ONNX", "model.onnx");
            string packagePath = Path.Combine(tempDir, "Packages", "package-model", "model.onnx");
            string recipeSnapshotPath = Path.Combine(tempDir, "Recipes", "Versions", "default_r2.json");
            string packageHash = new string('b', 64);
            var exporter = new DiagnosticPackageExporter();

            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe
                {
                    RecipeId = "recipe-a",
                    Version = "r2",
                    TargetLabel = "wire",
                    TargetCount = 2,
                    CurrentModelFileName = "model.onnx"
                },
                CurrentRecipeVersion = new RecipeVersionInfo
                {
                    RecipeId = "recipe-a",
                    Version = "r2",
                    SnapshotPath = recipeSnapshotPath
                },
                ModelEntries = new[]
                {
                    new ModelRegistryEntry
                    {
                        ModelId = "bare-model",
                        Version = "1",
                        ModelHash = new string('a', 64),
                        UsedModelName = "model.onnx",
                        ModelPath = barePath,
                        Status = ModelRegistryStatus.Warning
                    },
                    new ModelRegistryEntry
                    {
                        ModelId = "package-model",
                        Version = "2",
                        ModelHash = packageHash,
                        UsedModelName = "model.onnx",
                        ModelPath = packagePath,
                        ManifestPath = Path.Combine(tempDir, "Packages", "package-model", "manifest.json"),
                        IsPackage = true,
                        Status = ModelRegistryStatus.Ready,
                        TaskType = "Detect",
                        InputWidth = 640,
                        InputHeight = 640,
                        ApprovalStatus = ModelApprovalStatuses.Approved,
                        ApprovedForProduction = true
                    }
                },
                RuntimeModelSnapshot = new DetectionRuntimeModelSnapshot
                {
                    Primary = new DetectionModelSlotSnapshot
                    {
                        Role = ModelRole.Primary,
                        IsLoaded = true,
                        ModelPath = packagePath
                    }
                },
                StartupDiagnostics = new StartupDiagnosticReport
                {
                    Items = new[]
                    {
                        new StartupDiagnosticItem
                        {
                            Name = "Replay evidence gate",
                            Status = StartupDiagnosticStatus.Fail,
                            Message = "Approved model evidence validation failed.",
                            Details = "Primary package-model/2",
                            IsBlocking = true
                        }
                    }
                },
                HealthSnapshot = new HealthSnapshot
                {
                    ModelStatus = "Loaded:model.onnx:CPUExecutionProvider"
                }
            });

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            using JsonDocument slots = JsonDocument.Parse(ReadEntry(zip, "runtime_model_slots.json"));
            JsonElement primary = slots.RootElement
                .EnumerateArray()
                .First(slot => slot.GetProperty("Role").GetString() == "Primary");
            primary.GetProperty("ModelId").GetString().Should().Be("package-model");
            primary.GetProperty("Version").GetString().Should().Be("2");
            primary.GetProperty("ModelHash").GetString().Should().Be(packageHash);
            primary.GetProperty("RegistryMatchStrategy").GetString().Should().Be("ModelPath");
            primary.GetProperty("ModelPath").GetString().Should().Be(Path.GetFullPath(packagePath));

            using JsonDocument recipe = JsonDocument.Parse(ReadEntry(zip, "recipe_summary.json"));
            recipe.RootElement.GetProperty("RecipeId").GetString().Should().Be("recipe-a");
            recipe.RootElement.GetProperty("Version").GetString().Should().Be("r2");
            recipe.RootElement.GetProperty("VersionSnapshotPath").GetString().Should().Be(recipeSnapshotPath);

            using JsonDocument fieldDiagnostics = JsonDocument.Parse(ReadEntry(zip, "field_diagnostics.json"));
            fieldDiagnostics.RootElement.GetProperty("RecipeId").GetString().Should().Be("recipe-a");
            fieldDiagnostics.RootElement.GetProperty("RecipeVersion").GetString().Should().Be("r2");
            fieldDiagnostics.RootElement.GetProperty("RecipeTargetLabel").GetString().Should().Be("wire");
            fieldDiagnostics.RootElement.GetProperty("RecipeTargetCount").GetInt32().Should().Be(2);

            using JsonDocument blockers = JsonDocument.Parse(ReadEntry(zip, "startup_blockers.json"));
            blockers.RootElement.GetArrayLength().Should().Be(1);
            blockers.RootElement[0].GetProperty("Name").GetString().Should().Be("Replay evidence gate");

            using JsonDocument manifest = JsonDocument.Parse(ReadEntry(zip, "diagnostic_manifest.json"));
            manifest.RootElement.GetProperty("RuntimeModelSlotCount").GetInt32().Should().Be(1);
            manifest.RootElement.GetProperty("StartupReady").GetBoolean().Should().BeFalse();
            manifest.RootElement.GetProperty("StartupBlockingFailureCount").GetInt32().Should().Be(1);
            manifest.RootElement.GetProperty("MaintenanceAdviceCount").GetInt32().Should().BeGreaterThan(0);

            using JsonDocument advice = JsonDocument.Parse(ReadEntry(zip, "maintenance_advice.json"));
            advice.RootElement.GetArrayLength().Should().BeGreaterThan(0);
            advice.RootElement.EnumerateArray().Should().Contain(item =>
                item.GetProperty("Code").GetString() == "StartupBlocked" &&
                item.GetProperty("Advice").GetString()!.Contains("审批", StringComparison.OrdinalIgnoreCase));

            string report = ReadEntry(zip, "field_report.md");
            report.Should().Contain("package-model@2#bbbbbbbbbbbb");
            report.Should().Contain("Replay evidence gate");
            report.Should().Contain("StartupBlocked");
            report.Should().Contain("maintenance_advice.json");
            report.Should().Contain("diagnostic_index.json");
            report.Should().NotContain(packagePath);
            report.Should().NotContain(recipeSnapshotPath);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_连续导出生成唯一文件且不留下临时包()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();

            string firstZip = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });
            string secondZip = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            firstZip.Should().NotBe(secondZip);
            File.Exists(firstZip).Should().BeTrue();
            File.Exists(secondZip).Should().BeTrue();
            Directory.EnumerateFiles(outputDir, "*.zip", SearchOption.TopDirectoryOnly).Should().HaveCount(2);
            Directory.EnumerateFiles(outputDir, "*.tmp", SearchOption.TopDirectoryOnly).Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_取消导出不留下临时包或半成品()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var exporter = new DiagnosticPackageExporter();

            Func<Task> act = () => exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            }, cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            Directory.Exists(outputDir).Should().BeTrue();
            Directory.EnumerateFiles(outputDir, "*.zip", SearchOption.TopDirectoryOnly).Should().BeEmpty();
            Directory.EnumerateFiles(outputDir, "*.tmp", SearchOption.TopDirectoryOnly).Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void TryDeleteTemporaryPackageFile_删除输出目录内普通临时包()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outputDir);
            string tempPackage = Path.Combine(outputDir, ".ClearFrost_Diagnostics_test.zip.tmp");
            File.WriteAllText(tempPackage, "temp package");

            DiagnosticPackageExporter.TryDeleteTemporaryPackageFile(tempPackage, outputDir).Should().BeTrue();

            File.Exists(tempPackage).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void TryDeleteTemporaryPackageFile_拒绝链接临时包且不删除外部目标()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedTempPackage = string.Empty;
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outputDir);
            string externalPackage = Path.Combine(externalDir, "external.tmp");
            File.WriteAllText(externalPackage, "external package");
            linkedTempPackage = Path.Combine(outputDir, ".ClearFrost_Diagnostics_linked.zip.tmp");
            if (!TryCreateFileSymbolicLink(linkedTempPackage, externalPackage))
            {
                return;
            }

            DiagnosticPackageExporter.TryDeleteTemporaryPackageFile(linkedTempPackage, outputDir).Should().BeFalse();

            File.Exists(externalPackage).Should().BeTrue();
            File.ReadAllText(externalPackage).Should().Be("external package");
        }
        finally
        {
            TryDeleteFileLink(linkedTempPackage);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void TryDeleteTemporaryPackageFile_拒绝输出目录外临时包()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            Directory.CreateDirectory(outputDir);
            string externalPackage = Path.Combine(externalDir, "external.tmp");
            File.WriteAllText(externalPackage, "external package");

            DiagnosticPackageExporter.TryDeleteTemporaryPackageFile(externalPackage, outputDir).Should().BeFalse();

            File.Exists(externalPackage).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task TryAddSafeLogFileAsync_安全日志写入Zip并建立索引()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string logsDir = Path.Combine(tempDir, "Logs");
            Directory.CreateDirectory(logsDir);
            string logFile = Path.Combine(logsDir, "app.log");
            File.WriteAllText(logFile, "safe log");
            string zipPath = Path.Combine(tempDir, "logs.zip");
            var indexEntries = new List<DiagnosticPackageIndexEntry>();

            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                bool added = await DiagnosticPackageExporter.TryAddSafeLogFileAsync(
                    archive,
                    logsDir + Path.DirectorySeparatorChar,
                    logFile,
                    indexEntries);

                added.Should().BeTrue();
            }

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            zip.GetEntry("logs/app.log").Should().NotBeNull();
            indexEntries.Should().ContainSingle(entry => entry.EntryName == "logs/app.log");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task TryAddSafeLogFileAsync_拒绝链接日志文件且不读取外部目标()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedLogFile = string.Empty;
        try
        {
            string logsDir = Path.Combine(tempDir, "Logs");
            Directory.CreateDirectory(logsDir);
            string externalLog = Path.Combine(externalDir, "external.log");
            File.WriteAllText(externalLog, "external secret");
            linkedLogFile = Path.Combine(logsDir, "linked.log");
            if (!TryCreateFileSymbolicLink(linkedLogFile, externalLog))
            {
                return;
            }

            string zipPath = Path.Combine(tempDir, "logs.zip");
            var indexEntries = new List<DiagnosticPackageIndexEntry>();
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                bool added = await DiagnosticPackageExporter.TryAddSafeLogFileAsync(
                    archive,
                    logsDir + Path.DirectorySeparatorChar,
                    linkedLogFile,
                    indexEntries);

                added.Should().BeFalse();
            }

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Should().BeEmpty();
            indexEntries.Should().BeEmpty();
            File.ReadAllText(externalLog).Should().Be("external secret");
        }
        finally
        {
            TryDeleteFileLink(linkedLogFile);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_完整诊断包返回Healthy并匹配包级摘要()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeTrue();
            result.Status.Should().Be("Healthy");
            result.PackageSha256.Should().Be(ComputeSha256(File.ReadAllBytes(zipPath)));
            result.IndexEntryCount.Should().BeGreaterThan(0);
            result.VerifiedEntryCount.Should().Be(result.IndexEntryCount);
            result.Findings.Should().BeEmpty();

            using ZipArchive zip = ZipFile.OpenRead(zipPath);
            result.IndexSha256.Should().Be(ComputeSha256(ReadEntryBytes(zip, "diagnostic_index.json")));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_拒绝链接诊断包文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedPackage = Path.Combine(tempDir, "linked-diagnostics.zip");
        try
        {
            string externalPackage = Path.Combine(externalDir, "external.zip");
            File.WriteAllText(externalPackage, "external package");
            if (!TryCreateFileSymbolicLink(linkedPackage, externalPackage))
            {
                return;
            }

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(linkedPackage);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.PackageSha256.Should().BeEmpty();
            result.Findings.Should().ContainSingle(finding =>
                finding.EntryName == linkedPackage &&
                finding.ErrorCode == "DiagnosticPackageReparsePoint");
            File.ReadAllText(externalPackage).Should().Be("external package");
        }
        finally
        {
            TryDeleteFileLink(linkedPackage);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_拒绝链接诊断包父目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDirectory = Path.Combine(tempDir, "linked-packages");
        try
        {
            string externalPackage = Path.Combine(externalDir, "diagnostics.zip");
            File.WriteAllText(externalPackage, "external package");
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDir))
            {
                return;
            }

            string linkedPackage = Path.Combine(linkedDirectory, "diagnostics.zip");
            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(linkedPackage);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.PackageSha256.Should().BeEmpty();
            result.Findings.Should().ContainSingle(finding =>
                finding.EntryName == linkedPackage &&
                finding.ErrorCode == "DiagnosticPackageReparsePoint");
            File.ReadAllText(externalPackage).Should().Be("external package");
        }
        finally
        {
            TryDeleteDirectoryLink(linkedDirectory);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_条目被篡改返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry entry = zip.GetEntry("field_report.md") ?? throw new FileNotFoundException("field_report.md");
                entry.Delete();
                ZipArchiveEntry tampered = zip.CreateEntry("field_report.md");
                await using Stream stream = tampered.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("tampered field report");
            }

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.VerifiedEntryCount.Should().BeLessThan(result.IndexEntryCount);
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "field_report.md" &&
                finding.ErrorCode == "DiagnosticEntryHashMismatch" &&
                !string.Equals(finding.ExpectedSha256, finding.ActualSha256, StringComparison.OrdinalIgnoreCase));
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "field_report.md" &&
                finding.ErrorCode == "DiagnosticEntryLengthMismatch" &&
                finding.ExpectedLengthBytes != finding.ActualLengthBytes);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_索引元数据被篡改返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                string indexJson = ReadEntry(zip, "diagnostic_index.json");
                JsonObject index = JsonNode.Parse(indexJson)?.AsObject() ??
                                   throw new InvalidDataException("diagnostic_index.json");
                index["HashAlgorithm"] = "MD5";
                index["EntryCount"] = (index["EntryCount"]?.GetValue<int>() ?? 0) + 2;
                index["TotalUncompressedBytes"] = (index["TotalUncompressedBytes"]?.GetValue<long>() ?? 0) + 1;

                ZipArchiveEntry entry = zip.GetEntry("diagnostic_index.json") ??
                                        throw new FileNotFoundException("diagnostic_index.json");
                entry.Delete();
                ZipArchiveEntry tampered = zip.CreateEntry("diagnostic_index.json");
                await using Stream stream = tampered.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "diagnostic_index.json" &&
                finding.ErrorCode == "DiagnosticIndexHashAlgorithmUnsupported");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "diagnostic_index.json" &&
                finding.ErrorCode == "DiagnosticIndexEntryCountMismatch");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "diagnostic_index.json" &&
                finding.ErrorCode == "DiagnosticIndexTotalBytesMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_核心条目被包和索引同时移除返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            await RemoveIndexReferenceAsync(zipPath, "field_report.md", deleteEntry: true);

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "field_report.md" &&
                finding.ErrorCode == "DiagnosticCoreEntryMissing");
            result.Findings.Should().NotContain(finding =>
                finding.EntryName == "diagnostic_index.json" &&
                finding.ErrorCode == "DiagnosticIndexEntryCountMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_核心条目未纳入索引返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            await RemoveIndexReferenceAsync(zipPath, "operation_audit_chain.json", deleteEntry: false);

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "operation_audit_chain.json" &&
                finding.ErrorCode == "DiagnosticCoreEntryNotIndexed" &&
                finding.Severity == "Blocking");
            result.Findings.Should().NotContain(finding =>
                finding.EntryName == "operation_audit_chain.json" &&
                finding.ErrorCode == "DiagnosticEntryUnindexed");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_包内包含路径逃逸条目返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Update))
            {
                ZipArchiveEntry unsafeEntry = zip.CreateEntry("../escape.txt");
                await using Stream stream = unsafeEntry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("escape");
            }

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "../escape.txt" &&
                finding.ErrorCode == "DiagnosticEntryUnsafePath");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_索引声明路径逃逸条目返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            await RewriteIndexEntryNameAsync(zipPath, "field_report.md", "../field_report.md");

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "../field_report.md" &&
                finding.ErrorCode == "DiagnosticIndexEntryUnsafePath");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task VerifyAsync_索引条目长度和哈希格式被篡改返回Blocking()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string outputDir = Path.Combine(tempDir, "out");
            var exporter = new DiagnosticPackageExporter();
            string zipPath = await exporter.ExportAsync(new DiagnosticPackageRequest
            {
                OutputDirectory = outputDir,
                AppConfig = new AppConfig(),
                Recipe = new Recipe { RecipeId = "default", Version = "v1" }
            });

            await RewriteIndexEntryMetadataAsync(zipPath, "field_report.md", entry =>
            {
                entry["LengthBytes"] = -1;
                entry["Sha256"] = "not-a-sha256";
            });

            var verifier = new DiagnosticPackageIntegrityVerifier();
            DiagnosticPackageIntegrityVerificationResult result = await verifier.VerifyAsync(zipPath);

            result.Succeeded.Should().BeFalse();
            result.Status.Should().Be("Blocking");
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "field_report.md" &&
                finding.ErrorCode == "DiagnosticIndexEntryLengthInvalid" &&
                finding.ExpectedLengthBytes == -1);
            result.Findings.Should().Contain(finding =>
                finding.EntryName == "field_report.md" &&
                finding.ErrorCode == "DiagnosticIndexEntrySha256Invalid" &&
                finding.ExpectedSha256 == "not-a-sha256");
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

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
        using Stream stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task RemoveIndexReferenceAsync(
        string zipPath,
        string entryName,
        bool deleteEntry)
    {
        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        string indexJson = ReadEntry(zip, "diagnostic_index.json");
        JsonObject index = JsonNode.Parse(indexJson)?.AsObject() ??
                           throw new InvalidDataException("diagnostic_index.json");
        JsonArray entries = index["Entries"]?.AsArray() ??
                            throw new InvalidDataException("diagnostic_index.json entries");

        long removedBytes = 0;
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            JsonObject item = entries[i]?.AsObject() ??
                              throw new InvalidDataException("diagnostic_index.json entry");
            string indexedEntryName = item["EntryName"]?.GetValue<string>() ?? string.Empty;
            if (!string.Equals(indexedEntryName, entryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            removedBytes += item["LengthBytes"]?.GetValue<long>() ?? 0;
            entries.RemoveAt(i);
        }

        index["EntryCount"] = entries.Count;
        long totalBytes = index["TotalUncompressedBytes"]?.GetValue<long>() ?? 0;
        index["TotalUncompressedBytes"] = Math.Max(0, totalBytes - removedBytes);

        if (deleteEntry)
        {
            ZipArchiveEntry packageEntry = zip.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
            packageEntry.Delete();
        }

        ZipArchiveEntry indexEntry = zip.GetEntry("diagnostic_index.json") ??
                                     throw new FileNotFoundException("diagnostic_index.json");
        indexEntry.Delete();
        ZipArchiveEntry tampered = zip.CreateEntry("diagnostic_index.json");
        await using Stream stream = tampered.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RewriteIndexEntryNameAsync(
        string zipPath,
        string oldEntryName,
        string newEntryName)
    {
        await RewriteIndexEntryMetadataAsync(
            zipPath,
            oldEntryName,
            entry => entry["EntryName"] = newEntryName);
    }

    private static async Task RewriteIndexEntryMetadataAsync(
        string zipPath,
        string entryName,
        Action<JsonObject> rewrite)
    {
        using ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Update);
        string indexJson = ReadEntry(zip, "diagnostic_index.json");
        JsonObject index = JsonNode.Parse(indexJson)?.AsObject() ??
                           throw new InvalidDataException("diagnostic_index.json");
        JsonArray entries = index["Entries"]?.AsArray() ??
                            throw new InvalidDataException("diagnostic_index.json entries");

        JsonObject entry = entries
            .Select(node => node?.AsObject() ?? throw new InvalidDataException("diagnostic_index.json entry"))
            .First(item => string.Equals(
                item["EntryName"]?.GetValue<string>(),
                entryName,
                StringComparison.OrdinalIgnoreCase));
        rewrite(entry);

        ZipArchiveEntry indexEntry = zip.GetEntry("diagnostic_index.json") ??
                                     throw new FileNotFoundException("diagnostic_index.json");
        indexEntry.Delete();
        ZipArchiveEntry tampered = zip.CreateEntry("diagnostic_index.json");
        await using Stream stream = tampered.Open();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(index.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ComputeSha256(byte[] bytes)
    {
        using SHA256 sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
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

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(path);
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

    private static void TryDeleteFileLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
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
            Directory.Delete(path, true);
        }
    }
}
