using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace YOLO
{
    /// <summary>
    /// 单日统计记录
    /// </summary>
    public class DailyStatisticsRecord
    {
        public string Date { get; set; } = "";
        public int TotalCount { get; set; }
        public int QualifiedCount { get; set; }
        public int UnqualifiedCount { get; set; }

        public double QualifiedPercentage => TotalCount > 0 ? (double)QualifiedCount / TotalCount * 100 : 0;
    }

    /// <summary>
    /// 历史统计数据管理类，保留最近 7 天的记录
    /// </summary>
    public class StatisticsHistory
    {
        public List<DailyStatisticsRecord> Records { get; set; } = new List<DailyStatisticsRecord>();

        private string _savePath = "";
        private const int MaxDays = 7;

        /// <summary>
        /// 最后一次操作的错误信息
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastError { get; private set; }

        private static void LogError(string operation, Exception ex)
        {
            Debug.WriteLine($"[StatisticsHistory] {operation}: {ex.Message}");
        }

        /// <summary>
        /// 设置保存路径
        /// </summary>
        public void SetSavePath(string basePath)
        {
            string dir = Path.Combine(basePath, "System");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _savePath = Path.Combine(dir, "statistics_history.json");
        }

        /// <summary>
        /// 添加历史记录
        /// </summary>
        public void AddRecord(DailyStatisticsRecord record)
        {
            // 避免重复添加同一天的记录
            Records.RemoveAll(r => r.Date == record.Date);
            Records.Add(record);

            // 保持最多 7 天
            TrimToMaxDays();
            Save();
        }

        /// <summary>
        /// 保留最近 N 天的记录
        /// </summary>
        public void TrimToMaxDays()
        {
            if (Records.Count > MaxDays)
            {
                // 按日期排序，保留最新的 N 条
                Records = Records
                    .OrderByDescending(r => r.Date)
                    .Take(MaxDays)
                    .ToList();
            }
        }

        /// <summary>
        /// 从文件加载历史数据
        /// </summary>
        public static StatisticsHistory Load(string basePath)
        {
            string dir = Path.Combine(basePath, "System");
            string filePath = Path.Combine(dir, "statistics_history.json");

            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var history = JsonSerializer.Deserialize<StatisticsHistory>(json);
                    if (history != null)
                    {
                        history._savePath = filePath;
                        return history;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Load", ex);
            }

            var newHistory = new StatisticsHistory();
            newHistory.SetSavePath(basePath);
            return newHistory;
        }

        /// <summary>
        /// 保存历史数据到文件
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

        /// <summary>
        /// 获取用于前端显示的记录列表（按日期降序）
        /// </summary>
        public List<DailyStatisticsRecord> GetOrderedRecords()
        {
            return Records.OrderByDescending(r => r.Date).ToList();
        }
    }
}

