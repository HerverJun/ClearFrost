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
using ClearFrost.Models;

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

    public sealed class HealthTrend
    {
        public string Name { get; init; } = string.Empty;
        public string Unit { get; init; } = string.Empty;
        public double CurrentValue { get; init; }
        public double BaselineValue { get; init; }
        public double Change { get; init; }
        public int SampleDays { get; init; }
        public HealthLevel Level { get; init; } = HealthLevel.Ok;
        public string Message { get; init; } = string.Empty;
    }

    public sealed class MaintenanceAdvice
    {
        public HealthLevel Level { get; init; } = HealthLevel.Ok;
        public string Source { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
    }

    public sealed class InspectionCycleSlaOptions
    {
        public bool Enabled { get; init; } = true;
        public int WarningMs { get; init; } = 1500;
        public int CriticalMs { get; init; } = 3000;
        public int MinSamples { get; init; } = 10;
    }

    public sealed class QualityYieldSlaOptions
    {
        public bool Enabled { get; init; } = true;
        public double WarningPercent { get; init; } = 95.0;
        public double CriticalPercent { get; init; } = 90.0;
        public int MinSamples { get; init; } = 30;
    }

    public sealed class ConsecutiveNgSlaOptions
    {
        public bool Enabled { get; init; } = true;
        public int WarningCount { get; init; } = 3;
        public int CriticalCount { get; init; } = 5;
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
        public int RecentInspectionSampleCount { get; init; }
        public int InspectionCycleWarningMs { get; init; }
        public int InspectionCycleCriticalMs { get; init; }
        public int InspectionCycleMinSamples { get; init; }
        public int RecentInspectionQualifiedCount { get; init; }
        public int RecentInspectionUnqualifiedCount { get; init; }
        public double RecentInspectionQualifiedRatePercent { get; init; }
        public double QualityYieldWarningPercent { get; init; }
        public double QualityYieldCriticalPercent { get; init; }
        public int QualityYieldMinSamples { get; init; }
        public int ConsecutiveNgCount { get; init; }
        public int ConsecutiveNgWarningCount { get; init; }
        public int ConsecutiveNgCriticalCount { get; init; }
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
        public IReadOnlyList<HealthTrend> Trends { get; init; } = Array.Empty<HealthTrend>();
        public IReadOnlyList<MaintenanceAdvice> MaintenanceAdvices { get; init; } = Array.Empty<MaintenanceAdvice>();
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    }

    internal sealed class HealthMonitor
    {
        private const int MaxRecentErrors = 50;
        private const int MaxRecentInspectionSamples = 200;
        private const int PlcWriteBackWarningCount = 2;
        private const int PlcWriteBackCriticalCount = 3;
        private static readonly TimeSpan DefaultDiskProbeCacheTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan HardwareErrorAdviceWindow = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan PlcWriteBackAdviceWindow = TimeSpan.FromMinutes(10);

        private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
        private readonly ICameraService _cameraService;
        private readonly IPlcService _plcService;
        private readonly IDetectionService _detectionService;
        private readonly IStorageService _storageService;
        private readonly IStatisticsService? _statisticsService;
        private readonly Func<InspectionCycleSlaOptions>? _inspectionCycleSlaProvider;
        private readonly Func<QualityYieldSlaOptions>? _qualityYieldSlaProvider;
        private readonly Func<ConsecutiveNgSlaOptions>? _consecutiveNgSlaProvider;
        private readonly ImageSaveQueue _imageSaveQueue;
        private readonly DetectionRecordQueue _recordQueue;
        private readonly object _syncRoot = new object();
        private readonly object _diskProbeLock = new object();
        private readonly TimeSpan _diskProbeCacheTtl;
        private readonly Queue<HealthError> _recentErrors = new Queue<HealthError>();
        private readonly Queue<long> _recentInspectionMs = new Queue<long>();
        private readonly Queue<bool> _recentInspectionQualified = new Queue<bool>();
        private DiskProbeSnapshot _diskProbeSnapshot;
        private bool _hasDiskProbeSnapshot;
        private string _lastInspectionId = string.Empty;
        private long _lastInspectionTotalMs;

        public HealthMonitor(
            ICameraService cameraService,
            IPlcService plcService,
            IDetectionService detectionService,
            IStorageService storageService,
            ImageSaveQueue imageSaveQueue,
            DetectionRecordQueue recordQueue,
            IStatisticsService? statisticsService = null,
            Func<InspectionCycleSlaOptions>? inspectionCycleSlaProvider = null,
            Func<QualityYieldSlaOptions>? qualityYieldSlaProvider = null,
            Func<ConsecutiveNgSlaOptions>? consecutiveNgSlaProvider = null,
            TimeSpan? diskProbeCacheTtl = null)
        {
            _cameraService = cameraService ?? throw new ArgumentNullException(nameof(cameraService));
            _plcService = plcService ?? throw new ArgumentNullException(nameof(plcService));
            _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _statisticsService = statisticsService;
            _inspectionCycleSlaProvider = inspectionCycleSlaProvider;
            _qualityYieldSlaProvider = qualityYieldSlaProvider;
            _consecutiveNgSlaProvider = consecutiveNgSlaProvider;
            _imageSaveQueue = imageSaveQueue ?? throw new ArgumentNullException(nameof(imageSaveQueue));
            _recordQueue = recordQueue ?? throw new ArgumentNullException(nameof(recordQueue));
            _diskProbeCacheTtl = diskProbeCacheTtl ?? DefaultDiskProbeCacheTtl;
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

                if (context.IsQualified.HasValue)
                {
                    _recentInspectionQualified.Enqueue(context.IsQualified.Value);
                    while (_recentInspectionQualified.Count > MaxRecentInspectionSamples)
                    {
                        _recentInspectionQualified.Dequeue();
                    }
                }
            }
        }

        public HealthSnapshot GetSnapshot()
        {
            HealthError[] errors;
            long[] inspectionMs;
            bool[] inspectionQualified;
            string lastInspectionId;
            long lastInspectionTotalMs;

            lock (_syncRoot)
            {
                errors = _recentErrors.ToArray();
                inspectionMs = _recentInspectionMs.ToArray();
                inspectionQualified = _recentInspectionQualified.ToArray();
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
            DiskProbeSnapshot diskProbe = GetDiskProbeSnapshot();
            IReadOnlyList<HealthTrend> trends = BuildTrends();
            long recentP95Ms = Percentile(inspectionMs, 0.95);
            long recentP99Ms = Percentile(inspectionMs, 0.99);
            InspectionCycleSlaOptions cycleSla = GetInspectionCycleSlaOptions();
            QualityYieldSlaOptions yieldSla = GetQualityYieldSlaOptions();
            int recentQualifiedCount = inspectionQualified.Count(isQualified => isQualified);
            int recentUnqualifiedCount = inspectionQualified.Length - recentQualifiedCount;
            int consecutiveNgCount = CountTrailingNg(inspectionQualified);
            double recentQualifiedRate = inspectionQualified.Length == 0
                ? 100d
                : recentQualifiedCount * 100d / inspectionQualified.Length;
            ConsecutiveNgSlaOptions consecutiveNgSla = GetConsecutiveNgSlaOptions();
            IReadOnlyList<MaintenanceAdvice> maintenanceAdvices = BuildMaintenanceAdvices(
                trends,
                allErrors,
                diskProbe,
                recentP95Ms,
                recentP99Ms,
                inspectionMs.Length,
                cycleSla,
                recentQualifiedRate,
                recentQualifiedCount,
                recentUnqualifiedCount,
                inspectionQualified.Length,
                yieldSla,
                consecutiveNgCount,
                consecutiveNgSla,
                imagePending,
                _imageSaveQueue.Capacity,
                imageDropped,
                imageFailed,
                recordPending,
                _recordQueue.Capacity,
                recordDropped,
                recordFailed);

            HealthLevel level = allErrors.Length > 0 ? HealthLevel.Warning : HealthLevel.Ok;
            if (!diskProbe.LogWritable || !diskProbe.ImageWritable)
            {
                level = HealthLevel.Critical;
            }
            level = MaxHealthLevel(level, trends.Select(t => t.Level));
            level = MaxHealthLevel(level, maintenanceAdvices.Select(a => a.Level));

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
                RecentInspectionP95Ms = recentP95Ms,
                RecentInspectionP99Ms = recentP99Ms,
                RecentInspectionSampleCount = inspectionMs.Length,
                InspectionCycleWarningMs = cycleSla.WarningMs,
                InspectionCycleCriticalMs = cycleSla.CriticalMs,
                InspectionCycleMinSamples = cycleSla.MinSamples,
                RecentInspectionQualifiedCount = recentQualifiedCount,
                RecentInspectionUnqualifiedCount = recentUnqualifiedCount,
                RecentInspectionQualifiedRatePercent = Math.Round(recentQualifiedRate, 2),
                QualityYieldWarningPercent = yieldSla.WarningPercent,
                QualityYieldCriticalPercent = yieldSla.CriticalPercent,
                QualityYieldMinSamples = yieldSla.MinSamples,
                ConsecutiveNgCount = consecutiveNgCount,
                ConsecutiveNgWarningCount = consecutiveNgSla.WarningCount,
                ConsecutiveNgCriticalCount = consecutiveNgSla.CriticalCount,
                ImageQueueLength = imagePending,
                ImageQueueCapacity = _imageSaveQueue.Capacity,
                ImageQueueDroppedCount = imageDropped,
                ImageQueueFailedCount = imageFailed,
                RecordQueueLength = recordPending,
                RecordQueueCapacity = _recordQueue.Capacity,
                RecordQueueDroppedCount = recordDropped,
                RecordQueueFailedCount = recordFailed,
                FreeDiskGb = diskProbe.FreeDiskGb,
                MemoryMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024,
                RecentErrors = allErrors,
                Trends = trends,
                MaintenanceAdvices = maintenanceAdvices,
                UpdatedAt = DateTimeOffset.Now
            };
        }

        private DiskProbeSnapshot GetDiskProbeSnapshot()
        {
            string logPath = _storageService.LogBasePath;
            string imagePath = _storageService.ImageBasePath;
            DateTimeOffset now = DateTimeOffset.Now;

            lock (_diskProbeLock)
            {
                if (_hasDiskProbeSnapshot &&
                    _diskProbeCacheTtl > TimeSpan.Zero &&
                    now - _diskProbeSnapshot.UpdatedAt < _diskProbeCacheTtl &&
                    string.Equals(_diskProbeSnapshot.LogPath, logPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_diskProbeSnapshot.ImagePath, imagePath, StringComparison.OrdinalIgnoreCase))
                {
                    return _diskProbeSnapshot;
                }

                _diskProbeSnapshot = new DiskProbeSnapshot(
                    logPath,
                    imagePath,
                    IsStorageWritable(logPath),
                    IsStorageWritable(imagePath),
                    GetFreeDiskGb(imagePath),
                    now);
                _hasDiskProbeSnapshot = true;
                return _diskProbeSnapshot;
            }
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

        private IReadOnlyList<HealthTrend> BuildTrends()
        {
            if (_statisticsService == null)
            {
                return Array.Empty<HealthTrend>();
            }

            try
            {
                var points = BuildProductionPoints(_statisticsService);
                if (points.Count < 3)
                {
                    return Array.Empty<HealthTrend>();
                }

                DailyProductionPoint latest = points[^1];
                var baseline = points
                    .Take(points.Count - 1)
                    .Where(p => p.TotalCount > 0)
                    .TakeLast(6)
                    .ToArray();
                int baselineTotal = baseline.Sum(p => p.TotalCount);
                int baselineQualified = baseline.Sum(p => p.QualifiedCount);
                if (latest.TotalCount < 30 || baselineTotal < 30 || baseline.Length < 2)
                {
                    return Array.Empty<HealthTrend>();
                }

                double currentYield = latest.QualifiedPercentage;
                double baselineYield = baselineQualified * 100d / baselineTotal;
                double change = currentYield - baselineYield;
                HealthLevel level = change <= -10d
                    ? HealthLevel.Critical
                    : (change <= -5d ? HealthLevel.Warning : HealthLevel.Ok);
                string direction = change >= 0 ? "提升" : "下降";

                return new[]
                {
                    new HealthTrend
                    {
                        Name = "QualifiedRate",
                        Unit = "%",
                        CurrentValue = Math.Round(currentYield, 2),
                        BaselineValue = Math.Round(baselineYield, 2),
                        Change = Math.Round(change, 2),
                        SampleDays = baseline.Length + 1,
                        Level = level,
                        Message = $"最近良率较历史基线{direction} {Math.Abs(change):F1} 个百分点"
                    }
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HealthMonitor] BuildTrends failed: {ex.Message}");
                return Array.Empty<HealthTrend>();
            }
        }

        private IReadOnlyList<MaintenanceAdvice> BuildMaintenanceAdvices(
            IReadOnlyList<HealthTrend> trends,
            IReadOnlyList<HealthError> errors,
            DiskProbeSnapshot diskProbe,
            long recentP95Ms,
            long recentP99Ms,
            int inspectionSampleCount,
            InspectionCycleSlaOptions cycleSla,
            double recentQualifiedRate,
            int recentQualifiedCount,
            int recentUnqualifiedCount,
            int qualitySampleCount,
            QualityYieldSlaOptions yieldSla,
            int consecutiveNgCount,
            ConsecutiveNgSlaOptions consecutiveNgSla,
            long imagePending,
            int imageCapacity,
            long imageDropped,
            long imageFailed,
            long recordPending,
            int recordCapacity,
            long recordDropped,
            long recordFailed)
        {
            var advices = new List<MaintenanceAdvice>();

            foreach (HealthTrend trend in trends.Where(t => t.Level != HealthLevel.Ok))
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = trend.Level,
                    Source = trend.Name,
                    Message = trend.Message,
                    Action = "复核光源、镜头清洁、工装定位和最近模型/阈值变更；必要时抽取近 30 张 NG 图做复判"
                });
            }

            if (!diskProbe.LogWritable || !diskProbe.ImageWritable)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = HealthLevel.Critical,
                    Source = "Storage",
                    Message = "图像或日志目录不可写",
                    Action = "立即检查存储路径、磁盘权限和网络盘连接，恢复前不要继续生产"
                });
            }
            else if (diskProbe.FreeDiskGb > 0 && diskProbe.FreeDiskGb < 10)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = diskProbe.FreeDiskGb < 2 ? HealthLevel.Critical : HealthLevel.Warning,
                    Source = "Storage",
                    Message = $"剩余磁盘空间偏低: {diskProbe.FreeDiskGb:F2} GB",
                    Action = "清理历史图像/日志或切换到容量更高的数据盘，避免检测图保存失败"
                });
            }

            AddQueueAdvice(advices, "ImageSaveQueue", imagePending, imageCapacity, imageDropped, imageFailed);
            AddQueueAdvice(advices, "DetectionRecordQueue", recordPending, recordCapacity, recordDropped, recordFailed);
            AddInspectionCycleAdvice(advices, recentP95Ms, recentP99Ms, inspectionSampleCount, cycleSla);
            AddQualityYieldAdvice(
                advices,
                recentQualifiedRate,
                recentQualifiedCount,
                recentUnqualifiedCount,
                qualitySampleCount,
                yieldSla);
            AddConsecutiveNgAdvice(advices, consecutiveNgCount, consecutiveNgSla);
            DateTimeOffset now = DateTimeOffset.Now;
            AddPlcWriteBackAdvice(advices, errors, now);

            if (_cameraService.IsOpen && !_cameraService.IsGrabbing)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = HealthLevel.Warning,
                    Source = "Camera",
                    Message = "相机已打开但未处于采集状态",
                    Action = "先尝试刷新健康状态或执行一次检测；若仍未恢复，重新打开相机并检查触发模式"
                });
            }

            if (!_detectionService.IsModelLoaded)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = HealthLevel.Warning,
                    Source = "Model",
                    Message = "当前未加载检测模型",
                    Action = "在设置页选择已验收模型包，确认 ONNX 和 manifest 文件完整"
                });
            }

            int recentHardwareErrors = errors.Count(e =>
                IsHardwareSource(e.Source) &&
                now - e.Timestamp <= HardwareErrorAdviceWindow);
            if (recentHardwareErrors >= 3)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = HealthLevel.Warning,
                    Source = "Hardware",
                    Message = $"近 30 分钟硬件相关错误 {recentHardwareErrors} 次",
                    Action = "检查相机网线/USB、PLC 网络、触发信号和供电；导出诊断包留档"
                });
            }

            return advices
                .OrderByDescending(a => a.Level)
                .ThenBy(a => a.Source, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToArray();
        }

        private static List<DailyProductionPoint> BuildProductionPoints(IStatisticsService statisticsService)
        {
            var pointsByDate = new Dictionary<DateTime, DailyProductionPoint>();

            foreach (DailyStatisticsRecord record in statisticsService.History)
            {
                if (record.TotalCount <= 0 || !TryParseDate(record.Date, out DateTime date))
                {
                    continue;
                }

                pointsByDate[date] = new DailyProductionPoint(
                    date,
                    record.TotalCount,
                    record.QualifiedCount,
                    record.UnqualifiedCount);
            }

            StatisticsSnapshot current = statisticsService.Current;
            if (current.TotalCount > 0 && TryParseDate(current.CurrentDate, out DateTime currentDate))
            {
                pointsByDate[currentDate] = new DailyProductionPoint(
                    currentDate,
                    current.TotalCount,
                    current.QualifiedCount,
                    current.UnqualifiedCount);
            }

            return pointsByDate.Values
                .Where(p => p.TotalCount > 0)
                .OrderBy(p => p.Date)
                .ToList();
        }

        private static bool TryParseDate(string value, out DateTime date)
        {
            return DateTime.TryParse(value, out date);
        }

        private static void AddQueueAdvice(
            List<MaintenanceAdvice> advices,
            string source,
            long pending,
            int capacity,
            long dropped,
            long failed)
        {
            if (capacity > 0 && pending * 4L >= capacity * 3L)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = HealthLevel.Warning,
                    Source = source,
                    Message = $"队列压力偏高: {pending}/{capacity}",
                    Action = source == "ImageSaveQueue"
                        ? "检查图像保存磁盘写入速度，必要时降低触发频率或提高保存队列容量"
                        : "检查数据库文件和存储目录，确认记录写入没有被杀毒软件或网络盘阻塞"
                });
            }

            if (dropped > 0 || failed > 0)
            {
                advices.Add(new MaintenanceAdvice
                {
                    Level = HealthLevel.Warning,
                    Source = source,
                    Message = $"累计丢弃 {dropped}，失败 {failed}",
                    Action = "导出诊断包并检查对应队列错误日志，确认数据追溯没有断点"
                });
            }
        }

        private static void AddInspectionCycleAdvice(
            List<MaintenanceAdvice> advices,
            long recentP95Ms,
            long recentP99Ms,
            int inspectionSampleCount,
            InspectionCycleSlaOptions cycleSla)
        {
            if (!cycleSla.Enabled ||
                inspectionSampleCount < cycleSla.MinSamples ||
                cycleSla.WarningMs <= 0 ||
                cycleSla.CriticalMs <= 0)
            {
                return;
            }

            HealthLevel level = HealthLevel.Ok;
            int threshold = cycleSla.WarningMs;
            if (recentP99Ms >= cycleSla.CriticalMs || recentP95Ms >= cycleSla.CriticalMs)
            {
                level = HealthLevel.Critical;
                threshold = cycleSla.CriticalMs;
            }
            else if (recentP95Ms >= cycleSla.WarningMs)
            {
                level = HealthLevel.Warning;
            }

            if (level == HealthLevel.Ok)
            {
                return;
            }

            advices.Add(new MaintenanceAdvice
            {
                Level = level,
                Source = "InspectionCycle",
                Message = $"检测节拍超限: P95={recentP95Ms} ms, P99={recentP99Ms} ms, 阈值={threshold} ms",
                Action = "检查相机曝光/触发、模型推理设备、图像保存队列和数据库写入；必要时降低触发频率或切换更轻量模型"
            });
        }

        private InspectionCycleSlaOptions GetInspectionCycleSlaOptions()
        {
            InspectionCycleSlaOptions options;
            try
            {
                options = _inspectionCycleSlaProvider?.Invoke() ?? new InspectionCycleSlaOptions();
            }
            catch
            {
                options = new InspectionCycleSlaOptions();
            }

            int warningMs = Math.Clamp(options.WarningMs, 100, 600000);
            int criticalMs = Math.Clamp(options.CriticalMs, warningMs, 600000);
            int minSamples = Math.Clamp(options.MinSamples, 1, MaxRecentInspectionSamples);
            return new InspectionCycleSlaOptions
            {
                Enabled = options.Enabled,
                WarningMs = warningMs,
                CriticalMs = criticalMs,
                MinSamples = minSamples
            };
        }

        private static void AddQualityYieldAdvice(
            List<MaintenanceAdvice> advices,
            double recentQualifiedRate,
            int recentQualifiedCount,
            int recentUnqualifiedCount,
            int qualitySampleCount,
            QualityYieldSlaOptions yieldSla)
        {
            if (!yieldSla.Enabled ||
                qualitySampleCount < yieldSla.MinSamples ||
                yieldSla.WarningPercent <= 0)
            {
                return;
            }

            HealthLevel level = HealthLevel.Ok;
            double threshold = yieldSla.WarningPercent;
            if (recentQualifiedRate <= yieldSla.CriticalPercent)
            {
                level = HealthLevel.Critical;
                threshold = yieldSla.CriticalPercent;
            }
            else if (recentQualifiedRate <= yieldSla.WarningPercent)
            {
                level = HealthLevel.Warning;
            }

            if (level == HealthLevel.Ok)
            {
                return;
            }

            advices.Add(new MaintenanceAdvice
            {
                Level = level,
                Source = "QualityYield",
                Message = $"近期良率低于阈值: {recentQualifiedRate:F2}% <= {threshold:F2}% (OK={recentQualifiedCount}, NG={recentUnqualifiedCount})",
                Action = "暂停参数盲调，复核最近 NG 图、工装定位、光源稳定性、镜头污染和最近配置/模型变更"
            });
        }

        private QualityYieldSlaOptions GetQualityYieldSlaOptions()
        {
            QualityYieldSlaOptions options;
            try
            {
                options = _qualityYieldSlaProvider?.Invoke() ?? new QualityYieldSlaOptions();
            }
            catch
            {
                options = new QualityYieldSlaOptions();
            }

            double warningPercent = Math.Clamp(options.WarningPercent, 0.0, 100.0);
            double criticalPercent = Math.Clamp(options.CriticalPercent, 0.0, warningPercent);
            int minSamples = Math.Clamp(options.MinSamples, 1, MaxRecentInspectionSamples);
            return new QualityYieldSlaOptions
            {
                Enabled = options.Enabled,
                WarningPercent = warningPercent,
                CriticalPercent = criticalPercent,
                MinSamples = minSamples
            };
        }

        private static void AddConsecutiveNgAdvice(
            List<MaintenanceAdvice> advices,
            int consecutiveNgCount,
            ConsecutiveNgSlaOptions consecutiveNgSla)
        {
            if (!consecutiveNgSla.Enabled ||
                consecutiveNgCount < consecutiveNgSla.WarningCount)
            {
                return;
            }

            HealthLevel level = consecutiveNgCount >= consecutiveNgSla.CriticalCount
                ? HealthLevel.Critical
                : HealthLevel.Warning;
            int threshold = level == HealthLevel.Critical
                ? consecutiveNgSla.CriticalCount
                : consecutiveNgSla.WarningCount;

            advices.Add(new MaintenanceAdvice
            {
                Level = level,
                Source = "ConsecutiveNG",
                Message = $"连续 NG 已达 {consecutiveNgCount} 件，阈值={threshold} 件",
                Action = "立即复核最近 NG 图、来料批次、工装夹具、相机曝光和光源状态；必要时暂停自动触发"
            });
        }

        private static void AddPlcWriteBackAdvice(
            List<MaintenanceAdvice> advices,
            IReadOnlyList<HealthError> errors,
            DateTimeOffset now)
        {
            HealthError[] recentWriteBackErrors = errors
                .Where(error => IsPlcWriteBackError(error, now))
                .OrderBy(error => error.Timestamp)
                .ToArray();
            if (recentWriteBackErrors.Length < PlcWriteBackWarningCount)
            {
                return;
            }

            HealthLevel level = recentWriteBackErrors.Length >= PlcWriteBackCriticalCount
                ? HealthLevel.Critical
                : HealthLevel.Warning;
            string lastInspectionId = recentWriteBackErrors
                .LastOrDefault(error => !string.IsNullOrWhiteSpace(error.InspectionId))
                ?.InspectionId ?? string.Empty;
            string inspectionSuffix = string.IsNullOrWhiteSpace(lastInspectionId)
                ? string.Empty
                : $"，最近检测={lastInspectionId}";

            advices.Add(new MaintenanceAdvice
            {
                Level = level,
                Source = "PLC.WriteBack",
                Message = $"近 10 分钟 PLC 写回失败 {recentWriteBackErrors.Length} 次{inspectionSuffix}",
                Action = "确认 PLC 结果地址/握手地址、网络链路和 PLC 程序互锁；恢复前在手动模式验证 OK/NG 写回"
            });
        }

        private static bool IsPlcWriteBackError(HealthError error, DateTimeOffset now)
        {
            if (now - error.Timestamp > PlcWriteBackAdviceWindow ||
                !error.Source.Contains("PLC", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string message = error.Message ?? string.Empty;
            return message.Contains("写回", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("写入", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("Write", StringComparison.OrdinalIgnoreCase);
        }

        private ConsecutiveNgSlaOptions GetConsecutiveNgSlaOptions()
        {
            ConsecutiveNgSlaOptions options;
            try
            {
                options = _consecutiveNgSlaProvider?.Invoke() ?? new ConsecutiveNgSlaOptions();
            }
            catch
            {
                options = new ConsecutiveNgSlaOptions();
            }

            int warningCount = Math.Clamp(options.WarningCount, 1, MaxRecentInspectionSamples);
            int criticalCount = Math.Clamp(options.CriticalCount, warningCount, MaxRecentInspectionSamples);
            return new ConsecutiveNgSlaOptions
            {
                Enabled = options.Enabled,
                WarningCount = warningCount,
                CriticalCount = criticalCount
            };
        }

        private static int CountTrailingNg(IReadOnlyList<bool> qualifiedResults)
        {
            int count = 0;
            for (int i = qualifiedResults.Count - 1; i >= 0; i--)
            {
                if (qualifiedResults[i])
                {
                    break;
                }

                count++;
            }

            return count;
        }

        private static bool IsHardwareSource(string source)
        {
            return source.Contains("Camera", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("PLC", StringComparison.OrdinalIgnoreCase) ||
                   source.Contains("SerialTrigger", StringComparison.OrdinalIgnoreCase);
        }

        private static HealthLevel MaxHealthLevel(HealthLevel current, IEnumerable<HealthLevel> levels)
        {
            foreach (HealthLevel level in levels)
            {
                if (level > current)
                {
                    current = level;
                }
            }

            return current;
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

        private readonly record struct DiskProbeSnapshot(
            string LogPath,
            string ImagePath,
            bool LogWritable,
            bool ImageWritable,
            double FreeDiskGb,
            DateTimeOffset UpdatedAt);

        private readonly record struct DailyProductionPoint(
            DateTime Date,
            int TotalCount,
            int QualifiedCount,
            int UnqualifiedCount)
        {
            public double QualifiedPercentage => TotalCount > 0 ? QualifiedCount * 100d / TotalCount : 0d;
        }
    }
}
