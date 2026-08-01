using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUITracePagerContractTests
{
    [Fact]
    public void WebUi追溯分页_使用RequestId拒绝旧响应并显示通信失败()
    {
        string root = FindRepositoryRoot();
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        foreach (string script in new[] { historyJs, bundleJs })
        {
            script.Should().Contain("const TraceBridgeErrorMessage = \"追溯通信失败，请刷新页面后重试\"");
            script.Should().Contain("const TraceCommandFailedRequestId = \"__trace_command_failed__\"");
            script.Should().Contain("function showTraceLoadFailure");
            script.Should().Contain("tracePagerState.pendingRequestId = TraceCommandFailedRequestId");
            script.Should().Contain("window.showToast?.(text, \"error\", 1800)");
            script.Should().Contain("function sendTraceCommand");
            script.Should().Contain("bridge?.sendCommand?.(cmd, value)");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("console.error(`Trace command failed: ${cmd}`, error)");
            script.Should().Contain("return TraceCommandFailedRequestId");
            script.Should().Contain("function getTraceMessageRequestId");
            script.Should().Contain("message?.requestId || message?.RequestId || data?.requestId || data?.RequestId || \"\"");
            script.Should().Contain("function isStaleTraceResponse");
            script.Should().Contain("const pendingId = String(tracePagerState.pendingRequestId || \"\").trim()");
            script.Should().Contain("!pendingId");
            script.Should().Contain("!requestId");
            script.Should().Contain("requestId !== pendingId");
            script.Should().Contain("requestId === tracePagerState.lastHandledRequestId");
            script.Should().Contain("const requestId = sendTraceCommand(\"get_ng_images\", payload");
            script.Should().Contain("showTraceLoadFailure(TraceBridgeErrorMessage)");
            script.Should().Contain("tracePagerState.pendingRequestId = requestId");
            script.Should().Contain("if (requestId !== TraceCommandFailedRequestId)");
            script.Should().Contain("const requestId = getTraceMessageRequestId(data, message)");
            script.Should().Contain("if (isStaleTraceResponse(requestId))");
        }

        controller.Should().Contain("TryReadTraceImagesRequest(");
        controller.Should().Contain("if (TryParseTraceDate(traceDateKey, out _))");
        controller.Should().Contain("追溯日期格式无效: get_ng_hours");
        controller.Should().Contain("await SendNGImages(date, hour, pageSize, afterTimestamp, afterId, requestId)");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, traceError)");
        controller.Should().Contain("前端命令 value 不能为空: get_ng_hours");
        controller.Should().Contain("追溯图片请求 value 必须是对象");
        controller.Should().Contain("追溯图片请求缺少 date");
        controller.Should().Contain("追溯图片请求 date 格式无效");
        controller.Should().Contain("追溯图片请求 hour 必须是 0 到 23");
        controller.Should().Contain("追溯图片分页游标必须同时包含 afterTimestamp 和 afterId");
        controller.Should().Contain("追溯图片分页游标 afterTimestamp 格式无效");
        controller.Should().Contain("追溯图片分页游标 afterId 必须大于 0");
        controller.Should().Contain("TryNormalizeTraceCursorTimestamp");
        controller.Should().Contain("pageSize = Math.Clamp(TryGetInt32Property(valueElement, \"pageSize\") ?? 100, 1, 200)");
        controller.Should().Contain("PostMessage(\"historyImages\", new");
        controller.Should().Contain("}, requestId);");
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
