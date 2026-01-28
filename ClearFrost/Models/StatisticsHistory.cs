using System;
using ClearFrost.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ClearFrost.Models
{
    /// <summary>
    /// 
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
    /// 
    /// </summary>
    public class StatisticsHistory
    {
        public List<DailyStatisticsRecord> Records { get; set; } = new List<DailyStatisticsRecord>();

        private string _savePath = "";
        private const int MaxDays = 7;

        /// <summary>
        /// 
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastError { get; private set; }

        private static void LogError(string operation, Exception ex)
        {
            Debug.WriteLine($"[StatisticsHistory] {operation}: {ex.Message}");
        }

        /// <summary>
        /// 
        /// </summary>
        public void SetSavePath(string basePath)
        {
            string dir = Path.Combine(basePath, "System");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _savePath = Path.Combine(dir, "statistics_history.json");
        }

        /// <summary>
        /// 
        /// </summary>
        public void AddRecord(DailyStatisticsRecord record)
        {
            // 
            Records.RemoveAll(r => r.Date == record.Date);
            Records.Add(record);

            // 
            TrimToMaxDays();
            Save();
        }

        /// <summary>
        /// 
        /// </summary>
        public void TrimToMaxDays()
        {
            if (Records.Count > MaxDays)
            {
                // 
                Records = Records
                    .OrderByDescending(r => r.Date)
                    .Take(MaxDays)
                    .ToList();
            }
        }

        /// <summary>
        /// 
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
        /// 
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
        /// 
        /// </summary>
        public List<DailyStatisticsRecord> GetOrderedRecords()
        {
            return Records.OrderByDescending(r => r.Date).ToList();
        }

        /// <summary>
        /// 清空所有历史记录
        /// </summary>
        public void ClearAll()
        {
            Records.Clear();
            Save();
        }
    }
}



