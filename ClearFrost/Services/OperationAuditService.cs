// ============================================================================
// 文件名: OperationAuditService.cs
// 描述:   关键生产操作审计 outbox
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    internal sealed class OperationAuditService
    {
        private static readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
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
                Directory.CreateDirectory(_outboxDirectory);
                string path = Path.Combine(_outboxDirectory, $"operation-audit-{DateTime.Now:yyyyMMdd}.ndjson");
                string json = JsonSerializer.Serialize(record, new JsonSerializerOptions
                {
                    WriteIndented = false
                });

                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await File.AppendAllTextAsync(
                        path,
                        json + Environment.NewLine,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                    while (!reader.EndOfStream)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
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
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.AppendLine("Timestamp,CorrelationId,Operation,Status,OperatorId,Role,Reason,InspectionId,Details,FailureBlocker");
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
                    Csv(record.FailureBlocker)));
            }

            await File.WriteAllTextAsync(
                outputPath,
                builder.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
                cancellationToken).ConfigureAwait(false);
            return outputPath;
        }

        private IEnumerable<string> EnumerateAuditFiles()
        {
            return Directory.EnumerateFiles(_outboxDirectory, "operation-audit-*.ndjson", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);
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
    }
}
