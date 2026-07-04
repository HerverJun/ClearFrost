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
        renderMainJs.Should().Contain("fieldDebugResult");
        renderMainJs.Should().Contain("diagnosticPackageExportResult");
        stateJs.Should().Contain("applyFieldDebugResult");
        stateJs.Should().Contain("applyDiagnosticPackageExportResult");
        styleCss.Should().Contain(".cf-diagnostics-panel");
        styleCss.Should().Contain("@media (max-width: 640px)");

        foreach (string command in new[]
        {
            "export_diagnostic_package",
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
