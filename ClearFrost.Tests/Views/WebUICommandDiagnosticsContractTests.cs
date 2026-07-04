using FluentAssertions;

using System.Text.RegularExpressions;

namespace ClearFrost.Tests.Views;

public class WebUICommandDiagnosticsContractTests
{
    [Fact]
    public void WebUi命令桥_未知命令回传可见诊断()
    {
        string root = FindRepositoryRoot();
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
        string renderMainJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string bundle = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        controller.Should().Contain("UnknownCommand");
        controller.Should().Contain("MissingCommand");
        controller.Should().Contain("CommandException");
        controller.Should().Contain("前端命令处理异常");
        controller.Should().Contain("PostMessage(\"commandError\"");
        controller.Should().Contain("LogToFrontend(normalizedMessage, \"error\")");

        renderMainJs.Should().Contain("function handleCommandError");
        renderMainJs.Should().Contain("addLog(`${message}${requestId}`, \"error\")");
        renderMainJs.Should().Contain("registerMessageHandler(\"commandError\", handleCommandError)");

        bundle.Should().Contain("function handleCommandError");
        bundle.Should().Contain("registerMessageHandler(\"commandError\", handleCommandError)");
    }

    [Fact]
    public void WebUi命令桥_前端发出的命令后端均有分发Case()
    {
        string root = FindRepositoryRoot();
        string jsRoot = Path.Combine(root, "ClearFrost", "html", "js");

        var frontendCommands = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(jsRoot, "*.js").Where(file => !file.EndsWith("bundle.js", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in Regex.Matches(
                File.ReadAllText(file),
                "sendCommand\\(\\s*[\"']([A-Za-z0-9_]+)[\"']"))
            {
                frontendCommands.Add(match.Groups[1].Value);
            }
        }

        string indexHtml = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        foreach (Match match in Regex.Matches(indexHtml, "data-(?:change-)?cmd=[\"']([A-Za-z0-9_]+)[\"']"))
        {
            frontendCommands.Add(match.Groups[1].Value);
        }

        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
        var backendCommands = Regex.Matches(controller, "case\\s+\"([A-Za-z0-9_]+)\"\\s*:")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        frontendCommands.Should().NotBeEmpty();
        frontendCommands.Where(command => !backendCommands.Contains(command)).Should().BeEmpty();
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
