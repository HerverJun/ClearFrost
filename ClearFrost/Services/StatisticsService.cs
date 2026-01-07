// ============================================================================
// 文件名: StatisticsService.cs
// 描述:   统计服务实现
//
// 功能:
//   - 封装 DetectionStatistics 和 StatisticsHistory
//   - 提供统一的统计管理 API
//   - 事件驱动的 UI 更新
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using YOLO.Interfaces;

namespace YOLO.Services
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
        private bool _disposed;
        private System.Timers.Timer _checkDayTimer;

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

        public IReadOnlyList<DailyStatisticsRecord> History =>
            _statisticsHistory.GetOrderedRecords().AsReadOnly();

        #endregion

        #region 构造函数

        public StatisticsService(string basePath)
        {
            _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));

            // 加载现有数据
            _detectionStats = DetectionStatistics.Load(_basePath);
            _statisticsHistory = StatisticsHistory.Load(_basePath);

            // 初始化定时器，每60秒检查一次跨日
            _checkDayTimer = new System.Timers.Timer(60000);
            _checkDayTimer.Elapsed += (s, e) => CheckAndResetForNewDay();
            _checkDayTimer.AutoReset = true;
            _checkDayTimer.Start();

            Debug.WriteLine($"[StatisticsService] 初始化完成 - 今日: {TodayTotal} 条, 历史: {_statisticsHistory.Records.Count} 天");
        }

        #endregion

        #region 记录方法

        public void RecordDetection(bool isQualified)
        {
            // 记录前先检查是否跨日，防止数据记入错误日期
            CheckAndResetForNewDay();

            _detectionStats.AddRecord(isQualified);

            // 触发更新事件
            StatisticsUpdated?.Invoke(Current);

            Debug.WriteLine($"[StatisticsService] 记录检测: {(isQualified ? "合格" : "不合格")} (总计: {TodayTotal})");
        }

        public void ResetToday()
        {
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
                DayReset?.Invoke();
                StatisticsUpdated?.Invoke(Current);
                Debug.WriteLine("[StatisticsService] 检测到跨日，已自动重置");
            }

            return wasReset;
        }

        #endregion

        #region 持久化

        public void SaveAll()
        {
            try
            {
                _detectionStats.Save();
                _statisticsHistory.Save();
                Debug.WriteLine("[StatisticsService] 所有数据已保存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StatisticsService] 保存失败: {ex.Message}");
            }
        }

        public void LoadAll()
        {
            try
            {
                _detectionStats = DetectionStatistics.Load(_basePath);
                _statisticsHistory = StatisticsHistory.Load(_basePath);
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
        /// 获取底层 DetectionStatistics (向后兼容)
        /// </summary>
        public DetectionStatistics GetDetectionStats() => _detectionStats;

        /// <summary>
        /// 获取底层 StatisticsHistory (向后兼容)
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

            // 保存数据
            SaveAll();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
