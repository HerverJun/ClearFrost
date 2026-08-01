using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIReplayContractTests
{
    [Fact]
    public void WebUi源码_包含Replay闭环消息Normalize和Render契约()
    {
        string root = FindRepositoryRoot();
        string stateJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "state.js"));
        string renderMainJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string indexHtml = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        string controllerEvents = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.Messages.cs"));

        foreach (string field in new[]
        {
            "candidateNewMissedDetectionCount",
            "candidateFixedMissedDetectionCount",
            "candidateNewFalseRejectCount",
            "candidateFixedFalseRejectCount",
            "changedDecisionCount",
            "approvalAvailable",
            "rejectionReasons"
        })
        {
            stateJs.Should().Contain(field);
            renderMainJs.Should().Contain(field);
        }

        stateJs.Should().Contain("normalizeReplayMessage");
        stateJs.Should().Contain("normalizeManualReviewMessage");
        stateJs.Should().Contain("applyReplayUpdate");
        stateJs.Should().Contain("applyManualReviewUpdate");
        renderMainJs.Should().Contain("renderReplayStatus");
        renderMainJs.Should().Contain("renderManualReviewStatus");
        renderMainJs.Should().Contain("manualReviewRecords");
        renderMainJs.Should().Contain("manualReviewResponse");
        renderMainJs.Should().Contain("datasetCreateStatus");
        renderMainJs.Should().Contain("replayRunProgress");
        renderMainJs.Should().Contain("replayRunCompleted");
        renderMainJs.Should().Contain("replayRunFailed");
        renderMainJs.Should().Contain("replayRunCanceled");
        renderMainJs.Should().Contain("modelApprovalAvailability");
        renderMainJs.Should().Contain("replayApprovalResponse");
        historyJs.Should().Contain("const ReplayBridgeErrorMessage = \"回放操作通信失败，请刷新页面后重试\"");
        historyJs.Should().Contain("function sendReplayCommand(cmd, value, statusId, pendingText, failureText)");
        historyJs.Should().Contain("const ReplayDefaultQueryLimit = 100");
        historyJs.Should().Contain("const ReplayMaxQueryLimit = 1000");
        historyJs.Should().Contain("Math.min(ReplayMaxQueryLimit, Math.trunc(raw))");
        historyJs.Should().Contain("setReplayPanelStatus(statusId, `${failureText}：${ReplayBridgeErrorMessage}`)");
        historyJs.Should().Contain("}, ReplayBridgeErrorMessage)");
        historyJs.Should().Contain("setReplayPanelStatus(statusId, `${pendingText} ${requestId}`)");
        historyJs.Should().Contain("query_manual_review_records");
        historyJs.Should().Contain("save_manual_review");
        historyJs.Should().Contain("create_replay_dataset");
        historyJs.Should().Contain("preview_replay_dataset");
        historyJs.Should().Contain("query_replay_datasets");
        historyJs.Should().Contain("archive_replay_dataset");
        historyJs.Should().Contain("run_replay_comparison");
        historyJs.Should().Contain("cancel_replay_run");
        historyJs.Should().Contain("query_replay_runs");
        historyJs.Should().Contain("query_replay_report");
        historyJs.Should().Contain("query_model_approval_evidence");
        historyJs.Should().Contain("run_replay_integrity_scan");
        historyJs.Should().Contain("approve_replay_candidate");
        historyJs.Should().Contain("resultJson");
        historyJs.Should().Contain("DeepLearningSummary");
        historyJs.Should().Contain("formatTraceDeepLearningSummary");
        historyJs.Should().Contain("深度学习:");
        historyJs.Should().Contain("requestId");
        historyJs.Should().Contain("sendReplayCommand(\"query_manual_review_records\", {");
        historyJs.Should().Contain("}, \"manual-review-response\", \"查询中\", \"查询失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"save_manual_review\", {");
        historyJs.Should().Contain("function readManualReviewExpectedRevision()");
        historyJs.Should().Contain("当前追溯记录缺少数据库记录编号，无法保存真值");
        historyJs.Should().Contain("复核版本号必须是非负整数");
        historyJs.Should().Contain("expectedRevision: expectedRevision.value");
        historyJs.Should().Contain("}, \"manual-review-response\", \"保存中\", \"保存失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"create_replay_dataset\", payload, \"replay-run-status\", \"生成验证样本集\", \"生成失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"preview_replay_dataset\", payload, \"replay-run-status\", \"预览中\", \"预览失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"query_replay_datasets\", getReplayPanelPayload(), \"replay-run-status\", \"查询数据集\", \"查询失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"archive_replay_dataset\", getReplayPanelPayload(), \"replay-run-status\", \"归档中\", \"归档失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"run_replay_comparison\", payload, \"replay-run-status\", \"对比新旧模型\", \"对比失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"cancel_replay_run\", getReplayPanelPayload(), \"replay-run-status\", \"正在取消\", \"取消失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"query_replay_runs\", getReplayPanelPayload(), \"replay-run-status\", \"查询运行记录\", \"查询失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"query_replay_report\", getReplayPanelPayload(), \"replay-run-status\", \"生成报告\", \"生成失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"query_model_approval_evidence\", getReplayPanelPayload(), \"replay-approval-status\", \"查询验证记录\", \"查询失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"run_replay_integrity_scan\", getReplayPanelPayload(), \"replay-approval-status\", \"扫描中\", \"扫描失败\")");
        historyJs.Should().Contain("sendReplayCommand(\"approve_replay_candidate\", payload, \"replay-approval-status\", \"确认上线\", \"确认上线失败\")");
        indexHtml.Should().Contain("replay-acceptance-panel");
        indexHtml.Should().Contain("工程师：模型回放验证");
        indexHtml.Should().Contain("模型上线验证");
        indexHtml.Should().Contain("生成验证样本集");
        indexHtml.Should().Contain("对比新旧模型");
        indexHtml.Should().Contain("确认新模型可上线");
        indexHtml.Should().Contain("验证记录");
        indexHtml.Should().Contain("manual-review-ground-truth-input");
        indexHtml.Should().Contain("replay-baseline-model");
        indexHtml.Should().Contain("replay-candidate-model");
        indexHtml.Should().Contain("replay-approval-status");
        controllerEvents.Should().Contain("OnQueryManualReviewRecords");
        controllerEvents.Should().Contain("OnSaveManualReview");
        controllerEvents.Should().Contain("OnCreateReplayDataset");
        controllerEvents.Should().Contain("OnPreviewReplayDataset");
        controllerEvents.Should().Contain("OnQueryReplayDatasets");
        controllerEvents.Should().Contain("OnArchiveReplayDataset");
        controllerEvents.Should().Contain("OnRunReplayComparison");
        controllerEvents.Should().Contain("OnCancelReplayRun");
        controllerEvents.Should().Contain("OnQueryReplayRuns");
        controllerEvents.Should().Contain("OnQueryReplayReport");
        controllerEvents.Should().Contain("OnQueryModelApprovalEvidence");
        controllerEvents.Should().Contain("OnRunReplayIntegrityScan");
        controllerEvents.Should().Contain("OnApproveReplayCandidate");
        controllerEvents.Should().Contain("private async Task DispatchObjectCommandAsync(");
        controllerEvents.Should().Contain("TryReadObjectCommandValue(root, out string payloadJson)");
        controllerEvents.Should().Contain("前端命令 value 必须是对象");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryManualReviewRecords?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnSaveManualReview?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnCreateReplayDataset?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnPreviewReplayDataset?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryReplayDatasets?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnArchiveReplayDataset?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnRunReplayComparison?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnCancelReplayRun?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryReplayRuns?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryReplayReport?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnQueryModelApprovalEvidence?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnRunReplayIntegrityScan?.Invoke(this, args))");
        controllerEvents.Should().Contain("await DispatchObjectCommandAsync(cmd, requestId, root, args => OnApproveReplayCandidate?.Invoke(this, args))");
        controller.Should().Contain("SendReplayRunStatus");
        controller.Should().Contain("SendReplayRunCompleted");
        controller.Should().Contain("SendModelApprovalAvailability");
        controller.Should().Contain("SendReplayApprovalResponse");
        controller.Should().Contain("SendManualReviewRecords");
        controller.Should().Contain("SendManualReviewResponse");
    }

    [Fact]
    public void ManualReviewSave_ValidatesRecordIdAndExpectedRevisionBeforeSending()
    {
        string root = FindRepositoryRoot();
        string historyPath = Path.Combine(root, "ClearFrost", "html", "js", "history.js");
        string script =
            "const historyPath = " + JsonSerializer.Serialize(historyPath) + ";\n" +
            "const elements = new Map();\n" +
            "const commands = [];\n" +
            "const toasts = [];\n" +
            "function createElement(id, value = '') { const element = { id, value, textContent: '', classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } }, dataset: {}, style: {}, querySelector() { return null; }, addEventListener() {} }; elements.set(id, element); return element; }\n" +
            "createElement('manual-review-expected-revision', '');\n" +
            "createElement('manual-review-ground-truth-input', 'NG');\n" +
            "createElement('manual-review-disposition-input', 'Corrected');\n" +
            "createElement('manual-review-notes', 'checked');\n" +
            "createElement('manual-review-response', '');\n" +
            "global.document = { getElementById(id) { return elements.get(id) || null; }, querySelectorAll() { return []; }, createElement(tag) { return createElement(`created-${tag}-${Math.random()}`); } };\n" +
            "global.window = {\n" +
            "  CF_BRIDGE: { registerMessageHandler() {}, sendCommand(cmd, value) { commands.push({ cmd, value }); return `req-${commands.length}`; } },\n" +
            "  CF_UTILS: { escapeHtml(value) { return String(value ?? ''); } },\n" +
            "  CF_ERROR_ADVICE: { format() { return ''; } },\n" +
            "  addEventListener() {}, showToast(message) { toasts.push(String(message)); }, addLog() {}, currentNGDate: '2026-07-07', currentNGHour: '08'\n" +
            "};\n" +
            "require(historyPath);\n" +
            "window.openTraceViewer({ InspectionId: 'CF-1' });\n" +
            "window.saveManualReview();\n" +
            "if (commands.length !== 0) throw new Error('missing detection record id was submitted');\n" +
            "if (!toasts.some((message) => message.includes('缺少数据库记录编号'))) throw new Error('missing record id toast missing: ' + toasts.join('|'));\n" +
            "window.openTraceViewer({ InspectionId: 'CF-1', DetectionRecordId: 42 });\n" +
            "elements.get('manual-review-expected-revision').value = 'bad';\n" +
            "window.saveManualReview();\n" +
            "if (commands.length !== 0) throw new Error('invalid revision was submitted');\n" +
            "if (!toasts.some((message) => message.includes('复核版本号必须是非负整数'))) throw new Error('invalid revision toast missing: ' + toasts.join('|'));\n" +
            "elements.get('manual-review-expected-revision').value = '3';\n" +
            "window.saveManualReview();\n" +
            "if (commands.length !== 1) throw new Error('valid manual review was not submitted');\n" +
            "const command = commands[0];\n" +
            "if (command.cmd !== 'save_manual_review') throw new Error('unexpected command: ' + command.cmd);\n" +
            "if (command.value.detectionRecordId !== 42 || command.value.expectedRevision !== 3) throw new Error('unexpected payload: ' + JSON.stringify(command.value));\n" +
            "if (command.value.groundTruth !== 'NG' || command.value.disposition !== 'Corrected' || command.value.notes !== 'checked') throw new Error('manual review form values missing: ' + JSON.stringify(command.value));\n";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);

        process.Start().Should().BeTrue();
        process.WaitForExit(10_000).Should().BeTrue();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.ExitCode.Should().Be(0, $"Node stdout: {output}; stderr: {error}");
    }

    [Fact]
    public void ReplayPanelPayload_ClampsLimitToBackendRecordLimit()
    {
        string root = FindRepositoryRoot();
        string historyPath = Path.Combine(root, "ClearFrost", "html", "js", "history.js");
        string script =
            "const historyPath = " + JsonSerializer.Serialize(historyPath) + ";\n" +
            "const elements = new Map();\n" +
            "const commands = [];\n" +
            "function element(id, value = '') { const node = { id, value, textContent: '', classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } }, dataset: {}, querySelector() { return null; }, addEventListener() {} }; elements.set(id, node); return node; }\n" +
            "element('replay-query-limit', '10000');\n" +
            "element('replay-dataset-input', 'dataset-a');\n" +
            "element('replay-run-input', 'run-a');\n" +
            "element('replay-baseline-model', 'base.onnx');\n" +
            "element('replay-candidate-model', 'candidate.onnx');\n" +
            "element('replay-run-status', '');\n" +
            "global.document = { getElementById(id) { return elements.get(id) || null; }, querySelectorAll() { return []; }, createElement(tag) { return element(`created-${tag}-${Math.random()}`); } };\n" +
            "global.window = {\n" +
            "  CF_BRIDGE: { registerMessageHandler() {}, sendCommand(cmd, value) { commands.push({ cmd, value }); return `req-${commands.length}`; } },\n" +
            "  CF_UTILS: { escapeHtml(value) { return String(value ?? ''); } },\n" +
            "  CF_ERROR_ADVICE: { format() { return ''; } },\n" +
            "  addEventListener() {}, showToast() {}, addLog() {}, currentNGDate: '2026-07-07', currentNGHour: '08'\n" +
            "};\n" +
            "require(historyPath);\n" +
            "window.createReplayDataset();\n" +
            "if (commands[0].value.limit !== 1000) throw new Error('large limit was not clamped: ' + commands[0].value.limit);\n" +
            "commands.length = 0;\n" +
            "elements.get('replay-query-limit').value = '0';\n" +
            "window.createReplayDataset();\n" +
            "if (commands[0].value.limit !== 1) throw new Error('zero limit was not clamped to one: ' + commands[0].value.limit);\n" +
            "commands.length = 0;\n" +
            "elements.get('replay-query-limit').value = 'abc';\n" +
            "window.createReplayDataset();\n" +
            "if (commands[0].value.limit !== 100) throw new Error('invalid limit did not use default: ' + commands[0].value.limit);\n";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "node",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add(script);

        process.Start().Should().BeTrue();
        process.WaitForExit(10_000).Should().BeTrue();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.ExitCode.Should().Be(0, $"Node stdout: {output}; stderr: {error}");
    }

    [Fact]
    public void WebUiBundle_与源JS保持一致并包含Replay契约()
    {
        string root = FindRepositoryRoot();
        string jsRoot = Path.Combine(root, "ClearFrost", "html", "js");
        string[] files =
        {
            "bridge.js",
            "state.js",
            "coordinate-mapping.js",
            "render-main.js",
            "settings.js",
            "camera.js",
            "history.js",
            "roi.js",
            "boot.js",
            "app.js",
            "ui.js"
        };
        string expected = string.Join(Environment.NewLine, files.Select(file => File.ReadAllText(Path.Combine(jsRoot, file))));
        string bundle = File.ReadAllText(Path.Combine(jsRoot, "bundle.js"));

        bundle.Should().Be(expected);
        bundle.Should().Contain("normalizeReplayMessage");
        bundle.Should().Contain("normalizeManualReviewMessage");
        bundle.Should().Contain("replayRunCompleted");
        bundle.Should().Contain("manualReviewResponse");
        bundle.Should().Contain("modelApprovalAvailability");
        bundle.Should().Contain("replayApprovalResponse");
        bundle.Should().Contain("const ReplayBridgeErrorMessage = \"回放操作通信失败，请刷新页面后重试\"");
        bundle.Should().Contain("function sendReplayCommand(cmd, value, statusId, pendingText, failureText)");
        bundle.Should().Contain("create_replay_dataset");
        bundle.Should().Contain("preview_replay_dataset");
        bundle.Should().Contain("query_replay_datasets");
        bundle.Should().Contain("archive_replay_dataset");
        bundle.Should().Contain("cancel_replay_run");
        bundle.Should().Contain("query_replay_runs");
        bundle.Should().Contain("query_replay_report");
        bundle.Should().Contain("query_model_approval_evidence");
        bundle.Should().Contain("run_replay_integrity_scan");
        bundle.Should().Contain("approve_replay_candidate");
        bundle.Should().Contain("resultJson");
        bundle.Should().Contain("DeepLearningSummary");
        bundle.Should().Contain("formatTraceDeepLearningSummary");
    }

    [Fact]
    public void WebUi源码_Replay追溯工具条拥有独立Grid行()
    {
        string root = FindRepositoryRoot();
        string styleCss = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "css", "style.css"));

        styleCss.Should().Contain("grid-template-rows: 126px auto minmax(0, 1fr) 56px;");
        styleCss.Should().Contain(".cf-stitch-page #gallery-modal #replay-acceptance-panel");
        styleCss.Should().Contain("grid-column: 2;");
        styleCss.Should().Contain("grid-row: 2;");
        styleCss.Should().Contain(".cf-stitch-page #gallery-modal #ng-image-grid");
        styleCss.Should().Contain("grid-row: 3;");
        styleCss.Should().Contain(".cf-stitch-page #gallery-modal #trace-pagination");
        styleCss.Should().Contain("grid-row: 4;");
        styleCss.Should().Contain("grid-template-rows: auto 120px auto minmax(0, 1fr) 56px;");
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
