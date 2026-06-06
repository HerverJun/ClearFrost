// ============================================================================
// 文件名: StorageService.cs
// 描述:   存储服务实现
//
// 功能:
//   - 图像保存和管理
//   - 日志文件记录
//   - 历史数据自动清理
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    /// <summary>
    /// 存储服务实现
    /// </summary>
    public class StorageService : IStorageService
    {
        #region 私有字段

        private string _baseStoragePath;
        private bool _disposed;

        #endregion

        #region 属性

        public string ImageBasePath => Path.Combine(_baseStoragePath, "Images");
        public string LogBasePath => Path.Combine(_baseStoragePath, "Logs");
        public string SystemPath => Path.Combine(_baseStoragePath, "System");

        /// <summary>
        /// 启动日志路径
        /// </summary>
        public string StartupLogPath => Path.Combine(LogBasePath, "SoftwareStartLog.txt");

        #endregion

        #region 构造函数

        public StorageService(string? storagePath = null)
        {
            _baseStoragePath = ResolveStoragePath(storagePath);
            EnsureDirectoriesExist();
        }

        private string ResolveStoragePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return @"C:\GreeVisionData";
            }

            try
            {
                string? root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                {
                    return @"C:\GreeVisionData";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] Error checking drive: {ex.Message}");
                return @"C:\GreeVisionData";
            }

            return path;
        }

        #endregion

        #region 图像保存

        public void SaveDetectionImage(Bitmap bitmap, bool isQualified)
        {
            if (bitmap == null) return;

            try
            {
                DateTime now = DateTime.Now;
                string saveDir = Path.Combine(
                    ImageBasePath,
                    isQualified ? "Qualified" : "Unqualified",
                    now.ToString("yyyy年MM月dd日"),
                    now.ToString("HH"));

                if (!Directory.Exists(saveDir))
                    Directory.CreateDirectory(saveDir);

                string fileName = $"{(isQualified ? "PASS" : "FAIL")}_{now:HHmmssfff}.jpg";
                bitmap.Save(Path.Combine(saveDir, fileName), ImageFormat.Jpeg);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] SaveDetectionImage Error: {ex.Message}");
            }
        }

        public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified)
        {
            if (bitmap == null) return;

            Bitmap toSave = (Bitmap)bitmap.Clone();
            Task.Run(() =>
            {
                try
                {
                    SaveDetectionImage(toSave, isQualified);
                }
                finally
                {
                    toSave.Dispose();
                }
            });
        }

        #endregion

        #region 日志记录

        public void WriteDetectionLog(string content, bool isQualified)
        {
            try
            {
                DateTime now = DateTime.Now;
                string dir = Path.Combine(LogBasePath, "DetectionLogs", now.ToString("yyyy年MM月dd日"));

                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string fileName = $"{now:yyyyMMddHH}.txt";
                string fullContent = $"检测时间: {now}\r\n结果: {(isQualified ? "合格" : "不合格")}\r\n{content}\r\n";
                string filePath = Path.Combine(dir, fileName);

                using FileStream fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.SequentialScan);
                using StreamWriter writer = new StreamWriter(fs, Encoding.UTF8, 4096);
                writer.Write(fullContent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] WriteDetectionLog Error: {ex.Message}");
            }
        }

        public void WriteStartupLog(string action, string? serialNumber = null)
        {
            try
            {
                string msg = $"[{DateTime.Now}] {action} {(serialNumber != null ? "SN:" + serialNumber : "")}\n";
                File.AppendAllText(StartupLogPath, msg);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] WriteStartupLog Error: {ex.Message}");
            }
        }

        public void WriteErrorLog(string message)
        {
            try
            {
                DateTime now = DateTime.Now;
                string file = Path.Combine(LogBasePath, $"ErrorLog_{now:yyyyMMdd}.txt");
                string content = $"[{now:HH:mm:ss}] {message}\r\n";
                File.AppendAllText(file, content, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] WriteErrorLog Error: {ex.Message}");
            }
        }

        #endregion

        #region 数据清理

        public void CleanOldData(int retainDays)
        {
            try
            {
                DateTime limit = DateTime.Now.Date.AddDays(-retainDays);
                string[] types = { "Qualified", "Unqualified" };

                foreach (var type in types)
                {
                    string typePath = Path.Combine(ImageBasePath, type);
                    if (!Directory.Exists(typePath)) continue;

                    foreach (var dir in Directory.GetDirectories(typePath))
                    {
                        string dirName = Path.GetFileName(dir);

                        // 支持新旧两种日期格式
                        bool isLegacy = DateTime.TryParseExact(
                            dirName, "yyyyMMdd", null, DateTimeStyles.None, out DateTime fdLegacy);
                        bool isNew = DateTime.TryParseExact(
                            dirName, "yyyy年MM月dd日", null, DateTimeStyles.None, out DateTime fdNew);

                        DateTime? folderDate = isNew ? fdNew : (isLegacy ? fdLegacy : null);

                        if (folderDate.HasValue && folderDate.Value < limit)
                        {
                            Directory.Delete(dir, true);
                            Debug.WriteLine($"[StorageService] Deleted old folder: {dir}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteErrorLog($"CleanOldData Error: {ex.Message}");
            }
        }

        public double GetDiskFreeSpaceGb()
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(_baseStoragePath)) ?? string.Empty;
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

        public double PerformEmergencyCleanup()
        {
            double freeBefore = GetDiskFreeSpaceGb();
            Debug.WriteLine($"[StorageService] 紧急清理触发，当前剩余空间: {freeBefore} GB");
            const double thresholdGb = 1.0;
            DateTime today = DateTime.Now.Date;
            int checkInterval = 3; // 每删3个目录/文件才检查一次磁盘空间，减少IO

            try
            {
                // ========== 第1波：检测图片（Qualified + Unqualified）==========
                var imageDirs = new List<(DateTime date, string path)>();
                string[] types = { "Qualified", "Unqualified" };

                foreach (var type in types)
                {
                    string typePath = Path.Combine(ImageBasePath, type);
                    if (!Directory.Exists(typePath)) continue;

                    foreach (var dir in Directory.GetDirectories(typePath))
                    {
                        string dirName = Path.GetFileName(dir);
                        bool isLegacy = DateTime.TryParseExact(
                            dirName, "yyyyMMdd", null, DateTimeStyles.None, out DateTime fdLegacy);
                        bool isNew = DateTime.TryParseExact(
                            dirName, "yyyy年MM月dd日", null, DateTimeStyles.None, out DateTime fdNew);

                        DateTime? folderDate = isNew ? fdNew : (isLegacy ? fdLegacy : null);
                        if (folderDate.HasValue && folderDate.Value < today)
                        {
                            imageDirs.Add((folderDate.Value, dir));
                        }
                    }
                }

                imageDirs.Sort((a, b) => a.date.CompareTo(b.date));

                int deletedCount = 0;
                for (int i = 0; i < imageDirs.Count; i++)
                {
                    if (imageDirs[i].date >= today) continue;

                    try
                    {
                        Directory.Delete(imageDirs[i].path, true);
                        deletedCount++;
                    }
                    catch
                    {
                        // 静默跳过删除失败的目录，不记录日志以减少IO
                    }

                    if (deletedCount % checkInterval == 0 && GetDiskFreeSpaceGb() >= thresholdGb)
                        break;
                }

                // ========== 第2波：检测日志 ==========
                string detectionLogPath = Path.Combine(LogBasePath, "DetectionLogs");
                if (Directory.Exists(detectionLogPath) && GetDiskFreeSpaceGb() < thresholdGb)
                {
                    var logDirs = new List<(DateTime date, string path)>();

                    foreach (var dir in Directory.GetDirectories(detectionLogPath))
                    {
                        string dirName = Path.GetFileName(dir);
                        bool isLegacy = DateTime.TryParseExact(
                            dirName, "yyyyMMdd", null, DateTimeStyles.None, out DateTime fdLegacy);
                        bool isNew = DateTime.TryParseExact(
                            dirName, "yyyy年MM月dd日", null, DateTimeStyles.None, out DateTime fdNew);

                        DateTime? folderDate = isNew ? fdNew : (isLegacy ? fdLegacy : null);
                        if (folderDate.HasValue && folderDate.Value < today)
                        {
                            logDirs.Add((folderDate.Value, dir));
                        }
                    }

                    logDirs.Sort((a, b) => a.date.CompareTo(b.date));
                    deletedCount = 0;

                    for (int i = 0; i < logDirs.Count; i++)
                    {
                        if (logDirs[i].date >= today) continue;

                        try
                        {
                            Directory.Delete(logDirs[i].path, true);
                            deletedCount++;
                        }
                        catch
                        {
                            // 静默跳过
                        }

                        if (deletedCount % checkInterval == 0 && GetDiskFreeSpaceGb() >= thresholdGb)
                            break;
                    }
                }

                // ========== 第3波：错误日志 ==========
                if (Directory.Exists(LogBasePath) && GetDiskFreeSpaceGb() < thresholdGb)
                {
                    var errorLogs = new List<(DateTime date, string path)>();

                    foreach (var file in Directory.GetFiles(LogBasePath, "ErrorLog_*.txt"))
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file); // ErrorLog_yyyyMMdd
                        if (fileName.Length < 9) continue;

                        string datePart = fileName.Substring(9); // yyyyMMdd
                        if (DateTime.TryParseExact(
                            datePart, "yyyyMMdd", null, DateTimeStyles.None, out DateTime fd) && fd < today)
                        {
                            errorLogs.Add((fd, file));
                        }
                    }

                    errorLogs.Sort((a, b) => a.date.CompareTo(b.date));
                    deletedCount = 0;

                    for (int i = 0; i < errorLogs.Count; i++)
                    {
                        if (errorLogs[i].date >= today) continue;

                        try
                        {
                            File.Delete(errorLogs[i].path);
                            deletedCount++;
                        }
                        catch
                        {
                            // 静默跳过
                        }

                        if (deletedCount % checkInterval == 0 && GetDiskFreeSpaceGb() >= thresholdGb)
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                WriteErrorLog($"[EmergencyCleanup] 紧急清理异常: {ex.Message}");
            }

            double freeAfter = GetDiskFreeSpaceGb();
            if (freeAfter < 1.0)
            {
                WriteErrorLog($"[EmergencyCleanup] 警告：紧急清理后磁盘空间仍不足 1GB（当前 {freeAfter} GB），已无更多旧数据可删");
            }
            else if (freeAfter > freeBefore)
            {
                WriteErrorLog($"[EmergencyCleanup] 紧急清理完成，磁盘空间已恢复至 {freeAfter} GB");
            }

            return freeAfter;
        }

        public void EnsureDirectoriesExist()
        {
            try
            {
                if (!Directory.Exists(ImageBasePath))
                    Directory.CreateDirectory(ImageBasePath);

                if (!Directory.Exists(LogBasePath))
                    Directory.CreateDirectory(LogBasePath);

                if (!Directory.Exists(SystemPath))
                    Directory.CreateDirectory(SystemPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] EnsureDirectoriesExist Error: {ex.Message}");
            }
        }

        public void UpdateStoragePath(string storagePath)
        {
            string resolved = ResolveStoragePath(storagePath);
            if (string.Equals(_baseStoragePath, resolved, StringComparison.OrdinalIgnoreCase))
            {
                EnsureDirectoriesExist();
                return;
            }

            _baseStoragePath = resolved;
            EnsureDirectoriesExist();
            Debug.WriteLine($"[StorageService] 存储路径已刷新: {_baseStoragePath}");
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
