using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUICameraBridgeContractTests
{
    [Fact]
    public void CameraCommands_ShowVisibleFailureWhenBridgeSendFails()
    {
        string root = FindRepositoryRoot();
        string cameraJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "camera.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        foreach (string script in new[] { cameraJs, bundleJs })
        {
            script.Should().Contain("const CameraBridgeErrorMessage = \"相机操作通信失败，请刷新页面后重试\"");
            script.Should().Contain("const CameraSearchBridgeErrorMessage = \"相机搜索通信失败，请刷新页面后重试\"");
            script.Should().Contain("const CameraPreviewBridgeErrorMessage = \"相机预览通信失败，请刷新页面后重试\"");
            script.Should().Contain("const CameraDirectConnectBridgeErrorMessage = \"相机直连通信失败，请刷新页面后重试\"");
            script.Should().Contain("let pendingCameraSwitch = null");
            script.Should().Contain("let pendingCameraSearchRequestId = \"\"");
            script.Should().Contain("let pendingCameraPreviewRequestId = \"\"");
            script.Should().Contain("let pendingCameraMutationRequestId = \"\"");
            script.Should().Contain("let cameraMutationResetTimer = null");
            script.Should().Contain("const CameraMutationPendingTtlMs = 30000");
            script.Should().Contain("function sendCameraCommand(cmd, value = null, onFailure = null, failureMessage = CameraBridgeErrorMessage)");
            script.Should().Contain("bridge?.sendCommand?.(cmd, value)");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("console.error(`Camera command failed: ${cmd}`, error)");
            script.Should().Contain("window.showToast?.(failureMessage, \"error\", 1800)");
            script.Should().Contain("function handleCameraCommandError(event)");
            script.Should().Contain("function isMatchingCameraRequest(requestId, pendingRequestId)");
            script.Should().Contain("function setCameraMutationPending(isPending, action = \"\")");
            script.Should().Contain("document.querySelectorAll('[data-action=\"addNewCamera\"], [data-action=\"deleteCurrentCamera\"]')");
            script.Should().Contain("function clearCameraMutationPending()");
            script.Should().Contain("function readFiniteNumberInput(id, fallback)");
            script.Should().Contain("return Number.isFinite(value) ? value : fallback");
            script.Should().Contain("window.addEventListener(\"cf-command-error\", handleCameraCommandError)");
            script.Should().Contain("function setCameraSelection(id)");
            script.Should().Contain("const previousId = window.activeCameraId || store.state.activeCameraId || \"\"");
            script.Should().Contain("pendingCameraSwitch = { previousId, nextId: id, requestId: \"\" }");
            script.Should().Contain("const requestId = sendCameraCommand(\"switch_camera\", id, () =>");
            script.Should().Contain("setCameraSelection(previousId)");
            script.Should().Contain("if (cmd === \"switch_camera\"");
            script.Should().Contain("isMatchingCameraRequest(requestId, pendingCameraSwitch?.requestId || \"\")");
            script.Should().Contain("const previousId = pendingCameraSwitch?.previousId || \"\"");
            script.Should().Contain("const requestId = sendCameraCommand(\"add_camera\", {");
            script.Should().Contain("const exposureTime = readFiniteNumberInput(\"cfg-cam-exposure\", 50000)");
            script.Should().Contain("const gain = readFiniteNumberInput(\"cfg-cam-gain\", 1.0)");
            script.Should().Contain("if ((cmd === \"add_camera\" || cmd === \"delete_camera\") &&");
            script.Should().Contain("isMatchingCameraRequest(requestId, pendingCameraMutationRequestId)");
            script.Should().Contain("clearCameraMutationPending();");
            script.Should().Contain("pendingCameraMutationRequestId = requestId");
            script.Should().Contain("setCameraMutationPending(true, \"add\")");
            script.Should().Contain("window.addLog?.(`正在添加/更新相机: ${displayName}...`, \"info\")");
            script.Should().Contain("if (typeof window.confirm === \"function\" && !window.confirm(`");
            script.Should().Contain("const requestId = sendCameraCommand(\"delete_camera\", cameraId, () =>");
            script.Should().Contain("setCameraMutationPending(true, \"delete\")");
            script.Should().Contain("sendCameraCommand(\"search_huaray_cameras\", null, () =>");
            script.Should().Contain("pendingCameraSearchRequestId = requestId");
            script.Should().Contain("setSuperSearchFeedback(CameraSearchBridgeErrorMessage, \"error\")");
            script.Should().Contain("const requestId = sendCameraCommand(\"direct_connect_camera\", {");
            script.Should().Contain("if (requestId && directConnectPending) directConnectPending.requestId = requestId");
            script.Should().Contain("isMatchingCameraRequest(requestId, directConnectPending?.requestId || \"\")");
            script.Should().Contain("clearDirectConnectButtons(false)");
            script.Should().Contain("setSuperSearchFeedback(CameraDirectConnectBridgeErrorMessage, \"error\")");
            script.Should().Contain("sendCameraCommand(\"capture_camera_preview\", collectCameraPreviewPayload(), () =>");
            script.Should().Contain("exposureTime: readFiniteNumberInput(\"cfg-cam-exposure\", 50000)");
            script.Should().Contain("gain: readFiniteNumberInput(\"cfg-cam-gain\", 1.0)");
            script.Should().Contain("pendingCameraPreviewRequestId = requestId");
            script.Should().Contain("setCameraPreviewStatus({ isBusy: false, message: CameraPreviewBridgeErrorMessage, type: \"error\" })");
            script.Should().Contain("setCameraPreviewStatus({ isBusy: false, message: message || CameraPreviewBridgeErrorMessage, type: \"error\" })");
            script.Should().Contain("sendCameraCommand(\"super_search_cameras_hik\", null, () =>");
            script.Should().NotContain("bridge.sendCommand(\"switch_camera\"");
            script.Should().NotContain("bridge.sendCommand(\"add_camera\"");
            script.Should().NotContain("bridge.sendCommand(\"delete_camera\"");
            script.Should().NotContain("bridge.sendCommand(\"search_huaray_cameras\"");
            script.Should().NotContain("bridge.sendCommand(\"direct_connect_camera\"");
            script.Should().NotContain("bridge.sendCommand(\"capture_camera_preview\"");
            script.Should().NotContain("bridge.sendCommand(\"super_search_cameras_hik\"");
            script.Should().NotContain("parseFloat(byId(\"cfg-cam-exposure\")?.value) || 50000");
            script.Should().NotContain("parseFloat(byId(\"cfg-cam-gain\")?.value) || 1.0");
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
