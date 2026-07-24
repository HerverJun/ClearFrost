using ClearFrost.Models;
// ============================================================================
// 文件名: StatisticsService.cs
// 描述:   统计服务实现
//
// 功能:
//   - 封装 DetectionStatistics 和 StatisticsHistory
//   - 提供统一的统计功能 API
//   - 事件驱动的 UI 更新
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    /// <summary>
    /// 统计服务实现
    /// </summary>
    public class StatisticsService : IStatisticsService
    {
        #region 私有字段

        private string _basePath;
        private DetectionStatistics _detectionStats;
        private StatisticsHistory _statisticsHistory;
        private System.Timers.Timer _checkDayTimer;
        private System.Timers.Timer _flushTimer;
        private bool _disposed;
        private readonly object _statsLock = new object();
        private int _pendingSaveCount;

        private const int SaveBatchSize = 20;
        private const int SaveFlushIntervalMs = 5000;

        #endregion

        #region 事件

        public event Action<StatisticsSnapshot>? StatisticsUpdated;
        public event Action? DayReset;

        #endregion

        #region 属性

        public StatisticsSnapshot Current
        {
            get
            {
                lock (_statsLock)
                {
                    return CreateSnapshotUnsafe();
                }
            }
        }

        public int TodayQualified
        {
            get
            {
                lock (_statsLock)
                {
                    return _detectionStats.QualifiedCount;
                }
            }
        }

        public int TodayUnqualified
        {
            get
            {
                lock (_statsLock)
                {
                    return _detectionStats.UnqualifiedCount;
                }
            }
        }

        public int TodayTotal
        {
            get
            {
                lock (_statsLock)
                {
                    return _detectionStats.TotalCount;
                }
            }
        }

        public IReadOnlyList<DailyStatisticsRecord> History
        {
            get
            {
                lock (_statsLock)
                {
                    var records = _statisticsHistory.GetOrderedRecords();
                    return records.Select(r => new DailyStatisticsRecord
                    {
                        Date = r.Date,
                        TotalCount = r.TotalCount,
                        QualifiedCount = r.QualifiedCount,
                        UnqualifiedCount = r.UnqualifiedCount
                    }).ToList().AsReadOnly();
                }
            }
        }

        #endregion

        #region 构造函数

        public StatisticsService(string basePath)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

            // 加载持久化数据
            _detectionStats = DetectionStatistics.Load(_basePath);
            _statisticsHistory = StatisticsHistory.Load(_basePath);

            // 定时检查日期变更 (每10分钟)
            _checkDayTimer = new System.Timers.Timer(600000);
            _checkDayTimer.Elapsed += (s, e) => CheckAndResetForNewDay();
            _checkDayTimer.AutoReset = true;
            _checkDayTimer.Start();

            // 批量落盘：定时刷新，避免每次检测都触发磁盘写入
            _flushTimer = new System.Timers.Timer(SaveFlushIntervalMs);
            _flushTimer.Elapsed += (s, e) => FlushPendingStatistics();
            _flushTimer.AutoReset = true;
            _flushTimer.Start();

            Debug.WriteLine($"[StatisticsService] 初始化完成 - 今日: {TodayTotal} 件, 历史: {_statisticsHistory.Records.Count} 天");
        }

        #endregion

        #region 记录功能

        public void RecordDetection(bool isQualified)
        {
            bool wasReset;
            int pending;
            StatisticsSnapshot snapshot;

            lock (_statsLock)
            {
                wasReset = _detectionStats.CheckAndResetForNewDay(_statisticsHistory);
                if (wasReset)
                {
                    _pendingSaveCount = 0;
                }

                _detectionStats.AddRecord(isQualified, persist: false);
                _pendingSaveCount++;
                pending = _pendingSaveCount;
                snapshot = CreateSnapshotUnsafe();
            }

            if (pending >= SaveBatchSize)
            {
                FlushPendingStatistics();
            }

            if (wasReset)
            {
                DayReset?.Invoke();
                Debug.WriteLine("[StatisticsService] 检测到跨日，已自动重置");
            }

            StatisticsUpdated?.Invoke(snapshot);
            Debug.WriteLine($"[StatisticsService] 记录检测: {(isQualified ? "合格" : "不合格")} (总计: {snapshot.TotalCount})");
        }

        public void ResetToday()
        {
            FlushPendingStatistics();
            StatisticsSnapshot snapshot;
            lock (_statsLock)
            {
                _detectionStats.Reset();
                _detectionStats.Save();
                snapshot = CreateSnapshotUnsafe();
            }

            StatisticsUpdated?.Invoke(snapshot);
            Debug.WriteLine("[StatisticsService] 今日统计已重置");
        }

        public bool CheckAndResetForNewDay()
        {
            bool wasReset;
            StatisticsSnapshot snapshot;
            lock (_statsLock)
            {
                wasReset = _detectionStats.CheckAndResetForNewDay(_statisticsHistory);
                if (wasReset)
                {
                    _pendingSaveCount = 0;
                }

                snapshot = CreateSnapshotUnsafe();
            }

            if (wasReset)
            {
                DayReset?.Invoke();
                StatisticsUpdated?.Invoke(snapshot);
                Debug.WriteLine("[StatisticsService] 检测到跨日，已自动重置");
            }

            return wasReset;
        }

        #endregion

        #region 持久化

        private void FlushPendingStatistics()
        {
            int pending;
            lock (_statsLock)
            {
                pending = _pendingSaveCount;
                if (pending <= 0)
                {
                    return;
                }

                _pendingSaveCount = 0;

                try
                {
                    _detectionStats.Save();
                    Debug.WriteLine($"[StatisticsService] 批量落盘完成: {pending} 条");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StatisticsService] 批量落盘失败: {ex.Message}");
                }
            }
        }

        public void SaveAll()
        {
            try
            {
                FlushPendingStatistics();
                lock (_statsLock)
                {
                    _detectionStats.Save();
                    _statisticsHistory.Save();
                }

                Debug.WriteLine("[StatisticsService] 所有数据已保存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StatisticsService] 保存失败: {ex.Message}");
            }
        }

        public void ClearHistory()
        {
            StatisticsSnapshot snapshot;
            lock (_statsLock)
            {
                _statisticsHistory.ClearAll();
                snapshot = CreateSnapshotUnsafe();
            }

            StatisticsUpdated?.Invoke(snapshot);
            Debug.WriteLine("[StatisticsService] 历史记录已清空");
        }

        public void LoadAll()
        {
            try
            {
                StatisticsSnapshot snapshot;
                lock (_statsLock)
                {
                    _detectionStats = DetectionStatistics.Load(_basePath);
                    _statisticsHistory = StatisticsHistory.Load(_basePath);
                    _pendingSaveCount = 0;
                    snapshot = CreateSnapshotUnsafe();
                }

                StatisticsUpdated?.Invoke(snapshot);
                Debug.WriteLine("[StatisticsService] 所有数据已加载");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StatisticsService] 加载失败: {ex.Message}");
            }
        }

        public void UpdateStoragePath(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
            {
                throw new ArgumentException("统计存储路径不能为空", nameof(basePath));
            }

            FlushPendingStatistics();

            StatisticsSnapshot snapshot;
            lock (_statsLock)
            {
                _basePath = basePath;
                _detectionStats = DetectionStatistics.Load(_basePath);
                _statisticsHistory = StatisticsHistory.Load(_basePath);
                _pendingSaveCount = 0;
                snapshot = CreateSnapshotUnsafe();
            }

            StatisticsUpdated?.Invoke(snapshot);
            Debug.WriteLine($"[StatisticsService] 存储路径已刷新: {_basePath}");
        }

        #endregion

        private StatisticsSnapshot CreateSnapshotUnsafe()
        {
            return new StatisticsSnapshot
            {
                TotalCount = _detectionStats.TotalCount,
                QualifiedCount = _detectionStats.QualifiedCount,
                UnqualifiedCount = _detectionStats.UnqualifiedCount,
                QualifiedPercentage = _detectionStats.QualifiedPercentage,
                CurrentDate = _detectionStats.CurrentDate
            };
        }

        #region 兼容性方法

        /// <summary>
        /// 获取统计历史及汇总数据（供前端图表使用）
        /// </summary>
        public (StatisticsHistory history, DetectionStatistics stats) GetStatisticsData()
        {
            lock (_statsLock)
            {
                return (_statisticsHistory, _detectionStats);
            }
        }

        /// <summary>
        /// 获取底层 DetectionStatistics (供兼容)
        /// </summary>
        public DetectionStatistics GetDetectionStats() => _detectionStats;

        /// <summary>
        /// 获取底层 StatisticsHistory (供兼容)
        /// </summary>
        public StatisticsHistory GetStatisticsHistory() => _statisticsHistory;

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 停止定时器
            if (_checkDayTimer != null)
            {
                _checkDayTimer.Stop();
                _checkDayTimer.Dispose();
            }
            if (_flushTimer != null)
            {
                _flushTimer.Stop();
                _flushTimer.Dispose();
            }

            // 保存数据
            SaveAll();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
