// ============================================================================
// 文件名: OperationAuditService.cs
// 描述:   关键生产操作审计 outbox
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
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
    }
}
