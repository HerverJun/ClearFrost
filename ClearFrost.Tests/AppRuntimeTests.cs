using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Rules;
using ClearFrost.Core.Security;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Models;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests
{
#pragma warning disable CS0067
    [Collection(TestCollections.SqliteGlobalPool)]
    public class AppRuntimeTests
    {
        [Fact]
        public async Task DisposeAsync_会先排空后台任务再释放依赖()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig { StoragePath = tempDir };
                using var cameraManager = new CameraManager(true);
                var cameraService = new FakeCameraService(order);
                var plcService = new FakePlcService(order);
                var detectionService = new FakeDetectionService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var statisticsService = new FakeStatisticsService(order);
                var databaseService = new FakeDatabaseService(order);
                using var imageSaveQueue = new ImageSaveQueue();
                using var detectionRecordQueue = new DetectionRecordQueue(databaseService);
                var webUiController = new WebUIController();

                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    cameraService,
                    plcService,
                    detectionService,
                    storageService,
                    statisticsService,
                    databaseService,
                    imageSaveQueue,
                    detectionRecordQueue,
                    webUiController);

                string imagePath = Path.Combine(tempDir, "queued", "frame.jpg");
                using (var mat = new Mat(4, 4, MatType.CV_8UC1, Scalar.All(180)))
                {
                    runtime.ImageSaveQueue.Enqueue(mat, imagePath).Should().BeTrue();
                }

                runtime.DetectionRecordQueue.Enqueue(new DetectionPersistencePayload
                {
                    Timestamp = DateTime.Now,
                    IsQualified = true,
                    ModelName = "queued-model",
                    ActualCount = 1
                }).Should().BeTrue();

                await runtime.DisposeAsync();

                File.Exists(imagePath).Should().BeTrue();
                order.Should().ContainInOrder(
                    "plc-stop-monitoring",
                    "camera-stop-capture",
                    "camera-close",
                    "statistics-save-all",
                    "db-save",
                    "detection-dispose",
                    "statistics-dispose",
                    "db-dispose",
                    "plc-dispose",
                    "camera-dispose",
                    "storage-dispose");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_默认检测服务使用配置GpuIndex()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var appConfig = new AppConfig
            {
                StoragePath = tempDir,
                EnableGpu = true,
                GpuIndex = 2
            };

            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                cameraService: null,
                plcService: null,
                detectionService: null,
                storageService: null,
                statisticsService: null,
                databaseService: null,
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: null);

            try
            {
                runtime.DetectionService.RuntimeStatus.GpuRequested.Should().BeTrue();
                runtime.DetectionService.RuntimeStatus.GpuDeviceId.Should().Be(2);
            }
            finally
            {
                await runtime.DisposeAsync();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_统一创建Replay闭环服务和生产Gate()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    RequireApprovedModelsForProduction = true
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    new FakeStorageService(tempDir, order),
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                runtime.DecisionEvaluator.Should().NotBeNull();
                runtime.ReplayPolicy.Should().NotBeNull();
                runtime.ManualReviewStore.Should().NotBeNull();
                runtime.ReplayDatasetStore.Should().NotBeNull();
                runtime.ReplayRunStore.Should().NotBeNull();
                runtime.ModelApprovalEvidenceStore.Should().NotBeNull();
                runtime.ReplayProductionGate.Should().NotBeNull();
                runtime.ReplayCoordinator.Should().NotBeNull();
                runtime.ReplayAssetCoordinator.Should().NotBeNull();
                runtime.ReplayDatasetLifecycleService.Should().NotBeNull();
                runtime.ReplayIntegrityScanner.Should().NotBeNull();
                runtime.ReplayApplicationService.Should().NotBeNull();
                runtime.ReplayApprovalApplicationService.Should().NotBeNull();
                runtime.StartupDiagnostics.CurrentReport.Items.Should().Contain(item =>
                    item.Name == "Replay evidence gate" &&
                    item.Status == StartupDiagnosticStatus.Fail &&
                    item.IsBlocking &&
                    item.Message.Contains("Primary model reference is empty", StringComparison.OrdinalIgnoreCase));

                await runtime.DisposeAsync();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_为当前Primary批准包执行一次性Legacy迁移()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string packageRoot = Path.Combine(tempDir, "models");
                string modelPath = CreateApprovedPackage(packageRoot, "legacy-current", "1");
                string modelHash = ComputeSha256(modelPath);
                var reference = new ProductionModelReference
                {
                    Type = ProductionModelReferenceType.ApprovedPackage,
                    ModelId = "legacy-current",
                    Version = "1",
                    Sha256 = modelHash
                };
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    ModelPackageDirectory = packageRoot,
                    RequireApprovedModelsForProduction = true,
                    CurrentModelReference = reference,
                    CurrentModelFileName = Path.GetFileName(modelPath)
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    new FakeStorageService(tempDir, order),
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                ModelRegistryEntry entry = runtime.ModelRegistry.Resolve(modelPath)!;
                entry.Manifest!.Approval.LegacyMigration.Should().NotBeNull();
                entry.Manifest.Approval.LegacyMigration!.ModelRole.Should().Be("Primary");
                entry.Manifest.Approval.LegacyMigration.ConfigReference.Should().Be(reference.ToSelectionValue());
                entry.Manifest.Approval.LegacyMigration.ModelHash.Should().Be(modelHash);
                entry.Manifest.Approval.ReplayEvidenceId.Should().BeEmpty();
                runtime.StartupDiagnostics.CurrentReport.Items.Should().Contain(item =>
                    item.Name == "Replay evidence gate" &&
                    item.Status == StartupDiagnosticStatus.Pass &&
                    item.IsBlocking);

                await runtime.DisposeAsync();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void LegacyReplayMigration_拒绝扫描后Manifest模型路径逃逸()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string packageRoot = Path.Combine(tempDir, "models");
                string modelPath = CreateApprovedPackage(packageRoot, "legacy-path-tamper", "1");
                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });
                ModelRegistryEntry entry = registry.Resolve(modelPath)!;
                ModelPackageManifest manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                    File.ReadAllText(entry.ManifestPath),
                    ReplayJson.Options) ?? throw new InvalidOperationException("Manifest parse failed.");
                manifest.ModelFileName = Path.Combine("..", "outside.onnx");
                File.WriteAllText(entry.ManifestPath, JsonSerializer.Serialize(manifest, ReplayJson.Options));

                (bool succeeded, string skipReason) = InvokeLegacyMigrationAssetValidation(entry);

                succeeded.Should().BeFalse();
                skipReason.Should().Contain("ModelFileName");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void LegacyReplayMigration_拒绝扫描后模型文件被替换为链接()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string probeTarget = Path.Combine(tempDir, "probe-target.txt");
                string probeLink = Path.Combine(tempDir, "probe-link.txt");
                File.WriteAllText(probeTarget, "probe");
                if (!TryCreateFileSymbolicLink(probeLink, probeTarget))
                {
                    return;
                }

                File.Delete(probeLink);
                File.Delete(probeTarget);

                string packageRoot = Path.Combine(tempDir, "models");
                string modelPath = CreateApprovedPackage(packageRoot, "legacy-link-tamper", "1");
                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });
                ModelRegistryEntry entry = registry.Resolve(modelPath)!;
                string externalModel = Path.Combine(tempDir, "external-model.onnx");
                File.Copy(modelPath, externalModel);
                File.Delete(modelPath);
                TryCreateFileSymbolicLink(modelPath, externalModel).Should().BeTrue();

                (bool succeeded, string skipReason) = InvokeLegacyMigrationAssetValidation(entry);

                succeeded.Should().BeFalse();
                skipReason.Should().Contain("reparse point");
                File.Exists(externalModel).Should().BeTrue();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void LegacyReplayMigration_拒绝扫描后Manifest文件被替换为链接()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string probeTarget = Path.Combine(tempDir, "probe-target.txt");
                string probeLink = Path.Combine(tempDir, "probe-link.txt");
                File.WriteAllText(probeTarget, "probe");
                if (!TryCreateFileSymbolicLink(probeLink, probeTarget))
                {
                    return;
                }

                File.Delete(probeLink);
                File.Delete(probeTarget);

                string packageRoot = Path.Combine(tempDir, "models");
                string modelPath = CreateApprovedPackage(packageRoot, "legacy-manifest-link-tamper", "1");
                var registry = new ModelRegistry();
                registry.Scan(new ModelRegistryScanOptions
                {
                    PackageDirectory = packageRoot,
                    RequireProductionApproval = true,
                    Warmup = (_, _) => true
                });
                ModelRegistryEntry entry = registry.Resolve(modelPath)!;
                string externalManifest = Path.Combine(tempDir, "external-manifest.json");
                File.Copy(entry.ManifestPath, externalManifest);
                File.Delete(entry.ManifestPath);
                TryCreateFileSymbolicLink(entry.ManifestPath, externalManifest).Should().BeTrue();

                (bool succeeded, string skipReason) = InvokeLegacyMigrationAssetValidation(entry);

                succeeded.Should().BeFalse();
                skipReason.Should().Contain("manifest file is a reparse point");
                File.Exists(externalManifest).Should().BeTrue();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task Constructor_默认配置使用Cpu检测服务()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var appConfig = new AppConfig
            {
                StoragePath = tempDir
            };

            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                cameraService: null,
                plcService: null,
                detectionService: null,
                storageService: null,
                statisticsService: null,
                databaseService: null,
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: null);

            try
            {
                runtime.DetectionService.RuntimeStatus.GpuRequested.Should().BeFalse();
                runtime.DetectionService.RuntimeStatus.GpuActive.Should().BeFalse();
            }
            finally
            {
                await runtime.DisposeAsync();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task RefreshModelRegistry_运行中新加入Onnx会被重新发现()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string onnxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ONNX");
            string modelName = $"runtime-refresh-{Guid.NewGuid():N}.onnx";
            string modelPath = Path.Combine(onnxDir, modelName);
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(onnxDir);

            var appConfig = new AppConfig { StoragePath = tempDir };
            using var cameraManager = new CameraManager(true);
            var runtime = new AppRuntime(
                appConfig,
                cameraManager,
                cameraService: null,
                plcService: null,
                detectionService: null,
                storageService: null,
                statisticsService: null,
                databaseService: null,
                imageSaveQueue: null,
                detectionRecordQueue: null,
                webUIController: null);

            try
            {
                runtime.ModelRegistry.Resolve(modelName).Should().BeNull();

                File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });

                runtime.RefreshModelRegistry();

                ModelRegistryEntry? entry = runtime.ModelRegistry.Resolve(modelName);
                entry.Should().NotBeNull();
                entry!.UsedModelName.Should().Be(modelName);
                entry.ModelPath.Should().Be(modelPath);
            }
            finally
            {
                if (File.Exists(modelPath))
                {
                    File.Delete(modelPath);
                }

                await runtime.DisposeAsync();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task RefreshStoragePath_运行时服务切换到最新存储路径()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string newStoragePath = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(newStoragePath);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    RequireApprovedModelsForProduction = false
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var statisticsService = new FakeStatisticsService(order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    statisticsService,
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    appConfig.StoragePath = newStoragePath;

                    runtime.RefreshStoragePath();

                    storageService.BaseStoragePath.Should().Be(newStoragePath);
                    storageService.ImageBasePath.Should().Be(Path.Combine(newStoragePath, "Images"));
                    storageService.LogBasePath.Should().Be(Path.Combine(newStoragePath, "Logs"));
                    statisticsService.BasePath.Should().Be(newStoragePath);
                    runtime.StartupDiagnostics.CurrentReport.Items.Should().Contain(item =>
                        item.Name == "Storage directory" &&
                        item.Status == StartupDiagnosticStatus.Pass &&
                        item.Details.Contains(newStoragePath, StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }

                if (Directory.Exists(newStoragePath))
                {
                    Directory.Delete(newStoragePath, true);
                }
            }
        }

        [Fact]
        public async Task ExportDiagnosticPackageAsync_成功导出时写入操作审计()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "diag-operator",
                    CurrentOperatorRole = ProductionRole.Engineer,
                    RequireApprovedModelsForProduction = true
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    string outputDirectory = Path.Combine(storageService.LogBasePath, "Diagnostics");
                    DiagnosticPackageExportSummary summary = await runtime.ExportDiagnosticPackageAsync(outputDirectory);
                    string packagePath = summary.PackagePath;

                    File.Exists(packagePath).Should().BeTrue();
                    summary.SizeBytes.Should().Be(new FileInfo(packagePath).Length);
                    summary.PackageSha256.Should().Be(ComputeSha256(packagePath));
                    summary.IntegrityEntryCount.Should().BeGreaterThan(0);
                    summary.VerifiedEntryCount.Should().Be(summary.IntegrityEntryCount);
                    summary.IntegrityStatus.Should().Be("Healthy");
                    summary.IntegrityFindingCount.Should().Be(0);
                    using (ZipArchive zip = ZipFile.OpenRead(packagePath))
                    {
                        byte[] indexBytes = ReadEntryBytes(zip, "diagnostic_index.json");
                        summary.IndexSha256.Should().Be(ComputeSha256(indexBytes));
                    }

                    OperationAuditQueryResult auditResult = await runtime.OperationAuditService.QueryAsync(
                        new OperationAuditQuery
                        {
                            Operation = "DiagnosticPackageExport",
                            Limit = 10
                        });

                    auditResult.Succeeded.Should().BeTrue();
                    OperationAuditRecord record = auditResult.Records.Should().ContainSingle().Subject;
                    record.Operation.Should().Be("DiagnosticPackageExport");
                    record.Status.Should().Be(OperationAuditStatus.Succeeded);
                    record.OperatorId.Should().Be("diag-operator");
                    record.Role.Should().Be(ProductionRole.Engineer);
                    record.Reason.Should().Be("导出现场诊断包");
                    record.Details.Should().Contain(packagePath);
                    record.Details.Should().Contain($"SizeBytes={summary.SizeBytes}");
                    record.Details.Should().Contain($"PackageSha256={summary.PackageSha256}");
                    record.Details.Should().Contain($"IndexSha256={summary.IndexSha256}");
                    record.Details.Should().Contain($"IntegrityEntries={summary.IntegrityEntryCount}");
                    record.Details.Should().Contain($"VerifiedEntries={summary.VerifiedEntryCount}");
                    record.Details.Should().Contain("IntegrityStatus=Healthy");
                    record.Details.Should().Contain("IntegrityFindings=0");
                    record.Details.Should().Contain("StartupBlockers=");
                    record.Details.Should().Contain("MaintenanceAdvice=");
                    record.Details.Should().Contain("QueueBacklog=");
                    record.FailureBlocker.Should().BeEmpty();
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task DiagnosticPackageHistoryAndVerify_支持历史查询复核和目录约束()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "diag-reviewer",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    string outputDirectory = Path.Combine(storageService.LogBasePath, "Diagnostics");
                    DiagnosticPackageExportSummary first = await runtime.ExportDiagnosticPackageAsync(outputDirectory);
                    DiagnosticPackageExportSummary second = await runtime.ExportDiagnosticPackageAsync(outputDirectory);
                    File.SetLastWriteTime(first.PackagePath, DateTime.Now.AddMinutes(-5));
                    File.SetLastWriteTime(second.PackagePath, DateTime.Now);

                    IReadOnlyList<DiagnosticPackageHistoryItem> history =
                        runtime.QueryDiagnosticPackageHistory(outputDirectory);

                    history.Should().HaveCount(2);
                    history[0].PackagePath.Should().Be(second.PackagePath);
                    history[0].FileName.Should().Be(Path.GetFileName(second.PackagePath));
                    history[0].SizeBytes.Should().Be(new FileInfo(second.PackagePath).Length);
                    history[0].IntegrityStatus.Should().Be("Pending");
                    history[1].PackagePath.Should().Be(first.PackagePath);

                    DiagnosticPackageExportSummary verification = await runtime.VerifyDiagnosticPackageAsync(
                        outputDirectory,
                        Path.GetFileName(second.PackagePath));

                    verification.PackagePath.Should().Be(second.PackagePath);
                    verification.FileName.Should().Be(Path.GetFileName(second.PackagePath));
                    verification.PackageSha256.Should().Be(ComputeSha256(second.PackagePath));
                    verification.IntegrityStatus.Should().Be("Healthy");
                    verification.VerifiedEntryCount.Should().Be(verification.IntegrityEntryCount);
                    verification.IntegrityFindingCount.Should().Be(0);

                    string outsidePackage = Path.Combine(tempDir, "outside.zip");
                    File.WriteAllText(outsidePackage, "outside");
                    Func<Task> outsideAct = () => runtime.VerifyDiagnosticPackageAsync(outputDirectory, outsidePackage);
                    await outsideAct.Should().ThrowAsync<UnauthorizedAccessException>();

                    OperationAuditQueryResult auditResult = await runtime.OperationAuditService.QueryAsync(
                        new OperationAuditQuery
                        {
                            Operation = "DiagnosticPackageVerify",
                            Limit = 10
                        });

                    auditResult.Succeeded.Should().BeTrue();
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Succeeded &&
                        record.OperatorId == "diag-reviewer" &&
                        record.Details.Contains(second.PackagePath, StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("IntegrityStatus=Healthy"));
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Failed &&
                        record.FailureBlocker == "DiagnosticPackageVerificationFailed" &&
                        record.Details.Contains("只能复核诊断包目录内的文件"));
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task FieldEvidenceHistoryVerifyAndExport_拒绝链接证据路径()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "evidence-guard",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    string diagnosticsDirectory = Path.Combine(storageService.LogBasePath, "Diagnostics");
                    DiagnosticPackageExportSummary package = await runtime.ExportDiagnosticPackageAsync(diagnosticsDirectory);
                    string externalDirectory = Path.Combine(tempDir, "external-evidence");
                    Directory.CreateDirectory(externalDirectory);
                    string externalPackage = Path.Combine(externalDirectory, "outside.zip");
                    File.Copy(package.PackagePath, externalPackage);
                    string linkedPackage = Path.Combine(
                        diagnosticsDirectory,
                        "ClearFrost_Diagnostics_20990101_000000_000_link.zip");

                    bool packageLinkCreated = TryCreateFileSymbolicLink(linkedPackage, externalPackage);
                    if (packageLinkCreated)
                    {
                        runtime.QueryDiagnosticPackageHistory(diagnosticsDirectory)
                            .Should()
                            .NotContain(item => string.Equals(item.PackagePath, linkedPackage, StringComparison.OrdinalIgnoreCase));

                        Func<Task> verifyLinkedPackage = () => runtime.VerifyDiagnosticPackageAsync(
                            diagnosticsDirectory,
                            Path.GetFileName(linkedPackage));
                        await verifyLinkedPackage.Should().ThrowAsync<UnauthorizedAccessException>()
                            .WithMessage("*链接诊断包文件*");
                        File.Exists(externalPackage).Should().BeTrue();
                    }

                    string linkedDiagnosticsChild = Path.Combine(diagnosticsDirectory, "linked-child");
                    string externalDiagnosticsChild = Path.Combine(tempDir, "external-diagnostics-child");
                    Directory.CreateDirectory(externalDiagnosticsChild);
                    string externalChildPackage = Path.Combine(externalDiagnosticsChild, Path.GetFileName(package.PackagePath));
                    File.Copy(package.PackagePath, externalChildPackage);
                    bool diagnosticsChildLinkCreated = TryCreateDirectorySymbolicLink(
                        linkedDiagnosticsChild,
                        externalDiagnosticsChild);
                    if (diagnosticsChildLinkCreated)
                    {
                        Func<Task> verifyLinkedDiagnosticsChild = () =>
                            runtime.VerifyDiagnosticPackageAsync(
                                diagnosticsDirectory,
                                Path.Combine("linked-child", Path.GetFileName(externalChildPackage)));
                        await verifyLinkedDiagnosticsChild.Should().ThrowAsync<UnauthorizedAccessException>()
                            .WithMessage("*链接诊断包目录*");
                        File.Exists(externalChildPackage).Should().BeTrue();
                    }

                    string linkedDiagnosticsRoot = Path.Combine(tempDir, "diagnostics-linked-root");
                    string externalDiagnosticsRoot = Path.Combine(tempDir, "external-diagnostics-root");
                    Directory.CreateDirectory(externalDiagnosticsRoot);
                    bool diagnosticsRootLinkCreated = TryCreateDirectorySymbolicLink(
                        linkedDiagnosticsRoot,
                        externalDiagnosticsRoot);
                    if (diagnosticsRootLinkCreated)
                    {
                        runtime.QueryDiagnosticPackageHistory(linkedDiagnosticsRoot).Should().BeEmpty();
                        Func<Task> exportLinkedDiagnosticsRoot = () =>
                            runtime.ExportDiagnosticPackageAsync(linkedDiagnosticsRoot);
                        await exportLinkedDiagnosticsRoot.Should().ThrowAsync<InvalidOperationException>()
                            .WithMessage("*链接目录*");
                        Directory
                            .EnumerateFiles(externalDiagnosticsRoot, "ClearFrost_Diagnostics_*.zip")
                            .Should()
                            .BeEmpty();

                        string linkedDiagnosticsNested = Path.Combine(linkedDiagnosticsRoot, "nested");
                        Func<Task> exportLinkedDiagnosticsNested = () =>
                            runtime.ExportDiagnosticPackageAsync(linkedDiagnosticsNested);
                        await exportLinkedDiagnosticsNested.Should().ThrowAsync<InvalidOperationException>()
                            .WithMessage("*链接目录*");
                        Directory.Exists(Path.Combine(externalDiagnosticsRoot, "nested")).Should().BeFalse();

                        string externalDiagnosticsNested = Path.Combine(externalDiagnosticsRoot, "existing");
                        Directory.CreateDirectory(externalDiagnosticsNested);
                        string externalNestedPackage = Path.Combine(externalDiagnosticsNested, Path.GetFileName(package.PackagePath));
                        File.Copy(package.PackagePath, externalNestedPackage);
                        string linkedDiagnosticsExistingNested = Path.Combine(linkedDiagnosticsRoot, "existing");
                        runtime.QueryDiagnosticPackageHistory(linkedDiagnosticsExistingNested).Should().BeEmpty();
                        Func<Task> verifyLinkedDiagnosticsNested = () =>
                            runtime.VerifyDiagnosticPackageAsync(
                                linkedDiagnosticsExistingNested,
                                Path.GetFileName(externalNestedPackage));
                        await verifyLinkedDiagnosticsNested.Should().ThrowAsync<UnauthorizedAccessException>()
                            .WithMessage("*链接诊断包目录*");
                    }

                    string handoffDirectory = Path.Combine(storageService.LogBasePath, "HandoffReports");
                    FieldHandoffReportSummary handoff = await runtime.ExportFieldHandoffReportAsync(handoffDirectory);
                    string externalReport = Path.Combine(externalDirectory, "handoff-outside.md");
                    File.Copy(handoff.ReportPath, externalReport);
                    string linkedReport = Path.Combine(handoffDirectory, "handoff-linked.md");
                    bool handoffLinkCreated = TryCreateFileSymbolicLink(linkedReport, externalReport);
                    if (handoffLinkCreated)
                    {
                        runtime.QueryFieldHandoffReportHistory(handoffDirectory)
                            .Should()
                            .NotContain(item => string.Equals(item.ReportPath, linkedReport, StringComparison.OrdinalIgnoreCase));
                        File.Exists(externalReport).Should().BeTrue();
                    }

                    string linkedHandoffRoot = Path.Combine(tempDir, "handoff-linked-root");
                    string externalHandoffRoot = Path.Combine(tempDir, "external-handoff-root");
                    Directory.CreateDirectory(externalHandoffRoot);
                    bool handoffRootLinkCreated = TryCreateDirectorySymbolicLink(
                        linkedHandoffRoot,
                        externalHandoffRoot);
                    if (handoffRootLinkCreated)
                    {
                        runtime.QueryFieldHandoffReportHistory(linkedHandoffRoot).Should().BeEmpty();
                        Func<Task> exportLinkedHandoffRoot = () =>
                            runtime.ExportFieldHandoffReportAsync(linkedHandoffRoot);
                        await exportLinkedHandoffRoot.Should().ThrowAsync<InvalidOperationException>()
                            .WithMessage("*链接目录*");
                        Directory
                            .EnumerateFiles(externalHandoffRoot, "handoff-*.md")
                            .Should()
                            .BeEmpty();

                        string linkedHandoffNested = Path.Combine(linkedHandoffRoot, "nested");
                        Func<Task> exportLinkedHandoffNested = () =>
                            runtime.ExportFieldHandoffReportAsync(linkedHandoffNested);
                        await exportLinkedHandoffNested.Should().ThrowAsync<InvalidOperationException>()
                            .WithMessage("*链接目录*");
                        Directory.Exists(Path.Combine(externalHandoffRoot, "nested")).Should().BeFalse();

                        string externalHandoffNested = Path.Combine(externalHandoffRoot, "existing");
                        Directory.CreateDirectory(externalHandoffNested);
                        string externalNestedReport = Path.Combine(externalHandoffNested, Path.GetFileName(handoff.ReportPath));
                        File.Copy(handoff.ReportPath, externalNestedReport);
                        runtime.QueryFieldHandoffReportHistory(Path.Combine(linkedHandoffRoot, "existing"))
                            .Should()
                            .BeEmpty();
                    }
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task FieldEvidenceRetention_导出后保留最新证据并写入操作审计()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "retention-operator",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    string diagnosticsDirectory = Path.Combine(storageService.LogBasePath, "Diagnostics");
                    Directory.CreateDirectory(diagnosticsDirectory);
                    var oldPackages = new List<string>();
                    DateTime oldBase = DateTime.UtcNow.AddDays(-20);
                    for (int i = 0; i < 20; i++)
                    {
                        string path = Path.Combine(
                            diagnosticsDirectory,
                            $"ClearFrost_Diagnostics_20260101_0000{i:D2}_old{i:D2}.zip");
                        await File.WriteAllTextAsync(path, $"old package {i}");
                        File.SetLastWriteTimeUtc(path, oldBase.AddMinutes(i));
                        oldPackages.Add(path);
                    }

                    DiagnosticPackageExportSummary package =
                        await runtime.ExportDiagnosticPackageAsync(diagnosticsDirectory);

                    File.Exists(package.PackagePath).Should().BeTrue();
                    Directory
                        .EnumerateFiles(diagnosticsDirectory, "ClearFrost_Diagnostics_*.zip")
                        .Should()
                        .HaveCount(20);
                    File.Exists(oldPackages[0]).Should().BeFalse();
                    File.Exists(oldPackages[^1]).Should().BeTrue();

                    string handoffDirectory = Path.Combine(storageService.LogBasePath, "HandoffReports");
                    Directory.CreateDirectory(handoffDirectory);
                    var oldReports = new List<string>();
                    for (int i = 0; i < 20; i++)
                    {
                        string path = Path.Combine(handoffDirectory, $"handoff-old-{i:D2}.md");
                        await File.WriteAllTextAsync(path, $"# old handoff {i}");
                        File.SetLastWriteTimeUtc(path, oldBase.AddMinutes(i));
                        oldReports.Add(path);
                    }

                    FieldHandoffReportSummary report =
                        await runtime.ExportFieldHandoffReportAsync(handoffDirectory);

                    File.Exists(report.ReportPath).Should().BeTrue();
                    Directory
                        .EnumerateFiles(handoffDirectory, "handoff-*.md")
                        .Should()
                        .HaveCount(20);
                    File.Exists(oldReports[0]).Should().BeFalse();
                    File.Exists(oldReports[^1]).Should().BeTrue();

                    OperationAuditQueryResult auditResult = await runtime.OperationAuditService.QueryAsync(
                        new OperationAuditQuery
                        {
                            Operation = "FieldEvidenceRetention",
                            Limit = 10
                        });

                    auditResult.Succeeded.Should().BeTrue();
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Succeeded &&
                        record.OperatorId == "retention-operator" &&
                        record.Reason == "现场证据保留策略清理" &&
                        record.Details.Contains("EvidenceType=DiagnosticPackage", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("KeepLatest=20", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("BeforeCount=21", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("DeletedCount=1", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains(Path.GetFileName(oldPackages[0]), StringComparison.OrdinalIgnoreCase));
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Succeeded &&
                        record.OperatorId == "retention-operator" &&
                        record.Reason == "现场证据保留策略清理" &&
                        record.Details.Contains("EvidenceType=FieldHandoffReport", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("KeepLatest=20", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("BeforeCount=21", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("DeletedCount=1", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains(Path.GetFileName(oldReports[0]), StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void TryDeleteFieldEvidenceFile_拒绝链接文件且不删除外部目标()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(externalDir);
            string linkedFile = Path.Combine(tempDir, "linked-evidence.zip");
            try
            {
                string externalFile = Path.Combine(externalDir, "external-evidence.zip");
                File.WriteAllText(externalFile, "external evidence");
                if (!TryCreateFileSymbolicLink(linkedFile, externalFile))
                {
                    return;
                }

                AppRuntime.TryDeleteFieldEvidenceFile(linkedFile).Should().BeFalse();

                File.Exists(externalFile).Should().BeTrue();
                File.ReadAllText(externalFile).Should().Be("external evidence");
            }
            finally
            {
                TryDeleteFileLink(linkedFile);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }

                if (Directory.Exists(externalDir))
                {
                    Directory.Delete(externalDir, true);
                }
            }
        }

        [Fact]
        public void TryDeleteFieldEvidenceFile_删除普通证据文件()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string file = Path.Combine(tempDir, "evidence.zip");
                File.WriteAllText(file, "evidence");

                AppRuntime.TryDeleteFieldEvidenceFile(file).Should().BeTrue();

                File.Exists(file).Should().BeFalse();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void TryReadFieldHandoffReportHeader_读取安全顶层交接报告()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string reportPath = Path.Combine(tempDir, "handoff-20260705-080000.md");
                File.WriteAllLines(
                    reportPath,
                    new[]
                    {
                        "# ClearFrost 现场交接报告",
                        "- 交接结论: Healthy",
                        "- 生成时间: 2026-07-05T08:00:00+00:00",
                        "- 班次待办: 2"
                    });

                bool read = AppRuntime.TryReadFieldHandoffReportHeader(
                    tempDir,
                    new FileInfo(reportPath),
                    out IReadOnlyList<string> headerLines);

                read.Should().BeTrue();
                headerLines.Should().Contain("- 交接结论: Healthy");
                headerLines.Should().Contain("- 班次待办: 2");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void TryReadFieldHandoffReportHeader_拒绝链接报告且不读取外部目标()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(externalDir);
            string linkedReport = Path.Combine(tempDir, "handoff-linked.md");
            try
            {
                string externalReport = Path.Combine(externalDir, "handoff-outside.md");
                File.WriteAllText(externalReport, "- 交接结论: ExternalSecret");
                if (!TryCreateFileSymbolicLink(linkedReport, externalReport))
                {
                    return;
                }

                bool read = AppRuntime.TryReadFieldHandoffReportHeader(
                    tempDir,
                    new FileInfo(linkedReport),
                    out IReadOnlyList<string> headerLines);

                read.Should().BeFalse();
                headerLines.Should().BeEmpty();
                File.ReadAllText(externalReport).Should().Contain("ExternalSecret");
            }
            finally
            {
                TryDeleteFileLink(linkedReport);
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }

                if (Directory.Exists(externalDir))
                {
                    Directory.Delete(externalDir, true);
                }
            }
        }

        [Fact]
        public void TryReadFieldHandoffReportHeader_拒绝非交接报告文件名()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string reportPath = Path.Combine(tempDir, "operator-secret.md");
                File.WriteAllText(reportPath, "- 交接结论: ShouldNotRead");

                bool read = AppRuntime.TryReadFieldHandoffReportHeader(
                    tempDir,
                    new FileInfo(reportPath),
                    out IReadOnlyList<string> headerLines);

                read.Should().BeFalse();
                headerLines.Should().BeEmpty();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task MaintenanceAdviceAction_支持已处理复检和审计追溯()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "maintainer",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var cameraService = new FakeCameraService(order);
                var plcService = new FakePlcService(order);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    cameraService,
                    plcService,
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    FieldMaintenanceAdvice cameraAdvice = runtime
                        .BuildFieldDiagnosticsSnapshot()
                        .MaintenanceAdvice
                        .Should()
                        .Contain(advice => advice.Code == "CameraNotReady")
                        .Subject;

                    MaintenanceAdviceActionResult acknowledged = await runtime.HandleMaintenanceAdviceActionAsync(
                        cameraAdvice.AdviceId,
                        "acknowledge",
                        "已检查相机线缆");

                    acknowledged.Succeeded.Should().BeTrue();
                    acknowledged.Cleared.Should().BeFalse();
                    acknowledged.Status.Should().Be(MaintenanceAdviceResolutionStatuses.Acknowledged);
                    acknowledged.Record!.OperatorId.Should().Be("maintainer");

                    FieldMaintenanceAdvice enriched = runtime
                        .BuildFieldDiagnosticsSnapshot()
                        .MaintenanceAdvice
                        .Should()
                        .Contain(advice => advice.AdviceId == cameraAdvice.AdviceId)
                        .Subject;
                    enriched.ResolutionStatus.Should().Be(MaintenanceAdviceResolutionStatuses.Acknowledged);
                    enriched.LastActionBy.Should().Be("maintainer");

                    cameraService.IsOpen = true;
                    cameraService.IsGrabbing = true;
                    MaintenanceAdviceActionResult rechecked = await runtime.HandleMaintenanceAdviceActionAsync(
                        cameraAdvice.AdviceId,
                        "recheck",
                        "");

                    rechecked.Succeeded.Should().BeTrue();
                    rechecked.Cleared.Should().BeTrue();
                    rechecked.Status.Should().Be(MaintenanceAdviceResolutionStatuses.RecheckPassed);
                    runtime.BuildFieldDiagnosticsSnapshot().MaintenanceAdvice
                        .Should()
                        .NotContain(advice => advice.AdviceId == cameraAdvice.AdviceId);

                    FieldMaintenanceAdvice plcAdvice = runtime
                        .BuildFieldDiagnosticsSnapshot()
                        .MaintenanceAdvice
                        .Should()
                        .Contain(advice => advice.Code == "PlcNotConnected")
                        .Subject;
                    MaintenanceAdviceActionResult plcRecheck = await runtime.HandleMaintenanceAdviceActionAsync(
                        plcAdvice.AdviceId,
                        "recheck",
                        "");

                    plcRecheck.Succeeded.Should().BeTrue();
                    plcRecheck.Cleared.Should().BeFalse();
                    plcRecheck.Status.Should().Be(MaintenanceAdviceResolutionStatuses.RecheckFailed);

                    OperationAuditQueryResult auditResult = await runtime.OperationAuditService.QueryAsync(
                        new OperationAuditQuery
                        {
                            Operation = "MaintenanceAdviceAction",
                            Limit = 10
                        });

                    auditResult.Succeeded.Should().BeTrue();
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Succeeded &&
                        record.OperatorId == "maintainer" &&
                        record.Details.Contains(cameraAdvice.AdviceId, StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("AdviceStatus=RecheckPassed"));
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Failed &&
                        record.FailureBlocker == "MaintenanceAdviceRecheckFailed" &&
                        record.Details.Contains(plcAdvice.AdviceId, StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("AdviceStatus=RecheckFailed"));
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task MaintenanceAdviceResolutionStore_拒绝链接证据文件和目录()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                FieldMaintenanceAdvice advice = CreateMaintenanceAdvice("advice-link-guard");
                string externalRoot = Path.Combine(tempDir, "external-maintenance");
                Directory.CreateDirectory(externalRoot);

                string storeRoot = Path.Combine(tempDir, "store");
                Directory.CreateDirectory(storeRoot);
                string storePath = Path.Combine(storeRoot, "maintenance-advice-resolution.json");
                string externalResolution = Path.Combine(externalRoot, "external-resolution.json");
                File.WriteAllText(
                    externalResolution,
                    JsonSerializer.Serialize(new[]
                    {
                        new MaintenanceAdviceResolutionRecord
                        {
                            AdviceId = "external-advice",
                            Code = "External",
                            Source = "External",
                            Title = "external secret",
                            Status = MaintenanceAdviceResolutionStatuses.Acknowledged
                        }
                    }));

                bool storeFileLinkCreated = TryCreateFileSymbolicLink(storePath, externalResolution);
                if (storeFileLinkCreated)
                {
                    var store = new MaintenanceAdviceResolutionStore(storePath);
                    store.QueryRecent().Should().BeEmpty();

                    Func<Task> appendLinkedStore = async () => await store.AppendAsync(
                        advice,
                        MaintenanceAdviceResolutionStatuses.Acknowledged,
                        "engineer",
                        ProductionRole.Engineer,
                        "notes",
                        "message");
                    await appendLinkedStore.Should().ThrowAsync<InvalidOperationException>()
                        .WithMessage("*链接*");
                    File.ReadAllText(externalResolution).Should().Contain("external secret");
                }

                string safeStorePath = Path.Combine(tempDir, "safe-store", "maintenance-advice-resolution.json");
                var firstSeenStore = new MaintenanceAdviceResolutionStore(safeStorePath);
                Directory.CreateDirectory(Path.GetDirectoryName(firstSeenStore.FirstSeenPath)!);
                string externalFirstSeen = Path.Combine(externalRoot, "external-first-seen.json");
                File.WriteAllText(externalFirstSeen, "[]");
                bool firstSeenLinkCreated = TryCreateFileSymbolicLink(firstSeenStore.FirstSeenPath, externalFirstSeen);
                if (firstSeenLinkCreated)
                {
                    Action captureLinkedFirstSeen = () => firstSeenStore.CaptureFirstSeenTimes(new[] { advice });
                    captureLinkedFirstSeen.Should().Throw<InvalidOperationException>()
                        .WithMessage("*链接*");
                    File.ReadAllText(externalFirstSeen).Should().Be("[]");
                }

                string linkedRoot = Path.Combine(tempDir, "linked-store-root");
                string externalLinkedRoot = Path.Combine(tempDir, "external-store-root");
                Directory.CreateDirectory(externalLinkedRoot);
                bool rootLinkCreated = TryCreateDirectorySymbolicLink(linkedRoot, externalLinkedRoot);
                if (rootLinkCreated)
                {
                    var linkedRootStore = new MaintenanceAdviceResolutionStore(
                        Path.Combine(linkedRoot, "maintenance-advice-resolution.json"));
                    linkedRootStore.QueryRecent().Should().BeEmpty();

                    Func<Task> appendLinkedRoot = async () => await linkedRootStore.AppendAsync(
                        advice,
                        MaintenanceAdviceResolutionStatuses.Acknowledged,
                        "engineer",
                        ProductionRole.Engineer,
                        string.Empty,
                        "message");
                    await appendLinkedRoot.Should().ThrowAsync<InvalidOperationException>()
                        .WithMessage("*链接*");
                    Directory.EnumerateFiles(externalLinkedRoot).Should().BeEmpty();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task FieldHandoffReport_汇总诊断包复核维护复检和审计()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "shift-lead",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var plcService = new FakePlcService(order);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    plcService,
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    string diagnosticsDirectory = Path.Combine(storageService.LogBasePath, "Diagnostics");
                    DiagnosticPackageExportSummary package = await runtime.ExportDiagnosticPackageAsync(diagnosticsDirectory);
                    DiagnosticPackageExportSummary verification = await runtime.VerifyDiagnosticPackageAsync(
                        diagnosticsDirectory,
                        Path.GetFileName(package.PackagePath));
                    verification.IntegrityStatus.Should().Be("Healthy");

                    FieldMaintenanceAdvice plcAdvice = runtime
                        .BuildFieldDiagnosticsSnapshot()
                        .MaintenanceAdvice
                        .Should()
                        .Contain(advice => advice.Code == "PlcNotConnected")
                        .Subject;
                    FieldShiftTask plcTask = runtime
                        .BuildFieldDiagnosticsSnapshot()
                        .ShiftTasks
                        .Should()
                        .Contain(task => task.LinkedAdviceId == plcAdvice.AdviceId)
                        .Subject;
                    plcTask.SuggestedOwner.Should().Be("电气/PLC");
                    plcTask.DueAt.Should().NotBeNull();
                    plcTask.EscalationLevel.Should().Be("Medium");
                    ShiftTaskActionResult plcRecheck = await runtime.HandleShiftTaskActionAsync(
                        plcTask.TaskId,
                        plcTask.LinkedAdviceId,
                        "recheck",
                        "交班前复检");
                    plcRecheck.Status.Should().Be(MaintenanceAdviceResolutionStatuses.RecheckFailed);
                    plcRecheck.Tasks.Should().Contain(task => task.LinkedAdviceId == plcAdvice.AdviceId);
                    FieldDiagnosticsSnapshot diagnostics = runtime.BuildFieldDiagnosticsSnapshot();
                    diagnostics.ShiftTasks.Should().Contain(task =>
                        task.LinkedAdviceId == plcAdvice.AdviceId &&
                        task.Status == MaintenanceAdviceResolutionStatuses.RecheckFailed &&
                        task.SuggestedOwner == "电气/PLC" &&
                        task.EscalationLevel == "High" &&
                        task.DueAt.HasValue &&
                        task.Title.Contains("PLC", StringComparison.OrdinalIgnoreCase));

                    string handoffDirectory = Path.Combine(storageService.LogBasePath, "HandoffReports");
                    FieldHandoffReportSummary report = await runtime.ExportFieldHandoffReportAsync(handoffDirectory);

                    File.Exists(report.ReportPath).Should().BeTrue();
                    report.FileName.Should().EndWith(".md");
                    report.SizeBytes.Should().Be(new FileInfo(report.ReportPath).Length);
                    report.OverallStatus.Should().Be("Blocked");
                    report.ActiveAdviceCount.Should().BeGreaterThan(0);
                    report.ShiftTaskCount.Should().BeGreaterThan(0);
                    report.FailedRecheckCount.Should().Be(1);
                    report.DiagnosticPackageCount.Should().BeGreaterThan(0);
                    report.RecentAuditCount.Should().BeGreaterThan(0);
                    report.AuditChainStatus.Should().Be("Healthy");
                    report.AuditChainFindingCount.Should().Be(0);
                    report.AuditChainVerifiedRecords.Should().BeGreaterThan(0);

                    IReadOnlyList<FieldHandoffReportHistoryItem> history =
                        runtime.QueryFieldHandoffReportHistory(handoffDirectory);
                    history.Should().ContainSingle();
                    history[0].ReportPath.Should().Be(report.ReportPath);
                    history[0].FileName.Should().Be(report.FileName);
                    history[0].SizeBytes.Should().Be(report.SizeBytes);
                    history[0].OverallStatus.Should().Be(report.OverallStatus);
                    history[0].ShiftTaskCount.Should().Be(report.ShiftTaskCount);
                    history[0].GeneratedAt.Should().BeCloseTo(report.GeneratedAt, TimeSpan.FromSeconds(1));

                    string markdown = File.ReadAllText(report.ReportPath);
                    markdown.Should().Contain("ClearFrost 现场交接报告");
                    markdown.Should().Contain("诊断包与复核");
                    markdown.Should().Contain("DiagnosticPackageVerify");
                    markdown.Should().Contain("FieldEvidenceRetention");
                    markdown.Should().Contain("IntegrityStatus=Healthy");
                    markdown.Should().Contain("维护建议闭环");
                    markdown.Should().Contain("班次待办");
                    markdown.Should().Contain("审计链");
                    markdown.Should().Contain("Status=Healthy");
                    markdown.Should().Contain("电气/PLC");
                    markdown.Should().Contain("RecheckFailed");
                    markdown.Should().Contain("复检未通过");
                    markdown.Should().Contain("下一班关注项");
                    markdown.Should().Contain("shift-lead");
                    markdown.Should().Contain("<redacted-path>");
                    markdown.Should().NotContain(tempDir);

                    OperationAuditQueryResult auditResult = await runtime.OperationAuditService.QueryAsync(
                        new OperationAuditQuery
                        {
                            Operation = "FieldHandoffReportExport",
                            Limit = 5
                        });

                    auditResult.Succeeded.Should().BeTrue();
                    auditResult.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Succeeded &&
                        record.OperatorId == "shift-lead" &&
                        record.Reason == "导出现场交接报告" &&
                        record.Details.Contains(report.ReportPath, StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("OverallStatus=Blocked", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains($"ShiftTasks={report.ShiftTaskCount}", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("FailedRechecks=1", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("AuditChainStatus=Healthy", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("AuditChainFindings=0", StringComparison.OrdinalIgnoreCase));

                    OperationAuditQueryResult shiftTaskAudit = await runtime.OperationAuditService.QueryAsync(
                        new OperationAuditQuery
                        {
                            Operation = "ShiftTaskAction",
                            Limit = 5
                        });
                    shiftTaskAudit.Succeeded.Should().BeTrue();
                    shiftTaskAudit.Records.Should().Contain(record =>
                        record.Status == OperationAuditStatus.Failed &&
                        record.OperatorId == "shift-lead" &&
                        record.Reason == "班次待办处理/复检" &&
                        record.Details.Contains(plcTask.TaskId, StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("Action=recheck", StringComparison.OrdinalIgnoreCase) &&
                        record.Details.Contains("TaskStatus=RecheckFailed", StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task ShiftTasks_使用首次发现时间计算截止和超时升级()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "shift-lead",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    FieldDiagnosticsSnapshot initial = runtime.BuildFieldDiagnosticsSnapshot();
                    FieldMaintenanceAdvice plcAdvice = initial
                        .MaintenanceAdvice
                        .Should()
                        .Contain(advice => advice.Code == "PlcNotConnected")
                        .Subject;
                    FieldShiftTask initialTask = initial
                        .ShiftTasks
                        .Should()
                        .Contain(task => task.LinkedAdviceId == plcAdvice.AdviceId)
                        .Subject;

                    initialTask.FirstSeenAt.Should().NotBeNull();
                    initialTask.DueAt.Should().BeCloseTo(initialTask.FirstSeenAt!.Value.AddHours(2), TimeSpan.FromSeconds(2));
                    initialTask.IsOverdue.Should().BeFalse();

                    DateTimeOffset firstSeenAt = DateTimeOffset.Now.AddHours(-3);
                    File.WriteAllText(
                        runtime.MaintenanceAdviceResolutionStore.FirstSeenPath,
                        JsonSerializer.Serialize(new[]
                        {
                            new MaintenanceAdviceFirstSeenRecord
                            {
                                AdviceId = plcAdvice.AdviceId,
                                Code = plcAdvice.Code,
                                Source = plcAdvice.Source,
                                Title = plcAdvice.Title,
                                FirstSeenAt = firstSeenAt
                            }
                        }));

                    FieldShiftTask overdueTask = runtime
                        .BuildFieldDiagnosticsSnapshot()
                        .ShiftTasks
                        .Should()
                        .Contain(task => task.LinkedAdviceId == plcAdvice.AdviceId)
                        .Subject;

                    overdueTask.FirstSeenAt.Should().Be(firstSeenAt);
                    overdueTask.DueAt.Should().Be(firstSeenAt.AddHours(2));
                    overdueTask.IsOverdue.Should().BeTrue();
                    overdueTask.EscalationLevel.Should().Be("Overdue");
                    overdueTask.SuggestedOwner.Should().Be("电气/PLC");
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task FieldDiagnostics_审计链校验状态进入快照并在异常时生成建议()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var order = new List<string>();
                var appConfig = new AppConfig
                {
                    StoragePath = tempDir,
                    CurrentOperatorId = "qa-auditor",
                    CurrentOperatorRole = ProductionRole.Engineer
                };
                using var cameraManager = new CameraManager(true);
                var databaseService = new FakeDatabaseService(order);
                var storageService = new FakeStorageService(tempDir, order);
                var runtime = new AppRuntime(
                    appConfig,
                    cameraManager,
                    new FakeCameraService(order),
                    new FakePlcService(order),
                    new FakeDetectionService(order),
                    storageService,
                    new FakeStatisticsService(order),
                    databaseService,
                    new ImageSaveQueue(),
                    new DetectionRecordQueue(databaseService),
                    new WebUIController());

                try
                {
                    FieldDiagnosticsSnapshot initial = runtime.BuildFieldDiagnosticsSnapshot();
                    initial.AuditChain.Status.Should().Be("NotChecked");
                    initial.AuditChain.CheckedAt.Should().BeNull();

                    bool appended = await runtime.OperationAuditService.AppendAsync(new OperationAuditRecord
                    {
                        Operation = "AuditChainSmoke",
                        Status = OperationAuditStatus.Succeeded,
                        OperatorId = "qa-auditor",
                        Role = ProductionRole.Engineer,
                        Reason = "审计链现场快照测试"
                    });
                    appended.Should().BeTrue();

                    OperationAuditChainVerificationResult healthy =
                        await runtime.VerifyOperationAuditChainAsync();
                    healthy.Status.Should().Be("Healthy");

                    FieldDiagnosticsSnapshot healthySnapshot = runtime.BuildFieldDiagnosticsSnapshot();
                    healthySnapshot.AuditChain.Status.Should().Be("Healthy");
                    healthySnapshot.AuditChain.TotalRecords.Should().Be(1);
                    healthySnapshot.AuditChain.VerifiedRecords.Should().Be(1);
                    healthySnapshot.AuditChain.CheckedAt.Should().NotBeNull();
                    healthySnapshot.Components.Should().Contain(item =>
                        item.Name == "审计链" &&
                        item.Status == "Healthy" &&
                        item.Level == "ok");
                    healthySnapshot.MaintenanceAdvice.Should().NotContain(advice =>
                        advice.Code == "AuditChainBlocking");

                    string auditOutbox = Path.Combine(storageService.LogBasePath, "Outbox");
                    string auditFile = Directory.GetFiles(auditOutbox, "operation-audit-*.ndjson")
                        .Should()
                        .ContainSingle()
                        .Subject;
                    string tampered = File.ReadAllText(auditFile)
                        .Replace("AuditChainSmoke", "AuditChainTampered", StringComparison.Ordinal);
                    File.WriteAllText(auditFile, tampered);

                    OperationAuditChainVerificationResult blocking =
                        await runtime.VerifyOperationAuditChainAsync();
                    blocking.Status.Should().Be("Blocking");

                    FieldDiagnosticsSnapshot blockingSnapshot = runtime.BuildFieldDiagnosticsSnapshot();
                    blockingSnapshot.AuditChain.Status.Should().Be("Blocking");
                    blockingSnapshot.AuditChain.FindingCount.Should().BeGreaterThan(0);
                    blockingSnapshot.AuditChain.Findings.Should().Contain(finding =>
                        finding.ErrorCode == "AuditRecordHashMismatch");
                    blockingSnapshot.Components.Should().Contain(item =>
                        item.Name == "审计链" &&
                        item.Status == "Blocking" &&
                        item.Level == "warning");
                    blockingSnapshot.MaintenanceAdvice.Should().Contain(advice =>
                        advice.Source == "OperationAudit" &&
                        advice.Code == "AuditChainBlocking" &&
                        advice.Level == "critical" &&
                        advice.Evidence.Contains("AuditRecordHashMismatch", StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    await runtime.DisposeAsync();
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        private static string CreateApprovedPackage(string packageRoot, string modelId, string version)
        {
            string packageDir = Path.Combine(packageRoot, modelId);
            Directory.CreateDirectory(packageDir);
            string modelPath = Path.Combine(packageDir, "model.onnx");
            File.WriteAllBytes(modelPath, new byte[] { 3, 1, 4, (byte)modelId.Length, (byte)version.Length });
            string hash = ComputeSha256(modelPath);
            File.WriteAllText(
                Path.Combine(packageDir, "manifest.json"),
                JsonSerializer.Serialize(new ModelPackageManifest
                {
                    ModelId = modelId,
                    Version = version,
                    ModelFileName = "model.onnx",
                    ModelHash = hash,
                    Labels = new List<string> { "part" },
                    TaskType = "Detect",
                    InputWidth = 640,
                    InputHeight = 640,
                    Approval = new ModelApprovalMetadata
                    {
                        Status = ModelApprovalStatuses.Approved,
                        ApprovedBy = "qa",
                        ApprovedAt = DateTimeOffset.UtcNow
                    }
                }));
            return modelPath;
        }

        private static (bool Succeeded, string SkipReason) InvokeLegacyMigrationAssetValidation(ModelRegistryEntry entry)
        {
            MethodInfo method = typeof(AppRuntime).GetMethod(
                "TryLoadLegacyReplayApprovalManifest",
                BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(AppRuntime), "TryLoadLegacyReplayApprovalManifest");
            object?[] args = { entry, null, string.Empty };
            bool succeeded = (bool)method.Invoke(null, args)!;
            return (succeeded, (string)args[2]!);
        }

        private static FieldMaintenanceAdvice CreateMaintenanceAdvice(string adviceId)
        {
            return new FieldMaintenanceAdvice
            {
                AdviceId = adviceId,
                Code = "CameraNotReady",
                Source = "Camera",
                Title = "相机未就绪",
                Level = "critical",
                Evidence = "Camera=Closed",
                Advice = "检查相机连接并复检。"
            };
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
        {
            ZipArchiveEntry entry = archive.GetEntry(entryName) ?? throw new FileNotFoundException(entryName);
            using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
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

        private sealed class FakeCameraService : ICameraService
        {
            private readonly List<string> _order;

            public FakeCameraService(List<string> order) => _order = order;

            public event Action<Mat>? FrameCaptured;
            public event Action<bool>? ConnectionChanged;
            public event Action<string>? ErrorOccurred;

            public bool IsOpen { get; set; }
            public string CameraName => "Fake";
            public string? LastError => null;
            public Mat? LastFrame => null;
            public bool IsGrabbing { get; set; }

            public bool Open(string serialNumber, string manufacturer) => true;
            public void Close() => _order.Add("camera-close");
            public void StartCapture() => _order.Add("camera-start-capture");
            public void StopCapture() => _order.Add("camera-stop-capture");
            public void TriggerOnce() { }
            public Mat? CaptureFrame(int timeoutMs = 3000) => null;
            public void SetExposure(double exposureUs) { }
            public void SetGain(double gain) { }
            public void Dispose() => _order.Add("camera-dispose");
        }

        private sealed class FakePlcService : IPlcService
        {
            private readonly List<string> _order;

            public FakePlcService(List<string> order) => _order = order;

            public event Action<bool>? ConnectionChanged;
            public event Action? TriggerReceived;
            public event Action<PlcTriggerContext>? TriggerContextReceived;
            public event Action<string>? ErrorOccurred;

            public bool IsConnected { get; set; }
            public string ProtocolName => "Fake";
            public string? LastError => null;

            public Task<bool> ConnectAsync(PlcConnectionOptions options)
                => Task.FromResult(true);

            public void Disconnect() => _order.Add("plc-disconnect");
            public bool StartMonitoring(
                string triggerAddress,
                int pollingIntervalMs = 500,
                int triggerDelayMs = 800,
                PlcMonitoringOptions? options = null)
            {
                _order.Add("plc-start-monitoring");
                return true;
            }
            public void StopMonitoring() => _order.Add("plc-stop-monitoring");
            public Task StopMonitoringAsync(System.Threading.CancellationToken cancellationToken = default)
            {
                StopMonitoring();
                return Task.CompletedTask;
            }
            public Task<bool> WriteResultAsync(string resultAddress, bool isQualified) => Task.FromResult(true);
            public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite) => Task.FromResult(true);
            public Task<bool> WriteReleaseSignalAsync(string resultAddress) => Task.FromResult(true);
            public Task<(bool Success, short Value)> ReadWordAsync(string address)
                => Task.FromResult((true, (short)1));
            public Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
                => Task.FromResult((true, string.Empty));
            public void Dispose() => _order.Add("plc-dispose");
        }

        private sealed class FakeDetectionService : IDetectionService
        {
            private readonly List<string> _order;

            public FakeDetectionService(List<string> order) => _order = order;

            public event Action<DetectionResultData>? DetectionCompleted;
            public event Action<string>? ModelLoaded;
            public event Action<string>? ErrorOccurred;

            public bool IsModelLoaded => true;
            public string CurrentModelName => "fake-model";
            public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
            public long LastInferenceMs => 0;
            public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();
            public DetectionRuntimeModelSnapshot RuntimeModelSnapshot { get; } = new DetectionRuntimeModelSnapshot();

            public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
            public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
            public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(true);
            public Task<DetectionResultData> DetectAsync(
                Mat image,
                float confidence,
                float iouThreshold,
                InspectionFallbackGoal? fallbackGoal = null,
                MultiModelCandidateEvaluator? candidateEvaluator = null)
                => Task.FromResult(new DetectionResultData());
            public Task<DetectionResultData> DetectAsync(
                Bitmap image,
                float confidence,
                float iouThreshold,
                InspectionFallbackGoal? fallbackGoal = null,
                MultiModelCandidateEvaluator? candidateEvaluator = null)
                => Task.FromResult(new DetectionResultData());
            public Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels) => new Bitmap(original);
            public void SetTaskMode(int taskType) { }
            public void SetEnableFallback(bool enabled) { }
            public Task<bool> LoadAuxiliary1ModelAsync(string modelPath) => Task.FromResult(true);
            public Task<bool> LoadAuxiliary2ModelAsync(string modelPath) => Task.FromResult(true);
            public void UnloadPrimaryModel() { }
            public void UnloadAuxiliary1Model() { }
            public void UnloadAuxiliary2Model() { }
            public string[] GetLabels() => Array.Empty<string>();
            public object? GetLastMetrics() => null;
            public void Dispose() => _order.Add("detection-dispose");
        }

        private sealed class FakeStorageService : IStorageService
        {
            private readonly List<string> _order;

            public FakeStorageService(string basePath, List<string> order)
            {
                _order = order;
                BaseStoragePath = basePath;
                EnsureDirectoriesExist();
            }

            public string ImageBasePath => Path.Combine(BaseStoragePath, "Images");
            public string LogBasePath => Path.Combine(BaseStoragePath, "Logs");
            public string SystemPath => Path.Combine(BaseStoragePath, "System");
            public string BaseStoragePath { get; private set; }

            public void SaveDetectionImage(Bitmap bitmap, bool isQualified) { }
            public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified) { }
            public void WriteDetectionLog(string content, bool isQualified) { }
            public void WriteStartupLog(string action, string? serialNumber = null) { }
            public void WriteErrorLog(string message) { }
            public void CleanOldData(int retainDays) { }
            public double GetDiskFreeSpaceGb() => 100.0;
            public double PerformEmergencyCleanup() => 100.0;
            public void EnsureDirectoriesExist()
            {
                Directory.CreateDirectory(ImageBasePath);
                Directory.CreateDirectory(LogBasePath);
                Directory.CreateDirectory(SystemPath);
            }

            public void UpdateStoragePath(string storagePath)
            {
                BaseStoragePath = storagePath;
                EnsureDirectoriesExist();
            }
            public void Dispose() => _order.Add("storage-dispose");
        }

        private sealed class FakeStatisticsService : IStatisticsService
        {
            private readonly List<string> _order;
            private readonly StatisticsHistory _history = new StatisticsHistory();
            private readonly DetectionStatistics _stats = new DetectionStatistics();

            public FakeStatisticsService(List<string> order)
            {
                _order = order;
                string basePath = Path.Combine(Path.GetTempPath(), "ClearFrostRuntimeStats", Guid.NewGuid().ToString("N"));
                BasePath = basePath;
                _history.SetSavePath(basePath);
                _stats.SetSavePath(basePath);
            }

            public string BasePath { get; private set; }

            public event Action<StatisticsSnapshot>? StatisticsUpdated;
            public event Action? DayReset;

            public StatisticsSnapshot Current => new StatisticsSnapshot();
            public int TodayQualified => 0;
            public int TodayUnqualified => 0;
            public int TodayTotal => 0;
            public IReadOnlyList<DailyStatisticsRecord> History => Array.Empty<DailyStatisticsRecord>();

            public void RecordDetection(bool isQualified) { }
            public void ResetToday() { }
            public bool CheckAndResetForNewDay() => false;
            public void SaveAll() => _order.Add("statistics-save-all");
            public void ClearHistory() { }
            public void LoadAll() { }
            public void UpdateStoragePath(string basePath)
            {
                BasePath = basePath;
                _history.SetSavePath(basePath);
                _stats.SetSavePath(basePath);
            }
            public (StatisticsHistory history, DetectionStatistics stats) GetStatisticsData() => (_history, _stats);
            public void Dispose() => _order.Add("statistics-dispose");
        }

        private sealed class FakeDatabaseService : IDatabaseService
        {
            private readonly List<string> _order;

            public FakeDatabaseService(List<string> order) => _order = order;

            public Task InitializeAsync() => Task.CompletedTask;

            public async Task SaveDetectionRecordAsync(DetectionRecord record)
            {
                await Task.Delay(30);
                _order.Add("db-save");
            }

            public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
                => Task.FromResult(new List<DetectionRecord>());

            public Task<DetectionRecord?> GetDetectionRecordByIdAsync(long id)
                => Task.FromResult<DetectionRecord?>(null);

            public Task<List<DetectionRecord>> GetDetectionRecordsByInspectionIdAsync(string inspectionId)
                => Task.FromResult(new List<DetectionRecord>());

            public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
                => Task.FromResult(new List<DetectionTraceRecord>());

            public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
                => Task.FromResult(new DetectionTracePage());

            public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
                => Task.FromResult(new List<DetectionRecord>());

            public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
                => Task.FromResult(new List<string>());

            public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
                => Task.FromResult(new List<string>());

            public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
                => Task.FromResult((0, 0, 0));

            public Task<int> CleanupOldRecordsAsync(int daysToKeep)
                => Task.FromResult(0);

            public void Dispose() => _order.Add("db-dispose");
        }
    }
#pragma warning restore CS0067
}
