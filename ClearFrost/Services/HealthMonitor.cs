// ============================================================================
// 文件名: HealthMonitor.cs
// 描述:   工业运行健康快照与最近错误记录
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClearFrost.Core.Inspection;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    public enum HealthLevel
    {
        Ok,
        Warning,
        Critical
    }

    public sealed class HealthError
    {
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
        public string Source { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string InspectionId { get; init; } = string.Empty;
    }

    public sealed class HealthSnapshot
    {
        public TimeSpan SystemUptime { get; init; }
        public HealthLevel HealthLevel { get; init; }
        public string InspectionState { get; init; } = string.Empty;
        public string CameraStatus { get; init; } = string.Empty;
        public string PlcStatus { get; init; } = string.Empty;
        public string ModelStatus { get; init; } = string.Empty;
        public DetectionRuntimeStatus DetectionRuntime { get; init; } = new DetectionRuntimeStatus();
        public string RecipeStatus { get; init; } = string.Empty;
        public string StorageStatus { get; init; } = string.Empty;
        public string DatabaseStatus { get; init; } = string.Empty;
        public string LastInspectionId { get; init; } = string.Empty;
        public long LastInspectionTotalMs { get; init; }
        public long RecentInspectionP95Ms { get; init; }
        public long RecentInspectionP99Ms { get; init; }
        public long ImageQueueLength { get; init; }
        public int ImageQueueCapacity { get; init; }
        public long ImageQueueDroppedCount { get; init; }
        public long ImageQueueFailedCount { get; init; }
        public long RecordQueueLength { get; init; }
        public int RecordQueueCapacity { get; init; }
        public long RecordQueueDroppedCount { get; init; }
        public long RecordQueueFailedCount { get; init; }
        public double FreeDiskGb { get; init; }
        public long MemoryMb { get; init; }
        public IReadOnlyList<HealthError> RecentErrors { get; init; } = Array.Empty<HealthError>();
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    }

    internal sealed class HealthMonitor
    {
        private const int MaxRecentErrors = 50;
        private const int MaxRecentInspectionSamples = 200;

        private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
        private readonly ICameraService _cameraService;
        private readonly IPlcService _plcService;
        private readonly IDetectionService _detectionService;
        private readonly IStorageService _storageService;
        private readonly ImageSaveQueue _imageSaveQueue;
        private readonly DetectionRecordQueue _recordQueue;
        private readonly object _syncRoot = new object();
        private readonly Queue<HealthError> _recentErrors = new Queue<HealthError>();
        private readonly Queue<long> _recentInspectionMs = new Queue<long>();
        private string _lastInspectionId = string.Empty;
        private long _lastInspectionTotalMs;

        public HealthMonitor(
            ICameraService cameraService,
            IPlcService plcService,
            IDetectionService detectionService,
            IStorageService storageService,
            ImageSaveQueue imageSaveQueue,
            DetectionRecordQueue recordQueue)
        {
            _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _imageSaveQueue = imageSaveQueue ?? throw new ArgumentNullException(nameof(imageSaveQueue));
            _recordQueue = recordQueue ?? throw new ArgumentNullException(nameof(recordQueue));
        }

        public void RecordError(string source, string message, string? inspectionId = null)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            lock (_syncRoot)
            {
                _recentErrors.Enqueue(new HealthError
                {
                    Timestamp = DateTimeOffset.Now,
                    Source = source ?? string.Empty,
                    Message = message,
                    InspectionId = inspectionId ?? string.Empty
                });

                while (_recentErrors.Count > MaxRecentErrors)
                {
                    _recentErrors.Dequeue();
                }
            }
        }

        public void RecordInspection(InspectionContext context)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.InspectionId))
            {
                return;
            }

            lock (_syncRoot)
            {
                _lastInspectionId = context.InspectionId;
                _lastInspectionTotalMs = context.TotalMs;
                if (context.TotalMs > 0)
                {
                    _recentInspectionMs.Enqueue(context.TotalMs);
                    while (_recentInspectionMs.Count > MaxRecentInspectionSamples)
                    {
                        _recentInspectionMs.Dequeue();
                    }
                }
            }
        }

        public HealthSnapshot GetSnapshot()
        {
            HealthError[] errors;
            long[] inspectionMs;
            string lastInspectionId;
            long lastInspectionTotalMs;

            lock (_syncRoot)
            {
                errors = _recentErrors.ToArray();
                inspectionMs = _recentInspectionMs.ToArray();
                lastInspectionId = _lastInspectionId;
                lastInspectionTotalMs = _lastInspectionTotalMs;
            }

            long imageDropped = _imageSaveQueue.DroppedCount;
            long imageFailed = _imageSaveQueue.FailedCount;
            long recordDropped = _recordQueue.DroppedCount;
            long recordFailed = _recordQueue.FailedCount;
            long imagePending = _imageSaveQueue.PendingCount;
            long recordPending = _recordQueue.PendingCount;
            DetectionRuntimeStatus detectionRuntime = _detectionService.RuntimeStatus;
            var syntheticErrors = BuildQueueErrors(
                imagePending,
                _imageSaveQueue.Capacity,
                imageDropped,
                imageFailed,
                recordPending,
                _recordQueue.Capacity,
                recordDropped,
                recordFailed);
            var allErrors = errors.Concat(syntheticErrors).TakeLast(MaxRecentErrors).ToArray();

            HealthLevel level = allErrors.Length > 0 ? HealthLevel.Warning : HealthLevel.Ok;
            if (!IsStorageWritable(_storageService.LogBasePath) || !IsStorageWritable(_storageService.ImageBasePath))
            {
                level = HealthLevel.Critical;
            }

            return new HealthSnapshot
            {
                SystemUptime = DateTimeOffset.Now - _startedAt,
                HealthLevel = level,
                InspectionState = string.IsNullOrWhiteSpace(lastInspectionId) ? "Idle" : "Completed",
                CameraStatus = _cameraService.IsOpen ? (_cameraService.IsGrabbing ? "Grabbing" : "Open") : "Closed",
                PlcStatus = _plcService.IsConnected ? $"Connected:{_plcService.ProtocolName}" : "Disconnected",
                ModelStatus = _detectionService.IsModelLoaded
                    ? $"Loaded:{_detectionService.CurrentModelName}:{detectionRuntime.ExecutionProvider}"
                    : "NotLoaded",
                DetectionRuntime = detectionRuntime,
                RecipeStatus = "LegacyAppConfig",
                StorageStatus = level == HealthLevel.Critical ? "Error" : "Writable",
                DatabaseStatus = recordFailed > 0 ? "Warning" : "Ready",
                LastInspectionId = lastInspectionId,
                LastInspectionTotalMs = lastInspectionTotalMs,
                RecentInspectionP95Ms = Percentile(inspectionMs, 0.95),
                RecentInspectionP99Ms = Percentile(inspectionMs, 0.99),
                ImageQueueLength = imagePending,
                ImageQueueCapacity = _imageSaveQueue.Capacity,
                ImageQueueDroppedCount = imageDropped,
                ImageQueueFailedCount = imageFailed,
                RecordQueueLength = recordPending,
                RecordQueueCapacity = _recordQueue.Capacity,
                RecordQueueDroppedCount = recordDropped,
                RecordQueueFailedCount = recordFailed,
                FreeDiskGb = GetFreeDiskGb(_storageService.ImageBasePath),
                MemoryMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024,
                RecentErrors = allErrors,
                UpdatedAt = DateTimeOffset.Now
            };
        }

        private static IReadOnlyList<HealthError> BuildQueueErrors(
            long imagePending,
            int imageCapacity,
            long imageDropped,
            long imageFailed,
            long recordPending,
            int recordCapacity,
            long recordDropped,
            long recordFailed)
        {
            var errors = new List<HealthError>();
            AddIfNearCapacity(errors, imagePending, imageCapacity, "ImageSaveQueue", "图像保存队列超过75%，建议检查磁盘写入速度或降低触发频率");
            AddIfPositive(errors, imageDropped, "ImageSaveQueue", $"图像保存队列丢弃累计: {imageDropped}");
            AddIfPositive(errors, imageFailed, "ImageSaveQueue", $"图像保存失败累计: {imageFailed}");
            AddIfNearCapacity(errors, recordPending, recordCapacity, "DetectionRecordQueue", "数据库记录队列超过75%，建议检查数据库文件和存储目录");
            AddIfPositive(errors, recordDropped, "DetectionRecordQueue", $"数据库记录队列丢弃累计: {recordDropped}");
            AddIfPositive(errors, recordFailed, "DetectionRecordQueue", $"数据库记录保存失败累计: {recordFailed}");
            return errors;
        }

        private static void AddIfNearCapacity(List<HealthError> errors, long pending, int capacity, string source, string message)
        {
            if (capacity <= 0 || pending * 4L < capacity * 3L)
            {
                return;
            }

            errors.Add(new HealthError
            {
                Timestamp = DateTimeOffset.Now,
                Source = source,
                Message = $"{message} ({pending}/{capacity})"
            });
        }

        private static void AddIfPositive(List<HealthError> errors, long value, string source, string message)
        {
            if (value <= 0)
            {
                return;
            }

            errors.Add(new HealthError
            {
                Timestamp = DateTimeOffset.Now,
                Source = source,
                Message = message
            });
        }

        private static bool IsStorageWritable(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                string probe = Path.Combine(path, $".health-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double GetFreeDiskGb(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(root))
                {
                    return 0;
                }

                var drive = new DriveInfo(root);
                return Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 2);
            }
            catch
            {
                return 0;
            }
        }

        private static long Percentile(long[] values, double percentile)
        {
            if (values.Length == 0)
            {
                return 0;
            }

            Array.Sort(values);
            int index = (int)Math.Ceiling(percentile * values.Length) - 1;
            index = Math.Clamp(index, 0, values.Length - 1);
            return values[index];
        }
    }
}
