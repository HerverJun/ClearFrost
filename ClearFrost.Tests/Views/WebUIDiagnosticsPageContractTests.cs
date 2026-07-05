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
            "诊断/调试",
            "diag-camera-status",
            "diag-plc-status",
            "diag-current-model",
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
            "verifyAuditChain",
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
        renderMainJs.Should().Contain("packageSha256");
        renderMainJs.Should().Contain("diag-package-sha");
        renderMainJs.Should().Contain("integrityStatus");
        renderMainJs.Should().Contain("buildDiagnosticPackageSummaryText");
        renderMainJs.Should().Contain("copyDiagnosticPackageSummary");
        renderMainJs.Should().Contain("renderDiagnosticPackageHistory");
        renderMainJs.Should().Contain("verifyDiagnosticPackage");
        historyJs.Should().Contain("verifyAuditChain");
        historyJs.Should().Contain("verify_audit_chain");
        historyJs.Should().Contain("auditChainVerification");
        historyJs.Should().Contain("recordSha256");
        historyJs.Should().Contain("shortAuditHash");
        bundleJs.Should().Contain("renderFieldAcceptanceChecklist");
        bundleJs.Should().Contain("renderAuditChainChecklist");
        bundleJs.Should().Contain("renderMaintenanceAdviceList");
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
        bundleJs.Should().Contain("diag-model-slot-list");
        bundleJs.Should().Contain("diag-audit-chain");
        bundleJs.Should().Contain("diag-maintenance-advice");
        bundleJs.Should().Contain("diag-shift-task-list");
        bundleJs.Should().Contain("diag-handoff-report-path");
        bundleJs.Should().Contain("diag-handoff-report-history");
        bundleJs.Should().Contain("diag-package-sha");
        bundleJs.Should().Contain("diag-integrity-status");
        bundleJs.Should().Contain("buildDiagnosticPackageSummaryText");
        bundleJs.Should().Contain("copyDiagnosticPackageSummary");
        bundleJs.Should().Contain("renderDiagnosticPackageHistory");
        bundleJs.Should().Contain("verifyDiagnosticPackage");
        bundleJs.Should().Contain("verifyAuditChain");
        bundleJs.Should().Contain("auditChainVerification");
        bundleJs.Should().Contain("recordSha256");
        renderMainJs.Should().Contain("fieldDebugResult");
        renderMainJs.Should().Contain("diagnosticPackageExportResult");
        renderMainJs.Should().Contain("diagnosticPackageHistoryResult");
        renderMainJs.Should().Contain("diagnosticPackageVerificationResult");
        renderMainJs.Should().Contain("maintenanceAdviceActionResult");
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
