using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIMobileLayoutContractTests
{
    [Fact]
    public void WebUi样式_移动端覆盖旧面板宽度()
    {
        string root = FindRepositoryRoot();
        string styleCss = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "css", "style.css"));

        styleCss.Should().Contain("@media (max-width: 760px)");
        styleCss.Should().Contain("#left-panel.stitch-left-panel");
        styleCss.Should().Contain("#camera-panel.stitch-camera-panel");
        styleCss.Should().Contain("#right-panel.stitch-log-panel");
        styleCss.Should().Contain("width: 100% !important;");
        styleCss.Should().Contain("flex-direction: column !important;");
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
