using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIMainCommandBridgeContractTests
{
    [Fact]
    public void MainAndFieldDiagnosticCommands_ShowVisibleFailureWhenBridgeSendFails()
    {
        string root = FindRepositoryRoot();
        string renderMainJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        foreach (string script in new[] { renderMainJs, bundleJs })
        {
            script.Should().Contain("const MainBridgeErrorMessage = \"主界面通信失败，请刷新页面后重试\"");
            script.Should().Contain("const FieldDiagnosticsBridgeErrorMessage = \"现场诊断通信失败，请刷新页面后重试\"");
            script.Should().Contain("const SystemCommandBridgeErrorMessage = \"系统控制通信失败，请刷新页面后重试\"");
            script.Should().Contain("const MainCommandPendingTtlMs = 30000");
            script.Should().Contain("const pendingMainCommandFailures = new Map()");
            script.Should().Contain("function sendMainCommand(cmd, value = null, onFailure = null, failureMessage = MainBridgeErrorMessage)");
            script.Should().Contain("const requestId = window.sendCommand?.(cmd, value)");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("registerPendingMainCommandFailure(requestId, cmd, onFailure)");
            script.Should().Contain("console.error(`Main command failed: ${cmd}`, error)");
            script.Should().Contain("showToast(failureMessage, \"error\", 1800)");
            script.Should().Contain("addLog(`${failureMessage}: ${cmd}`, \"error\")");
            script.Should().Contain("function handleMainCommandError(event)");
            script.Should().Contain("const pending = takePendingMainCommandFailure(requestId, cmd)");
            script.Should().Contain("pending.onFailure(error)");
            script.Should().Contain("window.addEventListener(\"cf-command-error\", handleMainCommandError)");
            script.Should().Contain("sendMainCommand(\"query_diagnostic_packages\", null, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("sendMainCommand(\"query_field_handoff_reports\", null, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("sendMainCommand(\"verify_diagnostic_package\", { path: packagePath }, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("sendMainCommand(\"export_field_handoff_report\", null, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("sendMainCommand(\"maintenance_advice_action\", { adviceId: id, action }, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("sendMainCommand(\"shift_task_action\", { taskId, linkedAdviceId, action }, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("sendMainCommand(\"request_health_snapshot\", null, null, FieldDiagnosticsBridgeErrorMessage)");
            script.Should().Contain("const requestId = sendMainCommand(\"exit_app\", null, () =>");
            script.Should().Contain("exitAppPending = false");
            script.Should().Contain("setWindowButtonsBusy(false)");
            script.Should().Contain("const requestId = sendMainCommand(\"stop_system\", null, () =>");
            script.Should().Contain("setStartSystemButtonState(true, false)");
            script.Should().Contain("const requestId = sendMainCommand(\"start_system\", null, () =>");
            script.Should().Contain("openCameraPending = false");
            script.Should().Contain("setOpenCameraButtonBusy(false)");
            script.Should().NotContain("window.sendCommand(\"query_diagnostic_packages\"");
            script.Should().NotContain("window.sendCommand(\"query_field_handoff_reports\"");
            script.Should().NotContain("window.sendCommand(\"verify_diagnostic_package\"");
            script.Should().NotContain("window.sendCommand(\"export_field_handoff_report\"");
            script.Should().NotContain("window.sendCommand(\"maintenance_advice_action\"");
            script.Should().NotContain("window.sendCommand(\"shift_task_action\"");
            script.Should().NotContain("window.sendCommand(\"request_health_snapshot\"");
            script.Should().NotContain("window.sendCommand(\"exit_app\"");
            script.Should().NotContain("window.sendCommand(\"stop_system\"");
            script.Should().NotContain("window.sendCommand(\"start_system\"");
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
