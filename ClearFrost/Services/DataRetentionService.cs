// ============================================================================
// 文件名: DataRetentionService.cs
// 描述:   生产追溯数据保留与清理服务
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    public sealed class DataRetentionPolicy
    {
        public bool Enabled { get; init; } = true;
        public int ImageRetentionDays { get; init; } = 30;
        public int LogRetentionDays { get; init; } = 180;
        public int AuditLogRetentionDays { get; init; } = 365;
        public int ReportRetentionDays { get; init; } = 365;
        public int TraceRecordRetentionDays { get; init; } = 365;

        public DataRetentionPolicy Normalize()
        {
            return new DataRetentionPolicy
            {
                Enabled = Enabled,
                ImageRetentionDays = NormalizeDays(ImageRetentionDays),
                LogRetentionDays = NormalizeDays(LogRetentionDays),
                AuditLogRetentionDays = NormalizeDays(AuditLogRetentionDays),
                ReportRetentionDays = NormalizeDays(ReportRetentionDays),
                TraceRecordRetentionDays = NormalizeDays(TraceRecordRetentionDays)
            };
        }

        private static int NormalizeDays(int days)
        {
            return Math.Clamp(days, 1, 3650);
        }
    }

    public sealed class DataRetentionCleanupSummary
    {
        public DateTime StartedAt { get; init; }
        public DateTime CompletedAt { get; set; }
        public int ImageDirectoriesDeleted { get; set; }
        public int LogDirectoriesDeleted { get; set; }
        public int LogFilesDeleted { get; set; }
        public int ReportFilesDeleted { get; set; }
        public int TraceRecordsDeleted { get; set; }
        public List<string> Errors { get; } = new();
        public int ErrorCount => Errors.Count;
        public int TotalDeletedItems =>
            ImageDirectoriesDeleted +
            LogDirectoriesDeleted +
            LogFilesDeleted +
            ReportFilesDeleted +
            TraceRecordsDeleted;
    }

    public sealed class DataRetentionService
    {
        private static readonly Regex DateTokenRegex = new(@"(?<!\d)(\d{8})(?!\d)", RegexOptions.Compiled);

        private readonly string _storageRoot;
        private readonly Func<DateTime> _nowProvider;

        public DataRetentionService(string storageRoot, Func<DateTime>? nowProvider = null)
        {
            _storageRoot = Path.GetFullPath(StorageService.ResolveStoragePath(storageRoot))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _nowProvider = nowProvider ?? (() => DateTime.Now);
        }

        public async Task<DataRetentionCleanupSummary> CleanupAsync(
            DataRetentionPolicy? policy,
            IDatabaseService? databaseService = null,
            CancellationToken cancellationToken = default)
        {
            DateTime startedAt = _nowProvider();
            var summary = new DataRetentionCleanupSummary { StartedAt = startedAt };
            DataRetentionPolicy normalized = (policy ?? new DataRetentionPolicy()).Normalize();

            if (!normalized.Enabled)
            {
                summary.CompletedAt = _nowProvider();
                return summary;
            }

            try
            {
                CleanImageDirectories(normalized.ImageRetentionDays, summary, cancellationToken);
                CleanLogDirectories(normalized.LogRetentionDays, normalized.AuditLogRetentionDays, summary, cancellationToken);
                CleanReportFiles(normalized.ReportRetentionDays, summary, cancellationToken);
                await CleanTraceRecordsAsync(normalized.TraceRecordRetentionDays, databaseService, summary).ConfigureAwait(false);
            }
            finally
            {
                summary.CompletedAt = _nowProvider();
            }

            return summary;
        }

        private void CleanImageDirectories(
            int retentionDays,
            DataRetentionCleanupSummary summary,
            CancellationToken cancellationToken)
        {
            string imageRoot = Path.Combine(_storageRoot, "Images");
            DeleteDatedDirectories(Path.Combine(imageRoot, "Qualified"), retentionDays, summary, DeleteCategory.Image, cancellationToken);
            DeleteDatedDirectories(Path.Combine(imageRoot, "Unqualified"), retentionDays, summary, DeleteCategory.Image, cancellationToken);
        }

        private void CleanLogDirectories(
            int logRetentionDays,
            int auditLogRetentionDays,
            DataRetentionCleanupSummary summary,
            CancellationToken cancellationToken)
        {
            string logRoot = Path.Combine(_storageRoot, "Logs");
            DeleteDatedDirectories(Path.Combine(logRoot, "DetectionLogs"), logRetentionDays, summary, DeleteCategory.LogDirectory, cancellationToken);
            DeleteDatedDirectories(Path.Combine(logRoot, "AuditLogs"), auditLogRetentionDays, summary, DeleteCategory.LogDirectory, cancellationToken);
            DeleteOldFiles(logRoot, "ErrorLog_*.txt", logRetentionDays, summary, DeleteCategory.LogFile, cancellationToken);
        }

        private void CleanReportFiles(
            int reportRetentionDays,
            DataRetentionCleanupSummary summary,
            CancellationToken cancellationToken)
        {
            DeleteOldFiles(
                Path.Combine(_storageRoot, "Logs", "Reports"),
                "*.csv",
                reportRetentionDays,
                summary,
                DeleteCategory.ReportFile,
                cancellationToken);
        }

        private static async Task CleanTraceRecordsAsync(
            int retentionDays,
            IDatabaseService? databaseService,
            DataRetentionCleanupSummary summary)
        {
            if (databaseService == null)
            {
                return;
            }

            try
            {
                summary.TraceRecordsDeleted = await databaseService.CleanupOldRecordsAsync(retentionDays).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"TraceRecords: {ex.Message}");
            }
        }

        private void DeleteDatedDirectories(
            string parentPath,
            int retentionDays,
            DataRetentionCleanupSummary summary,
            DeleteCategory category,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(parentPath) || !IsUnderStorageRoot(parentPath))
            {
                return;
            }

            DateTime cutoff = _nowProvider().Date.AddDays(-retentionDays);
            foreach (string directory in Directory.EnumerateDirectories(parentPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(directory);
                if (!TryParseDate(name, out DateTime folderDate) || folderDate >= cutoff)
                {
                    continue;
                }

                TryDeleteDirectory(directory, category, summary);
            }
        }

        private void DeleteOldFiles(
            string parentPath,
            string searchPattern,
            int retentionDays,
            DataRetentionCleanupSummary summary,
            DeleteCategory category,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(parentPath) || !IsUnderStorageRoot(parentPath))
            {
                return;
            }

            DateTime cutoff = _nowProvider().Date.AddDays(-retentionDays);
            foreach (string file in Directory.EnumerateFiles(parentPath, searchPattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DateTime fileDate = TryGetDateFromFileName(file, out DateTime parsedDate)
                    ? parsedDate
                    : File.GetLastWriteTime(file).Date;

                if (fileDate >= cutoff)
                {
                    continue;
                }

                TryDeleteFile(file, category, summary);
            }
        }

        private void TryDeleteDirectory(
            string directory,
            DeleteCategory category,
            DataRetentionCleanupSummary summary)
        {
            try
            {
                if (!IsUnderStorageRoot(directory))
                {
                    summary.Errors.Add($"SkipDirectoryOutsideStorage: {directory}");
                    return;
                }

                Directory.Delete(directory, recursive: true);
                if (category == DeleteCategory.Image)
                {
                    summary.ImageDirectoriesDeleted++;
                }
                else
                {
                    summary.LogDirectoriesDeleted++;
                }
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"{category}:{directory}: {ex.Message}");
            }
        }

        private void TryDeleteFile(
            string file,
            DeleteCategory category,
            DataRetentionCleanupSummary summary)
        {
            try
            {
                if (!IsUnderStorageRoot(file))
                {
                    summary.Errors.Add($"SkipFileOutsideStorage: {file}");
                    return;
                }

                File.Delete(file);
                if (category == DeleteCategory.ReportFile)
                {
                    summary.ReportFilesDeleted++;
                }
                else
                {
                    summary.LogFilesDeleted++;
                }
            }
            catch (Exception ex)
            {
                summary.Errors.Add($"{category}:{file}: {ex.Message}");
            }
        }

        private bool IsUnderStorageRoot(string path)
        {
            string root = _storageRoot + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetDateFromFileName(string file, out DateTime date)
        {
            string fileName = Path.GetFileName(file);
            Match match = DateTokenRegex.Match(fileName);
            if (match.Success &&
                DateTime.TryParseExact(
                    match.Groups[1].Value,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
            {
                return true;
            }

            date = default;
            return false;
        }

        private static bool TryParseDate(string value, out DateTime date)
        {
            string[] formats = { "yyyy年MM月dd日", "yyyyMMdd", "yyyy-MM-dd" };
            return DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date);
        }

        private enum DeleteCategory
        {
            Image,
            LogDirectory,
            LogFile,
            ReportFile
        }
    }
}
