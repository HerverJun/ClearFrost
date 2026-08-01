// ============================================================================
// 文件名: FieldDiagnostics.cs
// 描述:   现场诊断中心快照模型
//
// 功能:
//   - 聚合健康快照、启动诊断、模型摘要、队列和最近检测阶段耗时
//   - 面向 WebUI 和诊断包导出，避免打包大图和敏感路径
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearFrost.Core.Inspection;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    public sealed class InspectionStageTimingItem
    {
        public string Stage { get; init; } = string.Empty;
        public bool Succeeded { get; init; }
        public long ElapsedMs { get; init; }
        public string Message { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
    }

    public sealed class RecentInspectionTimingSnapshot
    {
        public string InspectionId { get; init; } = string.Empty;
        public string TriggerSource { get; init; } = string.Empty;
        public DateTimeOffset TriggerTime { get; init; } = DateTimeOffset.Now;
        public bool FinalQualified { get; init; }
        public int AttemptCount { get; init; }
        public int FinalResultCount { get; init; }
        public string UsedModelName { get; init; } = string.Empty;
        public bool WasFallback { get; init; }
        public long TotalMs { get; init; }
        public long CaptureMs { get; init; }
        public long InferenceMs { get; init; }
        public long RoiFilterMs { get; init; }
        public long PlcWriteMs { get; init; }
        public long RenderToUiMs { get; init; }
        public long SaveQueueMs { get; init; }
        public long DbWriteMs { get; init; }
        public long HandshakeStartMs { get; init; }
        public long PlcResultWriteMs { get; init; }
        public long HandshakeCompleteMs { get; init; }
        public string ErrorStage { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
        public string ErrorMessage { get; init; } = string.Empty;
        public IReadOnlyList<InspectionStageTimingItem> Stages { get; init; } = Array.Empty<InspectionStageTimingItem>();

        public static RecentInspectionTimingSnapshot FromContext(InspectionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            return new RecentInspectionTimingSnapshot
            {
                InspectionId = context.InspectionId,
                TriggerSource = context.TriggerSource,
                TriggerTime = context.TriggerTime,
                TotalMs = context.TotalMs,
                CaptureMs = context.CaptureMs,
                InferenceMs = context.InferenceMs,
                RoiFilterMs = context.RoiMs,
                PlcWriteMs = context.PlcWriteMs,
                RenderToUiMs = context.RenderToUiMs,
                DbWriteMs = context.SaveRecordMs,
                HandshakeStartMs = context.HandshakeStartMs,
                PlcResultWriteMs = context.PlcResultWriteMs,
                HandshakeCompleteMs = context.HandshakeCompleteMs,
                ErrorStage = context.ErrorStage ?? string.Empty,
                ErrorCode = context.ErrorCode ?? string.Empty,
                ErrorMessage = context.ErrorMessage ?? string.Empty,
                Stages = BuildStagesFromContext(context)
            };
        }

        internal static RecentInspectionTimingSnapshot FromPipelineResult(InspectionPipelineResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            InspectionContext context = result.Context;
            return new RecentInspectionTimingSnapshot
            {
                InspectionId = context.InspectionId,
                TriggerSource = context.TriggerSource,
                TriggerTime = context.TriggerTime,
                FinalQualified = result.FinalQualified,
                AttemptCount = result.AttemptCount,
                FinalResultCount = result.FinalResultCount,
                UsedModelName = result.UsedModelName ?? string.Empty,
                WasFallback = result.WasFallback,
                TotalMs = context.TotalMs,
                CaptureMs = result.Timings.CaptureMs,
                InferenceMs = result.Timings.InferenceMs,
                RoiFilterMs = result.Timings.RoiFilterMs,
                PlcWriteMs = result.Timings.PlcWriteMs,
                RenderToUiMs = result.Timings.RenderToUiMs,
                SaveQueueMs = result.Timings.SaveQueueMs,
                DbWriteMs = result.Timings.DbWriteMs,
                HandshakeStartMs = result.Timings.HandshakeStartMs,
                PlcResultWriteMs = result.Timings.PlcResultWriteMs,
                HandshakeCompleteMs = result.Timings.HandshakeCompleteMs,
                ErrorStage = context.ErrorStage ?? string.Empty,
                ErrorCode = context.ErrorCode ?? string.Empty,
                ErrorMessage = context.ErrorMessage ?? string.Empty,
                Stages = result.Stages
                    .Select(stage => new InspectionStageTimingItem
                    {
                        Stage = stage.Stage.ToString(),
                        Succeeded = stage.Succeeded,
                        ElapsedMs = stage.ElapsedMs,
                        Message = stage.Message,
                        ErrorCode = stage.ErrorCode ?? string.Empty
                    })
                    .ToArray()
            };
        }

        private static IReadOnlyList<InspectionStageTimingItem> BuildStagesFromContext(InspectionContext context)
        {
            var stages = new List<InspectionStageTimingItem>();
            AddStage(stages, InspectionStage.Capture, context.CaptureMs, context);
            AddStage(stages, InspectionStage.Inference, context.InferenceMs, context);
            AddStage(stages, InspectionStage.RoiFilter, context.RoiMs, context);
            AddStage(stages, InspectionStage.PlcWrite, context.PlcWriteMs, context);
            AddStage(stages, InspectionStage.RenderToUi, context.RenderToUiMs, context);
            AddStage(stages, InspectionStage.SaveImage, context.SaveImageMs, context);
            AddStage(stages, InspectionStage.SaveRecord, context.SaveRecordMs, context);
            return stages;
        }

        private static void AddStage(
            List<InspectionStageTimingItem> stages,
            InspectionStage stage,
            long elapsedMs,
            InspectionContext context)
        {
            if (elapsedMs <= 0)
            {
                return;
            }

            bool failed = string.Equals(context.ErrorStage, stage.ToString(), StringComparison.OrdinalIgnoreCase);
            stages.Add(new InspectionStageTimingItem
            {
                Stage = stage.ToString(),
                Succeeded = !failed,
                ElapsedMs = elapsedMs,
                Message = failed ? context.ErrorMessage ?? string.Empty : string.Empty,
                ErrorCode = failed ? context.ErrorCode ?? string.Empty : string.Empty
            });
        }
    }

    public sealed class FieldDiagnosticItem
    {
        public string Name { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Level { get; init; } = "info";
    }

    public sealed class FieldMaintenanceAdvice
    {
        public string AdviceId { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Level { get; init; } = "info";
        public string Title { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string Advice { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string ResolutionStatus { get; init; } = "Open";
        public DateTimeOffset? FirstSeenAt { get; init; }
        public DateTimeOffset? LastActionAt { get; init; }
        public string LastActionBy { get; init; } = string.Empty;
        public string LastActionMessage { get; init; } = string.Empty;
    }

    public sealed class FieldShiftTask
    {
        public string TaskId { get; init; } = string.Empty;
        public string LinkedAdviceId { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Level { get; init; } = "info";
        public string Status { get; init; } = "Open";
        public string Title { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string SuggestedOwner { get; init; } = string.Empty;
        public DateTimeOffset? FirstSeenAt { get; init; }
        public DateTimeOffset? DueAt { get; init; }
        public bool IsOverdue { get; init; }
        public string EscalationLevel { get; init; } = "Normal";
        public DateTimeOffset? LastActionAt { get; init; }
        public string LastActionBy { get; init; } = string.Empty;
    }

    public sealed class FieldQueueStatus
    {
        public long ImagePending { get; init; }
        public int ImageCapacity { get; init; }
        public long ImagePendingBytes { get; init; }
        public long ImageMaxBufferedBytes { get; init; }
        public long ImageDroppedCount { get; init; }
        public long ImageFailedCount { get; init; }
        public long RecordPending { get; init; }
        public int RecordCapacity { get; init; }
        public long RecordDroppedCount { get; init; }
        public long RecordFailedCount { get; init; }
        public string BacklogLevel { get; init; } = "Ok";
    }

    public sealed class FieldModelSlotProbe
    {
        public string Role { get; init; } = string.Empty;
        public bool IsLoaded { get; init; }
        public string ModelPath { get; init; } = string.Empty;
        public string ModelFileName { get; init; } = string.Empty;
        public string RegistryModelPath { get; init; } = string.Empty;
        public string RegistryModelFileName { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ModelHash { get; init; } = string.Empty;
        public string ModelHashPrefix { get; init; } = string.Empty;
        public string TaskType { get; init; } = string.Empty;
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public string RegistryStatus { get; init; } = string.Empty;
        public bool ApprovedForProduction { get; init; }
        public bool RegistryMatched { get; init; }
        public string RegistryMatchStrategy { get; init; } = string.Empty;
    }

    public sealed class FieldModelProbeSummary
    {
        public string CurrentModelName { get; init; } = string.Empty;
        public bool IsModelLoaded { get; init; }
        public string ExecutionProvider { get; init; } = string.Empty;
        public bool GpuRequested { get; init; }
        public bool GpuActive { get; init; }
        public string GpuFailureReason { get; init; } = string.Empty;
        public int RegistryEntryCount { get; init; }
        public int ReadyEntryCount { get; init; }
        public int WarningEntryCount { get; init; }
        public int BlockedEntryCount { get; init; }
        public string LastMetricsType { get; init; } = string.Empty;
        public string LastMetricsJson { get; init; } = string.Empty;
        public IReadOnlyList<FieldModelSlotProbe> Slots { get; init; } = Array.Empty<FieldModelSlotProbe>();
    }

    public sealed class FieldAuditChainFindingSummary
    {
        public string AuditFileName { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string Severity { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public sealed class FieldAuditChainStatus
    {
        public string Status { get; init; } = "NotChecked";
        public DateTimeOffset? CheckedAt { get; init; }
        public int TotalRecords { get; init; }
        public int VerifiedRecords { get; init; }
        public int FindingCount { get; init; }
        public string LastRecordSha256 { get; init; } = string.Empty;
        public IReadOnlyList<FieldAuditChainFindingSummary> Findings { get; init; } =
            Array.Empty<FieldAuditChainFindingSummary>();
    }

    public sealed class FieldDiagnosticsSnapshot
    {
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
        public string OverallLevel { get; init; } = string.Empty;
        public string CameraStatus { get; init; } = string.Empty;
        public string PlcStatus { get; init; } = string.Empty;
        public string CurrentModelName { get; init; } = string.Empty;
        public string ModelStatus { get; init; } = string.Empty;
        public string StorageStatus { get; init; } = string.Empty;
        public string DatabaseStatus { get; init; } = string.Empty;
        public string RecipeId { get; init; } = string.Empty;
        public string RecipeVersion { get; init; } = string.Empty;
        public string RecipeTargetLabel { get; init; } = string.Empty;
        public int RecipeTargetCount { get; init; }
        public string LastInspectionId { get; init; } = string.Empty;
        public long LastInspectionTotalMs { get; init; }
        public long RecentInspectionP95Ms { get; init; }
        public long RecentInspectionP99Ms { get; init; }
        public long ImageQueueLength { get; init; }
        public int ImageQueueCapacity { get; init; }
        public long RecordQueueLength { get; init; }
        public int RecordQueueCapacity { get; init; }
        public double FreeDiskGb { get; init; }
        public long MemoryMb { get; init; }
        public HealthSnapshot? HealthSnapshot { get; init; }
        public StartupDiagnosticReport? StartupDiagnostics { get; init; }
        public FieldQueueStatus Queues { get; init; } = new FieldQueueStatus();
        public FieldModelProbeSummary ModelProbe { get; init; } = new FieldModelProbeSummary();
        public FieldAuditChainStatus AuditChain { get; init; } = new FieldAuditChainStatus();
        public IReadOnlyList<FieldDiagnosticItem> Components { get; init; } = Array.Empty<FieldDiagnosticItem>();
        public IReadOnlyList<FieldMaintenanceAdvice> MaintenanceAdvice { get; init; } = Array.Empty<FieldMaintenanceAdvice>();
        public IReadOnlyList<MaintenanceAdviceResolutionRecord> MaintenanceAdviceHistory { get; init; } =
            Array.Empty<MaintenanceAdviceResolutionRecord>();
        public IReadOnlyList<FieldShiftTask> ShiftTasks { get; init; } = Array.Empty<FieldShiftTask>();
        public IReadOnlyList<RecentInspectionTimingSnapshot> RecentInspectionTimings { get; init; } = Array.Empty<RecentInspectionTimingSnapshot>();
        public IReadOnlyList<HealthError> RecentErrors { get; init; } = Array.Empty<HealthError>();
    }

    internal static class FieldDiagnosticsSnapshotFactory
    {
        private const int MaxMetricsJsonChars = 2048;

        public static FieldDiagnosticsSnapshot Create(
            HealthSnapshot? health,
            StartupDiagnosticReport? startupDiagnostics,
            IReadOnlyList<ModelRegistryEntry>? modelEntries,
            DetectionRuntimeModelSnapshot? runtimeModelSnapshot,
            string? currentModelName,
            object? lastMetrics,
            Recipe? currentRecipe = null,
            FieldAuditChainStatus? auditChain = null)
        {
            health ??= new HealthSnapshot();
            auditChain ??= new FieldAuditChainStatus();
            FieldQueueStatus queueStatus = BuildQueueStatus(health);
            FieldModelProbeSummary modelProbe = BuildModelProbeSummary(
                health,
                modelEntries,
                runtimeModelSnapshot,
                currentModelName,
                lastMetrics);
            IReadOnlyList<FieldMaintenanceAdvice> maintenanceAdvice = BuildMaintenanceAdvice(
                health,
                startupDiagnostics,
                modelProbe,
                queueStatus,
                auditChain);

            return new FieldDiagnosticsSnapshot
            {
                UpdatedAt = DateTimeOffset.Now,
                OverallLevel = health.HealthLevel.ToString(),
                CameraStatus = health.CameraStatus,
                PlcStatus = health.PlcStatus,
                CurrentModelName = modelProbe.CurrentModelName,
                ModelStatus = health.ModelStatus,
                StorageStatus = health.StorageStatus,
                DatabaseStatus = health.DatabaseStatus,
                RecipeId = currentRecipe?.RecipeId ?? string.Empty,
                RecipeVersion = currentRecipe?.Version ?? string.Empty,
                RecipeTargetLabel = currentRecipe?.TargetLabel ?? string.Empty,
                RecipeTargetCount = currentRecipe?.TargetCount ?? 0,
                LastInspectionId = health.LastInspectionId,
                LastInspectionTotalMs = health.LastInspectionTotalMs,
                RecentInspectionP95Ms = health.RecentInspectionP95Ms,
                RecentInspectionP99Ms = health.RecentInspectionP99Ms,
                ImageQueueLength = health.ImageQueueLength,
                ImageQueueCapacity = health.ImageQueueCapacity,
                RecordQueueLength = health.RecordQueueLength,
                RecordQueueCapacity = health.RecordQueueCapacity,
                FreeDiskGb = health.FreeDiskGb,
                MemoryMb = health.MemoryMb,
                HealthSnapshot = health,
                StartupDiagnostics = startupDiagnostics,
                Queues = queueStatus,
                ModelProbe = modelProbe,
                AuditChain = auditChain,
                Components = BuildComponents(health, startupDiagnostics, modelProbe, auditChain),
                MaintenanceAdvice = maintenanceAdvice,
                RecentInspectionTimings = health.RecentInspectionTimings,
                RecentErrors = health.RecentErrors
            };
        }

        public static FieldAuditChainStatus BuildAuditChainStatus(
            OperationAuditChainVerificationResult? verification,
            DateTimeOffset checkedAt)
        {
            verification ??= new OperationAuditChainVerificationResult();
            return new FieldAuditChainStatus
            {
                Status = verification.Status,
                CheckedAt = checkedAt,
                TotalRecords = verification.TotalRecords,
                VerifiedRecords = verification.VerifiedRecords,
                FindingCount = verification.Findings.Count,
                LastRecordSha256 = verification.LastRecordSha256,
                Findings = verification.Findings
                    .Take(5)
                    .Select(finding => new FieldAuditChainFindingSummary
                    {
                        AuditFileName = string.IsNullOrWhiteSpace(finding.FilePath)
                            ? string.Empty
                            : Path.GetFileName(finding.FilePath),
                        LineNumber = finding.LineNumber,
                        Severity = finding.Severity,
                        ErrorCode = finding.ErrorCode,
                        Message = finding.Message
                    })
                    .ToList()
            };
        }

        public static FieldQueueStatus BuildQueueStatus(HealthSnapshot health)
        {
            if (health == null) throw new ArgumentNullException(nameof(health));

            string level = ResolveQueueLevel(
                health.ImageQueueLength,
                health.ImageQueueCapacity,
                health.RecordQueueLength,
                health.RecordQueueCapacity,
                health.ImageQueueDroppedCount + health.ImageQueueFailedCount +
                health.RecordQueueDroppedCount + health.RecordQueueFailedCount);

            return new FieldQueueStatus
            {
                ImagePending = health.ImageQueueLength,
                ImageCapacity = health.ImageQueueCapacity,
                ImagePendingBytes = health.ImageQueuePendingBytes,
                ImageMaxBufferedBytes = health.ImageQueueMaxBufferedBytes,
                ImageDroppedCount = health.ImageQueueDroppedCount,
                ImageFailedCount = health.ImageQueueFailedCount,
                RecordPending = health.RecordQueueLength,
                RecordCapacity = health.RecordQueueCapacity,
                RecordDroppedCount = health.RecordQueueDroppedCount,
                RecordFailedCount = health.RecordQueueFailedCount,
                BacklogLevel = level
            };
        }

        public static FieldModelProbeSummary BuildModelProbeSummary(
            HealthSnapshot health,
            IReadOnlyList<ModelRegistryEntry>? modelEntries,
            DetectionRuntimeModelSnapshot? runtimeModelSnapshot,
            string? currentModelName,
            object? lastMetrics)
        {
            modelEntries ??= Array.Empty<ModelRegistryEntry>();
            runtimeModelSnapshot ??= new DetectionRuntimeModelSnapshot();
            DetectionRuntimeStatus runtime = health?.DetectionRuntime ?? new DetectionRuntimeStatus();
            string modelName = string.IsNullOrWhiteSpace(currentModelName)
                ? ExtractCurrentModelName(health?.ModelStatus)
                : currentModelName!.Trim();

            return new FieldModelProbeSummary
            {
                CurrentModelName = modelName,
                IsModelLoaded = !string.IsNullOrWhiteSpace(modelName) && !string.Equals(modelName, "未加载", StringComparison.OrdinalIgnoreCase),
                ExecutionProvider = runtime.ExecutionProvider,
                GpuRequested = runtime.GpuRequested,
                GpuActive = runtime.GpuActive,
                GpuFailureReason = runtime.GpuFailureReason,
                RegistryEntryCount = modelEntries.Count,
                ReadyEntryCount = modelEntries.Count(entry => entry.Status == ModelRegistryStatus.Ready),
                WarningEntryCount = modelEntries.Count(entry => entry.Status == ModelRegistryStatus.Warning),
                BlockedEntryCount = modelEntries.Count(entry => entry.Status == ModelRegistryStatus.Blocked),
                LastMetricsType = lastMetrics?.GetType().Name ?? string.Empty,
                LastMetricsJson = SerializeMetricsSummary(lastMetrics),
                Slots = new[]
                {
                    BuildSlotProbe(runtimeModelSnapshot.Primary, modelEntries),
                    BuildSlotProbe(runtimeModelSnapshot.Auxiliary1, modelEntries),
                    BuildSlotProbe(runtimeModelSnapshot.Auxiliary2, modelEntries)
                }
            };
        }

        private static IReadOnlyList<FieldDiagnosticItem> BuildComponents(
            HealthSnapshot health,
            StartupDiagnosticReport? startupDiagnostics,
            FieldModelProbeSummary modelProbe,
            FieldAuditChainStatus auditChain)
        {
            int blockingFailures = startupDiagnostics?.BlockingFailureCount ?? 0;
            var items = new List<FieldDiagnosticItem>
            {
                Component("相机", health.CameraStatus, health.CameraStatus, IsCameraReady(health.CameraStatus)),
                Component("PLC", health.PlcStatus, health.PlcStatus, health.PlcStatus.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)),
                Component("模型", modelProbe.CurrentModelName, health.ModelStatus, modelProbe.IsModelLoaded),
                Component("存储", health.StorageStatus, $"剩余 {health.FreeDiskGb:F2} GB", string.Equals(health.StorageStatus, "Writable", StringComparison.OrdinalIgnoreCase)),
                Component("数据库", health.DatabaseStatus, health.DatabaseStatus, !string.Equals(health.DatabaseStatus, "Warning", StringComparison.OrdinalIgnoreCase)),
                Component("队列", health.HealthLevel.ToString(), $"图像 {health.ImageQueueLength}/{health.ImageQueueCapacity}，记录 {health.RecordQueueLength}/{health.RecordQueueCapacity}", blockingFailures == 0),
                Component("启动诊断", blockingFailures == 0 ? "Ready" : "Blocked", $"阻塞项 {blockingFailures}", blockingFailures == 0),
                Component("审计链", auditChain.Status, BuildAuditChainComponentMessage(auditChain), IsAuditChainHealthyForComponent(auditChain))
            };
            return items;
        }

        private static FieldDiagnosticItem Component(string name, string status, string message, bool ok)
        {
            return new FieldDiagnosticItem
            {
                Name = name,
                Status = status,
                Message = message,
                Level = ok ? "ok" : "warning"
            };
        }

        private static IReadOnlyList<FieldMaintenanceAdvice> BuildMaintenanceAdvice(
            HealthSnapshot health,
            StartupDiagnosticReport? startupDiagnostics,
            FieldModelProbeSummary modelProbe,
            FieldQueueStatus queueStatus,
            FieldAuditChainStatus auditChain)
        {
            var advice = new List<FieldMaintenanceAdvice>();
            AddStartupAdvice(advice, startupDiagnostics);
            AddRuntimeReadinessAdvice(advice, health, modelProbe);
            AddModelAdvice(advice, modelProbe);
            AddQueueAdvice(advice, queueStatus);
            AddAuditChainAdvice(advice, auditChain);
            AddRecentErrorAdvice(advice, health.RecentErrors);

            return advice
                .Where(item => !string.IsNullOrWhiteSpace(item.Title) && !string.IsNullOrWhiteSpace(item.Advice))
                .GroupBy(item => $"{item.Source}|{item.Title}|{item.Advice}", StringComparer.OrdinalIgnoreCase)
                .Select(group => EnsureAdviceId(group.First()))
                .Take(12)
                .ToList();
        }

        private static string BuildAuditChainComponentMessage(FieldAuditChainStatus auditChain)
        {
            if (auditChain.CheckedAt == null ||
                string.Equals(auditChain.Status, "NotChecked", StringComparison.OrdinalIgnoreCase))
            {
                return "尚未校验";
            }

            return $"已校验 {auditChain.VerifiedRecords}/{auditChain.TotalRecords}，异常 {auditChain.FindingCount}";
        }

        private static bool IsAuditChainHealthyForComponent(FieldAuditChainStatus auditChain)
        {
            return string.Equals(auditChain.Status, "Healthy", StringComparison.OrdinalIgnoreCase);
        }

        internal static string CreateMaintenanceAdviceId(string source, string code, string title)
        {
            string key = $"{source ?? string.Empty}|{code ?? string.Empty}|{title ?? string.Empty}".Trim();
            using SHA256 sha256 = SHA256.Create();
            string hash = Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
            string safeCode = string.IsNullOrWhiteSpace(code) ? "Advice" : code.Trim();
            return $"{safeCode}-{hash[..12]}";
        }

        private static FieldMaintenanceAdvice EnsureAdviceId(FieldMaintenanceAdvice advice)
        {
            string id = string.IsNullOrWhiteSpace(advice.AdviceId)
                ? CreateMaintenanceAdviceId(advice.Source, advice.Code, advice.Title)
                : advice.AdviceId;

            return new FieldMaintenanceAdvice
            {
                AdviceId = id,
                Source = advice.Source,
                Level = advice.Level,
                Title = advice.Title,
                Evidence = advice.Evidence,
                Advice = advice.Advice,
                Code = advice.Code,
                ResolutionStatus = advice.ResolutionStatus,
                FirstSeenAt = advice.FirstSeenAt,
                LastActionAt = advice.LastActionAt,
                LastActionBy = advice.LastActionBy,
                LastActionMessage = advice.LastActionMessage
            };
        }

        private static void AddStartupAdvice(List<FieldMaintenanceAdvice> advice, StartupDiagnosticReport? startupDiagnostics)
        {
            if (startupDiagnostics == null)
            {
                return;
            }

            foreach (StartupDiagnosticItem item in startupDiagnostics.Items
                .Where(item => item.Status == StartupDiagnosticStatus.Fail && item.IsBlocking))
            {
                string adviceText = OperatorFaultMessages.ForStartupItem(item);
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "StartupDiagnostics",
                    Level = "critical",
                    Title = adviceText == OperatorFaultMessages.StrictModelGateBlocked
                        ? "严格模型验证未通过"
                        : "启动前需要处理",
                    Evidence = item.Message,
                    Advice = adviceText,
                    Code = "StartupBlocked"
                });
            }
        }

        private static void AddRuntimeReadinessAdvice(
            List<FieldMaintenanceAdvice> advice,
            HealthSnapshot health,
            FieldModelProbeSummary modelProbe)
        {
            if (!IsCameraReady(health.CameraStatus))
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "Camera",
                    Level = "warning",
                    Title = "相机未启动",
                    Evidence = health.CameraStatus,
                    Advice = OperatorFaultMessages.ForCode("CameraNotReady"),
                    Code = "CameraNotReady"
                });
            }

            if (!health.PlcStatus.StartsWith("Connected", StringComparison.OrdinalIgnoreCase))
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "PLC",
                    Level = "warning",
                    Title = "PLC 未连接",
                    Evidence = health.PlcStatus,
                    Advice = OperatorFaultMessages.ForCode("PlcNotConnected"),
                    Code = "PlcNotConnected"
                });
            }

            if (!modelProbe.IsModelLoaded)
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "ModelRuntime",
                    Level = "critical",
                    Title = "模型未加载",
                    Evidence = string.IsNullOrWhiteSpace(modelProbe.CurrentModelName) ? "未加载" : modelProbe.CurrentModelName,
                    Advice = OperatorFaultMessages.ForCode("ModelNotLoaded"),
                    Code = "ModelNotLoaded"
                });
            }

            if (modelProbe.GpuRequested && !modelProbe.GpuActive)
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "ModelRuntime",
                    Level = "warning",
                    Title = "GPU 请求未生效",
                    Evidence = modelProbe.GpuFailureReason,
                    Advice = "检查 DirectML/显卡驱动、GPU 设备编号和 ONNX Runtime 环境；必要时切换 CPU 并记录节拍影响。",
                    Code = "GpuFallback"
                });
            }
        }

        private static void AddModelAdvice(List<FieldMaintenanceAdvice> advice, FieldModelProbeSummary modelProbe)
        {
            foreach (FieldModelSlotProbe slot in modelProbe.Slots.Where(slot => slot.IsLoaded && !slot.RegistryMatched))
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "ModelRegistry",
                    Level = "warning",
                    Title = $"{slot.Role} 模型未匹配注册表",
                    Evidence = string.IsNullOrWhiteSpace(slot.ModelPath) ? slot.ModelFileName : slot.ModelPath,
                    Advice = "刷新模型列表或重新扫描模型目录；确认运行时加载路径来自模型包/ONNX 目录，避免同名模型导致追溯字段缺失。",
                    Code = "RuntimeModelUnmatched"
                });
            }

            if (modelProbe.BlockedEntryCount > 0)
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "ModelRegistry",
                    Level = "warning",
                    Title = "模型注册表存在阻断项",
                    Evidence = $"Blocked={modelProbe.BlockedEntryCount}",
                    Advice = "请联系工程师打开诊断包，修复模型包 manifest、模型哈希或验证记录后重新扫描。",
                    Code = "ModelRegistryBlocked"
                });
            }
        }

        private static void AddQueueAdvice(List<FieldMaintenanceAdvice> advice, FieldQueueStatus queueStatus)
        {
            long imageFailures = queueStatus.ImageDroppedCount + queueStatus.ImageFailedCount;
            long recordFailures = queueStatus.RecordDroppedCount + queueStatus.RecordFailedCount;
            if (string.Equals(queueStatus.BacklogLevel, "Warning", StringComparison.OrdinalIgnoreCase) ||
                imageFailures > 0 ||
                recordFailures > 0)
            {
                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = "Queues",
                    Level = imageFailures + recordFailures > 0 ? "warning" : "info",
                    Title = "后台保存队列需要关注",
                    Evidence = $"Images={queueStatus.ImagePending}/{queueStatus.ImageCapacity}, Records={queueStatus.RecordPending}/{queueStatus.RecordCapacity}, Failures={imageFailures + recordFailures}",
                    Advice = "检查磁盘/数据库写入速度、存储路径权限和触发频率；若持续积压，降低节拍或切换到更快的存储介质。",
                    Code = "QueuePressure"
                });
            }
        }

        private static void AddAuditChainAdvice(List<FieldMaintenanceAdvice> advice, FieldAuditChainStatus auditChain)
        {
            if (auditChain == null ||
                string.Equals(auditChain.Status, "Healthy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(auditChain.Status, "NotChecked", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            bool blocking = string.Equals(auditChain.Status, "Blocking", StringComparison.OrdinalIgnoreCase);
            FieldAuditChainFindingSummary? firstFinding = auditChain.Findings.FirstOrDefault();
            advice.Add(new FieldMaintenanceAdvice
            {
                Source = "OperationAudit",
                Level = blocking ? "critical" : "warning",
                Title = blocking ? "操作审计链存在阻断异常" : "操作审计链存在警告",
                Evidence = firstFinding == null
                    ? $"Status={auditChain.Status}; Verified={auditChain.VerifiedRecords}/{auditChain.TotalRecords}; Findings={auditChain.FindingCount}"
                    : $"Status={auditChain.Status}; {firstFinding.ErrorCode} @ {firstFinding.AuditFileName}:{firstFinding.LineNumber}",
                Advice = blocking
                    ? "暂停将该审计 outbox 作为唯一追溯依据；保留原始文件，导出诊断包，并由工程/质量负责人复核是否存在截断、重排或篡改。"
                    : "确认是否为旧版本审计记录或迁移窗口；完成复核后重新执行审计链校验。",
                Code = blocking ? "AuditChainBlocking" : "AuditChainWarning"
            });
        }

        private static void AddRecentErrorAdvice(List<FieldMaintenanceAdvice> advice, IReadOnlyList<HealthError> errors)
        {
            foreach (HealthError error in (errors ?? Array.Empty<HealthError>())
                .Reverse()
                .Take(5))
            {
                string mappedAdvice = ResolveRecentErrorAdvice(error);
                if (string.IsNullOrWhiteSpace(mappedAdvice))
                {
                    continue;
                }

                advice.Add(new FieldMaintenanceAdvice
                {
                    Source = error.Source,
                    Level = "warning",
                    Title = $"最近错误: {error.Source}",
                    Evidence = error.Message,
                    Advice = mappedAdvice,
                    Code = "RecentError"
                });
            }
        }

        private static string ResolveStartupAdvice(StartupDiagnosticItem item)
        {
            string text = $"{item.Name} {item.Message} {item.Details}";
            if (ContainsAny(text, "WebView2"))
            {
                return "安装或修复 Microsoft WebView2 Runtime，并确认应用运行账户可创建 WebView2 环境。";
            }

            if (ContainsAny(text, "Storage", "Log directory", "Database directory", "Disk", "目录", "磁盘"))
            {
                return "检查存储盘符、目录权限和剩余空间；修正配置后保存设置并重新刷新启动诊断。";
            }

            if (ContainsAny(text, "PLC", "address", "协议", "地址"))
            {
                return "检查 PLC 协议、地址格式、握手地址和驱动提供方，确保与现场 PLC 程序一致。";
            }

            if (ContainsAny(text, "Replay evidence", "Approved model", "审批", "凭证"))
            {
                return OperatorFaultMessages.StrictModelGateBlocked;
            }

            if (ContainsAny(text, "Camera", "相机"))
            {
                return "检查相机配置、序列号和 SDK 依赖；确认现场相机已上电并能被驱动发现。";
            }

            return "按启动诊断详情修复阻断项，修复后刷新诊断状态并再次尝试启动系统。";
        }

        private static string ResolveRecentErrorAdvice(HealthError error)
        {
            string source = error.Source ?? string.Empty;
            string message = error.Message ?? string.Empty;
            string text = $"{source} {message}";
            if (ContainsAny(text, "ImageSaveQueue", "图像保存", "SaveImage"))
            {
                return "检查图像保存目录权限、磁盘空间和写盘速度；必要时降低触发频率或清理历史图片。";
            }

            if (ContainsAny(text, "DetectionRecordQueue", "数据库", "SaveRecord"))
            {
                return "检查数据库文件、存储目录权限和 SQLite 写入状态；必要时导出诊断包后重启服务。";
            }

            if (ContainsAny(text, "PLC", "Plc"))
            {
                return "检查 PLC 通讯、结果地址和握手时序；对照最近 InspectionId 复核 PLC 程序响应。";
            }

            if (ContainsAny(text, "Camera", "相机", "取图"))
            {
                return "检查相机连接、曝光、触发线和 SDK 日志；确认手动单步取图是否成功。";
            }

            if (ContainsAny(text, "Inference", "Detection", "模型", "推理", "ONNX"))
            {
                return "检查模型文件、输入图像尺寸、GPU/CPU 推理环境和模型注册表匹配状态。";
            }

            return string.IsNullOrWhiteSpace(message)
                ? string.Empty
                : "查看诊断包中的 recent_errors.json 与系统日志，按错误来源定位对应硬件或服务。";
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static string CombineEvidence(string message, string details)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                return message ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return details;
            }

            return $"{message} {details}";
        }

        private static bool IsCameraReady(string cameraStatus)
        {
            return string.Equals(cameraStatus, "Open", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(cameraStatus, "Grabbing", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveQueueLevel(
            long imagePending,
            int imageCapacity,
            long recordPending,
            int recordCapacity,
            long failures)
        {
            if (failures > 0)
            {
                return "Warning";
            }

            if (IsNearCapacity(imagePending, imageCapacity) || IsNearCapacity(recordPending, recordCapacity))
            {
                return "Warning";
            }

            return "Ok";
        }

        private static bool IsNearCapacity(long pending, long capacity)
        {
            return capacity > 0 && pending * 4L >= capacity * 3L;
        }

        private static FieldModelSlotProbe BuildSlotProbe(
            DetectionModelSlotSnapshot slot,
            IReadOnlyList<ModelRegistryEntry> modelEntries)
        {
            string fileName = Path.GetFileName(slot.ModelPath ?? string.Empty);
            ModelRegistryEntry? match = ResolveRuntimeSlotRegistryEntry(
                slot,
                modelEntries,
                out string matchStrategy);

            return new FieldModelSlotProbe
            {
                Role = slot.Role.ToString(),
                IsLoaded = slot.IsLoaded,
                ModelPath = GetFullPathSafe(slot.ModelPath),
                ModelFileName = fileName,
                RegistryModelPath = GetFullPathSafe(match?.ModelPath),
                RegistryModelFileName = Path.GetFileName(match?.ModelPath ?? string.Empty),
                ModelId = match?.ModelId ?? string.Empty,
                Version = match?.Version ?? string.Empty,
                ModelHash = match?.ModelHash ?? string.Empty,
                ModelHashPrefix = ShortHash(match?.ModelHash),
                TaskType = ResolveTaskType(match),
                InputWidth = ResolveInputWidth(match),
                InputHeight = ResolveInputHeight(match),
                RegistryStatus = match?.Status.ToString() ?? string.Empty,
                ApprovedForProduction = match?.ApprovedForProduction ?? false,
                RegistryMatched = match != null,
                RegistryMatchStrategy = matchStrategy
            };
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

        internal static ModelRegistryEntry? ResolveRuntimeSlotRegistryEntry(
            DetectionModelSlotSnapshot? slot,
            IReadOnlyList<ModelRegistryEntry>? modelEntries,
            out string matchStrategy)
        {
            matchStrategy = string.Empty;
            if (slot == null)
            {
                return null;
            }

            modelEntries ??= Array.Empty<ModelRegistryEntry>();
            string slotPath = GetFullPathSafe(slot.ModelPath);
            if (!string.IsNullOrWhiteSpace(slotPath))
            {
                ModelRegistryEntry? pathMatch = modelEntries.FirstOrDefault(entry =>
                    string.Equals(GetFullPathSafe(entry.ModelPath), slotPath, StringComparison.OrdinalIgnoreCase));
                if (pathMatch != null)
                {
                    matchStrategy = "ModelPath";
                    return pathMatch;
                }
            }

            string fileName = Path.GetFileName(slot.ModelPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            ModelRegistryEntry? usedNameMatch = modelEntries.FirstOrDefault(entry =>
                string.Equals(entry.UsedModelName, fileName, StringComparison.OrdinalIgnoreCase));
            if (usedNameMatch != null)
            {
                matchStrategy = "UsedModelName";
                return usedNameMatch;
            }

            ModelRegistryEntry? fileNameMatch = modelEntries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry.ModelPath ?? string.Empty), fileName, StringComparison.OrdinalIgnoreCase));
            if (fileNameMatch != null)
            {
                matchStrategy = "ModelFileName";
                return fileNameMatch;
            }

            return null;
        }

        private static string ExtractCurrentModelName(string? modelStatus)
        {
            if (string.IsNullOrWhiteSpace(modelStatus))
            {
                return string.Empty;
            }

            string[] parts = modelStatus.Split(':');
            return parts.Length >= 2 ? parts[1] : modelStatus;
        }

        private static string ShortHash(string? hash)
        {
            if (string.IsNullOrWhiteSpace(hash))
            {
                return string.Empty;
            }

            return hash.Length <= 12 ? hash : hash.Substring(0, 12);
        }

        internal static string GetFullPathSafe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path.Trim();
            }
        }

        private static string SerializeMetricsSummary(object? metrics)
        {
            if (metrics == null)
            {
                return string.Empty;
            }

            try
            {
                string json = JsonSerializer.Serialize(metrics);
                return json.Length <= MaxMetricsJsonChars
                    ? json
                    : json.Substring(0, MaxMetricsJsonChars) + "...";
            }
            catch
            {
                return metrics.ToString() ?? string.Empty;
            }
        }
    }

    internal static class FieldShiftTaskBuilder
    {
        public static IReadOnlyList<FieldShiftTask> Build(
            IReadOnlyList<FieldMaintenanceAdvice>? activeAdvice,
            IReadOnlyList<MaintenanceAdviceResolutionRecord>? maintenanceHistory,
            DateTimeOffset? now = null)
        {
            activeAdvice ??= Array.Empty<FieldMaintenanceAdvice>();
            maintenanceHistory ??= Array.Empty<MaintenanceAdviceResolutionRecord>();
            DateTimeOffset effectiveNow = now ?? DateTimeOffset.Now;

            var tasks = new List<FieldShiftTask>();
            foreach (FieldMaintenanceAdvice advice in activeAdvice)
            {
                if (string.IsNullOrWhiteSpace(advice.Title))
                {
                    continue;
                }

                string adviceId = ResolveAdviceId(advice);
                DateTimeOffset firstSeenAt = advice.FirstSeenAt ?? effectiveNow;
                DateTimeOffset dueAt = ResolveDueAt(
                    advice.Level,
                    advice.ResolutionStatus,
                    advice.LastActionAt ?? firstSeenAt);
                tasks.Add(new FieldShiftTask
                {
                    TaskId = $"Advice:{adviceId}",
                    LinkedAdviceId = adviceId,
                    Source = advice.Source,
                    Level = advice.Level,
                    Status = string.IsNullOrWhiteSpace(advice.ResolutionStatus) ? "Open" : advice.ResolutionStatus,
                    Title = advice.Title,
                    Evidence = advice.Evidence,
                    Action = advice.Advice,
                    SuggestedOwner = ResolveSuggestedOwner(advice.Source, advice.Code),
                    FirstSeenAt = firstSeenAt,
                    DueAt = dueAt,
                    IsOverdue = dueAt < effectiveNow,
                    EscalationLevel = ResolveEscalationLevel(advice.Level, advice.ResolutionStatus, dueAt, effectiveNow),
                    LastActionAt = advice.LastActionAt,
                    LastActionBy = advice.LastActionBy
                });
            }

            HashSet<string> activeAdviceIds = activeAdvice
                .Select(ResolveAdviceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (MaintenanceAdviceResolutionRecord record in maintenanceHistory)
            {
                if (!string.Equals(record.Status, MaintenanceAdviceResolutionStatuses.RecheckFailed, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(record.AdviceId) ||
                    activeAdviceIds.Contains(record.AdviceId))
                {
                    continue;
                }

                DateTimeOffset dueAt = ResolveDueAt("warning", record.Status, record.ActionAt);
                tasks.Add(new FieldShiftTask
                {
                    TaskId = $"FailedRecheck:{record.AdviceId}",
                    LinkedAdviceId = record.AdviceId,
                    Source = string.IsNullOrWhiteSpace(record.Source) ? "MaintenanceAdvice" : record.Source,
                    Level = "warning",
                    Status = record.Status,
                    Title = string.IsNullOrWhiteSpace(record.Title)
                        ? "历史维护建议复检未通过"
                        : $"复检未通过: {record.Title}",
                    Evidence = record.Message,
                    Action = "重新确认现场问题是否已消除；若已恢复，请执行维护建议复检完成闭环。",
                    SuggestedOwner = ResolveSuggestedOwner(record.Source, record.Code),
                    FirstSeenAt = record.ActionAt,
                    DueAt = dueAt,
                    IsOverdue = dueAt < effectiveNow,
                    EscalationLevel = ResolveEscalationLevel("warning", record.Status, dueAt, effectiveNow),
                    LastActionAt = record.ActionAt,
                    LastActionBy = record.OperatorId
                });
            }

            return tasks
                .Where(task => !string.IsNullOrWhiteSpace(task.Title))
                .GroupBy(task => task.TaskId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(task => ResolveTaskPriority(task.Level, task.Status, task.IsOverdue))
                .ThenByDescending(task => task.LastActionAt ?? DateTimeOffset.MinValue)
                .ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }

        private static string ResolveAdviceId(FieldMaintenanceAdvice advice)
        {
            return string.IsNullOrWhiteSpace(advice.AdviceId)
                ? FieldDiagnosticsSnapshotFactory.CreateMaintenanceAdviceId(advice.Source, advice.Code, advice.Title)
                : advice.AdviceId;
        }

        private static int ResolveTaskPriority(string level, string status, bool isOverdue)
        {
            if (isOverdue)
            {
                return 0;
            }

            if (string.Equals(status, MaintenanceAdviceResolutionStatuses.RecheckFailed, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            string normalizedLevel = (level ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedLevel is "critical" or "error")
            {
                return 2;
            }

            if (normalizedLevel == "warning")
            {
                return 3;
            }

            return 4;
        }

        private static DateTimeOffset ResolveDueAt(string level, string status, DateTimeOffset baseTime)
        {
            if (string.Equals(status, MaintenanceAdviceResolutionStatuses.RecheckFailed, StringComparison.OrdinalIgnoreCase))
            {
                return baseTime.AddMinutes(30);
            }

            string normalizedLevel = (level ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedLevel is "critical" or "error")
            {
                return baseTime.AddMinutes(30);
            }

            if (normalizedLevel == "warning")
            {
                return baseTime.AddHours(2);
            }

            return baseTime.AddHours(8);
        }

        private static string ResolveEscalationLevel(
            string level,
            string status,
            DateTimeOffset dueAt,
            DateTimeOffset now)
        {
            if (dueAt < now)
            {
                return "Overdue";
            }

            if (string.Equals(status, MaintenanceAdviceResolutionStatuses.RecheckFailed, StringComparison.OrdinalIgnoreCase))
            {
                return "High";
            }

            string normalizedLevel = (level ?? string.Empty).Trim().ToLowerInvariant();
            return normalizedLevel is "critical" or "error" ? "High" :
                normalizedLevel == "warning" ? "Medium" : "Normal";
        }

        private static string ResolveSuggestedOwner(string source, string code)
        {
            string key = $"{source}|{code}".ToLowerInvariant();
            if (key.Contains("plc", StringComparison.OrdinalIgnoreCase))
            {
                return "电气/PLC";
            }

            if (key.Contains("camera", StringComparison.OrdinalIgnoreCase))
            {
                return "设备维护";
            }

            if (key.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("registry", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("startup", StringComparison.OrdinalIgnoreCase))
            {
                return "工艺/算法工程";
            }

            if (key.Contains("queue", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("storage", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("database", StringComparison.OrdinalIgnoreCase))
            {
                return "系统维护";
            }

            return "现场班组";
        }
    }
}
