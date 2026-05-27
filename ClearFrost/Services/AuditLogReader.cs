// ============================================================================
// 文件名: AuditLogReader.cs
// 描述:   操作审计日志读取与过滤
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ClearFrost.Services
{
    public sealed class AuditLogQuery
    {
        public DateTime? StartTime { get; init; }
        public DateTime? EndTime { get; init; }
        public bool? Success { get; init; }
        public string? Category { get; init; }
        public string? Action { get; init; }
        public string? SearchText { get; init; }
        public int Limit { get; init; } = 300;
    }

    public sealed class AuditLogRecord
    {
        public DateTime Timestamp { get; init; }
        public bool Success { get; init; }
        public string Category { get; init; } = string.Empty;
        public string Action { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string PreviousHash { get; init; } = string.Empty;
        public string Hash { get; init; } = string.Empty;
        public string IntegrityStatus { get; init; } = AuditLogIntegrity.LegacyStatus;
        public string SourceFile { get; init; } = string.Empty;
    }

    public static class AuditLogReader
    {
        private const int DefaultLimit = 300;
        private const int MaxLimit = 2000;

        public static IReadOnlyList<AuditLogRecord> Read(string logBasePath, AuditLogQuery? query = null)
        {
            query ??= new AuditLogQuery();
            int limit = Math.Clamp(query.Limit <= 0 ? DefaultLimit : query.Limit, 1, MaxLimit);
            string auditRoot = Path.Combine(logBasePath ?? string.Empty, "AuditLogs");
            if (!Directory.Exists(auditRoot))
            {
                return Array.Empty<AuditLogRecord>();
            }

            var records = new List<AuditLogRecord>(limit);
            foreach (string filePath in EnumerateAuditFiles(auditRoot))
            {
                foreach (AuditLogRecord record in ReadFile(filePath).OrderByDescending(r => r.Timestamp))
                {
                    if (!Matches(record, query))
                    {
                        continue;
                    }

                    records.Add(record);
                    if (records.Count >= limit)
                    {
                        return records;
                    }
                }
            }

            return records;
        }

        private static IEnumerable<string> EnumerateAuditFiles(string auditRoot)
        {
            return Directory.GetFiles(auditRoot, "*.txt", SearchOption.AllDirectories)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<AuditLogRecord> ReadFile(string filePath)
        {
            string expectedPreviousHash = AuditLogIntegrity.GenesisHash;
            foreach (string rawLine in File.ReadLines(filePath))
            {
                if (!TryParseLine(rawLine, filePath, expectedPreviousHash, out AuditLogRecord? record))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(record!.Hash))
                {
                    expectedPreviousHash = record.Hash;
                }

                yield return record!;
            }
        }

        private static bool TryParseLine(
            string rawLine,
            string filePath,
            string expectedPreviousHash,
            out AuditLogRecord? record)
        {
            record = null;
            string line = (rawLine ?? string.Empty).TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("时间\t", StringComparison.Ordinal))
            {
                return false;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 5)
            {
                return false;
            }

            if (!DateTime.TryParseExact(
                    parts[0],
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTime timestamp))
            {
                return false;
            }

            string detail = parts.Length >= 7 ? parts[4] : string.Join('\t', parts.Skip(4));
            string previousHash = parts.Length >= 7 ? parts[5] : string.Empty;
            string hash = parts.Length >= 7 ? parts[6] : string.Empty;
            string integrityStatus = AuditLogIntegrity.LegacyStatus;
            if (parts.Length >= 7)
            {
                string expectedHash = AuditLogIntegrity.ComputeHash(
                    parts[0],
                    parts[1],
                    parts[2],
                    parts[3],
                    detail,
                    previousHash);
                integrityStatus =
                    string.Equals(previousHash, expectedPreviousHash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)
                        ? AuditLogIntegrity.ValidStatus
                        : AuditLogIntegrity.TamperedStatus;
            }

            record = new AuditLogRecord
            {
                Timestamp = timestamp,
                Success = string.Equals(parts[1], "成功", StringComparison.OrdinalIgnoreCase),
                Category = parts[2],
                Action = parts[3],
                Detail = detail,
                PreviousHash = previousHash,
                Hash = hash,
                IntegrityStatus = integrityStatus,
                SourceFile = filePath
            };
            return true;
        }

        private static bool Matches(AuditLogRecord record, AuditLogQuery query)
        {
            if (query.StartTime.HasValue && record.Timestamp < query.StartTime.Value)
            {
                return false;
            }

            if (query.EndTime.HasValue && record.Timestamp > query.EndTime.Value)
            {
                return false;
            }

            if (query.Success.HasValue && record.Success != query.Success.Value)
            {
                return false;
            }

            if (!Contains(record.Category, query.Category))
            {
                return false;
            }

            if (!Contains(record.Action, query.Action))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(query.SearchText))
            {
                string search = query.SearchText.Trim();
                return Contains(record.Category, search) ||
                    Contains(record.Action, search) ||
                    Contains(record.Detail, search);
            }

            return true;
        }

        private static bool Contains(string value, string? filter)
        {
            return string.IsNullOrWhiteSpace(filter) ||
                value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
