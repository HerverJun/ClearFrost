using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIBridgeContractTests
{
    [Fact]
    public void WebViewBridge_LogsAndRethrowsPostMessageFailures()
    {
        string root = FindRepositoryRoot();
        string bridgeJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bridge.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        foreach (string script in new[] { bridgeJs, bundleJs })
        {
            script.Should().Contain("console.error(\"ClearFrost message parse failed:\", error);");
            script.Should().Contain("window.addLog(\"后端消息解析失败\", \"error\");");
            script.Should().Contain("function reportCommandFailure(cmd, error)");
            script.Should().Contain("console.error(`ClearFrost command post failed: ${cmd}`, error);");
            script.Should().Contain("window.addLog(`命令发送失败: ${cmd}`, \"error\");");
            script.Should().Contain("try {");
            script.Should().Contain("window.chrome.webview.postMessage(payload);");
            script.Should().Contain("catch (error) {");
            script.Should().Contain("reportCommandFailure(cmd, error);");
            script.Should().Contain("throw error;");
            script.Should().Contain("return payload.requestId;");
            script.Should().Contain("window.CF_BRIDGE = {");
            script.Should().Contain("window.sendCommand = sendCommand");
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
