using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    public sealed class DiagnosticPackageRequest
    {
        public string OutputDirectory { get; init; } = Path.Combine(RuntimePaths.DataDirectory, "Diagnostics");
        public AppConfig AppConfig { get; init; } = new AppConfig();
        public Recipe? Recipe { get; init; }
        public RecipeVersionInfo? CurrentRecipeVersion { get; init; }
        public IReadOnlyList<ModelRegistryEntry> ModelEntries { get; init; } = Array.Empty<ModelRegistryEntry>();
        public DetectionRuntimeModelSnapshot RuntimeModelSnapshot { get; init; } = new DetectionRuntimeModelSnapshot();
        public StartupDiagnosticReport? StartupDiagnostics { get; init; }
        public HealthSnapshot? HealthSnapshot { get; init; }
        public FieldDiagnosticsSnapshot? FieldDiagnostics { get; init; }
        public IReadOnlyList<RecentInspectionTimingSnapshot> RecentInspectionTimings { get; init; } = Array.Empty<RecentInspectionTimingSnapshot>();
        public FieldModelProbeSummary? ModelProbeSummary { get; init; }
        public FieldQueueStatus? QueueStatus { get; init; }
        public OperationAuditChainVerificationResult OperationAuditChainVerification { get; init; } =
            new OperationAuditChainVerificationResult();
        public IReadOnlyList<DetectionRecord> RecentRecords { get; init; } = Array.Empty<DetectionRecord>();
        public string LogsDirectory { get; init; } = RuntimePaths.LogsDirectory;
    }

    public sealed class DiagnosticPackageManifest
    {
        public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
        public string AppVersion { get; init; } = string.Empty;
        public string RecipeId { get; init; } = string.Empty;
        public string RecipeVersion { get; init; } = string.Empty;
        public string CurrentModelName { get; init; } = string.Empty;
        public int RuntimeModelSlotCount { get; init; }
        public int RegistryEntryCount { get; init; }
        public bool StartupReady { get; init; }
        public int StartupBlockingFailureCount { get; init; }
        public string QueueBacklogLevel { get; init; } = string.Empty;
        public long ImageQueuePending { get; init; }
        public long RecordQueuePending { get; init; }
        public int RecentRecordCount { get; init; }
        public int RecentErrorCount { get; init; }
        public int MaintenanceAdviceCount { get; init; }
        public string AuditChainStatus { get; init; } = string.Empty;
        public int AuditChainVerifiedRecords { get; init; }
        public int AuditChainTotalRecords { get; init; }
        public int AuditChainFindingCount { get; init; }
    }

    public sealed class DiagnosticOperationAuditChainSummary
    {
        public string Status { get; init; } = string.Empty;
        public int TotalRecords { get; init; }
        public int VerifiedRecords { get; init; }
        public int FindingCount { get; init; }
        public string LastRecordSha256 { get; init; } = string.Empty;
        public IReadOnlyList<DiagnosticOperationAuditChainFindingSummary> Findings { get; init; } =
            Array.Empty<DiagnosticOperationAuditChainFindingSummary>();
    }

    public sealed class DiagnosticOperationAuditChainFindingSummary
    {
        public string AuditFileName { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string Severity { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string ExpectedPreviousSha256 { get; init; } = string.Empty;
        public string ActualPreviousSha256 { get; init; } = string.Empty;
        public string ExpectedRecordSha256 { get; init; } = string.Empty;
        public string ActualRecordSha256 { get; init; } = string.Empty;
    }

    public sealed class DiagnosticRecipeSummary
    {
        public string RecipeId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public string OperatorRole { get; init; } = string.Empty;
        public string ChangeSummary { get; init; } = string.Empty;
        public string TargetLabel { get; init; } = string.Empty;
        public int TargetCount { get; init; }
        public float Confidence { get; init; }
        public float IouThreshold { get; init; }
        public bool EnableMultiModelFallback { get; init; }
        public bool EnableGpu { get; init; }
        public int GpuIndex { get; init; }
        public int TaskType { get; init; }
        public int VisionMode { get; init; }
        public string ActiveCameraId { get; init; } = string.Empty;
        public int CameraCount { get; init; }
        public string CurrentModelFileName { get; init; } = string.Empty;
        public string CurrentModelReference { get; init; } = string.Empty;
        public string Auxiliary1ModelReference { get; init; } = string.Empty;
        public string Auxiliary2ModelReference { get; init; } = string.Empty;
        public string VersionSnapshotPath { get; init; } = string.Empty;
    }

    public sealed class DiagnosticRuntimeModelSlotSummary
    {
        public string Role { get; init; } = string.Empty;
        public bool IsLoaded { get; init; }
        public string ModelPath { get; init; } = string.Empty;
        public string ModelFileName { get; init; } = string.Empty;
        public bool RegistryMatched { get; init; }
        public string RegistryMatchStrategy { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ModelHash { get; init; } = string.Empty;
        public string ModelHashPrefix { get; init; } = string.Empty;
        public string UsedModelName { get; init; } = string.Empty;
        public string RegistryStatus { get; init; } = string.Empty;
        public string ApprovalStatus { get; init; } = string.Empty;
        public bool ApprovedForProduction { get; init; }
        public bool IsPackage { get; init; }
        public string TaskType { get; init; } = string.Empty;
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
    }

    public sealed class DiagnosticModelRegistryEntrySummary
    {
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ModelHash { get; init; } = string.Empty;
        public string ModelHashPrefix { get; init; } = string.Empty;
        public string UsedModelName { get; init; } = string.Empty;
        public string ModelPath { get; init; } = string.Empty;
        public string ManifestPath { get; init; } = string.Empty;
        public bool IsPackage { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string TaskType { get; init; } = string.Empty;
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public string ApprovalStatus { get; init; } = string.Empty;
        public bool ApprovedForProduction { get; init; }
        public int LabelCount { get; init; }
    }

    public sealed class DiagnosticStartupBlocker
    {
        public string Name { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public bool IsBlocking { get; init; }
    }

    public sealed class DiagnosticPackageIntegrityIndex
    {
        public string FormatVersion { get; init; } = "1";
        public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
        public string HashAlgorithm { get; init; } = "SHA-256";
        public int EntryCount { get; init; }
        public long TotalUncompressedBytes { get; init; }
        public IReadOnlyList<DiagnosticPackageIndexEntry> Entries { get; init; } = Array.Empty<DiagnosticPackageIndexEntry>();
    }

    public sealed class DiagnosticPackageIndexEntry
    {
        public string EntryName { get; init; } = string.Empty;
        public long LengthBytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
    }

    public sealed class DiagnosticPackageExporter
    {
        private const long MaxLogFileBytes = 2 * 1024 * 1024;
        private const int MaxLogFiles = 20;

        private static readonly UTF8Encoding Utf8BomEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        private static readonly StringComparison FileSystemPathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public async Task<string> ExportAsync(
            DiagnosticPackageRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string outputDirectory = ResolveSafeOutputDirectory(request.OutputDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            string zipPath = CreateDiagnosticPackagePath(outputDirectory);
            string tempPath = Path.Combine(
                outputDirectory,
                $".{Path.GetFileName(zipPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                await using (FileStream tempStream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None))
                using (ZipArchive archive = new ZipArchive(tempStream, ZipArchiveMode.Create))
                {
                    DetectionRuntimeModelSnapshot runtimeModelSnapshot = request.RuntimeModelSnapshot ??
                        new DetectionRuntimeModelSnapshot();
                    FieldDiagnosticsSnapshot fieldDiagnostics = request.FieldDiagnostics ??
                        BuildFallbackFieldDiagnostics(request);
                    IReadOnlyList<RecentInspectionTimingSnapshot> recentTimings =
                        request.RecentInspectionTimings.Count > 0
                            ? request.RecentInspectionTimings
                            : fieldDiagnostics.RecentInspectionTimings;
                    FieldModelProbeSummary modelProbe = request.ModelProbeSummary ?? fieldDiagnostics.ModelProbe;
                    FieldQueueStatus queueStatus = request.QueueStatus ?? fieldDiagnostics.Queues;
                    IReadOnlyList<DiagnosticRuntimeModelSlotSummary> runtimeModelSlots =
                        BuildRuntimeModelSlots(runtimeModelSnapshot, request.ModelEntries);
                    IReadOnlyList<DiagnosticStartupBlocker> startupBlockers =
                        BuildStartupBlockers(request.StartupDiagnostics);
                    DiagnosticOperationAuditChainSummary auditChain =
                        BuildOperationAuditChainSummary(request.OperationAuditChainVerification);
                    var indexEntries = new List<DiagnosticPackageIndexEntry>();

                    AddJson(archive, "diagnostic_manifest.json", BuildManifest(
                        request,
                        fieldDiagnostics,
                        queueStatus,
                        runtimeModelSlots,
                        startupBlockers,
                        auditChain),
                        indexEntries);
                    AddText(archive, "field_report.md", BuildFieldReport(
                        request,
                        fieldDiagnostics,
                        queueStatus,
                        runtimeModelSlots,
                        startupBlockers,
                        auditChain),
                        indexEntries);
                    AddText(archive, "config.sanitized.json", BuildSanitizedConfigJson(request.AppConfig), indexEntries);
                    AddJson(archive, "recipe.json", request.Recipe, indexEntries);
                    AddJson(archive, "recipe_summary.json", BuildRecipeSummary(request.Recipe, request.CurrentRecipeVersion), indexEntries);
                    AddJson(archive, "model_registry.json", SanitizeModelEntries(request.ModelEntries), indexEntries);
                    AddJson(archive, "model_registry_diagnostics.json", BuildModelRegistryDiagnostics(request.ModelEntries), indexEntries);
                    AddJson(archive, "runtime_model_slots.json", runtimeModelSlots, indexEntries);
                    AddJson(archive, "startup_diagnostics.json", request.StartupDiagnostics, indexEntries);
                    AddJson(archive, "startup_blockers.json", startupBlockers, indexEntries);
                    AddJson(archive, "health.json", request.HealthSnapshot, indexEntries);
                    AddJson(archive, "field_diagnostics.json", fieldDiagnostics, indexEntries);
                    AddJson(archive, "recent_inspection_timings.json", recentTimings, indexEntries);
                    AddJson(archive, "recent_errors.json", fieldDiagnostics.RecentErrors, indexEntries);
                    AddJson(archive, "maintenance_advice.json", fieldDiagnostics.MaintenanceAdvice, indexEntries);
                    AddJson(archive, "operation_audit_chain.json", auditChain, indexEntries);
                    AddJson(archive, "model_probe_summary.json", modelProbe, indexEntries);
                    AddJson(archive, "queue_status.json", queueStatus, indexEntries);
                    AddJson(archive, "recent_records.json", SanitizeRecentRecords(request.RecentRecords), indexEntries);
                    AddText(archive, "system_info.txt", BuildSystemInfo(), indexEntries);
                    AddText(archive, "native_dependencies.txt", BuildNativeDependencyManifest(), indexEntries);
                    await AddLogsAsync(archive, request.LogsDirectory, indexEntries, cancellationToken).ConfigureAwait(false);
                    AddJson(archive, "diagnostic_index.json", BuildIntegrityIndex(indexEntries));
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, zipPath);
                return zipPath;
            }
            catch
            {
                TryDeleteTemporaryPackageFile(tempPath, outputDirectory);
                throw;
            }
        }

        private static FieldDiagnosticsSnapshot BuildFallbackFieldDiagnostics(DiagnosticPackageRequest request)
        {
            HealthSnapshot health = request.HealthSnapshot ?? new HealthSnapshot();
            DetectionRuntimeModelSnapshot runtimeModelSnapshot = request.RuntimeModelSnapshot ??
                new DetectionRuntimeModelSnapshot();
            FieldModelProbeSummary modelProbe = request.ModelProbeSummary ??
                FieldDiagnosticsSnapshotFactory.BuildModelProbeSummary(
                    health,
                    request.ModelEntries,
                    runtimeModelSnapshot,
                    null,
                    null);
            FieldQueueStatus queueStatus = request.QueueStatus ??
                FieldDiagnosticsSnapshotFactory.BuildQueueStatus(health);
            IReadOnlyList<RecentInspectionTimingSnapshot> timings = request.RecentInspectionTimings.Count > 0
                ? request.RecentInspectionTimings
                : health.RecentInspectionTimings;

            FieldDiagnosticsSnapshot snapshot = FieldDiagnosticsSnapshotFactory.Create(
                health,
                request.StartupDiagnostics,
                request.ModelEntries,
                runtimeModelSnapshot,
                modelProbe.CurrentModelName,
                null,
                request.Recipe,
                FieldDiagnosticsSnapshotFactory.BuildAuditChainStatus(
                    request.OperationAuditChainVerification,
                    DateTimeOffset.Now));

            return new FieldDiagnosticsSnapshot
            {
                UpdatedAt = snapshot.UpdatedAt,
                OverallLevel = snapshot.OverallLevel,
                CameraStatus = snapshot.CameraStatus,
                PlcStatus = snapshot.PlcStatus,
                CurrentModelName = snapshot.CurrentModelName,
                ModelStatus = snapshot.ModelStatus,
                StorageStatus = snapshot.StorageStatus,
                DatabaseStatus = snapshot.DatabaseStatus,
                RecipeId = snapshot.RecipeId,
                RecipeVersion = snapshot.RecipeVersion,
                RecipeTargetLabel = snapshot.RecipeTargetLabel,
                RecipeTargetCount = snapshot.RecipeTargetCount,
                LastInspectionId = snapshot.LastInspectionId,
                LastInspectionTotalMs = snapshot.LastInspectionTotalMs,
                RecentInspectionP95Ms = snapshot.RecentInspectionP95Ms,
                RecentInspectionP99Ms = snapshot.RecentInspectionP99Ms,
                ImageQueueLength = snapshot.ImageQueueLength,
                ImageQueueCapacity = snapshot.ImageQueueCapacity,
                RecordQueueLength = snapshot.RecordQueueLength,
                RecordQueueCapacity = snapshot.RecordQueueCapacity,
                FreeDiskGb = snapshot.FreeDiskGb,
                MemoryMb = snapshot.MemoryMb,
                HealthSnapshot = health,
                StartupDiagnostics = request.StartupDiagnostics,
                Queues = queueStatus,
                ModelProbe = modelProbe,
                AuditChain = snapshot.AuditChain,
                Components = snapshot.Components,
                MaintenanceAdvice = snapshot.MaintenanceAdvice,
                RecentInspectionTimings = timings,
                RecentErrors = health.RecentErrors
            };
        }

        private static string CreateDiagnosticPackagePath(string outputDirectory)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                string suffix = Guid.NewGuid().ToString("N")[..8];
                string fileName = $"ClearFrost_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{suffix}.zip";
                string path = Path.Combine(outputDirectory, fileName);
                if (!File.Exists(path))
                {
                    return path;
                }
            }

            return Path.Combine(
                outputDirectory,
                $"ClearFrost_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.zip");
        }

        private static string ResolveSafeOutputDirectory(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("诊断包输出目录为空。", nameof(outputDirectory));
            }

            string fullDirectory = Path.GetFullPath(outputDirectory);
            EnsureExistingDirectoryAncestorsHaveNoReparsePoint(fullDirectory);
            Directory.CreateDirectory(fullDirectory);
            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new InvalidOperationException($"诊断包输出目录不能是链接目录: {fullDirectory}");
            }

            return fullDirectory;
        }

        internal static bool TryDeleteTemporaryPackageFile(string path, string outputDirectory)
        {
            try
            {
                string? outputRoot = GetSafeDirectoryRoot(outputDirectory);
                if (string.IsNullOrWhiteSpace(path) || outputRoot == null)
                {
                    return false;
                }

                string fullPath = Path.GetFullPath(path);
                if (!IsPathUnderDirectory(outputRoot, fullPath) ||
                    !string.Equals(Path.GetExtension(fullPath), ".tmp", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath))
                {
                    return false;
                }

                string? directory = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrWhiteSpace(directory) || DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                if (!file.Exists || HasReparsePoint(file))
                {
                    return false;
                }

                file.Delete();
                return true;
            }
            catch
            {
                // 诊断包导出失败时不再抛出清理异常，保留原始失败原因。
                return false;
            }
        }

        private static DiagnosticPackageManifest BuildManifest(
            DiagnosticPackageRequest request,
            FieldDiagnosticsSnapshot fieldDiagnostics,
            FieldQueueStatus queueStatus,
            IReadOnlyList<DiagnosticRuntimeModelSlotSummary> runtimeModelSlots,
            IReadOnlyList<DiagnosticStartupBlocker> startupBlockers,
            DiagnosticOperationAuditChainSummary auditChain)
        {
            Recipe? recipe = request.Recipe;
            return new DiagnosticPackageManifest
            {
                GeneratedAt = DateTimeOffset.Now,
                AppVersion = AppVersion.DisplayVersion,
                RecipeId = recipe?.RecipeId ?? string.Empty,
                RecipeVersion = recipe?.Version ?? string.Empty,
                CurrentModelName = fieldDiagnostics.CurrentModelName,
                RuntimeModelSlotCount = runtimeModelSlots.Count(slot => slot.IsLoaded),
                RegistryEntryCount = request.ModelEntries?.Count ?? 0,
                StartupReady = request.StartupDiagnostics?.IsReady ?? true,
                StartupBlockingFailureCount = startupBlockers.Count,
                QueueBacklogLevel = queueStatus.BacklogLevel,
                ImageQueuePending = queueStatus.ImagePending,
                RecordQueuePending = queueStatus.RecordPending,
                RecentRecordCount = request.RecentRecords?.Count ?? 0,
                RecentErrorCount = fieldDiagnostics.RecentErrors?.Count ?? 0,
                MaintenanceAdviceCount = fieldDiagnostics.MaintenanceAdvice?.Count ?? 0,
                AuditChainStatus = auditChain.Status,
                AuditChainVerifiedRecords = auditChain.VerifiedRecords,
                AuditChainTotalRecords = auditChain.TotalRecords,
                AuditChainFindingCount = auditChain.FindingCount
            };
        }

        private static string BuildFieldReport(
            DiagnosticPackageRequest request,
            FieldDiagnosticsSnapshot fieldDiagnostics,
            FieldQueueStatus queueStatus,
            IReadOnlyList<DiagnosticRuntimeModelSlotSummary> runtimeModelSlots,
            IReadOnlyList<DiagnosticStartupBlocker> startupBlockers,
            DiagnosticOperationAuditChainSummary auditChain)
        {
            var sb = new StringBuilder();
            Recipe? recipe = request.Recipe;
            sb.AppendLine("# ClearFrost 现场诊断报告");
            sb.AppendLine();
            sb.AppendLine($"- 生成时间: {DateTimeOffset.Now:O}");
            sb.AppendLine($"- 应用版本: {AppVersion.DisplayVersion}");
            sb.AppendLine($"- 总体状态: {SafeReportText(fieldDiagnostics.OverallLevel, request)}");
            sb.AppendLine($"- 启动状态: {(request.StartupDiagnostics?.IsReady ?? true ? "Ready" : "Blocked")}");
            sb.AppendLine($"- 当前模型: {SafeReportText(fieldDiagnostics.CurrentModelName, request)}");
            sb.AppendLine($"- 当前配方: {SafeReportText(recipe?.RecipeId ?? fieldDiagnostics.RecipeId, request)} / {SafeReportText(recipe?.Version ?? fieldDiagnostics.RecipeVersion, request)}");
            sb.AppendLine($"- 审计链: {SafeReportText(auditChain.Status, request)} ({auditChain.VerifiedRecords}/{auditChain.TotalRecords}, Findings={auditChain.FindingCount})");
            if (!string.IsNullOrWhiteSpace(recipe?.TargetLabel ?? fieldDiagnostics.RecipeTargetLabel))
            {
                sb.AppendLine($"- 检测目标: {SafeReportText(recipe?.TargetLabel ?? fieldDiagnostics.RecipeTargetLabel, request)} x{recipe?.TargetCount ?? fieldDiagnostics.RecipeTargetCount}");
            }

            sb.AppendLine();
            sb.AppendLine("## 运行时模型槽位");
            foreach (DiagnosticRuntimeModelSlotSummary slot in runtimeModelSlots)
            {
                string loaded = slot.IsLoaded ? "Loaded" : "Empty";
                string identity = string.IsNullOrWhiteSpace(slot.ModelId)
                    ? "-"
                    : $"{slot.ModelId}@{slot.Version}#{slot.ModelHashPrefix}";
                string matched = slot.RegistryMatched
                    ? $"Matched:{slot.RegistryMatchStrategy}"
                    : "Unmatched";
                sb.AppendLine($"- {slot.Role}: {loaded}; File={SafeReportText(slot.ModelFileName, request)}; Identity={SafeReportText(identity, request)}; Registry={matched}; Approved={slot.ApprovedForProduction}");
            }

            sb.AppendLine();
            sb.AppendLine("## 队列与性能");
            sb.AppendLine($"- 图像队列: {queueStatus.ImagePending}/{queueStatus.ImageCapacity}; Dropped={queueStatus.ImageDroppedCount}; Failed={queueStatus.ImageFailedCount}");
            sb.AppendLine($"- 记录队列: {queueStatus.RecordPending}/{queueStatus.RecordCapacity}; Dropped={queueStatus.RecordDroppedCount}; Failed={queueStatus.RecordFailedCount}");
            sb.AppendLine($"- 队列级别: {queueStatus.BacklogLevel}");
            sb.AppendLine($"- 最近检测: Last={SafeReportText(fieldDiagnostics.LastInspectionId, request)}; Total={fieldDiagnostics.LastInspectionTotalMs}ms; P95={fieldDiagnostics.RecentInspectionP95Ms}ms; P99={fieldDiagnostics.RecentInspectionP99Ms}ms");

            sb.AppendLine();
            sb.AppendLine("## 启动阻断");
            if (startupBlockers.Count == 0)
            {
                sb.AppendLine("- 无阻断项");
            }
            else
            {
                foreach (DiagnosticStartupBlocker blocker in startupBlockers)
                {
                    sb.AppendLine($"- {SafeReportText(blocker.Name, request)}: {SafeReportText(blocker.Message, request)} {SafeReportText(blocker.Details, request)}".TrimEnd());
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 维护建议");
            if (fieldDiagnostics.MaintenanceAdvice.Count == 0)
            {
                sb.AppendLine("- 当前无维护建议");
            }
            else
            {
                foreach (FieldMaintenanceAdvice advice in fieldDiagnostics.MaintenanceAdvice)
                {
                    sb.AppendLine($"- [{SafeReportText(advice.Level, request)}] {SafeReportText(advice.Title, request)} ({SafeReportText(advice.Code, request)})");
                    if (!string.IsNullOrWhiteSpace(advice.Evidence))
                    {
                        sb.AppendLine($"  - 证据: {SafeReportText(advice.Evidence, request)}");
                    }

                    sb.AppendLine($"  - 建议: {SafeReportText(advice.Advice, request)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 操作审计链");
            sb.AppendLine($"- 状态: {SafeReportText(auditChain.Status, request)}");
            sb.AppendLine($"- 已验证记录: {auditChain.VerifiedRecords}/{auditChain.TotalRecords}");
            sb.AppendLine($"- 异常数量: {auditChain.FindingCount}");
            if (!string.IsNullOrWhiteSpace(auditChain.LastRecordSha256))
            {
                sb.AppendLine($"- 最后一条哈希: {SafeReportText(auditChain.LastRecordSha256, request)}");
            }

            if (auditChain.Findings.Count == 0)
            {
                sb.AppendLine("- 无审计链异常");
            }
            else
            {
                foreach (DiagnosticOperationAuditChainFindingSummary finding in auditChain.Findings.Take(5))
                {
                    sb.AppendLine($"- {SafeReportText(finding.ErrorCode, request)}: {SafeReportText(finding.Message, request)} ({SafeReportText(finding.AuditFileName, request)}:{finding.LineNumber})");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 最近错误");
            if (fieldDiagnostics.RecentErrors.Count == 0)
            {
                sb.AppendLine("- 无最近错误");
            }
            else
            {
                foreach (HealthError error in fieldDiagnostics.RecentErrors.TakeLast(10))
                {
                    sb.AppendLine($"- {error.Timestamp:O} [{SafeReportText(error.Source, request)}] {SafeReportText(error.Message, request)}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("## 包内关键文件");
            sb.AppendLine("- diagnostic_manifest.json: 机器可读总览");
            sb.AppendLine("- diagnostic_index.json: 包内文件 SHA-256 完整性索引");
            sb.AppendLine("- field_diagnostics.json: WebUI 现场诊断快照");
            sb.AppendLine("- runtime_model_slots.json: 运行时模型槽位与注册表匹配");
            sb.AppendLine("- maintenance_advice.json: 结构化维护建议");
            sb.AppendLine("- operation_audit_chain.json: 操作审计链脱敏校验摘要");
            sb.AppendLine("- recent_inspection_timings.json: 最近检测阶段耗时");
            sb.AppendLine("- logs/: 最近日志片段");
            return sb.ToString();
        }

        private static DiagnosticOperationAuditChainSummary BuildOperationAuditChainSummary(
            OperationAuditChainVerificationResult? verification)
        {
            verification ??= new OperationAuditChainVerificationResult();
            return new DiagnosticOperationAuditChainSummary
            {
                Status = verification.Status,
                TotalRecords = verification.TotalRecords,
                VerifiedRecords = verification.VerifiedRecords,
                FindingCount = verification.Findings.Count,
                LastRecordSha256 = verification.LastRecordSha256,
                Findings = verification.Findings
                    .Take(20)
                    .Select(finding => new DiagnosticOperationAuditChainFindingSummary
                    {
                        AuditFileName = string.IsNullOrWhiteSpace(finding.FilePath)
                            ? string.Empty
                            : Path.GetFileName(finding.FilePath),
                        LineNumber = finding.LineNumber,
                        Severity = finding.Severity,
                        ErrorCode = finding.ErrorCode,
                        Message = finding.Message,
                        ExpectedPreviousSha256 = finding.ExpectedPreviousSha256,
                        ActualPreviousSha256 = finding.ActualPreviousSha256,
                        ExpectedRecordSha256 = finding.ExpectedRecordSha256,
                        ActualRecordSha256 = finding.ActualRecordSha256
                    })
                    .ToList()
            };
        }

        private static DiagnosticRecipeSummary BuildRecipeSummary(
            Recipe? recipe,
            RecipeVersionInfo? versionInfo)
        {
            if (recipe == null)
            {
                return new DiagnosticRecipeSummary();
            }

            return new DiagnosticRecipeSummary
            {
                RecipeId = recipe.RecipeId ?? string.Empty,
                Version = recipe.Version ?? string.Empty,
                CreatedAt = recipe.CreatedAt,
                OperatorRole = recipe.OperatorRole ?? string.Empty,
                ChangeSummary = recipe.ChangeSummary ?? string.Empty,
                TargetLabel = recipe.TargetLabel ?? string.Empty,
                TargetCount = recipe.TargetCount,
                Confidence = recipe.Confidence,
                IouThreshold = recipe.IouThreshold,
                EnableMultiModelFallback = recipe.EnableMultiModelFallback,
                EnableGpu = recipe.EnableGpu,
                GpuIndex = recipe.GpuIndex,
                TaskType = recipe.TaskType,
                VisionMode = recipe.VisionMode,
                ActiveCameraId = recipe.ActiveCameraId ?? string.Empty,
                CameraCount = recipe.Cameras?.Count ?? 0,
                CurrentModelFileName = recipe.CurrentModelFileName ?? string.Empty,
                CurrentModelReference = recipe.CurrentModelReference?.ToString() ?? string.Empty,
                Auxiliary1ModelReference = recipe.Auxiliary1ModelReference?.ToString() ?? string.Empty,
                Auxiliary2ModelReference = recipe.Auxiliary2ModelReference?.ToString() ?? string.Empty,
                VersionSnapshotPath = versionInfo?.SnapshotPath ?? string.Empty
            };
        }

        private static IReadOnlyList<DiagnosticRuntimeModelSlotSummary> BuildRuntimeModelSlots(
            DetectionRuntimeModelSnapshot runtimeModelSnapshot,
            IReadOnlyList<ModelRegistryEntry> modelEntries)
        {
            runtimeModelSnapshot ??= new DetectionRuntimeModelSnapshot();
            return new[]
            {
                BuildRuntimeModelSlot(runtimeModelSnapshot.Primary, modelEntries),
                BuildRuntimeModelSlot(runtimeModelSnapshot.Auxiliary1, modelEntries),
                BuildRuntimeModelSlot(runtimeModelSnapshot.Auxiliary2, modelEntries)
            };
        }

        private static DiagnosticRuntimeModelSlotSummary BuildRuntimeModelSlot(
            DetectionModelSlotSnapshot slot,
            IReadOnlyList<ModelRegistryEntry> modelEntries)
        {
            ModelRegistryEntry? match = FieldDiagnosticsSnapshotFactory.ResolveRuntimeSlotRegistryEntry(
                slot,
                modelEntries,
                out string matchStrategy);

            return new DiagnosticRuntimeModelSlotSummary
            {
                Role = slot.Role.ToString(),
                IsLoaded = slot.IsLoaded,
                ModelPath = FieldDiagnosticsSnapshotFactory.GetFullPathSafe(slot.ModelPath),
                ModelFileName = Path.GetFileName(slot.ModelPath ?? string.Empty),
                RegistryMatched = match != null,
                RegistryMatchStrategy = matchStrategy,
                ModelId = match?.ModelId ?? string.Empty,
                Version = match?.Version ?? string.Empty,
                ModelHash = match?.ModelHash ?? string.Empty,
                ModelHashPrefix = ShortHash(match?.ModelHash),
                UsedModelName = match?.UsedModelName ?? string.Empty,
                RegistryStatus = match?.Status.ToString() ?? string.Empty,
                ApprovalStatus = match?.ApprovalStatus ?? string.Empty,
                ApprovedForProduction = match?.ApprovedForProduction ?? false,
                IsPackage = match?.IsPackage ?? false,
                TaskType = ResolveTaskType(match),
                InputWidth = ResolveInputWidth(match),
                InputHeight = ResolveInputHeight(match)
            };
        }

        private static IReadOnlyList<DiagnosticModelRegistryEntrySummary> BuildModelRegistryDiagnostics(
            IReadOnlyList<ModelRegistryEntry> entries)
        {
            return (entries ?? Array.Empty<ModelRegistryEntry>())
                .Select(entry => new DiagnosticModelRegistryEntrySummary
                {
                    ModelId = entry.ModelId ?? string.Empty,
                    Version = entry.Version ?? string.Empty,
                    ModelHash = entry.ModelHash ?? string.Empty,
                    ModelHashPrefix = ShortHash(entry.ModelHash),
                    UsedModelName = entry.UsedModelName ?? string.Empty,
                    ModelPath = FieldDiagnosticsSnapshotFactory.GetFullPathSafe(entry.ModelPath),
                    ManifestPath = FieldDiagnosticsSnapshotFactory.GetFullPathSafe(entry.ManifestPath),
                    IsPackage = entry.IsPackage,
                    Status = entry.Status.ToString(),
                    Message = entry.Message ?? string.Empty,
                    TaskType = ResolveTaskType(entry),
                    InputWidth = ResolveInputWidth(entry),
                    InputHeight = ResolveInputHeight(entry),
                    ApprovalStatus = entry.ApprovalStatus ?? string.Empty,
                    ApprovedForProduction = entry.ApprovedForProduction,
                    LabelCount = ResolveLabels(entry).Count
                })
                .ToList();
        }

        private static IReadOnlyList<DiagnosticStartupBlocker> BuildStartupBlockers(
            StartupDiagnosticReport? startupDiagnostics)
        {
            return (startupDiagnostics?.Items ?? Array.Empty<StartupDiagnosticItem>())
                .Where(item => item.Status == StartupDiagnosticStatus.Fail && item.IsBlocking)
                .Select(item => new DiagnosticStartupBlocker
                {
                    Name = item.Name ?? string.Empty,
                    Status = item.Status.ToString(),
                    Message = item.Message ?? string.Empty,
                    Details = item.Details ?? string.Empty,
                    IsBlocking = item.IsBlocking
                })
                .ToList();
        }

        private static string ShortHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return string.Empty;
            }

            return hash.Length <= 12 ? hash : hash[..12];
        }

        private static string SafeReportText(string? value, DiagnosticPackageRequest request)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = value.Trim();
            foreach (string sensitivePath in EnumerateSensitivePaths(request))
            {
                if (!string.IsNullOrWhiteSpace(sensitivePath))
                {
                    text = text.Replace(sensitivePath, "<redacted-path>", StringComparison.OrdinalIgnoreCase);
                }
            }

            text = Regex.Replace(
                text,
                @"[A-Za-z]:\\[^\r\n;，,]+",
                "<redacted-path>",
                RegexOptions.CultureInvariant);
            return text;
        }

        private static IEnumerable<string> EnumerateSensitivePaths(DiagnosticPackageRequest request)
        {
            yield return request.AppConfig.StoragePath ?? string.Empty;
            yield return request.OutputDirectory ?? string.Empty;
            yield return request.LogsDirectory ?? string.Empty;
            yield return request.CurrentRecipeVersion?.SnapshotPath ?? string.Empty;

            string? recipeDirectory = Path.GetDirectoryName(request.CurrentRecipeVersion?.SnapshotPath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(recipeDirectory))
            {
                yield return recipeDirectory;
            }
        }

        private static string BuildSanitizedConfigJson(AppConfig config)
        {
            JsonNode? node = JsonSerializer.SerializeToNode(config, JsonOptions);
            if (node is JsonObject obj)
            {
                Redact(obj, nameof(AppConfig.CurrentOperatorId));
                Redact(obj, nameof(AppConfig.StoragePath));
            }

            return node?.ToJsonString(JsonOptions) ?? "{}";
        }

        private static IReadOnlyList<object> SanitizeModelEntries(IReadOnlyList<ModelRegistryEntry> entries)
        {
            return (entries ?? Array.Empty<ModelRegistryEntry>())
                .Select(entry => new
                {
                    entry.ModelId,
                    entry.Version,
                    entry.ModelHash,
                    ModelFileName = Path.GetFileName(entry.ModelPath ?? string.Empty),
                    entry.IsPackage,
                    entry.Status,
                    entry.Message,
                    TaskType = ResolveTaskType(entry),
                    InputWidth = ResolveInputWidth(entry),
                    InputHeight = ResolveInputHeight(entry),
                    entry.ApprovalStatus,
                    entry.ApprovedForProduction
                })
                .Cast<object>()
                .ToList();
        }

        private static string ResolveTaskType(ModelRegistryEntry? entry)
        {
            if (entry == null)
            {
                return string.Empty;
            }

            return entry.GetEffectiveTaskType();
        }

        private static int ResolveInputWidth(ModelRegistryEntry? entry)
        {
            if (entry == null)
            {
                return 0;
            }

            return entry.GetEffectiveInputWidth();
        }

        private static int ResolveInputHeight(ModelRegistryEntry? entry)
        {
            if (entry == null)
            {
                return 0;
            }

            return entry.GetEffectiveInputHeight();
        }

        private static IReadOnlyList<string> ResolveLabels(ModelRegistryEntry entry)
        {
            return entry.GetEffectiveLabels()
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .ToArray();
        }

        private static IReadOnlyList<object> SanitizeRecentRecords(IReadOnlyList<DetectionRecord> records)
        {
            return (records ?? Array.Empty<DetectionRecord>())
                .Select(record => new
                {
                    record.Id,
                    record.Timestamp,
                    record.IsQualified,
                    record.InspectionId,
                    record.TriggerSource,
                    ProductBarcode = Redacted(record.ProductBarcode),
                    Barcode = Redacted(record.Barcode),
                    record.TraceStatus,
                    record.QueueStatus,
                    ImagePath = Redacted(record.ImagePath),
                    RenderedImagePath = Redacted(record.RenderedImagePath),
                    TraceImagePath = Redacted(record.TraceImagePath),
                    record.ErrorStage,
                    record.ErrorCode,
                    record.ErrorMessage,
                    record.TotalMs,
                    record.RecipeId,
                    record.RecipeVersion,
                    record.ModelId,
                    record.ModelVersion,
                    record.ModelHash,
                    record.WasFallback,
                    record.UsedModelName,
                    record.TargetLabel,
                    record.ExpectedCount,
                    record.ActualCount,
                    record.InferenceMs,
                    record.ModelName,
                    record.CameraId,
                    record.RuleSummary
                })
                .Cast<object>()
                .ToList();
        }

        private static void Redact(JsonObject obj, string name)
        {
            if (obj.ContainsKey(name))
            {
                obj[name] = "<redacted>";
            }
        }

        private static string Redacted(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : "<redacted>";
        }

        private static string BuildSystemInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"GeneratedAt: {DateTimeOffset.Now:O}");
            sb.AppendLine($"MachineName: {Environment.MachineName}");
            sb.AppendLine($"OSVersion: {Environment.OSVersion}");
            sb.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"ProcessArchitecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"BaseDirectory: {AppDomain.CurrentDomain.BaseDirectory}");
            sb.AppendLine($"WorkingSetMb: {Environment.WorkingSet / 1024 / 1024}");
            return sb.ToString();
        }

        private static string BuildNativeDependencyManifest()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] names =
            {
                "MVSDK_Net.dll",
                "HaoCommunication.dll",
                "MvCameraControl.dll",
                "MVSDKmd.dll"
            };

            var sb = new StringBuilder();
            sb.AppendLine($"BaseDirectory: {baseDirectory}");
            foreach (string name in names)
            {
                string path = Path.Combine(baseDirectory, name);
                if (!File.Exists(path))
                {
                    sb.AppendLine($"{name}: missing");
                    continue;
                }

                var info = new FileInfo(path);
                sb.AppendLine($"{name}: present, {info.Length} bytes, {info.LastWriteTimeUtc:O}");
            }

            return sb.ToString();
        }

        private static async Task AddLogsAsync(
            ZipArchive archive,
            string logsDirectory,
            List<DiagnosticPackageIndexEntry> indexEntries,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(logsDirectory) || !Directory.Exists(logsDirectory))
            {
                return;
            }

            string? logsRoot = GetSafeDirectoryRoot(logsDirectory);
            if (logsRoot == null)
            {
                return;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            var files = Directory.EnumerateFiles(logsRoot, "*.*", options)
                .Select(path => new FileInfo(path))
                .Where(info => IsSafeLogFileForPackage(logsRoot, info))
                .Where(info => IsAllowedLogFile(info))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(MaxLogFiles)
                .ToList();

            foreach (FileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await TryAddSafeLogFileAsync(
                    archive,
                    logsRoot,
                    file.FullName,
                    indexEntries,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        internal static async Task<bool> TryAddSafeLogFileAsync(
            ZipArchive archive,
            string logsRoot,
            string logFilePath,
            List<DiagnosticPackageIndexEntry> indexEntries,
            CancellationToken cancellationToken = default)
        {
            if (archive == null || indexEntries == null)
            {
                return false;
            }

            var file = new FileInfo(logFilePath ?? string.Empty);
            if (!IsSafeLogFileForPackage(logsRoot, file) || !IsAllowedLogFile(file))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(file.FullName);
            string relative = Path.GetRelativePath(logsRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (IsUnsafeZipEntryName(relative))
            {
                return false;
            }

            await using var source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            byte[] bytes = buffer.ToArray();
            AddBytes(archive, $"logs/{relative}", bytes, indexEntries);
            return true;
        }

        private static bool IsUnsafeZipEntryName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName) ||
                entryName.StartsWith("/", StringComparison.Ordinal) ||
                entryName.StartsWith("\\", StringComparison.Ordinal) ||
                entryName.Contains('\\') ||
                Path.IsPathRooted(entryName))
            {
                return true;
            }

            string normalized = entryName.Replace('\\', '/');
            string firstSegment = normalized.Split('/')[0];
            if (firstSegment.EndsWith(":", StringComparison.Ordinal))
            {
                return true;
            }

            return normalized
                .Split('/')
                .Any(segment =>
                    string.IsNullOrWhiteSpace(segment) ||
                    string.Equals(segment, "..", StringComparison.Ordinal));
        }

        internal static bool IsSafeLogFileForPackage(string logsDirectory, FileInfo file)
        {
            string? logsRoot = GetSafeDirectoryRoot(logsDirectory);
            if (logsRoot == null || file == null)
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(file.FullName);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!IsPathUnderDirectory(logsRoot, fullPath) || HasReparsePoint(file))
            {
                return false;
            }

            DirectoryInfo? directory = file.Directory;
            while (directory != null)
            {
                string directoryPath;
                try
                {
                    directoryPath = Path.GetFullPath(directory.FullName);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return false;
                }

                if (IsSamePath(logsRoot, directoryPath))
                {
                    return true;
                }

                if (!IsPathUnderDirectory(logsRoot, directoryPath) || HasReparsePoint(directory))
                {
                    return false;
                }

                directory = directory.Parent;
            }

            return false;
        }

        private static string? GetSafeDirectoryRoot(string directory)
        {
            try
            {
                string fullPath = Path.GetFullPath(directory);
                return Path.EndsInDirectorySeparator(fullPath)
                    ? fullPath
                    : fullPath + Path.DirectorySeparatorChar;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static bool IsPathUnderDirectory(string directoryRoot, string path)
        {
            return path.StartsWith(directoryRoot, FileSystemPathComparison);
        }

        private static bool IsSamePath(string left, string right)
        {
            string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
            string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
            return string.Equals(normalizedLeft, normalizedRight, FileSystemPathComparison);
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }

        private static void EnsureExistingDirectoryAncestorsHaveNoReparsePoint(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null && !current.Exists)
            {
                current = current.Parent;
            }

            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    throw new InvalidOperationException($"诊断包输出目录不能包含链接目录: {current.FullName}");
                }

                current = current.Parent;
            }
        }

        private static bool IsAllowedLogFile(FileInfo info)
        {
            if (!info.Exists || info.Length > MaxLogFileBytes)
            {
                return false;
            }

            string extension = info.Extension.ToLowerInvariant();
            if (extension != ".log" && extension != ".txt" && extension != ".json")
            {
                return false;
            }

            string fullPath = info.FullName.ToLowerInvariant();
            if (fullPath.Contains($"{Path.DirectorySeparatorChar}images{Path.DirectorySeparatorChar}") ||
                fullPath.Contains(".onnx") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}outbox{Path.DirectorySeparatorChar}") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}replayevidence{Path.DirectorySeparatorChar}") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}replaydatasets{Path.DirectorySeparatorChar}") ||
                fullPath.Contains($"{Path.DirectorySeparatorChar}replayreports{Path.DirectorySeparatorChar}"))
            {
                return false;
            }

            return true;
        }

        private static DiagnosticPackageIntegrityIndex BuildIntegrityIndex(
            IReadOnlyList<DiagnosticPackageIndexEntry> entries)
        {
            return new DiagnosticPackageIntegrityIndex
            {
                GeneratedAt = DateTimeOffset.Now,
                EntryCount = entries.Count,
                TotalUncompressedBytes = entries.Sum(entry => entry.LengthBytes),
                Entries = entries.ToList()
            };
        }

        private static void AddJson<T>(
            ZipArchive archive,
            string entryName,
            T value,
            List<DiagnosticPackageIndexEntry>? indexEntries = null)
        {
            string json = JsonSerializer.Serialize(value, JsonOptions);
            AddText(archive, entryName, json, indexEntries);
        }

        private static void AddText(
            ZipArchive archive,
            string entryName,
            string content,
            List<DiagnosticPackageIndexEntry>? indexEntries = null)
        {
            AddBytes(archive, entryName, EncodeUtf8Bom(content ?? string.Empty), indexEntries);
        }

        private static void AddBytes(
            ZipArchive archive,
            string entryName,
            byte[] bytes,
            List<DiagnosticPackageIndexEntry>? indexEntries = null)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            using Stream stream = entry.Open();
            stream.Write(bytes, 0, bytes.Length);
            indexEntries?.Add(new DiagnosticPackageIndexEntry
            {
                EntryName = entryName,
                LengthBytes = bytes.Length,
                Sha256 = ComputeSha256(bytes)
            });
        }

        private static byte[] EncodeUtf8Bom(string content)
        {
            byte[] preamble = Utf8BomEncoding.GetPreamble();
            byte[] payload = Utf8BomEncoding.GetBytes(content);
            if (preamble.Length == 0)
            {
                return payload;
            }

            var bytes = new byte[preamble.Length + payload.Length];
            Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            Buffer.BlockCopy(payload, 0, bytes, preamble.Length, payload.Length);
            return bytes;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
        }
    }
}
