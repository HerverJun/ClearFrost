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
using System.Threading;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    /// <summary>
    /// 统计服务实现
    /// </summary>
    public class StatisticsService : IStatisticsService
    {
        #region 私有字段

        private readonly string _basePath;
        private DetectionStatistics _detectionStats;
        private StatisticsHistory _statisticsHistory;
        private System.Timers.Timer _checkDayTimer;
        private System.Timers.Timer _flushTimer;
        private bool _disposed;
        private readonly object _saveLock = new object();
        private int _pendingSaveCount;

        private const int SaveBatchSize = 20;
        private const int SaveFlushIntervalMs = 5000;

        #endregion

        #region 事件

        public event Action<StatisticsSnapshot>? StatisticsUpdated;
        public event Action? DayReset;

        #endregion

        #region 属性

        public StatisticsSnapshot Current => new StatisticsSnapshot
        {
            TotalCount = _detectionStats.TotalCount,
            QualifiedCount = _detectionStats.QualifiedCount,
            UnqualifiedCount = _detectionStats.UnqualifiedCount,
            QualifiedPercentage = _detectionStats.QualifiedPercentage,
            CurrentDate = _detectionStats.CurrentDate
        };

        public int TodayQualified => _detectionStats.QualifiedCount;
        public int TodayUnqualified => _detectionStats.UnqualifiedCount;
        public int TodayTotal => _detectionStats.TotalCount;

        public IReadOnlyList<DailyStatisticsRecord> History
        {
            get
            {
                var records = _statisticsHistory.GetOrderedRecords();
                return records.Select(r => new DailyStatisticsRecord
                {
                    Date = r.Date,
                    QualifiedCount = r.QualifiedCount,
                    UnqualifiedCount = r.UnqualifiedCount
                }).ToList().AsReadOnly();
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
            // 记录前先检查是否跨日，防止数据计入错误日期
            CheckAndResetForNewDay();

            _detectionStats.AddRecord(isQualified, persist: false);
            int pending = Interlocked.Increment(ref _pendingSaveCount);
            if (pending >= SaveBatchSize)
            {
                FlushPendingStatistics();
            }

            // 触发更新事件
            StatisticsUpdated?.Invoke(Current);

            Debug.WriteLine($"[StatisticsService] 记录检测: {(isQualified ? "合格" : "不合格")} (总计: {TodayTotal})");
        }

        public void ResetToday()
        {
            FlushPendingStatistics();
            _detectionStats.Reset();
            _detectionStats.Save();
            StatisticsUpdated?.Invoke(Current);
            Debug.WriteLine("[StatisticsService] 今日统计已重置");
        }

        public bool CheckAndResetForNewDay()
        {
            bool wasReset = _detectionStats.CheckAndResetForNewDay(_statisticsHistory);

            if (wasReset)
            {
                Interlocked.Exchange(ref _pendingSaveCount, 0);
                DayReset?.Invoke();
                StatisticsUpdated?.Invoke(Current);
                Debug.WriteLine("[StatisticsService] 检测到跨日，已自动重置");
            }

            return wasReset;
        }

        #endregion

        #region 持久化

        private void FlushPendingStatistics()
        {
            if (Interlocked.CompareExchange(ref _pendingSaveCount, 0, 0) <= 0)
            {
                return;
            }

            lock (_saveLock)
            {
                int pending = Interlocked.Exchange(ref _pendingSaveCount, 0);
                if (pending <= 0)
                {
                    return;
                }

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
                _detectionStats.Save();
                _statisticsHistory.Save();
                Debug.WriteLine("[StatisticsService] 所有数据已保存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StatisticsService] 保存失败: {ex.Message}");
            }
        }

        public void ClearHistory()
        {
            _statisticsHistory.ClearAll();
            StatisticsUpdated?.Invoke(Current);
            Debug.WriteLine("[StatisticsService] 历史记录已清空");
        }

        public void LoadAll()
        {
            try
            {
                _detectionStats = DetectionStatistics.Load(_basePath);
                _statisticsHistory = StatisticsHistory.Load(_basePath);
                Interlocked.Exchange(ref _pendingSaveCount, 0);
                StatisticsUpdated?.Invoke(Current);
                Debug.WriteLine("[StatisticsService] 所有数据已加载");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StatisticsService] 加载失败: {ex.Message}");
            }
        }

        #endregion

        #region 兼容性方法

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
