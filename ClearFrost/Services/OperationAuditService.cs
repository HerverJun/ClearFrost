// ============================================================================
// 文件名: OperationAuditService.cs
// 描述:   关键生产操作审计 outbox
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Security;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    internal enum OperationAuditStatus
    {
        Requested,
        Denied,
        Succeeded,
        Failed
    }

    internal sealed class OperationAuditRecord
    {
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
        public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
        public string Operation { get; init; } = string.Empty;
        public OperationAuditStatus Status { get; init; }
        public string OperatorId { get; init; } = string.Empty;
        public ProductionRole Role { get; init; } = ProductionRole.Operator;
        public string Reason { get; init; } = string.Empty;
        public string InspectionId { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public string FailureBlocker { get; init; } = string.Empty;
        public string PreviousRecordSha256 { get; init; } = string.Empty;
        public string RecordSha256 { get; init; } = string.Empty;
    }

    internal sealed class OperationAuditQuery
    {
        public DateTimeOffset? StartTime { get; init; }
        public DateTimeOffset? EndTime { get; init; }
        public string Operation { get; init; } = string.Empty;
        public string OperatorId { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public OperationAuditStatus? Status { get; init; }
        public string FailureReason { get; init; } = string.Empty;
        public int Limit { get; init; } = 200;
    }

    internal sealed class OperationAuditQueryResult
    {
        public IReadOnlyList<OperationAuditRecord> Records { get; init; } = Array.Empty<OperationAuditRecord>();
        public string ErrorMessage { get; init; } = string.Empty;
        public bool Succeeded => string.IsNullOrWhiteSpace(ErrorMessage);
    }

    public sealed class OperationAuditChainFinding
    {
        public string FilePath { get; init; } = string.Empty;
        public int LineNumber { get; init; }
        public string Severity { get; init; } = "Blocking";
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string ExpectedPreviousSha256 { get; init; } = string.Empty;
        public string ActualPreviousSha256 { get; init; } = string.Empty;
        public string ExpectedRecordSha256 { get; init; } = string.Empty;
        public string ActualRecordSha256 { get; init; } = string.Empty;
    }

    public sealed class OperationAuditChainVerificationResult
    {
        public int TotalRecords { get; init; }
        public int VerifiedRecords { get; init; }
        public string LastRecordSha256 { get; init; } = string.Empty;
        public IReadOnlyList<OperationAuditChainFinding> Findings { get; init; } =
            Array.Empty<OperationAuditChainFinding>();

        public bool Succeeded => Findings.Count == 0;

        public string Status => Findings.Any(finding =>
            string.Equals(finding.Severity, "Blocking", StringComparison.OrdinalIgnoreCase))
                ? "Blocking"
                : Findings.Count > 0
                    ? "Warning"
                    : "Healthy";
    }

    internal sealed class OperationAuditService
    {
        private static readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private static readonly StringComparison FileSystemPathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        private static readonly UTF8Encoding AuditLineEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static readonly JsonSerializerOptions CompactJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false
        };

        private readonly string _outboxDirectory;

        public OperationAuditService(string? outboxDirectory = null)
        {
            _outboxDirectory = string.IsNullOrWhiteSpace(outboxDirectory)
                ? Path.Combine(RuntimePaths.DataDirectory, "outbox")
                : outboxDirectory;
        }

        public async Task<bool> AppendAsync(OperationAuditRecord record, CancellationToken cancellationToken = default)
        {
            if (record == null)
            {
                return false;
            }

            try
            {
                string outboxDirectory = EnsureOutboxDirectorySafeForWrite();
                string path = Path.Combine(outboxDirectory, $"operation-audit-{DateTime.Now:yyyyMMdd}.ndjson");

                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    EnsureAuditFileSafeForAppend(path, outboxDirectory);
                    string previousHash = ResolveLatestRecordHash();
                    OperationAuditRecord sealedRecord = SealRecord(record, previousHash);
                    string json = JsonSerializer.Serialize(sealedRecord, CompactJsonOptions);
                    await AppendLineDurablyAsync(
                        path,
                        json,
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OperationAudit] 写入审计 outbox 失败: {ex.Message}");
                return false;
            }
        }

        public Task<OperationAuditChainVerificationResult> VerifyChainAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!Directory.Exists(_outboxDirectory))
                {
                    return Task.FromResult(new OperationAuditChainVerificationResult());
                }

                var findings = new List<OperationAuditChainFinding>();
                string previousHash = string.Empty;
                int totalRecords = 0;
                int verifiedRecords = 0;

                foreach (string path in EnumerateAuditFilesChronologically())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int lineNumber = 0;
                    foreach (string line in ReadSafeAuditLines(_outboxDirectory, path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        lineNumber++;
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        OperationAuditRecord? record = TryDeserialize(line);
                        if (record == null)
                        {
                            findings.Add(new OperationAuditChainFinding
                            {
                                FilePath = path,
                                LineNumber = lineNumber,
                                ErrorCode = "AuditRecordInvalidJson",
                                Message = "审计记录不是有效 JSON，链式校验无法继续确认该行内容。"
                            });
                            continue;
                        }

                        totalRecords++;
                        bool verified = true;
                        if (string.IsNullOrWhiteSpace(record.PreviousRecordSha256) &&
                            !string.IsNullOrWhiteSpace(previousHash))
                        {
                            findings.Add(new OperationAuditChainFinding
                            {
                                FilePath = path,
                                LineNumber = lineNumber,
                                ErrorCode = "AuditPreviousHashMissing",
                                Message = "审计记录缺少上一条记录哈希，无法证明链路连续。",
                                ExpectedPreviousSha256 = previousHash
                            });
                            verified = false;
                        }
                        else if (!string.Equals(
                                     record.PreviousRecordSha256 ?? string.Empty,
                                     previousHash,
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            findings.Add(new OperationAuditChainFinding
                            {
                                FilePath = path,
                                LineNumber = lineNumber,
                                ErrorCode = "AuditPreviousHashMismatch",
                                Message = "审计记录上一条哈希不匹配，审计链可能被截断或重排。",
                                ExpectedPreviousSha256 = previousHash,
                                ActualPreviousSha256 = record.PreviousRecordSha256 ?? string.Empty
                            });
                            verified = false;
                        }

                        string actualHash = ComputeRecordHash(record);
                        if (string.IsNullOrWhiteSpace(record.RecordSha256))
                        {
                            findings.Add(new OperationAuditChainFinding
                            {
                                FilePath = path,
                                LineNumber = lineNumber,
                                Severity = "Warning",
                                ErrorCode = "AuditRecordHashMissing",
                                Message = "审计记录缺少自身哈希，可能来自旧版本 outbox。",
                                ExpectedRecordSha256 = actualHash
                            });
                            verified = false;
                        }
                        else if (!string.Equals(record.RecordSha256, actualHash, StringComparison.OrdinalIgnoreCase))
                        {
                            findings.Add(new OperationAuditChainFinding
                            {
                                FilePath = path,
                                LineNumber = lineNumber,
                                ErrorCode = "AuditRecordHashMismatch",
                                Message = "审计记录自身哈希不匹配，记录内容可能被改写。",
                                ExpectedRecordSha256 = actualHash,
                                ActualRecordSha256 = record.RecordSha256
                            });
                            verified = false;
                        }

                        if (verified)
                        {
                            verifiedRecords++;
                        }

                        previousHash = string.IsNullOrWhiteSpace(record.RecordSha256)
                            ? ComputeSha256(line)
                            : record.RecordSha256;
                    }
                }

                return Task.FromResult(new OperationAuditChainVerificationResult
                {
                    TotalRecords = totalRecords,
                    VerifiedRecords = verifiedRecords,
                    LastRecordSha256 = previousHash,
                    Findings = findings
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OperationAudit] 审计链校验失败: {ex.Message}");
                return Task.FromResult(new OperationAuditChainVerificationResult
                {
                    Findings = new[]
                    {
                        new OperationAuditChainFinding
                        {
                            ErrorCode = "AuditChainVerificationFailed",
                            Message = ex.Message
                        }
                    }
                });
            }
        }

        public async Task<OperationAuditQueryResult> QueryAsync(
            OperationAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            query ??= new OperationAuditQuery();
            int limit = Math.Clamp(query.Limit <= 0 ? 200 : query.Limit, 1, 1000);

            try
            {
                if (!Directory.Exists(_outboxDirectory))
                {
                    return new OperationAuditQueryResult();
                }

                var records = new List<OperationAuditRecord>();
                foreach (string path in EnumerateAuditFiles())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryOpenSafeAuditFileForRead(_outboxDirectory, path, out StreamReader? reader))
                    {
                        continue;
                    }

                    using StreamReader safeReader = reader!;
                    while (!safeReader.EndOfStream)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? line = await safeReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        OperationAuditRecord? record = TryDeserialize(line);
                        if (record == null || !Matches(record, query))
                        {
                            continue;
                        }

                        records.Add(record);
                    }
                }

                return new OperationAuditQueryResult
                {
                    Records = records
                        .OrderByDescending(record => record.Timestamp)
                        .Take(limit)
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OperationAudit] 查询审计 outbox 失败: {ex.Message}");
                return new OperationAuditQueryResult
                {
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<string> ExportCsvAsync(
            OperationAuditQuery query,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            OperationAuditQueryResult result = await QueryAsync(query, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.ErrorMessage);
            }

            string directory = Path.GetDirectoryName(outputPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                CreateSafeDirectory(directory, "审计 CSV 输出目录");
            }

            var builder = new StringBuilder();
            builder.AppendLine("Timestamp,CorrelationId,Operation,Status,OperatorId,Role,Reason,InspectionId,Details,FailureBlocker,PreviousRecordSha256,RecordSha256");
            foreach (OperationAuditRecord record in result.Records)
            {
                builder.AppendLine(string.Join(
                    ",",
                    Csv(record.Timestamp.ToString("O")),
                    Csv(record.CorrelationId),
                    Csv(record.Operation),
                    Csv(record.Status.ToString()),
                    Csv(record.OperatorId),
                    Csv(record.Role.ToString()),
                    Csv(record.Reason),
                    Csv(record.InspectionId),
                    Csv(record.Details),
                    Csv(record.FailureBlocker),
                    Csv(record.PreviousRecordSha256),
                    Csv(record.RecordSha256)));
            }

            AtomicFileWriter.WriteAllText(outputPath, builder.ToString());
            return outputPath;
        }

        private IEnumerable<string> EnumerateAuditFiles()
        {
            return EnumerateSafeAuditFiles()
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<string> EnumerateAuditFilesChronologically()
        {
            return EnumerateSafeAuditFiles()
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static async Task AppendLineDurablyAsync(
            string path,
            string line,
            CancellationToken cancellationToken)
        {
            byte[] bytes = AuditLineEncoding.GetBytes((line ?? string.Empty) + Environment.NewLine);
            await using var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                FileOptions.SequentialScan | FileOptions.WriteThrough);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        internal static IReadOnlyList<string> ReadSafeAuditLines(string outboxDirectory, string auditFilePath)
        {
            if (!TryOpenSafeAuditFileForRead(outboxDirectory, auditFilePath, out StreamReader? reader))
            {
                return Array.Empty<string>();
            }

            try
            {
                using StreamReader safeReader = reader!;
                var lines = new List<string>();
                while (!safeReader.EndOfStream)
                {
                    string? line = safeReader.ReadLine();
                    if (line != null)
                    {
                        lines.Add(line);
                    }
                }

                return lines;
            }
            catch (Exception ex) when (IsRecoverableAuditFileReadException(ex))
            {
                return Array.Empty<string>();
            }
        }

        private static bool TryOpenSafeAuditFileForRead(
            string outboxDirectory,
            string auditFilePath,
            out StreamReader? reader)
        {
            reader = null;
            if (string.IsNullOrWhiteSpace(auditFilePath))
            {
                return false;
            }

            FileInfo file;
            try
            {
                file = new FileInfo(auditFilePath);
            }
            catch (Exception ex) when (IsRecoverableAuditFileReadException(ex))
            {
                return false;
            }

            if (!IsSafeAuditFileForRead(outboxDirectory, file))
            {
                return false;
            }

            try
            {
                var stream = new FileStream(
                    auditFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);

                if (!IsSafeAuditFileForRead(outboxDirectory, new FileInfo(auditFilePath)))
                {
                    stream.Dispose();
                    return false;
                }

                reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return true;
            }
            catch (Exception ex) when (IsRecoverableAuditFileReadException(ex))
            {
                return false;
            }
        }

        private string EnsureOutboxDirectorySafeForWrite()
        {
            return CreateSafeDirectory(_outboxDirectory, "审计 outbox 目录");
        }

        private static string CreateSafeDirectory(string directory, string displayName)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException($"{displayName}为空。", nameof(directory));
            }

            string fullDirectory = Path.GetFullPath(directory);
            EnsureExistingDirectoryAncestorsHaveNoReparsePoint(fullDirectory, displayName);
            Directory.CreateDirectory(fullDirectory);
            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new IOException($"{displayName}不能包含链接目录: {fullDirectory}");
            }

            return fullDirectory;
        }

        private static void EnsureAuditFileSafeForAppend(string path, string outboxDirectory)
        {
            string fullPath = Path.GetFullPath(path);
            string fullOutbox = Path.GetFullPath(outboxDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fileDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (!string.Equals(fileDirectory, fullOutbox, FileSystemPathComparison))
            {
                throw new UnauthorizedAccessException("审计文件必须位于审计 outbox 顶层目录。");
            }

            if (DirectoryPathHasReparsePoint(fullOutbox))
            {
                throw new IOException($"审计 outbox 目录不能包含链接目录: {fullOutbox}");
            }

            if (Directory.Exists(fullPath))
            {
                throw new IOException($"审计文件路径是目录，拒绝写入: {fullPath}");
            }

            if (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath)))
            {
                throw new IOException($"审计文件是链接文件，拒绝写入: {fullPath}");
            }
        }

        private IEnumerable<string> EnumerateSafeAuditFiles()
        {
            string? outboxRoot = GetSafeDirectoryRoot(_outboxDirectory);
            if (outboxRoot == null)
            {
                return Array.Empty<string>();
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            return Directory
                .EnumerateFiles(outboxRoot, "operation-audit-*.ndjson", options)
                .Select(path => new FileInfo(path))
                .Where(info => IsSafeAuditFileForRead(outboxRoot, info))
                .Select(info => info.FullName)
                .ToList();
        }

        internal static bool IsSafeAuditFileForRead(string outboxDirectory, FileInfo file)
        {
            string? outboxRoot = GetSafeDirectoryRoot(outboxDirectory);
            if (outboxRoot == null || file == null)
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(file.FullName);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }

            if (!fullPath.StartsWith(outboxRoot, FileSystemPathComparison))
            {
                return false;
            }

            string fileDirectory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string normalizedOutbox = Path.TrimEndingDirectorySeparator(outboxRoot);
            if (!string.Equals(fileDirectory, normalizedOutbox, FileSystemPathComparison))
            {
                return false;
            }

            string fileName = Path.GetFileName(fullPath);
            if (!fileName.StartsWith("operation-audit-", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                file.Refresh();
            }
            catch (Exception ex) when (IsRecoverableAuditFileReadException(ex))
            {
                return false;
            }

            return file.Exists && !HasReparsePoint(file);
        }

        private static string? GetSafeDirectoryRoot(string directory)
        {
            try
            {
                string fullPath = Path.GetFullPath(directory);
                if (Directory.Exists(fullPath) && DirectoryPathHasReparsePoint(fullPath))
                {
                    return null;
                }

                return Path.EndsInDirectorySeparator(fullPath)
                    ? fullPath
                    : fullPath + Path.DirectorySeparatorChar;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void EnsureExistingDirectoryAncestorsHaveNoReparsePoint(string directory, string displayName)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null && !current.Exists)
            {
                current = current.Parent;
            }

            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    throw new IOException($"{displayName}不能包含链接目录: {current.FullName}");
                }

                current = current.Parent;
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
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

        private static bool IsRecoverableAuditFileReadException(Exception ex)
        {
            return ex is ArgumentException
                or NotSupportedException
                or PathTooLongException
                or IOException
                or UnauthorizedAccessException;
        }

        private static OperationAuditRecord? TryDeserialize(string line)
        {
            try
            {
                return JsonSerializer.Deserialize<OperationAuditRecord>(line);
            }
            catch
            {
                return null;
            }
        }

        private static bool Matches(OperationAuditRecord record, OperationAuditQuery query)
        {
            if (query.StartTime.HasValue && record.Timestamp < query.StartTime.Value)
            {
                return false;
            }

            if (query.EndTime.HasValue && record.Timestamp > query.EndTime.Value)
            {
                return false;
            }

            if (!ContainsOrEmpty(record.Operation, query.Operation))
            {
                return false;
            }

            if (!ContainsOrEmpty(record.OperatorId, query.OperatorId))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.Role) &&
                !string.Equals(record.Role.ToString(), query.Role.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (query.Status.HasValue && record.Status != query.Status.Value)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.FailureReason))
            {
                string needle = query.FailureReason.Trim();
                return ContainsOrEmpty(record.FailureBlocker, needle) ||
                       ContainsOrEmpty(record.Details, needle) ||
                       ContainsOrEmpty(record.Reason, needle);
            }

            return true;
        }

        private static bool ContainsOrEmpty(string value, string filter)
        {
            return string.IsNullOrWhiteSpace(filter) ||
                   (value ?? string.Empty).Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string Csv(string value)
        {
            string safe = value ?? string.Empty;
            if (!safe.Contains(',') && !safe.Contains('"') && !safe.Contains('\r') && !safe.Contains('\n'))
            {
                return safe;
            }

            return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        private OperationAuditRecord SealRecord(OperationAuditRecord record, string previousHash)
        {
            var sealedRecord = new OperationAuditRecord
            {
                Timestamp = record.Timestamp,
                CorrelationId = string.IsNullOrWhiteSpace(record.CorrelationId)
                    ? Guid.NewGuid().ToString("N")
                    : record.CorrelationId,
                Operation = record.Operation,
                Status = record.Status,
                OperatorId = record.OperatorId,
                Role = record.Role,
                Reason = record.Reason,
                InspectionId = record.InspectionId,
                Details = record.Details,
                FailureBlocker = record.FailureBlocker,
                PreviousRecordSha256 = previousHash
            };

            return CopyWithRecordHash(sealedRecord, ComputeRecordHash(sealedRecord));
        }

        private string ResolveLatestRecordHash()
        {
            foreach (string path in EnumerateAuditFiles())
            {
                foreach (string line in ReadSafeAuditLines(_outboxDirectory, path).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    OperationAuditRecord? record = TryDeserialize(line);
                    if (!string.IsNullOrWhiteSpace(record?.RecordSha256))
                    {
                        return record.RecordSha256;
                    }

                    return ComputeSha256(line);
                }
            }

            return string.Empty;
        }

        private static OperationAuditRecord CopyWithRecordHash(OperationAuditRecord record, string recordHash)
        {
            return new OperationAuditRecord
            {
                Timestamp = record.Timestamp,
                CorrelationId = record.CorrelationId,
                Operation = record.Operation,
                Status = record.Status,
                OperatorId = record.OperatorId,
                Role = record.Role,
                Reason = record.Reason,
                InspectionId = record.InspectionId,
                Details = record.Details,
                FailureBlocker = record.FailureBlocker,
                PreviousRecordSha256 = record.PreviousRecordSha256,
                RecordSha256 = recordHash
            };
        }

        private static string ComputeRecordHash(OperationAuditRecord record)
        {
            OperationAuditRecord canonical = CopyWithRecordHash(record, string.Empty);
            string json = JsonSerializer.Serialize(canonical, CompactJsonOptions);
            return ComputeSha256(json);
        }

        private static string ComputeSha256(string value)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
