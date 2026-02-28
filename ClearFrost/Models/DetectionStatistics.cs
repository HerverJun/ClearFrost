using System;
using ClearFrost.Models;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClearFrost.Models
{
    /// <summary>
    /// 
    /// </summary>
    public class DetectionStatistics
    {
        public int TotalCount { get; set; }
        public int QualifiedCount { get; set; }
        public int UnqualifiedCount { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string CurrentDate { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

        // 
        public double QualifiedPercentage => TotalCount > 0 ? (double)QualifiedCount / TotalCount * 100 : 0;

        // 
        public double UnqualifiedPercentage => TotalCount > 0 ? (double)UnqualifiedCount / TotalCount * 100 : 0;

        // 
        private string _savePath = "";

        /// <summary>
        /// 
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string? LastError { get; private set; }

        private static void LogError(string operation, Exception ex)
        {
            Debug.WriteLine($"[DetectionStatistics] {operation}: {ex.Message}");
        }

        /// <summary>
        /// 
        /// </summary>
        public void SetSavePath(string basePath)
        {
            string dir = Path.Combine(basePath, "System");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            _savePath = Path.Combine(dir, "statistics.json");
        }

        /// <summary>
        /// 
        /// </summary>
        public void AddRecord(bool isQualified, bool persist = true)
        {
            TotalCount++;
            if (isQualified)
                QualifiedCount++;
            else
                UnqualifiedCount++;

            if (persist)
            {
                Save();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public void Reset()
        {
            TotalCount = 0;
            QualifiedCount = 0;
            UnqualifiedCount = 0;
            CurrentDate = DateTime.Now.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 
        /// </summary>
        /// 
        /// 
        public bool CheckAndResetForNewDay(StatisticsHistory history)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            if (CurrentDate != today)
            {
                // 
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

                // 
                Reset();
                Save();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 
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

            // 
            var newStats = new DetectionStatistics();
            newStats.SetSavePath(basePath);
            return newStats;
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
    }
}



