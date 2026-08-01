using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIShellRoiBridgeContractTests
{
    [Fact]
    public void ShellAndRoiCommands_ShowVisibleFailureWhenBridgeSendFails()
    {
        string root = FindRepositoryRoot();
        string bootJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "boot.js"));
        string roiJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "roi.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        foreach (string script in new[] { bootJs, bundleJs })
        {
            script.Should().Contain("const ShellBridgeErrorMessage = \"界面通信失败，请刷新页面后重试\"");
            script.Should().Contain("const ShellCommandPendingTtlMs = 30000");
            script.Should().Contain("const pendingShellCommandFailures = new Map()");
            script.Should().Contain("function sendShellCommand(cmd, value = null, onFailure = null, failureMessage = ShellBridgeErrorMessage)");
            script.Should().Contain("const requestId = window.sendCommand?.(cmd, value)");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("registerPendingShellCommandFailure(requestId, cmd, onFailure)");
            script.Should().Contain("console.error(`Shell command failed: ${cmd}`, error)");
            script.Should().Contain("window.showToast?.(failureMessage, \"error\", 1800)");
            script.Should().Contain("window.addLog?.(`${failureMessage}: ${cmd}`, \"error\")");
            script.Should().Contain("function handleShellCommandError(event)");
            script.Should().Contain("const pending = takePendingShellCommandFailure(requestId, cmd)");
            script.Should().Contain("pending.onFailure(error)");
            script.Should().Contain("window.addEventListener(\"cf-command-error\", handleShellCommandError)");
            script.Should().Contain("sendShellCommand(\"start_drag\")");
            script.Should().Contain("const requestId = sendShellCommand(\"manual_release\", payload, (error) =>");
            script.Should().Contain("modal.classList.remove(\"hidden\")");
            script.Should().Contain("document.getElementById(\"manual-release-token\")?.focus()");
            script.Should().Contain("if (!requestId) return");
            script.Should().Contain("const requestId = sendShellCommand(cmd, value === undefined ? null : value)");
            script.Should().Contain("function getChangeCommandValue(element)");
            script.Should().Contain("function restoreChangeCommandValue(element, value)");
            script.Should().Contain("function captureChangeCommandBaseline(event)");
            script.Should().Contain("document.addEventListener(\"pointerdown\", captureChangeCommandBaseline)");
            script.Should().Contain("document.addEventListener(\"focusin\", captureChangeCommandBaseline)");
            script.Should().Contain("function confirmIfNeeded(element)");
            script.Should().Contain("if (typeof window.confirm !== \"function\")");
            script.Should().Contain("window.showToast?.(warning, \"warning\", 2200)");
            script.Should().Contain("return window.confirm(message)");
            script.Should().Contain("const previousValue = commandElement.dataset.confirmedValue ?? commandElement.dataset.previousValue ?? \"\"");
            script.Should().Contain("let requestId = \"\"");
            script.Should().Contain("requestId = sendShellCommand(cmd, nextValue, (error) =>");
            script.Should().Contain("commandElement.dataset.pendingChangeRequestId !== requestId");
            script.Should().Contain("restoreChangeCommandValue(commandElement, previousValue)");
            script.Should().Contain("commandElement.dataset.confirmedValue = String(previousValue ?? \"\")");
            script.Should().Contain("delete commandElement.dataset.pendingChangeRequestId");
            script.Should().Contain("window.showToast?.(message, \"error\", 2200)");
            script.Should().Contain("commandElement.dataset.pendingChangeRequestId = requestId");
            script.Should().Contain("commandElement.dataset.confirmedValue = String(nextValue ?? \"\")");
            script.Should().Contain("setTimeout(() => sendShellCommand(\"app_ready\"), 500)");
            script.Should().Contain("sendShellCommand,");
            script.Should().NotContain("window.sendCommand(\"manual_release\"");
            script.Should().NotContain("window.sendCommand(cmd, value === undefined ? null : value)");
            script.Should().NotContain("window.sendCommand(commandElement.dataset.changeCmd");
            script.Should().NotContain("window.sendCommand(\"app_ready\"");
        }

        foreach (string script in new[] { roiJs, bundleJs })
        {
            script.Should().Contain("const RoiBridgeErrorMessage = \"ROI 通信失败，请刷新页面后重试\"");
            script.Should().Contain("const RoiCommandPendingTtlMs = 30000");
            script.Should().Contain("const pendingRoiCommands = new Map()");
            script.Should().Contain("function sendRoiCommand(rect, onSuccess, onFailure = null)");
            script.Should().Contain("const requestId = window.sendCommand?.(\"update_roi\", { rect })");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("registerPendingRoiCommand(requestId, onFailure)");
            script.Should().Contain("console.error(\"ROI command failed:\", error)");
            script.Should().Contain("window.showToast?.(RoiBridgeErrorMessage, \"error\", 1800)");
            script.Should().Contain("window.addLog?.(RoiBridgeErrorMessage, \"error\")");
            script.Should().Contain("function handleRoiCommandError(event)");
            script.Should().Contain("const pending = takePendingRoiCommand(requestId)");
            script.Should().Contain("pending.onFailure(message || RoiBridgeErrorMessage)");
            script.Should().Contain("window.addEventListener(\"cf-command-error\", handleRoiCommandError)");
            script.Should().Contain("function restoreNormalizedRoiRect(rect)");
            script.Should().Contain("const requestId = sendRoiCommand([normX, normY, normW, normH], () =>");
            script.Should().Contain("restoreNormalizedRoiRect(previousRect)");
            script.Should().Contain("if (!requestId) return");
            script.Should().Contain("sendRoiCommand([0, 0, 0, 0], () =>");
            script.Should().Contain("window.sendRoiCommand = sendRoiCommand");
            script.Should().NotContain("window.sendCommand(\"update_roi\"");
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
