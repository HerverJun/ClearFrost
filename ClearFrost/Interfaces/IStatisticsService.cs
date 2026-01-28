using ClearFrost.Models;
// ============================================================================
// 文件名: IStatisticsService.cs
// 描述:   统计服务接口
//
// 功能:
//   - 检测统计功能
//   - 历史记录管理
//   - 跨日自动重置
// ============================================================================

using System;
using System.Collections.Generic;

namespace ClearFrost.Interfaces
{
    /// <summary>
    /// 统计数据快照
    /// </summary>
    public class StatisticsSnapshot
    {
        public int TotalCount { get; set; }
        public int QualifiedCount { get; set; }
        public int UnqualifiedCount { get; set; }
        public double QualifiedPercentage { get; set; }
        public string CurrentDate { get; set; } = "";
    }

    /// <summary>
    /// 统计服务接口
    /// </summary>
    public interface IStatisticsService : IDisposable
    {
        #region 事件

        /// <summary>
        /// 统计数据更新事件
        /// </summary>
        event Action<StatisticsSnapshot>? StatisticsUpdated;

        /// <summary>
        /// 跨日重置事件
        /// </summary>
        event Action? DayReset;

        #endregion

        #region 属性

        /// <summary>
        /// 当前统计数据快照
        /// </summary>
        StatisticsSnapshot Current { get; }

        /// <summary>
        /// 今日合格数
        /// </summary>
        int TodayQualified { get; }

        /// <summary>
        /// 今日不合格数
        /// </summary>
        int TodayUnqualified { get; }

        /// <summary>
        /// 今日总计检测数
        /// </summary>
        int TodayTotal { get; }

        /// <summary>
        /// 历史记录 (最近7天)
        /// </summary>
        IReadOnlyList<DailyStatisticsRecord> History { get; }

        #endregion

        #region 方法

        /// <summary>
        /// 记录检测结果
        /// </summary>
        void RecordDetection(bool isQualified);

        /// <summary>
        /// 重置今日统计
        /// </summary>
        void ResetToday();

        /// <summary>
        /// 检查并处理跨日
        /// </summary>
        /// <returns>是否发生了跨日重置</returns>
        bool CheckAndResetForNewDay();

        /// <summary>
        /// 保存所有数据
        /// </summary>
        void SaveAll();

        /// <summary>
        /// 清空历史记录
        /// </summary>
        void ClearHistory();

        /// <summary>
        /// 加载所有数据
        /// </summary>
        void LoadAll();

        #endregion
    }
}
