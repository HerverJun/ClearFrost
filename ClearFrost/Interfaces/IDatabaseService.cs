using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClearFrost.Interfaces
{
    /// <summary>
    /// 检测记录数据模型
    /// </summary>
    public class DetectionRecord
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsQualified { get; set; }
        public string TargetLabel { get; set; } = "";
        public int ExpectedCount { get; set; }
        public int ActualCount { get; set; }
        public int InferenceMs { get; set; }
        public string ModelName { get; set; } = "";
        public string CameraId { get; set; } = "";
        public string ResultJson { get; set; } = "";
    }

    /// <summary>
    /// 数据库服务接口
    /// </summary>
    public interface IDatabaseService : IDisposable
    {
        /// <summary>
        /// 初始化数据库（创建表等）
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 保存检测记录
        /// </summary>
        Task SaveDetectionRecordAsync(DetectionRecord record);

        /// <summary>
        /// 查询检测记录
        /// </summary>
        /// <param name="startDate">开始日期（可选）</param>
        /// <param name="endDate">结束日期（可选）</param>
        /// <param name="isQualified">是否合格（可选筛选）</param>
        /// <param name="limit">返回记录数限制</param>
        Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100);

        /// <summary>
        /// 获取统计数据
        /// </summary>
        Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date);

        /// <summary>
        /// 清理旧数据
        /// </summary>
        Task<int> CleanupOldRecordsAsync(int daysToKeep);
    }
}
