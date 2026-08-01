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
        controller.Should().Contain("MissingValue");
        controller.Should().Contain("InvalidValue");
        controller.Should().Contain("CommandException");
        controller.Should().Contain("前端命令处理异常");
        controller.Should().Contain("前端命令缺少 value 字段");
        controller.Should().Contain("PostMessage(\"commandError\"");
        controller.Should().Contain("LogToFrontend(normalizedMessage, \"error\")");

        renderMainJs.Should().Contain("function handleCommandError");
        renderMainJs.Should().Contain("addLog(`${message}${requestId}`, \"error\")");
        renderMainJs.Should().Contain("showToast(message, \"error\", 1800)");
        renderMainJs.Should().Contain("window.dispatchEvent(new CustomEvent(\"cf-command-error\"");
        renderMainJs.Should().Contain("requestId: envelope?.requestId || \"\"");
        renderMainJs.Should().Contain("registerMessageHandler(\"commandError\", handleCommandError)");

        bundle.Should().Contain("function handleCommandError");
        bundle.Should().Contain("showToast(message, \"error\", 1800)");
        bundle.Should().Contain("window.dispatchEvent(new CustomEvent(\"cf-command-error\"");
        bundle.Should().Contain("registerMessageHandler(\"commandError\", handleCommandError)");
    }

    [Fact]
    public void WebUi命令桥_空字符串和MalformedRoi会回传可见错误()
    {
        string root = FindRepositoryRoot();
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        controller.Should().Contain("TryReadNonEmptyStringCommandValue(root, out string modelName)");
        controller.Should().Contain("TryReadStringCommandValue(root, out string aux1ModelName)");
        controller.Should().Contain("TryReadStringCommandValue(root, out string aux2ModelName)");
        controller.Should().Contain("TryReadNonEmptyStringCommandValue(root, out string camId)");
        controller.Should().Contain("TryReadNonEmptyStringCommandValue(root, out string camIdToDelete)");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, \"前端命令 value 不能为空: change_model\")");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, \"前端命令 value 必须是字符串: set_auxiliary1_model\")");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, \"前端命令 value 必须是字符串: set_auxiliary2_model\")");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, \"前端命令 value 不能为空: switch_camera\")");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, \"前端命令 value 不能为空: delete_camera\")");
        controller.Should().Contain("TryReadRoiRect(root, out float[] rectArray, out string roiError)");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, roiError)");
        controller.Should().Contain("前端命令缺少 ROI rect 字段");
        controller.Should().Contain("ROI rect 必须包含 4 个数值");
        controller.Should().Contain("ROI rect 必须是有限数值");
        controller.Should().Contain("ROI rect 数值必须在 0 到 1 之间");
        controller.Should().Contain("ROI rect 宽高必须大于 0，清除 ROI 请使用 [0,0,0,0]");
        controller.Should().Contain("ROI rect 不能超出图像边界");
        controller.Should().Contain("ROI rect 解析失败");
        controller.Should().Contain("private Task SendInvalidValueAsync(string cmd, string? requestId, string message)");
    }

    [Fact]
    public void WebUi命令桥_整数阈值解析失败会回传可见错误()
    {
        string root = FindRepositoryRoot();
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        controller.Should().Contain("TryReadInt32CommandValue(root, out int threshold)");
        controller.Should().Contain("OnThresholdChanged?.Invoke(this, threshold)");
        controller.Should().Contain("前端命令 value 必须是整数");
        controller.Should().Contain("int.TryParse(valueElement.GetString(), out value)");
        controller.Should().Contain("await SendInvalidValueAsync(cmd, requestId, $\"前端命令 value 必须是整数: {cmd}\")");
    }

    [Fact]
    public void WebUi命令桥_数值布尔和任务类型解析失败会回传可见错误()
    {
        string root = FindRepositoryRoot();
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        controller.Should().Contain("TryReadUnitFloatCommandValue(root, out float conf)");
        controller.Should().Contain("TryReadUnitFloatCommandValue(root, out float iou)");
        controller.Should().Contain("value is >= 0f and <= 1f");
        controller.Should().Contain("前端命令 value 必须是 0 到 1 的数值: set_confidence");
        controller.Should().Contain("前端命令 value 必须是 0 到 1 的数值: set_iou");
        controller.Should().Contain("TryReadInt32CommandValue(root, out int taskType) && IsSupportedTaskType(taskType)");
        controller.Should().Contain("return taskType is 0 or 1 or 2 or 3 or 5 or 6");
        controller.Should().Contain("前端命令 value 不是受支持的任务类型: set_task_type");
        controller.Should().Contain("TryReadBoolCommandValue(root, out bool enableMultiModel)");
        controller.Should().Contain("bool.TryParse(valueElement.GetString(), out value)");
        controller.Should().Contain("前端命令 value 必须是布尔值: toggle_multi_model");
    }

    [Fact]
    public void WebUi命令桥_对象Payload命令类型错误会回传可见错误()
    {
        string root = FindRepositoryRoot();
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        controller.Should().Contain("TryReadObjectCommandValue(root, out string presetSaveJson)");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string settingsJson)");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string historyRuleJson)");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string releasePayload)");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string previewPayload)");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string addCameraJson)");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string directConnectJson)");
        controller.Should().Contain("private async Task DispatchObjectCommandAsync(");
        controller.Should().Contain("TryReadObjectCommandValue(root, out string payloadJson)");
        controller.Should().Contain("前端命令 value 必须是对象: save_project_preset");
        controller.Should().Contain("前端命令 value 必须是对象: save_settings");
        controller.Should().Contain("前端命令 value 必须是对象: run_history_rule_preview");
        controller.Should().Contain("前端命令 value 必须是对象: manual_release");
        controller.Should().Contain("前端命令 value 必须是对象: capture_camera_preview");
        controller.Should().Contain("前端命令 value 必须是对象: add_camera");
        controller.Should().Contain("前端命令 value 必须是对象: direct_connect_camera");
        controller.Should().Contain("前端命令 value 必须是对象: {cmd}");
    }

    [Fact]
    public void WebUi命令桥_需要Value的命令缺参时不会静默跳过()
    {
        string root = FindRepositoryRoot();
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        controller.Should().Contain("private static readonly HashSet<string> CommandsRequiringValue");
        controller.Should().Contain("CommandRequiresValue(cmd) && IsMissingCommandValue(root)");
        controller.Should().Contain("await SendCommandErrorAsync(");
        controller.Should().Contain("\"MissingValue\"");
        controller.Should().Contain("return;");

        foreach (string command in new[]
        {
            "save_project_preset",
            "delete_project_preset",
            "change_model",
            "update_roi",
            "set_confidence",
            "set_iou",
            "set_task_type",
            "save_settings",
            "get_ng_hours",
            "get_ng_images",
            "run_history_rule_preview",
            "manual_release",
            "capture_camera_preview",
            "verify_diagnostic_package",
            "maintenance_advice_action",
            "shift_task_action",
            "vision_debug_query_recent",
            "vision_debug_run_current",
            "vision_debug_run_history",
            "vision_debug_run_batch",
            "vision_debug_save_params",
            "vision_debug_apply_template",
            "switch_camera",
            "add_camera",
            "delete_camera",
            "direct_connect_camera",
            "toggle_multi_model",
            "query_manual_review_records",
            "save_manual_review",
            "create_replay_dataset",
            "run_replay_comparison",
            "approve_replay_candidate",
            "preview_replay_dataset",
            "query_replay_datasets",
            "archive_replay_dataset",
            "cancel_replay_run",
            "query_replay_runs",
            "query_replay_report",
            "query_model_approval_evidence",
            "run_replay_integrity_scan"
        })
        {
            controller.Should().Contain($"\"{command}\"");
        }
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

    [Fact]
    public void WebUi命令桥_Html声明的Action均有前端实现()
    {
        string root = FindRepositoryRoot();
        string indexHtml = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        string jsRoot = Path.Combine(root, "ClearFrost", "html", "js");
        string source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(jsRoot, "*.js")
                .Where(file => !file.EndsWith("bundle.js", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));

        var actions = new SortedSet<string>(
            Regex.Matches(indexHtml, "data-action=[\"']([A-Za-z0-9_]+)[\"']")
                .Select(match => match.Groups[1].Value),
            StringComparer.Ordinal);

        actions.Should().NotBeEmpty();
        actions.Where(action =>
                !Regex.IsMatch(source, $@"\bfunction\s+{Regex.Escape(action)}\b") &&
                !Regex.IsMatch(source, $@"\b{Regex.Escape(action)}\s*[,=:]"))
            .Should()
            .BeEmpty();
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
