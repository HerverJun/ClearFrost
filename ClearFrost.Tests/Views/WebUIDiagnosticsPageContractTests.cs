using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIDiagnosticsPageContractTests
{
    [Fact]
    public void WebUi诊断调试页_包含现场关键元素和命令入口()
    {
        string root = FindRepositoryRoot();
        string indexHtml = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        string renderMainJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));
        string stateJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "state.js"));
        string styleCss = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "css", "style.css"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        foreach (string htmlToken in new[]
        {
            "field-diagnostics-modal",
            "现场诊断",
            "现场体检面板",
            "diag-camera-status",
            "diag-plc-status",
            "diag-trigger-source",
            "diag-current-model",
            "diag-storage-status",
            "diag-production-readiness",
            "diag-production-guidance",
            "当前是否可以生产",
            "待处理问题",
            "常用操作",
            "工程师详情（高级）",
            "供视觉/设备工程师排查使用，一线操作无需关注。",
            "diag-last-inspection-id",
            "diag-p95",
            "diag-p99",
            "diag-image-queue",
            "diag-record-queue",
            "diag-last-error",
            "diag-acceptance-list",
            "diag-model-slot-list",
            "diag-recipe-version",
            "diag-startup-blockers",
            "diag-queue-health",
            "diag-audit-chain",
            "diag-maintenance-advice",
            "diag-maintenance-history",
            "diag-shift-task-list",
            "diag-handoff-report-path",
            "diag-handoff-status",
            "diag-handoff-size",
            "diag-handoff-generated-at",
            "diag-handoff-report-history",
            "exportFieldHandoffReport",
            "copyFieldHandoffReportSummary",
            "requestFieldHandoffReportHistory",
            "diag-package-integrity",
            "diag-package-sha",
            "diag-index-sha",
            "diag-package-size",
            "diag-integrity-status",
            "diag-index-entry-count",
            "diag-package-history",
            "audit-chain-status",
            "audit-chain-badge",
            "audit-chain-last-hash",
            "audit-operation-options",
            "audit-failure-filter",
            "audit-role-filter",
            "clearAuditFilters",
            "copyAuditChainSummary",
            "verifyAuditChain",
            "status-trigger-source",
            "status-trigger-source-text",
            "status-trigger-source-dot",
            "copyFaultSummary",
            "copyDiagnosticPackageSummary",
            "requestDiagnosticPackageHistory",
            "export_diagnostic_package",
            "field_debug_step_capture",
            "field_debug_step_infer",
            "field_debug_plc_write_test",
            "field_debug_barcode_read_test",
            "field_debug_simulate_trigger"
        })
        {
            indexHtml.Should().Contain(htmlToken);
        }

        renderMainJs.Should().Contain("renderFieldDiagnostics");
        renderMainJs.Should().Contain("renderFieldAcceptanceChecklist");
        renderMainJs.Should().Contain("renderModelSlotChecklist");
        renderMainJs.Should().Contain("renderAuditChainChecklist");
        renderMainJs.Should().Contain("renderMaintenanceAdviceList");
        renderMainJs.Should().Contain("下一步：");
        renderMainJs.Should().NotContain("const evidence = item.evidence || item.Evidence");
        renderMainJs.Should().Contain("renderMaintenanceAdviceHistory");
        renderMainJs.Should().Contain("renderShiftTaskBoard");
        renderMainJs.Should().Contain("acknowledgeShiftTask");
        renderMainJs.Should().Contain("recheckShiftTask");
        renderMainJs.Should().Contain("shiftTaskActionResult");
        renderMainJs.Should().Contain("firstSeenAt");
        renderMainJs.Should().Contain("suggestedOwner");
        renderMainJs.Should().Contain("dueAt");
        renderMainJs.Should().Contain("escalationLevel");
        renderMainJs.Should().Contain("isOverdue");
        renderMainJs.Should().Contain("renderMaintenanceHistoryRecheckButton");
        renderMainJs.Should().Contain("acknowledgeMaintenanceAdvice");
        renderMainJs.Should().Contain("recheckMaintenanceAdvice");
        renderMainJs.Should().Contain("exportFieldHandoffReport");
        renderMainJs.Should().Contain("fieldHandoffReportResult");
        renderMainJs.Should().Contain("renderFieldHandoffReportHistory");
        renderMainJs.Should().Contain("copyFieldHandoffReportSummary");
        renderMainJs.Should().Contain("fieldHandoffReportHistoryResult");
        renderMainJs.Should().Contain("function getDiagnosticStatusBadgeClass");
        renderMainJs.Should().Contain("\"healthy\", \"ready\", \"pass\", \"passed\", \"ok\", \"success\"");
        renderMainJs.Should().Contain("\"blocking\", \"blocked\", \"failed\", \"fail\", \"error\", \"critical\"");
        renderMainJs.Should().Contain("const statusClass = getDiagnosticStatusBadgeClass(status);");
        renderMainJs.Should().Contain("已验证 ${escapeHtml(verifiedRecords)}/${escapeHtml(totalRecords)}");
        renderMainJs.Should().Contain("异常 ${escapeHtml(findingCount)}");
        renderMainJs.Should().Contain("packageSha256");
        renderMainJs.Should().Contain("diag-package-sha");
        renderMainJs.Should().Contain("integrityStatus");
        renderMainJs.Should().Contain("buildDiagnosticPackageSummaryText");
        renderMainJs.Should().Contain("buildFaultSummaryText");
        renderMainJs.Should().Contain("copyFaultSummary");
        renderMainJs.Should().Contain("updateTriggerSourceStatus");
        renderMainJs.Should().Contain("getVisibleMaintenanceAdvice");
        renderMainJs.Should().Contain("可进行手动检测；自动生产触发未启用。");
        renderMainJs.Should().Contain("需要连接串口光电触发器后才能自动生产。");
        renderMainJs.Should().Contain("需要连接 PLC 后才能自动生产。");
        renderMainJs.Should().Contain("当前暂无待处理问题，设备状态可以继续生产。");
        renderMainJs.Should().Contain("copyDiagnosticPackageSummary");
        renderMainJs.Should().Contain("renderDiagnosticPackageHistory");
        renderMainJs.Should().Contain("verifyDiagnosticPackage");
        historyJs.Should().Contain("verifyAuditChain");
        historyJs.Should().Contain("verify_audit_chain");
        historyJs.Should().Contain("auditChainVerification");
        historyJs.Should().Contain("function resetAuditChainVerificationState");
        historyJs.Should().Contain("resetAuditChainVerificationState();");
        historyJs.Should().Contain("let pendingAuditRecordsRequestId = \"\"");
        historyJs.Should().Contain("let pendingAuditExportRequestId = \"\"");
        historyJs.Should().Contain("let pendingAuditChainRequestId = \"\"");
        historyJs.Should().Contain("let auditErrorSource = \"\"");
        historyJs.Should().Contain("const AuditBridgeErrorMessage = \"前端通信失败，请刷新页面后重试\"");
        historyJs.Should().Contain("const AuditExportEmptyPathMessage = \"审计 CSV 导出未返回文件路径\"");
        historyJs.Should().Contain("function resetAuditModalSessionState");
        historyJs.Should().Contain("resetAuditModalSessionState();");
        historyJs.Should().Contain("pendingAuditRecordsRequestId = \"__audit_modal_open_records__\"");
        historyJs.Should().Contain("pendingAuditExportRequestId = \"__audit_modal_open_export__\"");
        historyJs.Should().Contain("pendingAuditChainRequestId = \"__audit_modal_open_chain__\"");
        historyJs.Should().Contain("pendingAuditRecordsRequestId = \"__audit_modal_closed_records__\"");
        historyJs.Should().Contain("pendingAuditExportRequestId = \"__audit_modal_closed_export__\"");
        historyJs.Should().Contain("pendingAuditChainRequestId = \"__audit_modal_closed_chain__\"");
        historyJs.Should().Contain("function isAuditModalOpen");
        historyJs.Should().Contain("const modal = byId(\"audit-modal\")");
        historyJs.Should().Contain("return Boolean(modal && !modal.classList.contains(\"hidden\"))");
        historyJs.Should().Contain("if (!isAuditModalOpen())");
        historyJs.Should().Contain("auditErrorSource = \"\"");
        historyJs.Should().Contain("function getMessageRequestId");
        historyJs.Should().Contain("function isLocalAuditResponse");
        historyJs.Should().Contain("return message?.local === true || message?.Local === true");
        historyJs.Should().Contain("function isStaleAuditResponse");
        historyJs.Should().Contain("if (isLocalAuditResponse(message)) return false");
        historyJs.Should().Contain("const pendingId = String(pendingRequestId || \"\").trim()");
        historyJs.Should().Contain("if (!pendingId) return true");
        historyJs.Should().Contain("if (!requestId) return true");
        historyJs.Should().Contain("return requestId !== pendingId");
        historyJs.Should().Contain("function sendAuditCommand");
        historyJs.Should().Contain("function setAuditError(message, source = \"\")");
        historyJs.Should().Contain("if (!text && source && auditErrorSource && auditErrorSource !== source) return");
        historyJs.Should().Contain("auditErrorSource = text ? (source || \"general\") : \"\"");
        historyJs.Should().Contain("function getAuditCommandErrorSource");
        historyJs.Should().Contain("if (cmd === \"query_audit_records\") return \"auditRecords\"");
        historyJs.Should().Contain("if (cmd === \"export_audit_records\") return \"auditExport\"");
        historyJs.Should().Contain("if (cmd === \"verify_audit_chain\") return \"auditChainVerification\"");
        historyJs.Should().Contain("bridge?.sendCommand?.(cmd, value)");
        historyJs.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
        historyJs.Should().Contain("setAuditError(AuditBridgeErrorMessage, getAuditCommandErrorSource(cmd))");
        historyJs.Should().Contain("window.showToast?.(AuditBridgeErrorMessage, \"error\", 1800)");
        historyJs.Should().Contain("return \"__audit_command_failed__\"");
        historyJs.Should().Contain("pendingAuditRecordsRequestId = \"__invalid_audit_records_request__\"");
        historyJs.Should().Contain("pendingAuditExportRequestId = \"__invalid_audit_export_request__\"");
        historyJs.Should().Contain("pendingAuditRecordsRequestId = sendAuditCommand(\"query_audit_records\", query");
        historyJs.Should().Contain("pendingAuditExportRequestId = sendAuditCommand(\"export_audit_records\", query");
        historyJs.Should().Contain("pendingAuditChainRequestId = sendAuditCommand(\"verify_audit_chain\", {}");
        historyJs.Should().Contain("if (isStaleAuditResponse(message, pendingAuditRecordsRequestId))");
        historyJs.Should().Contain("if (isStaleAuditResponse(message, pendingAuditExportRequestId))");
        historyJs.Should().Contain("if (isStaleAuditResponse(message, pendingAuditChainRequestId))");
        historyJs.Should().Contain("}, { local: true });");
        historyJs.Should().Contain("errorCode: \"AuditBridgeUnavailable\"");
        historyJs.Should().Contain("function clearAuditFilters");
        historyJs.Should().Contain("function copyAuditChainSummary");
        historyJs.Should().Contain("function copyAuditExportPath");
        historyJs.Should().Contain("function buildAuditChainSummaryText");
        historyJs.Should().Contain("function writeClipboardText");
        historyJs.Should().Contain("function buildAuditFilterSummary");
        historyJs.Should().Contain("function parseAuditDateValue");
        historyJs.Should().Contain("function validateAuditQuery");
        historyJs.Should().Contain("开始时间格式无效");
        historyJs.Should().Contain("结束时间格式无效");
        historyJs.Should().Contain("开始时间不能晚于结束时间");
        historyJs.Should().Contain("请调整时间范围后再查询");
        historyJs.Should().Contain("function setAuditCountBadge");
        historyJs.Should().Contain("function setAuditExportPath(text, title = \"\")");
        historyJs.Should().Contain("node.title = title || text || \"\"");
        historyJs.Should().Contain("setAuditCountBadge(\"加载中\")");
        historyJs.Should().Contain("setAuditCountBadge(\"0 条\")");
        historyJs.Should().Contain("setAuditExportPath(\"正在导出审计 CSV...\")");
        historyJs.Should().Contain("setAuditExportPath(\"\")");
        historyJs.Should().Contain("setAuditExportPath(`已导出: ${path}`, path)");
        historyJs.Should().Contain("setAuditError(error, \"auditRecords\")");
        historyJs.Should().Contain("setAuditError(\"\", \"auditRecords\")");
        historyJs.Should().Contain("setAuditError(error, \"auditExport\")");
        historyJs.Should().Contain("const path = String(data?.path || data?.Path || \"\").trim()");
        historyJs.Should().Contain("if (!path)");
        historyJs.Should().Contain("setAuditError(AuditExportEmptyPathMessage, \"auditExport\")");
        historyJs.Should().Contain("setAuditError(\"\", \"auditExport\")");
        historyJs.Should().Contain("setAuditError(error, \"auditChainVerification\")");
        historyJs.Should().Contain("setAuditError(\"\", \"auditChainVerification\")");
        historyJs.Should().Contain("let lastAuditExportPath = \"\"");
        historyJs.Should().Contain("lastAuditExportPath = path");
        historyJs.Should().Contain("审计查询失败");
        historyJs.Should().Contain("${escapeHtml(error)}");
        historyJs.Should().Contain("请先导出审计 CSV");
        historyJs.Should().Contain("审计导出路径已复制");
        historyJs.Should().Contain("copyAuditExportPath,");
        historyJs.Should().Contain("const validationError = validateAuditQuery(query)");
        historyJs.Should().Contain("sendAuditCommand(\"query_audit_records\", query");
        historyJs.Should().Contain("sendAuditCommand(\"export_audit_records\", query");
        historyJs.Should().Contain("const source = query || buildAuditQuery()");
        historyJs.Should().Contain("filters.push(`时间 ${startTime || \"-\"} 至 ${endTime || \"-\"}`)");
        historyJs.Should().Contain("role: byId(\"audit-role-filter\")?.value || \"\"");
        historyJs.Should().Contain("const role = source.role || source.Role || \"\"");
        historyJs.Should().Contain("filters.push(`角色 ${formatProductionRole(role) || role}`)");
        historyJs.Should().Contain("filters.push(`失败原因 ${failureReason}`)");
        historyJs.Should().Contain("当前筛选：${escapeHtml(filterSummary)}");
        historyJs.Should().Contain("const title = filterSummary ? \"未匹配到审计记录\" : \"暂无审计记录\"");
        historyJs.Should().Contain("let clipboardError = null");
        historyJs.Should().Contain("catch (error)");
        historyJs.Should().Contain("clipboardError = error");
        historyJs.Should().Contain("const target = document.body || document.documentElement");
        historyJs.Should().Contain("document.execCommand?.(\"copy\") === true");
        historyJs.Should().Contain("textarea.remove();");
        historyJs.Should().Contain("lastAuditChainVerification = data || {}");
        historyJs.Should().Contain("lastAuditChainVerification = null");
        historyJs.Should().Contain("countNode.textContent = \"已验证 -/-\"");
        historyJs.Should().Contain("findingNode.textContent = \"异常 -\"");
        historyJs.Should().Contain("hashNode.textContent = \"最后 -\"");
        historyJs.Should().Contain("hashNode.title = \"\"");
        historyJs.Should().Contain("messageNode.textContent = \"\"");
        historyJs.Should().Contain("messageNode.title = \"\"");
        historyJs.Should().Contain("source.checkedAt || source.CheckedAt");
        historyJs.Should().Contain("已验证 ${verifiedRecords}/${totalRecords}");
        historyJs.Should().Contain("异常 ${findingCount}");
        historyJs.Should().Contain("最后 ${shortAuditHash(lastHash) || \"-\"}");
        historyJs.Should().Contain("\"audit-start-time\", \"audit-end-time\", \"audit-operation-filter\", \"audit-operator-filter\", \"audit-role-filter\", \"audit-status-filter\", \"audit-failure-filter\"");
        historyJs.Should().Contain("failureReason: byId(\"audit-failure-filter\")?.value || \"\"");
        historyJs.Should().Contain("function findLabel");
        historyJs.Should().Contain("function replaceLabelTokens");
        historyJs.Should().Contain("function normalizeAuditOperationFilter");
        historyJs.Should().Contain("const AuditChainStatusLabels = Object.freeze");
        historyJs.Should().Contain("Healthy: \"正常\"");
        historyJs.Should().Contain("Warning: \"有警告\"");
        historyJs.Should().Contain("Blocking: \"阻断\"");
        historyJs.Should().Contain("Unavailable: \"不可用\"");
        historyJs.Should().Contain("const AuditChainSeverityLabels = Object.freeze");
        historyJs.Should().Contain("Blocking: \"阻断\"");
        historyJs.Should().Contain("Warning: \"警告\"");
        historyJs.Should().Contain("function formatAuditChainStatus");
        historyJs.Should().Contain("function formatAuditChainStatusForSummary");
        historyJs.Should().Contain("function getAuditChainFindings");
        historyJs.Should().Contain("function formatAuditChainSeverity");
        historyJs.Should().Contain("function limitAuditSummaryText");
        historyJs.Should().Contain("function formatAuditChainFindingHint(finding, fallback = \"\", preferFullPath = false)");
        historyJs.Should().Contain("? (filePath || auditFileName || entry)");
        historyJs.Should().Contain(": (auditFileName || filePath || entry)");
        historyJs.Should().Contain("const place = location ? `${location}${line ? `:${line}` : \"\"}` : (line ? `行 ${line}` : \"\")");
        historyJs.Should().Contain("const message = limitAuditSummaryText(source.message || source.Message || \"\", 80)");
        historyJs.Should().Contain("const hint = formatAuditChainFindingHint(firstFinding, error)");
        historyJs.Should().Contain("messageNode.textContent = hint");
        historyJs.Should().Contain("messageNode.title = formatAuditChainFindingHint(firstFinding, error, true)");
        historyJs.Should().Contain("function formatAuditHashComparison");
        historyJs.Should().Contain("return `${label} 期望 ${expectedText} 实际 ${actualText}`");
        historyJs.Should().Contain("replace(/\\s+/g, \" \").trim()");
        historyJs.Should().Contain("if (Array.isArray(source?.findings)) return source.findings");
        historyJs.Should().Contain("if (Array.isArray(source?.Findings)) return source.Findings");
        historyJs.Should().Contain("const findings = getAuditChainFindings(source)");
        historyJs.Should().Contain("const findings = getAuditChainFindings(data)");
        historyJs.Should().Contain("const listedFindings = findings.slice(0, 3)");
        historyJs.Should().Contain("const filePath = finding.filePath || finding.FilePath || \"\"");
        historyJs.Should().Contain("const auditFileName = finding.auditFileName || finding.AuditFileName || \"\"");
        historyJs.Should().Contain("const location = filePath || auditFileName || entry");
        historyJs.Should().Contain("const severity = finding.severity || finding.Severity || \"\"");
        historyJs.Should().Contain("const severityText = formatAuditChainSeverity(severity) || severity");
        historyJs.Should().Contain("const message = limitAuditSummaryText(finding.message || finding.Message || \"\")");
        historyJs.Should().Contain("const expectedPreviousSha256 = finding.expectedPreviousSha256 || finding.ExpectedPreviousSha256 || \"\"");
        historyJs.Should().Contain("const actualPreviousSha256 = finding.actualPreviousSha256 || finding.ActualPreviousSha256 || \"\"");
        historyJs.Should().Contain("const expectedRecordSha256 = finding.expectedRecordSha256 || finding.ExpectedRecordSha256 || \"\"");
        historyJs.Should().Contain("const actualRecordSha256 = finding.actualRecordSha256 || finding.ActualRecordSha256 || \"\"");
        historyJs.Should().Contain("severityText ? `级别 ${severityText}` : \"\"");
        historyJs.Should().Contain("message ? `消息 ${message}` : \"\"");
        historyJs.Should().Contain("formatAuditHashComparison(\"上一哈希\", expectedPreviousSha256, actualPreviousSha256)");
        historyJs.Should().Contain("formatAuditHashComparison(\"记录哈希\", expectedRecordSha256, actualRecordSha256)");
        historyJs.Should().Contain("const remainingFindings = Math.max(0, findings.length - listedFindings.length)");
        historyJs.Should().Contain("其余异常: ${remainingFindings} 条未列出");
        historyJs.Should().Contain("const statusText = formatAuditChainStatusForSummary(status)");
        historyJs.Should().Contain("return label && label !== raw ? `${label} (${raw})` : raw");
        historyJs.Should().Contain("状态: ${statusText || \"-\"}");
        historyJs.Should().Contain("badge.textContent = formatAuditChainStatus(status) || status");
        historyJs.Should().Contain("operation: normalizeAuditOperationFilter(byId(\"audit-operation-filter\")?.value)");
        historyJs.Should().Contain("raw.toLowerCase() === value.toLowerCase()");
        historyJs.Should().Contain("String(label || \"\").trim().toLowerCase() === value.toLowerCase()");
        historyJs.Should().Contain("function getAuditChainStatusClass");
        historyJs.Should().Contain("function getAuditRecordStatusClass");
        historyJs.Should().Contain("key.toLowerCase() === normalized.toLowerCase()");
        historyJs.Should().Contain("DiagnosticPackageExport: \"导出诊断包\"");
        historyJs.Should().Contain("DiagnosticPackageVerify: \"复核诊断包\"");
        historyJs.Should().Contain("FieldHandoffReportExport: \"导出交接报告\"");
        historyJs.Should().Contain("MaintenanceAdviceAction: \"维护建议处理\"");
        historyJs.Should().Contain("ShiftTaskAction: \"班次待办处理\"");
        historyJs.Should().Contain("FieldDebugPlcWriteTest: \"PLC 写入测试\"");
        historyJs.Should().Contain("\"failed\", \"denied\", \"blocking\", \"error\"");
        historyJs.Should().Contain("\"requested\", \"pending\", \"warning\"");
        historyJs.Should().Contain("recordSha256");
        historyJs.Should().Contain("shortAuditHash");
        indexHtml.Should().Contain("list=\"audit-operation-options\"");
        indexHtml.Should().Contain("data-action=\"clearAuditFilters\"");
        indexHtml.Should().Contain("data-action=\"copyAuditChainSummary\"");
        indexHtml.Should().Contain("data-action=\"copyAuditExportPath\"");
        indexHtml.Should().Contain("id=\"audit-export-path\" class=\"block min-w-0 max-w-[52vw] md:max-w-[520px] text-[10px] text-ink-400 font-mono truncate\"");
        indexHtml.Should().Contain("复制路径");
        indexHtml.Should().Contain("id=\"audit-failure-filter\"");
        indexHtml.Should().Contain("placeholder=\"失败原因\"");
        indexHtml.Should().Contain("id=\"audit-role-filter\"");
        indexHtml.Should().Contain("<option value=\"\">全部角色</option>");
        indexHtml.Should().Contain("<option value=\"Operator\">操作员</option>");
        indexHtml.Should().Contain("<option value=\"ShiftLead\">班组长</option>");
        indexHtml.Should().Contain("<option value=\"Engineer\">工程师</option>");
        indexHtml.Should().Contain("flex flex-wrap items-center gap-2");
        indexHtml.Should().Contain("已验证 -/-");
        indexHtml.Should().Contain("异常 -");
        indexHtml.Should().Contain("最后 -");
        indexHtml.Should().Contain("id=\"audit-chain-message\" class=\"text-rouge-600 truncate max-w-[420px] min-w-0\"");
        indexHtml.Should().Contain("value=\"导出诊断包\" label=\"DiagnosticPackageExport\"");
        indexHtml.Should().Contain("value=\"复核诊断包\" label=\"DiagnosticPackageVerify\"");
        indexHtml.Should().Contain("value=\"导出交接报告\" label=\"FieldHandoffReportExport\"");
        indexHtml.Should().Contain("value=\"维护建议处理\" label=\"MaintenanceAdviceAction\"");
        indexHtml.Should().Contain("value=\"班次待办处理\" label=\"ShiftTaskAction\"");
        indexHtml.Should().Contain("value=\"PLC 写入测试\" label=\"FieldDebugPlcWriteTest\"");
        bundleJs.Should().Contain("renderFieldAcceptanceChecklist");
        bundleJs.Should().Contain("renderAuditChainChecklist");
        bundleJs.Should().Contain("renderMaintenanceAdviceList");
        bundleJs.Should().Contain("下一步：");
        bundleJs.Should().NotContain("const evidence = item.evidence || item.Evidence");
        bundleJs.Should().Contain("renderMaintenanceAdviceHistory");
        bundleJs.Should().Contain("renderShiftTaskBoard");
        bundleJs.Should().Contain("acknowledgeShiftTask");
        bundleJs.Should().Contain("recheckShiftTask");
        bundleJs.Should().Contain("shiftTaskActionResult");
        bundleJs.Should().Contain("firstSeenAt");
        bundleJs.Should().Contain("suggestedOwner");
        bundleJs.Should().Contain("dueAt");
        bundleJs.Should().Contain("escalationLevel");
        bundleJs.Should().Contain("isOverdue");
        bundleJs.Should().Contain("renderMaintenanceHistoryRecheckButton");
        bundleJs.Should().Contain("acknowledgeMaintenanceAdvice");
        bundleJs.Should().Contain("recheckMaintenanceAdvice");
        bundleJs.Should().Contain("exportFieldHandoffReport");
        bundleJs.Should().Contain("fieldHandoffReportResult");
        bundleJs.Should().Contain("renderFieldHandoffReportHistory");
        bundleJs.Should().Contain("copyFieldHandoffReportSummary");
        bundleJs.Should().Contain("fieldHandoffReportHistoryResult");
        bundleJs.Should().Contain("function getDiagnosticStatusBadgeClass");
        bundleJs.Should().Contain("\"healthy\", \"ready\", \"pass\", \"passed\", \"ok\", \"success\"");
        bundleJs.Should().Contain("\"blocking\", \"blocked\", \"failed\", \"fail\", \"error\", \"critical\"");
        bundleJs.Should().Contain("const statusClass = getDiagnosticStatusBadgeClass(status);");
        bundleJs.Should().Contain("已验证 ${escapeHtml(verifiedRecords)}/${escapeHtml(totalRecords)}");
        bundleJs.Should().Contain("异常 ${escapeHtml(findingCount)}");
        bundleJs.Should().Contain("diag-model-slot-list");
        bundleJs.Should().Contain("diag-audit-chain");
        bundleJs.Should().Contain("diag-maintenance-advice");
        bundleJs.Should().Contain("diag-shift-task-list");
        bundleJs.Should().Contain("diag-handoff-report-path");
        bundleJs.Should().Contain("diag-handoff-report-history");
        bundleJs.Should().Contain("diag-package-sha");
        bundleJs.Should().Contain("diag-integrity-status");
        bundleJs.Should().Contain("buildDiagnosticPackageSummaryText");
        bundleJs.Should().Contain("buildFaultSummaryText");
        bundleJs.Should().Contain("copyFaultSummary");
        bundleJs.Should().Contain("updateTriggerSourceStatus");
        bundleJs.Should().Contain("getVisibleMaintenanceAdvice");
        bundleJs.Should().Contain("可进行手动检测；自动生产触发未启用。");
        bundleJs.Should().Contain("需要连接串口光电触发器后才能自动生产。");
        bundleJs.Should().Contain("需要连接 PLC 后才能自动生产。");
        bundleJs.Should().Contain("当前暂无待处理问题，设备状态可以继续生产。");
        bundleJs.Should().Contain("copyDiagnosticPackageSummary");
        bundleJs.Should().Contain("renderDiagnosticPackageHistory");
        bundleJs.Should().Contain("verifyDiagnosticPackage");
        bundleJs.Should().Contain("verifyAuditChain");
        bundleJs.Should().Contain("auditChainVerification");
        bundleJs.Should().Contain("function resetAuditChainVerificationState");
        bundleJs.Should().Contain("resetAuditChainVerificationState();");
        bundleJs.Should().Contain("let pendingAuditRecordsRequestId = \"\"");
        bundleJs.Should().Contain("let pendingAuditExportRequestId = \"\"");
        bundleJs.Should().Contain("let pendingAuditChainRequestId = \"\"");
        bundleJs.Should().Contain("let auditErrorSource = \"\"");
        bundleJs.Should().Contain("const AuditBridgeErrorMessage = \"前端通信失败，请刷新页面后重试\"");
        bundleJs.Should().Contain("const AuditExportEmptyPathMessage = \"审计 CSV 导出未返回文件路径\"");
        bundleJs.Should().Contain("function resetAuditModalSessionState");
        bundleJs.Should().Contain("resetAuditModalSessionState();");
        bundleJs.Should().Contain("pendingAuditRecordsRequestId = \"__audit_modal_open_records__\"");
        bundleJs.Should().Contain("pendingAuditExportRequestId = \"__audit_modal_open_export__\"");
        bundleJs.Should().Contain("pendingAuditChainRequestId = \"__audit_modal_open_chain__\"");
        bundleJs.Should().Contain("pendingAuditRecordsRequestId = \"__audit_modal_closed_records__\"");
        bundleJs.Should().Contain("pendingAuditExportRequestId = \"__audit_modal_closed_export__\"");
        bundleJs.Should().Contain("pendingAuditChainRequestId = \"__audit_modal_closed_chain__\"");
        bundleJs.Should().Contain("function isAuditModalOpen");
        bundleJs.Should().Contain("const modal = byId(\"audit-modal\")");
        bundleJs.Should().Contain("return Boolean(modal && !modal.classList.contains(\"hidden\"))");
        bundleJs.Should().Contain("if (!isAuditModalOpen())");
        bundleJs.Should().Contain("auditErrorSource = \"\"");
        bundleJs.Should().Contain("function getMessageRequestId");
        bundleJs.Should().Contain("function isLocalAuditResponse");
        bundleJs.Should().Contain("return message?.local === true || message?.Local === true");
        bundleJs.Should().Contain("function isStaleAuditResponse");
        bundleJs.Should().Contain("if (isLocalAuditResponse(message)) return false");
        bundleJs.Should().Contain("const pendingId = String(pendingRequestId || \"\").trim()");
        bundleJs.Should().Contain("if (!pendingId) return true");
        bundleJs.Should().Contain("if (!requestId) return true");
        bundleJs.Should().Contain("return requestId !== pendingId");
        bundleJs.Should().Contain("function sendAuditCommand");
        bundleJs.Should().Contain("function setAuditError(message, source = \"\")");
        bundleJs.Should().Contain("if (!text && source && auditErrorSource && auditErrorSource !== source) return");
        bundleJs.Should().Contain("auditErrorSource = text ? (source || \"general\") : \"\"");
        bundleJs.Should().Contain("function getAuditCommandErrorSource");
        bundleJs.Should().Contain("if (cmd === \"query_audit_records\") return \"auditRecords\"");
        bundleJs.Should().Contain("if (cmd === \"export_audit_records\") return \"auditExport\"");
        bundleJs.Should().Contain("if (cmd === \"verify_audit_chain\") return \"auditChainVerification\"");
        bundleJs.Should().Contain("bridge?.sendCommand?.(cmd, value)");
        bundleJs.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
        bundleJs.Should().Contain("setAuditError(AuditBridgeErrorMessage, getAuditCommandErrorSource(cmd))");
        bundleJs.Should().Contain("window.showToast?.(AuditBridgeErrorMessage, \"error\", 1800)");
        bundleJs.Should().Contain("return \"__audit_command_failed__\"");
        bundleJs.Should().Contain("pendingAuditRecordsRequestId = \"__invalid_audit_records_request__\"");
        bundleJs.Should().Contain("pendingAuditExportRequestId = \"__invalid_audit_export_request__\"");
        bundleJs.Should().Contain("pendingAuditRecordsRequestId = sendAuditCommand(\"query_audit_records\", query");
        bundleJs.Should().Contain("pendingAuditExportRequestId = sendAuditCommand(\"export_audit_records\", query");
        bundleJs.Should().Contain("pendingAuditChainRequestId = sendAuditCommand(\"verify_audit_chain\", {}");
        bundleJs.Should().Contain("if (isStaleAuditResponse(message, pendingAuditRecordsRequestId))");
        bundleJs.Should().Contain("if (isStaleAuditResponse(message, pendingAuditExportRequestId))");
        bundleJs.Should().Contain("if (isStaleAuditResponse(message, pendingAuditChainRequestId))");
        bundleJs.Should().Contain("}, { local: true });");
        bundleJs.Should().Contain("errorCode: \"AuditBridgeUnavailable\"");
        bundleJs.Should().Contain("function clearAuditFilters");
        bundleJs.Should().Contain("function copyAuditChainSummary");
        bundleJs.Should().Contain("function copyAuditExportPath");
        bundleJs.Should().Contain("function buildAuditChainSummaryText");
        bundleJs.Should().Contain("function writeClipboardText");
        bundleJs.Should().Contain("function buildAuditFilterSummary");
        bundleJs.Should().Contain("function parseAuditDateValue");
        bundleJs.Should().Contain("function validateAuditQuery");
        bundleJs.Should().Contain("开始时间格式无效");
        bundleJs.Should().Contain("结束时间格式无效");
        bundleJs.Should().Contain("开始时间不能晚于结束时间");
        bundleJs.Should().Contain("请调整时间范围后再查询");
        bundleJs.Should().Contain("function setAuditCountBadge");
        bundleJs.Should().Contain("function setAuditExportPath(text, title = \"\")");
        bundleJs.Should().Contain("node.title = title || text || \"\"");
        bundleJs.Should().Contain("setAuditCountBadge(\"加载中\")");
        bundleJs.Should().Contain("setAuditCountBadge(\"0 条\")");
        bundleJs.Should().Contain("setAuditExportPath(\"正在导出审计 CSV...\")");
        bundleJs.Should().Contain("setAuditExportPath(\"\")");
        bundleJs.Should().Contain("setAuditExportPath(`已导出: ${path}`, path)");
        bundleJs.Should().Contain("setAuditError(error, \"auditRecords\")");
        bundleJs.Should().Contain("setAuditError(\"\", \"auditRecords\")");
        bundleJs.Should().Contain("setAuditError(error, \"auditExport\")");
        bundleJs.Should().Contain("const path = String(data?.path || data?.Path || \"\").trim()");
        bundleJs.Should().Contain("if (!path)");
        bundleJs.Should().Contain("setAuditError(AuditExportEmptyPathMessage, \"auditExport\")");
        bundleJs.Should().Contain("setAuditError(\"\", \"auditExport\")");
        bundleJs.Should().Contain("setAuditError(error, \"auditChainVerification\")");
        bundleJs.Should().Contain("setAuditError(\"\", \"auditChainVerification\")");
        bundleJs.Should().Contain("let lastAuditExportPath = \"\"");
        bundleJs.Should().Contain("lastAuditExportPath = path");
        bundleJs.Should().Contain("审计查询失败");
        bundleJs.Should().Contain("${escapeHtml(error)}");
        bundleJs.Should().Contain("请先导出审计 CSV");
        bundleJs.Should().Contain("审计导出路径已复制");
        bundleJs.Should().Contain("copyAuditExportPath,");
        bundleJs.Should().Contain("const validationError = validateAuditQuery(query)");
        bundleJs.Should().Contain("sendAuditCommand(\"query_audit_records\", query");
        bundleJs.Should().Contain("sendAuditCommand(\"export_audit_records\", query");
        bundleJs.Should().Contain("const source = query || buildAuditQuery()");
        bundleJs.Should().Contain("filters.push(`时间 ${startTime || \"-\"} 至 ${endTime || \"-\"}`)");
        bundleJs.Should().Contain("role: byId(\"audit-role-filter\")?.value || \"\"");
        bundleJs.Should().Contain("const role = source.role || source.Role || \"\"");
        bundleJs.Should().Contain("filters.push(`角色 ${formatProductionRole(role) || role}`)");
        bundleJs.Should().Contain("filters.push(`失败原因 ${failureReason}`)");
        bundleJs.Should().Contain("当前筛选：${escapeHtml(filterSummary)}");
        bundleJs.Should().Contain("const title = filterSummary ? \"未匹配到审计记录\" : \"暂无审计记录\"");
        bundleJs.Should().Contain("let clipboardError = null");
        bundleJs.Should().Contain("catch (error)");
        bundleJs.Should().Contain("clipboardError = error");
        bundleJs.Should().Contain("const target = document.body || document.documentElement");
        bundleJs.Should().Contain("document.execCommand?.(\"copy\") === true");
        bundleJs.Should().Contain("textarea.remove();");
        bundleJs.Should().Contain("lastAuditChainVerification = data || {}");
        bundleJs.Should().Contain("lastAuditChainVerification = null");
        bundleJs.Should().Contain("countNode.textContent = \"已验证 -/-\"");
        bundleJs.Should().Contain("findingNode.textContent = \"异常 -\"");
        bundleJs.Should().Contain("hashNode.textContent = \"最后 -\"");
        bundleJs.Should().Contain("hashNode.title = \"\"");
        bundleJs.Should().Contain("messageNode.textContent = \"\"");
        bundleJs.Should().Contain("messageNode.title = \"\"");
        bundleJs.Should().Contain("source.checkedAt || source.CheckedAt");
        bundleJs.Should().Contain("已验证 ${verifiedRecords}/${totalRecords}");
        bundleJs.Should().Contain("异常 ${findingCount}");
        bundleJs.Should().Contain("最后 ${shortAuditHash(lastHash) || \"-\"}");
        bundleJs.Should().Contain("\"audit-start-time\", \"audit-end-time\", \"audit-operation-filter\", \"audit-operator-filter\", \"audit-role-filter\", \"audit-status-filter\", \"audit-failure-filter\"");
        bundleJs.Should().Contain("failureReason: byId(\"audit-failure-filter\")?.value || \"\"");
        bundleJs.Should().Contain("function findLabel");
        bundleJs.Should().Contain("function replaceLabelTokens");
        bundleJs.Should().Contain("function normalizeAuditOperationFilter");
        bundleJs.Should().Contain("const AuditChainStatusLabels = Object.freeze");
        bundleJs.Should().Contain("Healthy: \"正常\"");
        bundleJs.Should().Contain("Warning: \"有警告\"");
        bundleJs.Should().Contain("Blocking: \"阻断\"");
        bundleJs.Should().Contain("Unavailable: \"不可用\"");
        bundleJs.Should().Contain("const AuditChainSeverityLabels = Object.freeze");
        bundleJs.Should().Contain("Blocking: \"阻断\"");
        bundleJs.Should().Contain("Warning: \"警告\"");
        bundleJs.Should().Contain("function formatAuditChainStatus");
        bundleJs.Should().Contain("function formatAuditChainStatusForSummary");
        bundleJs.Should().Contain("function getAuditChainFindings");
        bundleJs.Should().Contain("function formatAuditChainSeverity");
        bundleJs.Should().Contain("function limitAuditSummaryText");
        bundleJs.Should().Contain("function formatAuditChainFindingHint(finding, fallback = \"\", preferFullPath = false)");
        bundleJs.Should().Contain("? (filePath || auditFileName || entry)");
        bundleJs.Should().Contain(": (auditFileName || filePath || entry)");
        bundleJs.Should().Contain("const place = location ? `${location}${line ? `:${line}` : \"\"}` : (line ? `行 ${line}` : \"\")");
        bundleJs.Should().Contain("const message = limitAuditSummaryText(source.message || source.Message || \"\", 80)");
        bundleJs.Should().Contain("const hint = formatAuditChainFindingHint(firstFinding, error)");
        bundleJs.Should().Contain("messageNode.textContent = hint");
        bundleJs.Should().Contain("messageNode.title = formatAuditChainFindingHint(firstFinding, error, true)");
        bundleJs.Should().Contain("function formatAuditHashComparison");
        bundleJs.Should().Contain("return `${label} 期望 ${expectedText} 实际 ${actualText}`");
        bundleJs.Should().Contain("replace(/\\s+/g, \" \").trim()");
        bundleJs.Should().Contain("if (Array.isArray(source?.findings)) return source.findings");
        bundleJs.Should().Contain("if (Array.isArray(source?.Findings)) return source.Findings");
        bundleJs.Should().Contain("const findings = getAuditChainFindings(source)");
        bundleJs.Should().Contain("const findings = getAuditChainFindings(data)");
        bundleJs.Should().Contain("const listedFindings = findings.slice(0, 3)");
        bundleJs.Should().Contain("const filePath = finding.filePath || finding.FilePath || \"\"");
        bundleJs.Should().Contain("const auditFileName = finding.auditFileName || finding.AuditFileName || \"\"");
        bundleJs.Should().Contain("const location = filePath || auditFileName || entry");
        bundleJs.Should().Contain("const severity = finding.severity || finding.Severity || \"\"");
        bundleJs.Should().Contain("const severityText = formatAuditChainSeverity(severity) || severity");
        bundleJs.Should().Contain("const message = limitAuditSummaryText(finding.message || finding.Message || \"\")");
        bundleJs.Should().Contain("const expectedPreviousSha256 = finding.expectedPreviousSha256 || finding.ExpectedPreviousSha256 || \"\"");
        bundleJs.Should().Contain("const actualPreviousSha256 = finding.actualPreviousSha256 || finding.ActualPreviousSha256 || \"\"");
        bundleJs.Should().Contain("const expectedRecordSha256 = finding.expectedRecordSha256 || finding.ExpectedRecordSha256 || \"\"");
        bundleJs.Should().Contain("const actualRecordSha256 = finding.actualRecordSha256 || finding.ActualRecordSha256 || \"\"");
        bundleJs.Should().Contain("severityText ? `级别 ${severityText}` : \"\"");
        bundleJs.Should().Contain("message ? `消息 ${message}` : \"\"");
        bundleJs.Should().Contain("formatAuditHashComparison(\"上一哈希\", expectedPreviousSha256, actualPreviousSha256)");
        bundleJs.Should().Contain("formatAuditHashComparison(\"记录哈希\", expectedRecordSha256, actualRecordSha256)");
        bundleJs.Should().Contain("const remainingFindings = Math.max(0, findings.length - listedFindings.length)");
        bundleJs.Should().Contain("其余异常: ${remainingFindings} 条未列出");
        bundleJs.Should().Contain("const statusText = formatAuditChainStatusForSummary(status)");
        bundleJs.Should().Contain("return label && label !== raw ? `${label} (${raw})` : raw");
        bundleJs.Should().Contain("状态: ${statusText || \"-\"}");
        bundleJs.Should().Contain("badge.textContent = formatAuditChainStatus(status) || status");
        bundleJs.Should().Contain("operation: normalizeAuditOperationFilter(byId(\"audit-operation-filter\")?.value)");
        bundleJs.Should().Contain("raw.toLowerCase() === value.toLowerCase()");
        bundleJs.Should().Contain("String(label || \"\").trim().toLowerCase() === value.toLowerCase()");
        bundleJs.Should().Contain("function getAuditChainStatusClass");
        bundleJs.Should().Contain("function getAuditRecordStatusClass");
        bundleJs.Should().Contain("key.toLowerCase() === normalized.toLowerCase()");
        bundleJs.Should().Contain("DiagnosticPackageExport: \"导出诊断包\"");
        bundleJs.Should().Contain("DiagnosticPackageVerify: \"复核诊断包\"");
        bundleJs.Should().Contain("FieldHandoffReportExport: \"导出交接报告\"");
        bundleJs.Should().Contain("MaintenanceAdviceAction: \"维护建议处理\"");
        bundleJs.Should().Contain("ShiftTaskAction: \"班次待办处理\"");
        bundleJs.Should().Contain("FieldDebugPlcWriteTest: \"PLC 写入测试\"");
        bundleJs.Should().Contain("\"failed\", \"denied\", \"blocking\", \"error\"");
        bundleJs.Should().Contain("\"requested\", \"pending\", \"warning\"");
        bundleJs.Should().Contain("recordSha256");
        renderMainJs.Should().Contain("fieldDebugResult");
        renderMainJs.Should().Contain("diagnosticPackageExportResult");
        renderMainJs.Should().Contain("diagnosticPackageHistoryResult");
        renderMainJs.Should().Contain("diagnosticPackageVerificationResult");
        renderMainJs.Should().Contain("maintenanceAdviceActionResult");
        renderMainJs.Should().Contain("const FieldDebugPendingTimeoutMessage = \"现场调试等待超时，请查看日志或稍后重试\"");
        renderMainJs.Should().Contain("const FieldExportPendingTimeoutMessage = \"现场报告导出等待超时，请查看日志或稍后重试\"");
        renderMainJs.Should().Contain("const FieldDebugPendingTtlMs = 30000");
        renderMainJs.Should().Contain("const FieldExportPendingTtlMs = 60000");
        renderMainJs.Should().Contain("const fieldExportPendingTimers = new Map()");
        renderMainJs.Should().Contain("const FieldExportOperationLabels = Object.freeze");
        renderMainJs.Should().Contain("export_diagnostic_package: \"导出诊断包\"");
        renderMainJs.Should().Contain("export_field_handoff_report: \"导出交接报告\"");
        renderMainJs.Should().Contain("function setFieldExportPending(cmd, isPending)");
        renderMainJs.Should().Contain("function markFieldExportPending(cmd)");
        renderMainJs.Should().Contain("applyFieldExportPendingState(cmd");
        renderMainJs.Should().Contain("errorCode: \"FieldExportTimeout\"");
        renderMainJs.Should().Contain("const FieldDebugCommandLabels = Object.freeze");
        renderMainJs.Should().Contain("field_debug_step_capture: \"单步取图\"");
        renderMainJs.Should().Contain("function setFieldDebugPending(isPending)");
        renderMainJs.Should().Contain("document.querySelectorAll('[data-cmd^=\"field_debug_\"]')");
        renderMainJs.Should().Contain("button.disabled = !!isPending");
        renderMainJs.Should().Contain("function markFieldDebugCommandPending(cmd)");
        renderMainJs.Should().Contain("pending: true");
        renderMainJs.Should().Contain("errorCode: \"FieldDebugTimeout\"");
        renderMainJs.Should().Contain("markFieldExportPending(\"export_field_handoff_report\")");
        renderMainJs.Should().Contain("markFieldExportPending(cmd)");
        renderMainJs.Should().Contain("markFieldDebugCommandPending(cmd)");
        bundleJs.Should().Contain("const FieldDebugPendingTimeoutMessage = \"现场调试等待超时，请查看日志或稍后重试\"");
        bundleJs.Should().Contain("const FieldExportPendingTimeoutMessage = \"现场报告导出等待超时，请查看日志或稍后重试\"");
        bundleJs.Should().Contain("function markFieldExportPending(cmd)");
        bundleJs.Should().Contain("function markFieldDebugCommandPending(cmd)");
        stateJs.Should().Contain("applyFieldDebugResult");
        stateJs.Should().Contain("applyDiagnosticPackageExportResult");
        stateJs.Should().Contain("applyDiagnosticPackageHistoryResult");
        stateJs.Should().Contain("applyDiagnosticPackageVerificationResult");
        stateJs.Should().Contain("applyMaintenanceAdviceActionResult");
        stateJs.Should().Contain("applyShiftTaskActionResult");
        stateJs.Should().Contain("applyFieldHandoffReportResult");
        stateJs.Should().Contain("applyFieldHandoffReportHistoryResult");
        styleCss.Should().Contain(".cf-diagnostics-panel");
        styleCss.Should().Contain(".cf-diagnostics-checklist");
        styleCss.Should().Contain(".cf-maintenance-advice-list");
        styleCss.Should().Contain(".cf-maintenance-history");
        styleCss.Should().Contain(".cf-shift-task-list");
        styleCss.Should().Contain(".cf-shift-task-item.overdue");
        styleCss.Should().Contain(".cf-handoff-report-meta");
        styleCss.Should().Contain(".cf-handoff-report-history");
        styleCss.Should().Contain(".cf-diagnostics-package-history");
        styleCss.Should().Contain(".cf-diagnostic-badge");
        styleCss.Should().Contain("@media (max-width: 640px)");
        controller.Should().Contain("SendAuditChainVerificationAsync");
        controller.Should().Contain("AuditChainVerifier");
        controller.Should().Contain("checkedAt = DateTimeOffset.Now");
        controller.Should().Contain("JsonElement auditQueryElement = root.TryGetProperty(\"value\", out JsonElement auditQueryValueElement)");
        controller.Should().Contain("? auditQueryValueElement");
        controller.Should().Contain("JsonElement auditExportElement = root.TryGetProperty(\"value\", out JsonElement auditExportValueElement)");
        controller.Should().Contain("? auditExportValueElement");
        controller.Should().Contain("query = BuildAuditQueryEcho(queryElement, query)");
        controller.Should().Contain("private static object BuildAuditQueryEcho");
        controller.Should().Contain("failureReason = query.FailureReason");
        controller.Should().Contain("element.ValueKind != JsonValueKind.Object");
        controller.Should().Contain("return new OperationAuditQuery();");
        controller.Should().Contain("propertyElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined");
        controller.Should().Contain("propertyElement.GetRawText()");
        controller.Should().Contain("Audit records query failed");
        controller.Should().Contain("records = Array.Empty<object>()");
        controller.Should().Contain("error = ex.Message");
        controller.Should().Contain("filePath = finding.FilePath");
        controller.Should().Contain("auditFileName = string.IsNullOrWhiteSpace(finding.FilePath)");
        controller.Should().Contain("expectedPreviousSha256 = finding.ExpectedPreviousSha256");
        controller.Should().Contain("actualPreviousSha256 = finding.ActualPreviousSha256");
        controller.Should().Contain("expectedRecordSha256 = finding.ExpectedRecordSha256");
        controller.Should().Contain("actualRecordSha256 = finding.ActualRecordSha256");
        controller.Should().Contain("Audit chain verification failed");
        controller.Should().Contain("findingCount = 1");
        controller.Should().Contain("errorCode = \"AuditChainVerificationFailed\"");
        controller.Should().Contain("severity = \"Blocking\"");
        controller.Should().Contain("error = ex.Message");
        controller.Should().Contain("previousRecordSha256");
        controller.Should().Contain("recordSha256");

        foreach (string command in new[]
        {
            "export_diagnostic_package",
            "query_diagnostic_packages",
            "verify_diagnostic_package",
            "maintenance_advice_action",
            "shift_task_action",
            "export_field_handoff_report",
            "query_field_handoff_reports",
            "verify_audit_chain",
            "field_debug_step_capture",
            "field_debug_step_infer",
            "field_debug_plc_write_test",
            "field_debug_barcode_read_test",
            "field_debug_simulate_trigger"
        })
        {
            controller.Should().Contain($"case \"{command}\"");
        }

        controller.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnVerifyDiagnosticPackage?.Invoke(this, args))");
        controller.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnMaintenanceAdviceAction?.Invoke(this, args))");
        controller.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnShiftTaskAction?.Invoke(this, args))");
    }

    [Fact]
    public void AuditQuery_RejectsInvalidDateTextBeforeSendingCommand()
    {
        string root = FindRepositoryRoot();
        string historyPath = Path.Combine(root, "ClearFrost", "html", "js", "history.js");
        string script =
            "const historyPath = " + JsonSerializer.Serialize(historyPath) + ";\n" +
            "const elements = new Map();\n" +
            "const commands = [];\n" +
            "function classList() { return { add() {}, remove() {}, toggle() {}, contains() { return false; } }; }\n" +
            "function element(id, value = '') { const node = { id, value, innerHTML: '', textContent: '', title: '', classList: classList(), dataset: {}, style: {}, querySelector() { return null; }, addEventListener() {} }; elements.set(id, node); return node; }\n" +
            "element('audit-start-time', 'not-a-date');\n" +
            "element('audit-end-time', '2026-07-07T10:00');\n" +
            "element('audit-operation-filter', '');\n" +
            "element('audit-operator-filter', '');\n" +
            "element('audit-role-filter', '');\n" +
            "element('audit-status-filter', '');\n" +
            "element('audit-failure-filter', '');\n" +
            "element('audit-error', '');\n" +
            "element('audit-count-badge', '');\n" +
            "element('audit-export-path', '');\n" +
            "element('audit-table', '');\n" +
            "global.document = { getElementById(id) { return elements.get(id) || null; }, querySelectorAll() { return []; }, createElement(tag) { return element(`created-${tag}-${Math.random()}`); }, body: { appendChild() {} }, documentElement: {} };\n" +
            "global.window = {\n" +
            "  CF_BRIDGE: { registerMessageHandler() {}, sendCommand(cmd, value) { commands.push({ cmd, value }); return `req-${commands.length}`; } },\n" +
            "  CF_UTILS: { escapeHtml(value) { return String(value ?? ''); } },\n" +
            "  CF_ERROR_ADVICE: { format() { return ''; } },\n" +
            "  addEventListener() {}, showToast() {}, addLog() {}, currentNGDate: '2026-07-07', currentNGHour: '08'\n" +
            "};\n" +
            "require(historyPath);\n" +
            "window.queryAuditRecords();\n" +
            "if (commands.length !== 0) throw new Error('invalid start time was sent');\n" +
            "if (!elements.get('audit-error').textContent.includes('开始时间格式无效')) throw new Error('start time validation error missing: ' + elements.get('audit-error').textContent);\n" +
            "if (!elements.get('audit-table').innerHTML.includes('开始时间格式无效')) throw new Error('table validation message missing: ' + elements.get('audit-table').innerHTML);\n" +
            "elements.get('audit-start-time').value = '2026-07-07T08:00';\n" +
            "elements.get('audit-end-time').value = 'bad-end';\n" +
            "window.exportAuditRecords();\n" +
            "if (commands.length !== 0) throw new Error('invalid end time was sent');\n" +
            "if (!elements.get('audit-error').textContent.includes('结束时间格式无效')) throw new Error('end time validation error missing: ' + elements.get('audit-error').textContent);\n" +
            "elements.get('audit-end-time').value = '2026-07-07T10:00';\n" +
            "window.queryAuditRecords();\n" +
            "if (commands.length !== 1 || commands[0].cmd !== 'query_audit_records') throw new Error('valid audit query not sent: ' + JSON.stringify(commands));\n";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);

        process.Start().Should().BeTrue();
        process.WaitForExit(10_000).Should().BeTrue();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.ExitCode.Should().Be(0, $"Node stdout: {output}; stderr: {error}");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
