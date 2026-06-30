// ============================================================================
// 文件名: ReplayIntegrityScanner.cs
// 描述:   Replay Evidence 完整性扫描服务
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Models;
using ClearFrost.Core.Security;

namespace ClearFrost.Services.Replay
{
    internal sealed class ReplayIntegrityFinding
    {
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    internal sealed class ReplayIntegrityScanResult
    {
        public bool Succeeded => Findings.Count == 0;
        public IReadOnlyList<ReplayIntegrityFinding> Findings { get; init; } = Array.Empty<ReplayIntegrityFinding>();
    }

    internal sealed class ReplayIntegrityScanner
    {
        private readonly ModelRegistry _registry;
        private readonly ReplayApprovalEvidenceProductionGate _gate;
        private readonly OperationAuditService? _auditService;

        public ReplayIntegrityScanner(
            ModelRegistry registry,
            ReplayApprovalEvidenceProductionGate gate,
            OperationAuditService? auditService = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _auditService = auditService;
        }

        public async Task<ReplayIntegrityScanResult> ScanApprovedModelsAsync(
            CancellationToken cancellationToken = default)
        {
            var findings = new List<ReplayIntegrityFinding>();
            foreach (ModelRegistryEntry entry in _registry.Entries.Where(item => item.IsPackage && item.ApprovedForProduction))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProductionModelReadinessResult result = _gate.Validate(entry);
                if (!result.Succeeded)
                {
                    findings.Add(new ReplayIntegrityFinding
                    {
                        ModelId = entry.ModelId,
                        Version = entry.Version,
                        ErrorCode = result.ErrorCode,
                        Message = result.Message
                    });
                }
            }

            ReplayIntegrityScanResult scan = new ReplayIntegrityScanResult { Findings = findings };
            if (_auditService != null)
            {
                await _auditService.AppendAsync(new OperationAuditRecord
                {
                    Operation = "ReplayIntegrityScan",
                    Status = scan.Succeeded ? OperationAuditStatus.Succeeded : OperationAuditStatus.Failed,
                    OperatorId = "system",
                    Role = ProductionRole.Engineer,
                    Details = scan.Succeeded
                        ? "Replay integrity scan succeeded."
                        : string.Join("; ", findings.Select(item => $"{item.ModelId}/{item.Version}:{item.ErrorCode}")),
                    FailureBlocker = scan.Succeeded
                        ? string.Empty
                        : "Replay evidence integrity"
                }, cancellationToken).ConfigureAwait(false);
            }

            return scan;
        }
    }
}
