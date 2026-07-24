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
using System.Runtime.InteropServices;
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
        private static readonly StringComparison FileSystemPathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        #endregion

        #region 属性

        public string ImageBasePath => Path.Combine(_baseStoragePath, "Images");
        public string LogBasePath => Path.Combine(_baseStoragePath, "Logs");
        public string SystemPath => Path.Combine(_baseStoragePath, "System");
        private string AuditOutboxPath => Path.Combine(LogBasePath, "Outbox");
        private string DiagnosticPackagePath => Path.Combine(LogBasePath, "Diagnostics");
        private string HandoffReportPath => Path.Combine(LogBasePath, "HandoffReports");

        /// <summary>
        /// 已解析的存储根路径（进行驱动器/盘符校验后的实际路径）
        /// </summary>
        public string BaseStoragePath => _baseStoragePath;

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

                string fileName = $"{(isQualified ? "PASS" : "FAIL")}_{now:HHmmssfff}.jpg";
                string filePath = Path.Combine(saveDir, fileName);
                EnsureSafeFileTargetForWrite(filePath, "检测图像");
                bitmap.Save(filePath, ImageFormat.Jpeg);
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

                string fileName = $"{now:yyyyMMddHH}.txt";
                string fullContent = $"检测时间: {now}\r\n结果: {(isQualified ? "合格" : "不合格")}\r\n{content}\r\n";
                string filePath = Path.Combine(dir, fileName);

                EnsureSafeFileTargetForWrite(filePath, "检测日志");
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
                EnsureSafeFileTargetForWrite(StartupLogPath, "启动日志");
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
                EnsureSafeFileTargetForWrite(file, "错误日志");
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
                            if (TryDeleteCleanupDirectory(dir))
                            {
                                Debug.WriteLine($"[StorageService] Deleted old folder: {dir}");
                            }
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
                        if (TryDeleteCleanupDirectory(imageDirs[i].path))
                        {
                            deletedCount++;
                        }
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
                            if (TryDeleteCleanupDirectory(logDirs[i].path))
                            {
                                deletedCount++;
                            }
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
                            if (TryDeleteCleanupFile(errorLogs[i].path))
                            {
                                deletedCount++;
                            }
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
                EnsureSafeDirectoryForWrite(ImageBasePath, "图像目录");
                EnsureSafeDirectoryForWrite(LogBasePath, "日志目录");
                EnsureSafeDirectoryForWrite(SystemPath, "系统证据目录");
                EnsureSafeDirectoryForWrite(AuditOutboxPath, "审计发件箱目录");
                EnsureSafeDirectoryForWrite(DiagnosticPackagePath, "诊断包目录");
                EnsureSafeDirectoryForWrite(HandoffReportPath, "交接报告目录");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] EnsureDirectoriesExist Error: {ex.Message}");
            }
        }

        private bool TryDeleteCleanupDirectory(string path)
        {
            if (!IsSafeCleanupDirectoryPath(path))
            {
                Debug.WriteLine($"[StorageService] Skip unsafe/protected directory cleanup: {path}");
                return false;
            }

            Directory.Delete(path, true);
            return true;
        }

        private bool TryDeleteCleanupFile(string path)
        {
            if (!IsSafeCleanupFilePath(path))
            {
                Debug.WriteLine($"[StorageService] Skip unsafe/protected file cleanup: {path}");
                return false;
            }

            File.Delete(path);
            return true;
        }

        internal bool IsSafeCleanupDirectoryPath(string path)
        {
            if (!IsCleanupPathInsideStorage(path) || IsProtectedEvidencePath(path))
            {
                return false;
            }

            var info = new DirectoryInfo(path);
            return !DirectoryPathHasReparsePoint(path) &&
                   info.Exists &&
                   !HasReparsePoint(info) &&
                   !DirectoryTreeContainsReparsePoint(info);
        }

        internal bool IsSafeCleanupFilePath(string path)
        {
            if (!IsCleanupPathInsideStorage(path) || IsProtectedEvidencePath(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            string directory = Path.GetDirectoryName(info.FullName) ?? string.Empty;
            return !string.IsNullOrWhiteSpace(directory) &&
                   !DirectoryPathHasReparsePoint(directory) &&
                   info.Exists &&
                   !HasReparsePoint(info);
        }

        private bool IsCleanupPathInsideStorage(string path)
        {
            try
            {
                return IsSameOrChildPath(path, _baseStoragePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] Cleanup path root check failed, protected by default: {ex.Message}");
                return false;
            }
        }

        private bool IsProtectedEvidencePath(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string[] protectedRoots =
                {
                    SystemPath,
                    AuditOutboxPath,
                    DiagnosticPackagePath,
                    HandoffReportPath
                };

                return protectedRoots.Any(root => IsSameOrChildPath(fullPath, root));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StorageService] Cleanup path safety check failed, protected by default: {ex.Message}");
                return true;
            }
        }

        private static bool IsSameOrChildPath(string candidatePath, string rootPath)
        {
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, root, FileSystemPathComparison))
            {
                return true;
            }

            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, FileSystemPathComparison);
        }

        private static void EnsureSafeFileTargetForWrite(string path, string displayName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException($"{displayName}路径为空。", nameof(path));
            }

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                EnsureSafeDirectoryForWrite(directory, $"{displayName}目录");
            }

            var file = new FileInfo(fullPath);
            file.Refresh();
            if (file.Exists && HasReparsePoint(file))
            {
                throw new IOException($"{displayName}目标是链接文件，拒绝写入: {fullPath}");
            }
        }

        private static void EnsureSafeDirectoryForWrite(string directory, string displayName)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException($"{displayName}路径为空。", nameof(directory));
            }

            string fullDirectory = Path.GetFullPath(directory);
            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new IOException($"{displayName}包含链接目录，拒绝写入: {fullDirectory}");
            }

            Directory.CreateDirectory(fullDirectory);

            var info = new DirectoryInfo(fullDirectory);
            info.Refresh();
            if (info.Exists && HasReparsePoint(info))
            {
                throw new IOException($"{displayName}是链接目录，拒绝写入: {fullDirectory}");
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
        {
            try
            {
                var current = new DirectoryInfo(Path.GetFullPath(directory));
                while (current != null)
                {
                    current.Refresh();
                    if (current.Exists && HasReparsePoint(current))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[StorageService] Directory reparse point check failed, protected by default: {ex.Message}");
                return true;
            }
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool DirectoryTreeContainsReparsePoint(DirectoryInfo root)
        {
            try
            {
                var pending = new Stack<DirectoryInfo>();
                pending.Push(root);

                while (pending.Count > 0)
                {
                    DirectoryInfo current = pending.Pop();
                    current.Refresh();
                    if (!current.Exists)
                    {
                        continue;
                    }

                    foreach (FileSystemInfo entry in current.EnumerateFileSystemInfos())
                    {
                        entry.Refresh();
                        if (HasReparsePoint(entry))
                        {
                            return true;
                        }

                        if (entry is DirectoryInfo directory)
                        {
                            pending.Push(directory);
                        }
                    }
                }

                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"[StorageService] Cleanup directory tree scan failed, protected by default: {ex.Message}");
                return true;
            }
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
