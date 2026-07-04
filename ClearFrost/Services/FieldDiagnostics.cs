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
using System.Text.Json;
using ClearFrost.Core.Inspection;
using ClearFrost.Core.Models;
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
        public string ModelFileName { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ModelHashPrefix { get; init; } = string.Empty;
        public string TaskType { get; init; } = string.Empty;
        public int InputWidth { get; init; }
        public int InputHeight { get; init; }
        public string RegistryStatus { get; init; } = string.Empty;
        public bool ApprovedForProduction { get; init; }
        public bool RegistryMatched { get; init; }
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
        public IReadOnlyList<FieldDiagnosticItem> Components { get; init; } = Array.Empty<FieldDiagnosticItem>();
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
            object? lastMetrics)
        {
            health ??= new HealthSnapshot();
            FieldQueueStatus queueStatus = BuildQueueStatus(health);
            FieldModelProbeSummary modelProbe = BuildModelProbeSummary(
                health,
                modelEntries,
                runtimeModelSnapshot,
                currentModelName,
                lastMetrics);

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
                Components = BuildComponents(health, startupDiagnostics, modelProbe),
                RecentInspectionTimings = health.RecentInspectionTimings,
                RecentErrors = health.RecentErrors
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
            FieldModelProbeSummary modelProbe)
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
                Component("启动诊断", blockingFailures == 0 ? "Ready" : "Blocked", $"阻塞项 {blockingFailures}", blockingFailures == 0)
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
            ModelRegistryEntry? match = modelEntries.FirstOrDefault(entry =>
                string.Equals(Path.GetFileName(entry.ModelPath ?? string.Empty), fileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.UsedModelName, fileName, StringComparison.OrdinalIgnoreCase));

            return new FieldModelSlotProbe
            {
                Role = slot.Role.ToString(),
                IsLoaded = slot.IsLoaded,
                ModelFileName = fileName,
                ModelId = match?.ModelId ?? string.Empty,
                Version = match?.Version ?? string.Empty,
                ModelHashPrefix = ShortHash(match?.ModelHash),
                TaskType = match?.TaskType ?? string.Empty,
                InputWidth = match?.InputWidth ?? 0,
                InputHeight = match?.InputHeight ?? 0,
                RegistryStatus = match?.Status.ToString() ?? string.Empty,
                ApprovedForProduction = match?.ApprovedForProduction ?? false,
                RegistryMatched = match != null
            };
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
}
