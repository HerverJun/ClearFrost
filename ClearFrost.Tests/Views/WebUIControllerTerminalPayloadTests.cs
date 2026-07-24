using System.Reflection;
using ClearFrost.Core.Inspection;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIControllerTerminalPayloadTests
{
    [Fact]
    public void BuildInspectionPayload_包含终态握手和周期字段()
    {
        var context = new InspectionContext
        {
            InspectionId = "CF-1",
            TriggerSource = "PLC",
            TerminalHandshakeAttempted = true,
            TerminalHandshakeSucceeded = false,
            TerminalHandshakeErrorCode = "AckTimeout",
            TerminalHandshakeSignalName = "ResultAck",
            TerminalHandshakeAddress = "D120",
            TerminalHandshakeMessage = "ACK timeout",
            CycleSucceeded = false
        };

        object payload = BuildPayload(context, isOk: true);

        payload.GetPropertyValue("isOk").Should().Be(true);
        payload.GetPropertyValue("terminalHandshakeAttempted").Should().Be(true);
        payload.GetPropertyValue("terminalHandshakeSucceeded").Should().Be(false);
        payload.GetPropertyValue("terminalHandshakeErrorCode").Should().Be("AckTimeout");
        payload.GetPropertyValue("terminalHandshakeSignalName").Should().Be("ResultAck");
        payload.GetPropertyValue("terminalHandshakeAddress").Should().Be("D120");
        payload.GetPropertyValue("terminalHandshakeMessage").Should().Be("ACK timeout");
        payload.GetPropertyValue("cycleSucceeded").Should().Be(false);
    }

    [Fact]
    public void WebUi源码_终态失败以独立周期失败状态覆盖主卡和流水()
    {
        string root = FindRepositoryRoot();
        string stateJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "state.js"));
        string renderMainJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string styleCss = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "css", "style.css"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        foreach (string field in new[]
        {
            "terminalHandshakeAttempted",
            "terminalHandshakeSucceeded",
            "terminalHandshakeErrorCode",
            "terminalHandshakeSignalName",
            "terminalHandshakeAddress",
            "terminalHandshakeMessage",
            "cycleSucceeded"
        })
        {
            stateJs.Should().Contain(field);
            renderMainJs.Should().Contain(field);
            controller.Should().Contain(field);
        }

        controller.Should().Contain("inspection = inspectionPayload");
        renderMainJs.Should().Contain("item?.terminalHandshakeAttempted === true && item?.terminalHandshakeSucceeded === false");
        renderMainJs.Should().Contain("result-cycle-failed");
        renderMainJs.Should().Contain("cycle-failed");
        renderMainJs.Should().Contain("terminalFailed ? \"周期失败\"");
        styleCss.Should().Contain(".result-cycle-failed");
        styleCss.Should().Contain(".cf-flow-row.cycle-failed");
    }

    private static object BuildPayload(InspectionContext context, bool? isOk)
    {
        MethodInfo method = typeof(WebUIController).GetMethod(
            "BuildInspectionPayload",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(WebUIController), "BuildInspectionPayload");

        return method.Invoke(
            null,
            new object?[]
            {
                context,
                isOk,
                "message",
                1,
                "model.onnx",
                false,
                true,
                "SN-1",
                true,
                string.Empty,
                "rule",
                string.Empty,
                Array.Empty<string>()
            })!;
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

internal static class ReflectionPropertyExtensions
{
    public static object? GetPropertyValue(this object instance, string name)
    {
        return instance.GetType().GetProperty(name)?.GetValue(instance);
    }
}
