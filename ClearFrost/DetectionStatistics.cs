using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace YOLO
{
    /// <summary>
    /// 检测统计数据类，支持持久化存储和跨日重置
    /// </summary>
    public class DetectionStatistics
    {
        public int TotalCount { get; set; }
        public int QualifiedCount { get; set; }
        public int UnqualifiedCount { get; set; }

        /// <summary>
        /// 当前数据对应的日期 (yyyy-MM-dd 格式)
        /// </summary>
        public string CurrentDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

        // 计算合格率
        public double QualifiedPercentage => TotalCount > 0 ? (double)QualifiedCount / TotalCount * 100 : 0;

        // 计算不合格率
        public double UnqualifiedPercentage => TotalCount > 0 ? (double)UnqualifiedCount / TotalCount * 100 : 0;

        // 持久化文件路径 (由外部设置)
        private string _savePath = "";

        /// <summary>
        /// 最后一次操作的错误信息
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastError { get; private set; }

        private static void LogError(string operation, Exception ex)
        {
            Debug.WriteLine($"[DetectionStatistics] {operation}: {ex.Message}");
        }

        /// <summary>
        /// 设置保存路径
        /// </summary>
        public void SetSavePath(string basePath)
        {
            string dir = Path.Combine(basePath, "System");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _savePath = Path.Combine(dir, "statistics.json");
        }

        /// <summary>
        /// 添加检测结果
        /// </summary>
        public void AddRecord(bool isQualified)
        {
            TotalCount++;
            if (isQualified)
                QualifiedCount++;
            else
                UnqualifiedCount++;

            // 每次检测后自动保存
            Save();
        }

        /// <summary>
        /// 重置统计数据
        /// </summary>
        public void Reset()
        {
            TotalCount = 0;
            QualifiedCount = 0;
            UnqualifiedCount = 0;
            CurrentDate = DateTime.Now.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 检测是否跨日，如果是则保存历史并重置
        /// </summary>
        /// <param name="history">历史记录对象</param>
        /// <returns>true 表示发生了跨日重置</returns>
        public bool CheckAndResetForNewDay(StatisticsHistory history)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (CurrentDate != today)
            {
                // 保存昨日数据到历史
                if (TotalCount > 0)
                {
                    history.AddRecord(new DailyStatisticsRecord
                    {
                        Date = CurrentDate,
                        TotalCount = TotalCount,
                        QualifiedCount = QualifiedCount,
                        UnqualifiedCount = UnqualifiedCount
                    });
                }

                // 重置当日数据
                Reset();
                Save();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 从文件加载统计数据
        /// </summary>
        public static DetectionStatistics Load(string basePath)
        {
            string dir = Path.Combine(basePath, "System");
            string filePath = Path.Combine(dir, "statistics.json");

            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var stats = JsonSerializer.Deserialize<DetectionStatistics>(json);
                    if (stats != null)
                    {
                        stats._savePath = filePath;
                        return stats;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Load", ex);
            }

            // 返回新实例
            var newStats = new DetectionStatistics();
            newStats.SetSavePath(basePath);
            return newStats;
        }

        /// <summary>
        /// 保存统计数据到文件
        /// </summary>
        public bool Save()
        {
            if (string.IsNullOrEmpty(_savePath)) return false;

            try
            {
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_savePath, json);
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogError("Save", ex);
                return false;
            }
        }
    }
}

