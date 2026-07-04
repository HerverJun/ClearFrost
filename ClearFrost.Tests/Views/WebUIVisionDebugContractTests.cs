using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIVisionDebugContractTests
{
    [Fact]
    public void WebUi算法调试_关键元素命令和消息存在()
    {
        string root = FindRepositoryRoot();
        string index = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
        string renderMain = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string state = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "state.js"));
        string bundle = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        index.Should().Contain("id=\"vision-debug-modal\"");
        index.Should().Contain("算法调试/视觉调试");
        index.Should().Contain("id=\"vision-debug-run-current\"");
        index.Should().Contain("id=\"vision-debug-confidence\"");
        index.Should().Contain("id=\"vision-debug-iou\"");
        index.Should().Contain("id=\"vision-debug-target-label\"");
        index.Should().Contain("id=\"vision-debug-target-count\"");
        index.Should().Contain("id=\"vision-debug-roi-enabled\"");
        index.Should().Contain("id=\"vision-debug-box-list\"");
        index.Should().Contain("id=\"vision-debug-rule-details\"");
        index.Should().Contain("id=\"vision-debug-overlay\"");

        controller.Should().Contain("OnVisionDebugCommand");
        controller.Should().Contain("case \"vision_debug_run_current\"");
        controller.Should().Contain("case \"vision_debug_run_history\"");
        controller.Should().Contain("case \"vision_debug_save_params\"");
        controller.Should().Contain("case \"vision_debug_apply_template\"");
        controller.Should().Contain("PostMessage(\"visionDebugResult\"");

        renderMain.Should().Contain("function openVisionDebugPanel");
        renderMain.Should().Contain("function runVisionDebugCurrent");
        renderMain.Should().Contain("function redrawVisionDebugOverlay");
        renderMain.Should().Contain("registerMessageHandler(\"visionDebugResult\"");
        state.Should().Contain("applyVisionDebugResult");

        bundle.Should().Contain("function openVisionDebugPanel");
        bundle.Should().Contain("registerMessageHandler(\"visionDebugResult\"");
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
