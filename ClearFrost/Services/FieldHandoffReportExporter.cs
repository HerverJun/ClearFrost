// ============================================================================
// 文件名: FieldHandoffReportExporter.cs
// 描述:   现场交接报告导出器
//
// 功能:
//   - 汇总当前诊断快照、诊断包复核和维护建议闭环
//   - 生成可归档的 Markdown 交接报告
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Security;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    internal sealed class FieldHandoffReportRequest
    {
        public string OutputDirectory { get; init; } = Path.Combine(RuntimePaths.DataDirectory, "HandoffReports");
        public FieldDiagnosticsSnapshot FieldDiagnostics { get; init; } = new FieldDiagnosticsSnapshot();
        public IReadOnlyList<ClearFrost.DiagnosticPackageHistoryItem> DiagnosticPackages { get; init; } =
            Array.Empty<ClearFrost.DiagnosticPackageHistoryItem>();
        public IReadOnlyList<MaintenanceAdviceResolutionRecord> MaintenanceAdviceHistory { get; init; } =
            Array.Empty<MaintenanceAdviceResolutionRecord>();
        public IReadOnlyList<OperationAuditRecord> RecentAuditRecords { get; init; } =
            Array.Empty<OperationAuditRecord>();
        public OperationAuditChainVerificationResult AuditChainVerification { get; init; } =
            new OperationAuditChainVerificationResult();
        public string OperatorId { get; init; } = string.Empty;
        public ProductionRole Role { get; init; } = ProductionRole.Operator;
    }

    internal sealed class FieldHandoffReportSummary
    {
        public string ReportPath { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public long SizeBytes { get; init; }
        public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
        public string OverallStatus { get; init; } = string.Empty;
        public int ActiveAdviceCount { get; init; }
        public int ShiftTaskCount { get; init; }
        public int FailedRecheckCount { get; init; }
        public int DiagnosticPackageCount { get; init; }
        public int RecentAuditCount { get; init; }
        public string AuditChainStatus { get; init; } = string.Empty;
        public int AuditChainFindingCount { get; init; }
        public int AuditChainVerifiedRecords { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    internal sealed class FieldHandoffReportExporter
    {
        private const int MaxDiagnosticPackages = 5;
        private const int MaxMaintenanceHistory = 8;
        private const int MaxAuditRecords = 12;
        private static readonly Regex WindowsPathRegex = new Regex(
            @"[A-Za-z]:\\[^\r\n\|;，,]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UncPathRegex = new Regex(
            @"\\\\[^\r\n\|;，,]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public Task<FieldHandoffReportSummary> ExportAsync(
            FieldHandoffReportRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            {
                throw new ArgumentException("交接报告输出目录为空。", nameof(request));
            }

            cancellationToken.ThrowIfCancellationRequested();
            string outputDirectory = ResolveSafeOutputDirectory(request.OutputDirectory);

            DateTimeOffset generatedAt = DateTimeOffset.Now;
            string reportId = $"handoff-{generatedAt:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}";
            string reportPath = Path.Combine(outputDirectory, $"{reportId}.md");
            string overallStatus = ResolveOverallStatus(request);
            string markdown = BuildMarkdown(request, generatedAt, reportId, overallStatus);

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFileWriter.WriteAllText(reportPath, markdown);

            FileInfo info = new FileInfo(reportPath);
            int failedRechecks = CountFailedRechecks(request.MaintenanceAdviceHistory);
            OperationAuditChainVerificationResult auditChain =
                request.AuditChainVerification ?? new OperationAuditChainVerificationResult();
            var summary = new FieldHandoffReportSummary
            {
                ReportPath = reportPath,
                FileName = Path.GetFileName(reportPath),
                SizeBytes = info.Exists ? info.Length : 0,
                GeneratedAt = generatedAt,
                OverallStatus = overallStatus,
                ActiveAdviceCount = request.FieldDiagnostics.MaintenanceAdvice?.Count ?? 0,
                ShiftTaskCount = request.FieldDiagnostics.ShiftTasks?.Count ?? 0,
                FailedRecheckCount = failedRechecks,
                DiagnosticPackageCount = request.DiagnosticPackages?.Count ?? 0,
                RecentAuditCount = request.RecentAuditRecords?.Count ?? 0,
                AuditChainStatus = auditChain.Status,
                AuditChainFindingCount = auditChain.Findings.Count,
                AuditChainVerifiedRecords = auditChain.VerifiedRecords,
                Message = $"现场交接报告已导出: {reportPath}"
            };
            return Task.FromResult(summary);
        }

        private static string ResolveSafeOutputDirectory(string outputDirectory)
        {
            string fullDirectory = Path.GetFullPath(outputDirectory);
            EnsureExistingDirectoryAncestorsHaveNoReparsePoint(fullDirectory);
            Directory.CreateDirectory(fullDirectory);
            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new InvalidOperationException($"交接报告输出目录不能是链接目录: {fullDirectory}");
            }

            return fullDirectory;
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

        private static void EnsureExistingDirectoryAncestorsHaveNoReparsePoint(string directory)
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
                    throw new InvalidOperationException($"交接报告输出目录不能包含链接目录: {current.FullName}");
                }

                current = current.Parent;
            }
        }

        private static string BuildMarkdown(
            FieldHandoffReportRequest request,
            DateTimeOffset generatedAt,
            string reportId,
            string overallStatus)
        {
            FieldDiagnosticsSnapshot snapshot = request.FieldDiagnostics ?? new FieldDiagnosticsSnapshot();
            var sb = new StringBuilder();

            sb.AppendLine("# ClearFrost 现场交接报告");
            sb.AppendLine();
            sb.AppendLine($"- 报告编号: {reportId}");
            sb.AppendLine($"- 生成时间: {generatedAt:O}");
            sb.AppendLine($"- 应用版本: {AppVersion.DisplayVersion}");
            sb.AppendLine($"- 操作员: {Inline(request.OperatorId)}");
            sb.AppendLine($"- 角色: {request.Role}");
            sb.AppendLine($"- 交接结论: {overallStatus}");
            sb.AppendLine($"- 班次待办: {snapshot.ShiftTasks?.Count ?? 0}");
            sb.AppendLine($"- 审计链: {(request.AuditChainVerification ?? new OperationAuditChainVerificationResult()).Status}");
            sb.AppendLine();

            AppendCurrentState(sb, snapshot);
            AppendModelAndRecipe(sb, snapshot);
            AppendDiagnosticPackages(sb, request);
            AppendMaintenanceAdvice(sb, snapshot, request.MaintenanceAdviceHistory);
            AppendShiftTasks(sb, snapshot);
            AppendAuditTrail(sb, request.RecentAuditRecords, request.AuditChainVerification);
            AppendNextShiftFocus(sb, request, overallStatus);

            return sb.ToString();
        }

        private static void AppendCurrentState(StringBuilder sb, FieldDiagnosticsSnapshot snapshot)
        {
            FieldQueueStatus queue = snapshot.Queues ?? new FieldQueueStatus();
            sb.AppendLine("## 当前运行状态");
            sb.AppendLine();
            sb.AppendLine("| 项目 | 当前值 | 证据 |");
            sb.AppendLine("| --- | --- | --- |");
            sb.AppendLine($"| 总体 | {Cell(snapshot.OverallLevel)} | 更新时间 {Cell(snapshot.UpdatedAt.ToString("O"))} |");
            sb.AppendLine($"| 相机 | {Cell(snapshot.CameraStatus)} | PLC {Cell(snapshot.PlcStatus)} |");
            sb.AppendLine($"| 模型 | {Cell(snapshot.CurrentModelName)} | {Cell(snapshot.ModelStatus)} |");
            sb.AppendLine($"| 存储 | {Cell(snapshot.StorageStatus)} | 剩余 {Cell(snapshot.FreeDiskGb.ToString("F2"))} GB |");
            sb.AppendLine($"| 数据库 | {Cell(snapshot.DatabaseStatus)} | 内存 {Cell(snapshot.MemoryMb.ToString())} MB |");
            sb.AppendLine($"| 队列 | {Cell(queue.BacklogLevel)} | 图像 {queue.ImagePending}/{queue.ImageCapacity}, 记录 {queue.RecordPending}/{queue.RecordCapacity} |");
            sb.AppendLine($"| 性能 | P95 {snapshot.RecentInspectionP95Ms}ms | P99 {snapshot.RecentInspectionP99Ms}ms, 最近 {Cell(snapshot.LastInspectionId, 80)} |");
            sb.AppendLine();
        }

        private static void AppendModelAndRecipe(StringBuilder sb, FieldDiagnosticsSnapshot snapshot)
        {
            FieldModelProbeSummary modelProbe = snapshot.ModelProbe ?? new FieldModelProbeSummary();
            sb.AppendLine("## 模型与配方");
            sb.AppendLine();
            sb.AppendLine($"- 当前配方: {Inline(snapshot.RecipeId)} / {Inline(snapshot.RecipeVersion)}");
            sb.AppendLine($"- 目标: {Inline(snapshot.RecipeTargetLabel)} x{snapshot.RecipeTargetCount}");
            sb.AppendLine($"- 推理后端: {Inline(modelProbe.ExecutionProvider)}, GPU Active={modelProbe.GpuActive}");
            sb.AppendLine();
            sb.AppendLine("| 槽位 | 文件 | 注册表 | 版本 | 哈希 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (FieldModelSlotProbe slot in modelProbe.Slots ?? Array.Empty<FieldModelSlotProbe>())
            {
                string registry = slot.RegistryMatched
                    ? $"{slot.RegistryMatchStrategy}/{slot.RegistryStatus}"
                    : "未匹配";
                sb.AppendLine(
                    $"| {Cell(slot.Role)} | {Cell(slot.ModelFileName)} | {Cell(registry)} | {Cell(slot.ModelId)}@{Cell(slot.Version)} | {Cell(slot.ModelHashPrefix)} |");
            }

            sb.AppendLine();
        }

        private static void AppendDiagnosticPackages(StringBuilder sb, FieldHandoffReportRequest request)
        {
            sb.AppendLine("## 诊断包与复核");
            sb.AppendLine();

            IReadOnlyList<ClearFrost.DiagnosticPackageHistoryItem> packages =
                request.DiagnosticPackages ?? Array.Empty<ClearFrost.DiagnosticPackageHistoryItem>();
            if (packages.Count == 0)
            {
                sb.AppendLine("- 本班未发现历史诊断包。");
            }
            else
            {
                sb.AppendLine("| 文件 | 大小 | 状态 | 时间 |");
                sb.AppendLine("| --- | ---: | --- | --- |");
                foreach (ClearFrost.DiagnosticPackageHistoryItem item in packages.Take(MaxDiagnosticPackages))
                {
                    sb.AppendLine(
                        $"| {Cell(item.FileName)} | {item.SizeBytes} | {Cell(item.IntegrityStatus)} | {Cell(item.LastWriteTime.ToString("O"))} |");
                }
            }

            IReadOnlyList<OperationAuditRecord> packageAudits = (request.RecentAuditRecords ?? Array.Empty<OperationAuditRecord>())
                .Where(record =>
                    string.Equals(record.Operation, "DiagnosticPackageExport", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(record.Operation, "DiagnosticPackageVerify", StringComparison.OrdinalIgnoreCase))
                .Take(MaxAuditRecords)
                .ToList();

            sb.AppendLine();
            if (packageAudits.Count == 0)
            {
                sb.AppendLine("- 本班未发现诊断包导出/复核审计记录。");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("| 时间 | 操作 | 状态 | 摘要 |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (OperationAuditRecord record in packageAudits)
            {
                sb.AppendLine(
                    $"| {Cell(record.Timestamp.ToString("O"))} | {Cell(record.Operation)} | {Cell(record.Status.ToString())} | {Cell(record.Details, 520)} |");
            }

            sb.AppendLine();
        }

        private static void AppendMaintenanceAdvice(
            StringBuilder sb,
            FieldDiagnosticsSnapshot snapshot,
            IReadOnlyList<MaintenanceAdviceResolutionRecord> history)
        {
            IReadOnlyList<FieldMaintenanceAdvice> activeAdvice =
                snapshot.MaintenanceAdvice ?? Array.Empty<FieldMaintenanceAdvice>();
            history ??= Array.Empty<MaintenanceAdviceResolutionRecord>();

            sb.AppendLine("## 维护建议闭环");
            sb.AppendLine();
            if (activeAdvice.Count == 0)
            {
                sb.AppendLine("- 当前无未闭环维护建议。");
            }
            else
            {
                sb.AppendLine("| 等级 | 编码 | 标题 | 状态 | 建议 |");
                sb.AppendLine("| --- | --- | --- | --- | --- |");
                foreach (FieldMaintenanceAdvice advice in activeAdvice.Take(MaxMaintenanceHistory))
                {
                    sb.AppendLine(
                        $"| {Cell(advice.Level)} | {Cell(advice.Code)} | {Cell(advice.Title)} | {Cell(advice.ResolutionStatus)} | {Cell(advice.Advice, 180)} |");
                }
            }

            sb.AppendLine();
            if (history.Count == 0)
            {
                sb.AppendLine("- 暂无维护建议处理/复检记录。");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("| 时间 | 状态 | 维护项 | 操作员 | 结论 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (MaintenanceAdviceResolutionRecord record in history.Take(MaxMaintenanceHistory))
            {
                sb.AppendLine(
                    $"| {Cell(record.ActionAt.ToString("O"))} | {Cell(record.Status)} | {Cell(record.Title)} | {Cell(record.OperatorId)} | {Cell(record.Message, 160)} |");
            }

            sb.AppendLine();
        }

        private static void AppendAuditTrail(
            StringBuilder sb,
            IReadOnlyList<OperationAuditRecord> auditRecords,
            OperationAuditChainVerificationResult? auditChain)
        {
            auditRecords ??= Array.Empty<OperationAuditRecord>();
            sb.AppendLine("## 最近关键审计");
            sb.AppendLine();
            if (auditRecords.Count == 0)
            {
                sb.AppendLine("- 暂无关键审计记录。");
                sb.AppendLine();
                AppendAuditChain(sb, auditChain);
                return;
            }

            sb.AppendLine("| 时间 | 操作 | 状态 | 操作员 | 阻断 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (OperationAuditRecord record in auditRecords.Take(MaxAuditRecords))
            {
                sb.AppendLine(
                    $"| {Cell(record.Timestamp.ToString("O"))} | {Cell(record.Operation)} | {Cell(record.Status.ToString())} | {Cell(record.OperatorId)} | {Cell(record.FailureBlocker)} |");
            }

            sb.AppendLine();
            AppendAuditChain(sb, auditChain);
        }

        private static void AppendAuditChain(StringBuilder sb, OperationAuditChainVerificationResult? auditChain)
        {
            auditChain ??= new OperationAuditChainVerificationResult();
            sb.AppendLine("- 审计链校验:");
            sb.AppendLine(
                $"  - Status={Inline(auditChain.Status)}, Verified={auditChain.VerifiedRecords}/{auditChain.TotalRecords}, Findings={auditChain.Findings.Count}");
            if (!string.IsNullOrWhiteSpace(auditChain.LastRecordSha256))
            {
                sb.AppendLine($"  - LastRecordSha256={Inline(auditChain.LastRecordSha256)}");
            }

            foreach (OperationAuditChainFinding finding in auditChain.Findings.Take(3))
            {
                sb.AppendLine(
                    $"  - {Inline(finding.ErrorCode)}: {Inline(finding.Message)} ({Inline(Path.GetFileName(finding.FilePath))}:{finding.LineNumber})");
            }

            sb.AppendLine();
        }

        private static void AppendShiftTasks(StringBuilder sb, FieldDiagnosticsSnapshot snapshot)
        {
            IReadOnlyList<FieldShiftTask> tasks = snapshot.ShiftTasks ?? Array.Empty<FieldShiftTask>();
            sb.AppendLine("## 班次待办");
            sb.AppendLine();
            if (tasks.Count == 0)
            {
                sb.AppendLine("- 当前无班次待办。");
                sb.AppendLine();
                return;
            }

            sb.AppendLine("| 等级 | 状态 | 升级 | 责任组 | 首次发现 | 截止 | 待办 | 处理动作 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (FieldShiftTask task in tasks.Take(10))
            {
                sb.AppendLine(
                    $"| {Cell(task.Level)} | {Cell(task.Status)} | {Cell(task.EscalationLevel)} | {Cell(task.SuggestedOwner)} | {Cell(task.FirstSeenAt?.ToString("O"))} | {Cell(task.DueAt?.ToString("O"))} | {Cell(task.Title)} | {Cell(task.Action, 180)} |");
            }

            sb.AppendLine();
        }

        private static void AppendNextShiftFocus(
            StringBuilder sb,
            FieldHandoffReportRequest request,
            string overallStatus)
        {
            FieldDiagnosticsSnapshot snapshot = request.FieldDiagnostics ?? new FieldDiagnosticsSnapshot();
            FieldQueueStatus queue = snapshot.Queues ?? new FieldQueueStatus();
            IReadOnlyList<FieldMaintenanceAdvice> activeAdvice =
                snapshot.MaintenanceAdvice ?? Array.Empty<FieldMaintenanceAdvice>();
            IReadOnlyList<MaintenanceAdviceResolutionRecord> history =
                request.MaintenanceAdviceHistory ?? Array.Empty<MaintenanceAdviceResolutionRecord>();
            IReadOnlyList<OperationAuditRecord> audits =
                request.RecentAuditRecords ?? Array.Empty<OperationAuditRecord>();

            var focus = new List<string>();
            if (HasStartupBlocker(snapshot))
            {
                focus.Add("先处理启动阻断项，确认检测入口和 PLC 监听不再被拦截。");
            }

            foreach (FieldMaintenanceAdvice advice in activeAdvice
                .Where(item => string.Equals(item.Level, "critical", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(item.Level, "warning", StringComparison.OrdinalIgnoreCase))
                .Take(4))
            {
                focus.Add($"{advice.Code}: {advice.Title}，建议 {advice.Advice}");
            }

            foreach (MaintenanceAdviceResolutionRecord record in history
                .Where(item => string.Equals(item.Status, MaintenanceAdviceResolutionStatuses.RecheckFailed, StringComparison.OrdinalIgnoreCase))
                .Take(3))
            {
                focus.Add($"{record.Code}: 复检未通过，下一班继续确认。");
            }

            if (!string.Equals(queue.BacklogLevel, "Ok", StringComparison.OrdinalIgnoreCase))
            {
                focus.Add($"队列状态为 {queue.BacklogLevel}，建议观察图像/记录写入速度和磁盘压力。");
            }

            bool hasHealthyPackageVerify = audits.Any(record =>
                string.Equals(record.Operation, "DiagnosticPackageVerify", StringComparison.OrdinalIgnoreCase) &&
                record.Status == OperationAuditStatus.Succeeded &&
                record.Details.Contains("IntegrityStatus=Healthy", StringComparison.OrdinalIgnoreCase));
            if (!hasHealthyPackageVerify)
            {
                focus.Add("交班前建议复核最近诊断包，确保远程排障材料可用。");
            }

            if (focus.Count == 0)
            {
                focus.Add(string.Equals(overallStatus, "Ready", StringComparison.OrdinalIgnoreCase)
                    ? "当前无阻断项，下一班按标准巡检继续观察。"
                    : "暂无自动生成的专项建议，请结合现场工况复核。");
            }

            sb.AppendLine("## 下一班关注项");
            sb.AppendLine();
            foreach (string item in focus.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
            {
                sb.AppendLine($"- {Inline(item)}");
            }
        }

        private static string ResolveOverallStatus(FieldHandoffReportRequest request)
        {
            FieldDiagnosticsSnapshot snapshot = request.FieldDiagnostics ?? new FieldDiagnosticsSnapshot();
            bool hasBlocking = HasStartupBlocker(snapshot);
            bool hasCriticalAdvice = (snapshot.MaintenanceAdvice ?? Array.Empty<FieldMaintenanceAdvice>())
                .Any(item => string.Equals(item.Level, "critical", StringComparison.OrdinalIgnoreCase));
            int failedRechecks = CountFailedRechecks(request.MaintenanceAdviceHistory);
            bool hasFailedDiagnosticPackageAudit = (request.RecentAuditRecords ?? Array.Empty<OperationAuditRecord>())
                .Any(record =>
                    string.Equals(record.Operation, "DiagnosticPackageVerify", StringComparison.OrdinalIgnoreCase) &&
                    record.Status == OperationAuditStatus.Failed);
            bool auditChainBlocking = string.Equals(
                request.AuditChainVerification?.Status,
                "Blocking",
                StringComparison.OrdinalIgnoreCase);

            if (hasBlocking || hasCriticalAdvice || failedRechecks > 0 || hasFailedDiagnosticPackageAudit || auditChainBlocking)
            {
                return "Blocked";
            }

            bool hasActiveAdvice = (snapshot.MaintenanceAdvice?.Count ?? 0) > 0;
            bool queueNeedsAttention = !string.Equals(
                snapshot.Queues?.BacklogLevel ?? "Ok",
                "Ok",
                StringComparison.OrdinalIgnoreCase);
            bool packageMissing = (request.DiagnosticPackages?.Count ?? 0) == 0;
            bool auditChainWarning = string.Equals(
                request.AuditChainVerification?.Status,
                "Warning",
                StringComparison.OrdinalIgnoreCase);
            return hasActiveAdvice || queueNeedsAttention || packageMissing || auditChainWarning ? "Attention" : "Ready";
        }

        private static int CountFailedRechecks(IReadOnlyList<MaintenanceAdviceResolutionRecord>? history)
        {
            return (history ?? Array.Empty<MaintenanceAdviceResolutionRecord>())
                .Count(record => string.Equals(
                    record.Status,
                    MaintenanceAdviceResolutionStatuses.RecheckFailed,
                    StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasStartupBlocker(FieldDiagnosticsSnapshot snapshot)
        {
            return snapshot.StartupDiagnostics?.Items?.Any(item =>
                item.IsBlocking && item.Status == StartupDiagnosticStatus.Fail) == true;
        }

        private static string Inline(string? value)
        {
            return Clean(value, 220);
        }

        private static string Cell(string? value, int maxChars = 120)
        {
            return Clean(value, maxChars).Replace("|", "/", StringComparison.Ordinal);
        }

        private static string Clean(string? value, int maxChars)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
            text = text
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
            text = RedactSensitiveText(text);
            if (text.Length <= maxChars)
            {
                return text;
            }

            return text[..Math.Max(1, maxChars - 1)] + "...";
        }

        private static string RedactSensitiveText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            text = WindowsPathRegex.Replace(text, "<redacted-path>");
            return UncPathRegex.Replace(text, "<redacted-path>");
        }
    }
}
