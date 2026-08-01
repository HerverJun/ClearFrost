using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUISettingsBridgeContractTests
{
    [Fact]
    public void SettingsCommands_ShowVisibleFailureAndRollbackOptimisticStateWhenBridgeSendFails()
    {
        string root = FindRepositoryRoot();
        string settingsJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "settings.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        foreach (string script in new[] { settingsJs, bundleJs })
        {
            script.Should().Contain("const SettingsBridgeErrorMessage = \"设置通信失败，请刷新页面后重试\"");
            script.Should().Contain("const DatasetCollectBridgeErrorMessage = \"数据集采集通信失败，请刷新页面后重试\"");
            script.Should().Contain("const PendingSettingsFailureTtlMs = 30000");
            script.Should().Contain("const pendingSettingsFailures = new Map()");
            script.Should().Contain("function sendSettingsCommand(cmd, value = null, onFailure = null, failureMessage = SettingsBridgeErrorMessage)");
            script.Should().Contain("bridge?.sendCommand?.(cmd, value)");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("console.error(`Settings command failed: ${cmd}`, error)");
            script.Should().Contain("window.showToast?.(failureMessage, \"error\", 1800)");
            script.Should().Contain("registerPendingSettingsFailure(requestId, onFailure)");
            script.Should().Contain("function handleSettingsCommandError(event)");
            script.Should().Contain("if (runPendingSettingsFailure(requestId, message)) return");
            script.Should().Contain("window.addEventListener(\"cf-command-error\", handleSettingsCommandError)");
            script.Should().Contain("const previousEnabled = Boolean(store.state.settings?.EnableMultiModelFallback ?? !enabled)");
            script.Should().Contain("applyMultiModelUiState(previousEnabled)");
            script.Should().Contain("sendSettingsCommand(\"toggle_multi_model\", enabled");
            script.Should().Contain("sendSettingsCommand(\"save_project_preset\", { id: presetId, name, preset }, () =>");
            script.Should().Contain("pendingProjectPresetId = \"\"");
            script.Should().Contain("const requestId = sendSettingsCommand(\"delete_project_preset\", presetId)");
            script.Should().Contain("sendSettingsCommand(\"export_config_migration\")");
            script.Should().Contain("sendSettingsCommand(\"import_config_migration\")");
            script.Should().Contain("const previousSettings = store.state.settings || {}");
            script.Should().Contain("function readFiniteNumberInput(input)");
            script.Should().Contain("const raw = String(input?.value ?? \"\").trim()");
            script.Should().Contain("return Number.isFinite(value) ? value : null");
            script.Should().Contain("const SupportedTaskTypes = new Set([0, 1, 2, 3, 5, 6])");
            script.Should().Contain("function readSupportedTaskType(value)");
            script.Should().Contain("return Number.isInteger(taskType) && SupportedTaskTypes.has(taskType) ? taskType : null");
            script.Should().Contain("const numVal = readFiniteNumberInput(input)");
            script.Should().Contain("if (numVal !== null) {");
            script.Should().Contain("sendSettingsCommand(\"save_settings\", data, () =>");
            script.Should().Contain("store.state.settings = previousSettings");
            script.Should().Contain("function markChangeCommandConfirmedValue(element)");
            script.Should().Contain("element.dataset.confirmedValue = String(element.value ?? \"\")");
            script.Should().Contain("delete element.dataset.pendingChangeRequestId");
            script.Should().Contain("delete element.dataset.previousValue");
            script.Should().Contain("markChangeCommandConfirmedValue(select)");
            script.Should().Contain("sendSettingsCommand(\"change_model\", select.value)");
            script.Should().Contain("function normalizePostprocessOptions(options)");
            script.Should().Contain("const seenKeys = new Set()");
            script.Should().Contain("const lookupKey = key.toLowerCase()");
            script.Should().Contain("if (!key || seenKeys.has(lookupKey)) return normalized");
            script.Should().Contain("seenKeys.add(lookupKey)");
            script.Should().Contain("normalized[key] = String(rawValue ?? \"\")");
            script.Should().Contain("const taskType = String(item?.taskType || item?.TaskType || item?.task || item?.Task || \"\").trim()");
            script.Should().Contain("const modelId = String(item?.modelId || item?.ModelId || \"\").trim()");
            script.Should().Contain("const version = String(item?.version || item?.Version || \"\").trim()");
            script.Should().Contain("const sha256 = String(item?.sha256 || item?.Sha256 || item?.modelHash || item?.ModelHash || \"\").trim().toLowerCase()");
            script.Should().Contain("const postprocessorKey = String(item?.postprocessorKey || item?.PostprocessorKey || item?.postprocessor || item?.Postprocessor || \"\").trim()");
            script.Should().Contain("const scoreNormalization = String(item?.scoreNormalization || item?.ScoreNormalization || item?.normalization || item?.Normalization || \"\").trim()");
            script.Should().Contain("const inputWidth = normalizePositiveInteger(item?.inputWidth ?? item?.InputWidth ?? item?.width ?? item?.Width)");
            script.Should().Contain("const inputHeight = normalizePositiveInteger(item?.inputHeight ?? item?.InputHeight ?? item?.height ?? item?.Height)");
            script.Should().Contain("const labelCount = normalizePositiveInteger(item?.labelCount ?? item?.LabelCount ?? item?.labelsCount ?? item?.LabelsCount)");
            script.Should().Contain("const isApprovedPackage = normalizeBoolean(item?.isApprovedPackage ?? item?.IsApprovedPackage)");
            script.Should().Contain("item?.postprocessOptions || item?.PostprocessOptions || item?.postprocessorOptions || item?.PostprocessorOptions");
            script.Should().Contain("return { value, text, fileName, modelId, version, sha256, taskType, postprocessorKey, scoreNormalization, postprocessOptions, inputWidth, inputHeight, labelCount, isApprovedPackage }");
            script.Should().Contain("function normalizePositiveInteger(value)");
            script.Should().Contain("function normalizeBoolean(value)");
            script.Should().Contain("function formatPostprocessOptions(options, limit = Number.POSITIVE_INFINITY)");
            script.Should().Contain(".sort(([left], [right]) => left.localeCompare(right))");
            script.Should().Contain("function formatModelOptionMetadata(model, optionLimit = 3, fullHash = false)");
            script.Should().Contain("formatModelIdentity(model)");
            script.Should().Contain("formatModelHash(model, fullHash)");
            script.Should().Contain("function formatModelIdentity(model)");
            script.Should().Contain("function formatModelHash(model, fullHash = false)");
            script.Should().Contain("formatInputSize(model)");
            script.Should().Contain("formatLabelCount(model)");
            script.Should().Contain("formatPostprocessOptions(model.postprocessOptions, optionLimit)");
            script.Should().Contain("function applyModelOptionMetadata(option, model)");
            script.Should().Contain("const titleMetadata = formatModelOptionMetadata(model, Number.POSITIVE_INFINITY, true)");
            script.Should().Contain("option.dataset.modelId = model.modelId");
            script.Should().Contain("option.dataset.version = model.version");
            script.Should().Contain("option.dataset.sha256 = model.sha256");
            script.Should().Contain("option.dataset.isApprovedPackage = model.isApprovedPackage ? \"true\" : \"false\"");
            script.Should().Contain("option.dataset.taskType = model.taskType");
            script.Should().Contain("option.dataset.postprocessorKey = model.postprocessorKey");
            script.Should().Contain("option.dataset.scoreNormalization = model.scoreNormalization");
            script.Should().Contain("option.dataset.postprocessOptions = formatPostprocessOptions(model.postprocessOptions)");
            script.Should().Contain("option.dataset.inputWidth = model.inputWidth ? String(model.inputWidth) : \"\"");
            script.Should().Contain("option.dataset.inputHeight = model.inputHeight ? String(model.inputHeight) : \"\"");
            script.Should().Contain("option.dataset.labelCount = model.labelCount ? String(model.labelCount) : \"\"");
            script.Should().Contain("option.title = titleMetadata ?");
            script.Should().Contain("applyModelOptionMetadata(option, model)");
            script.Should().Contain("sendSettingsCommand(\"get_project_presets\")");
            script.Should().Contain("sendSettingsCommand(\"open_settings\")");
            script.Should().Contain("sendSettingsCommand(\"set_confidence\", value, () =>");
            script.Should().Contain("setThresholdControl(\"conf-input\", \"conf-slider\", fallback, fallback)");
            script.Should().Contain("sendSettingsCommand(\"set_iou\", value, () =>");
            script.Should().Contain("setThresholdControl(\"iou-input\", \"iou-slider\", fallback, fallback)");
            script.Should().Contain("window.showToast?.(\"不支持的检测任务类型\", \"error\", 1800)");
            script.Should().Contain("restoreTaskTypeSelection(previousTaskType)");
            script.Should().Contain("sendSettingsCommand(\"set_task_type\", taskType, () =>");
            script.Should().Contain("sendSettingsCommand(\"collect_dataset\", null, (error) =>");
            script.Should().Contain("btn.disabled = false");
            script.Should().Contain("function setDatasetCollectFailure(message = DatasetCollectBridgeErrorMessage)");
            script.Should().Contain("setDatasetCollectFailure(error?.message || DatasetCollectBridgeErrorMessage)");
            script.Should().Contain("resultDiv.textContent = message || DatasetCollectBridgeErrorMessage");
            script.Should().NotContain("bridge.sendCommand(\"toggle_multi_model\"");
            script.Should().NotContain("bridge.sendCommand(\"save_project_preset\"");
            script.Should().NotContain("bridge.sendCommand(\"delete_project_preset\"");
            script.Should().NotContain("bridge.sendCommand(\"export_config_migration\"");
            script.Should().NotContain("bridge.sendCommand(\"import_config_migration\"");
            script.Should().NotContain("bridge.sendCommand(\"save_settings\"");
            script.Should().NotContain("bridge.sendCommand(\"change_model\"");
            script.Should().NotContain("bridge.sendCommand(\"get_project_presets\"");
            script.Should().NotContain("bridge.sendCommand(\"open_settings\"");
            script.Should().NotContain("bridge.sendCommand(\"set_confidence\"");
            script.Should().NotContain("bridge.sendCommand(\"set_iou\"");
            script.Should().NotContain("bridge.sendCommand(\"set_task_type\"");
            script.Should().NotContain("bridge.sendCommand(\"collect_dataset\"");
        }
    }

    [Fact]
    public void SettingsModelList_NormalizesPostprocessOptionMetadataForSelectOptions()
    {
        string root = FindRepositoryRoot();
        string settingsPath = Path.Combine(root, "ClearFrost", "html", "js", "settings.js");
        string script =
            "const settingsPath = " + JsonSerializer.Serialize(settingsPath) + ";\n" +
            "const elements = new Map();\n" +
            "function createSelect(id) {\n" +
            "  return { id, options: [], value: '', selectedIndex: -1, dataset: {}, add(option) { this.options.push(option); this.value = option.value; this.selectedIndex = this.options.length - 1; } };\n" +
            "}\n" +
            "function createClassList() { return { add() {}, remove() {}, contains() { return false; } }; }\n" +
            "elements.set('model-select', createSelect('model-select'));\n" +
            "elements.set('auxiliary1-select', createSelect('auxiliary1-select'));\n" +
            "elements.set('auxiliary2-select', createSelect('auxiliary2-select'));\n" +
            "global.document = {\n" +
            "  getElementById(id) { return elements.get(id) || null; },\n" +
            "  createElement(tag) { return { tagName: tag, dataset: {}, classList: createClassList(), value: '', text: '', title: '' }; }\n" +
            "};\n" +
            "global.window = {\n" +
            "  CF_BRIDGE: { registerMessageHandler() {}, sendCommand() { return 'request-id'; } },\n" +
            "  CF_STORE: { state: { settings: {} }, notify() {}, applyBootstrapSnapshot() {} },\n" +
            "  addEventListener() {}, addLog() {}, showToast() {}, requestAnimationFrame(callback) { if (callback) callback(); },\n" +
            "  setTimeout() { return 0; }, clearTimeout() {}, confirm() { return true; }\n" +
            "};\n" +
            "require(settingsPath);\n" +
            "window.initModelList([{ value: 'approved:x:y:z', text: 'pkg', fileName: 'classifier.onnx', modelId: 'pkg-classifier', version: 'v2', sha256: 'ABCDEF1234567890', isApprovedPackage: 'true', taskType: 'Classification', postprocessorKey: 'classification', scoreNormalization: 'Softmax', inputWidth: '224', inputHeight: 224.9, labelCount: 2, postprocessOptions: { ' top_k ': '3', TOP_K: 'ignored', apply_nms: true, score_index: 2, box_format: 'xyxy' } }]);\n" +
            "const option = elements.get('model-select').options[0];\n" +
            "if (!option) throw new Error('model option missing');\n" +
            "if (option.dataset.postprocessOptions !== 'apply_nms=true, box_format=xyxy, score_index=2, top_k=3') throw new Error('unexpected dataset: ' + option.dataset.postprocessOptions);\n" +
            "if (option.dataset.inputWidth !== '224' || option.dataset.inputHeight !== '224' || option.dataset.labelCount !== '2') throw new Error('unexpected dimensions: ' + JSON.stringify(option.dataset));\n" +
            "if (option.dataset.modelId !== 'pkg-classifier' || option.dataset.version !== 'v2' || option.dataset.sha256 !== 'abcdef1234567890' || option.dataset.isApprovedPackage !== 'true') throw new Error('unexpected identity: ' + JSON.stringify(option.dataset));\n" +
            "if (!option.text.includes('pkg-classifier@v2') || !option.text.includes('#abcdef123456')) throw new Error('identity summary missing: ' + option.text);\n" +
            "if (!option.text.includes('224x224') || !option.text.includes('labels=2')) throw new Error('dimension summary missing: ' + option.text);\n" +
            "if (!option.text.includes('apply_nms=true, box_format=xyxy, score_index=2 +1')) throw new Error('bounded summary missing: ' + option.text);\n" +
            "if (!option.title.includes('#abcdef1234567890') || !option.title.includes('top_k=3') || option.title.includes('TOP_K=ignored')) throw new Error('full title normalization failed: ' + option.title);\n";

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
    public void SettingsSave_SkipsBlankOrInvalidNumericFieldsAndKeepsZeroValues()
    {
        string root = FindRepositoryRoot();
        string settingsPath = Path.Combine(root, "ClearFrost", "html", "js", "settings.js");
        string script =
            "const settingsPath = " + JsonSerializer.Serialize(settingsPath) + ";\n" +
            "const elements = new Map();\n" +
            "function input(id, value, type = 'text', checked = false) { const element = { id, value, type, checked, classList: { toggle() {}, add() {}, remove() {} }, dataset: {}, style: { setProperty() {}, removeProperty() {} } }; elements.set(id, element); return element; }\n" +
            "input('cfg-trigger-source', 'None');\n" +
            "input('cfg-storage-path', 'C:/ClearFrost');\n" +
            "input('cfg-serial-baud', '', 'number');\n" +
            "input('cfg-serial-debounce', '0', 'number');\n" +
            "input('cfg-plc-port', 'abc', 'number');\n" +
            "input('cfg-plc-trigger-delay', '25', 'number');\n" +
            "input('cfg-cam-exposure', '', 'number');\n" +
            "input('cfg-cam-gain', '0', 'number');\n" +
            "input('cfg-yolo-gpu', '', 'checkbox', false);\n" +
            "input('task-type-select', 'bad', 'select-one');\n" +
            "input('conf-input', '0.61', 'number');\n" +
            "input('iou-input', '0.42', 'number');\n" +
            "let payload = null;\n" +
            "global.document = { getElementById(id) { return elements.get(id) || null; }, querySelectorAll() { return []; }, createElement() { return { dataset: {}, classList: { add() {}, remove() {}, toggle() {} }, add() {}, options: [] }; } };\n" +
            "global.window = {\n" +
            "  CF_BRIDGE: { registerMessageHandler() {}, sendCommand(cmd, value) { if (cmd === 'save_settings') payload = value; return 'request-id'; } },\n" +
            "  CF_STORE: { state: { settings: {}, inspectionRuleSet: { Rules: [{ Type: 'Count', Enabled: true, Label: 'part', Count: 1 }] } }, notify() {}, applyBootstrapSnapshot() {} },\n" +
            "  CF_UTILS: { escapeHtml(value) { return String(value ?? ''); } },\n" +
            "  addEventListener() {}, addLog() {}, showToast() {}, updateOperatorStatus() {}, requestAnimationFrame(callback) { if (callback) callback(); },\n" +
            "  setTimeout() { return 0; }, clearTimeout() {}, confirm() { return true; }\n" +
            "};\n" +
            "global.alert = (message) => { throw new Error('unexpected alert: ' + message); };\n" +
            "require(settingsPath);\n" +
            "window.saveSettings();\n" +
            "if (!payload) throw new Error('save_settings payload missing');\n" +
            "if ('SerialPhotoelectricBaudRate' in payload) throw new Error('blank numeric field was submitted');\n" +
            "if ('PlcPort' in payload) throw new Error('invalid numeric field was submitted');\n" +
            "if ('ExposureTime' in payload) throw new Error('blank exposure was submitted');\n" +
            "if (payload.SerialPhotoelectricDebounceMs !== 0) throw new Error('zero debounce was not preserved: ' + payload.SerialPhotoelectricDebounceMs);\n" +
            "if (payload.GainRaw !== 0) throw new Error('zero gain was not preserved: ' + payload.GainRaw);\n" +
            "if (payload.PlcTriggerDelayMs !== 25) throw new Error('valid numeric field missing: ' + payload.PlcTriggerDelayMs);\n" +
            "if (payload.EnableGpu !== false) throw new Error('checkbox false not preserved');\n" +
            "if ('TaskType' in payload) throw new Error('invalid task type was submitted: ' + payload.TaskType);\n" +
            "if (payload.Confidence !== 0.61 || payload.IouThreshold !== 0.42) throw new Error('thresholds missing: ' + JSON.stringify(payload));\n";

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
    public void UpdateTaskType_RejectsUnsupportedValuesAndRollsBackBridgeFailure()
    {
        string root = FindRepositoryRoot();
        string settingsPath = Path.Combine(root, "ClearFrost", "html", "js", "settings.js");
        string script =
            "const settingsPath = " + JsonSerializer.Serialize(settingsPath) + ";\n" +
            "const listeners = new Map();\n" +
            "const commands = [];\n" +
            "const toasts = [];\n" +
            "const select = { id: 'task-type-select', value: '1', type: 'select-one', classList: { toggle() {}, add() {}, remove() {} }, dataset: {}, style: { setProperty() {}, removeProperty() {} } };\n" +
            "global.document = { getElementById(id) { return id === 'task-type-select' ? select : null; }, querySelectorAll() { return []; }, createElement() { return { dataset: {}, classList: { add() {}, remove() {}, toggle() {} }, add() {}, options: [] }; } };\n" +
            "global.window = {\n" +
            "  CF_BRIDGE: { registerMessageHandler() {}, sendCommand(cmd, value) { commands.push({ cmd, value }); return 'task-request'; } },\n" +
            "  CF_STORE: { state: { settings: { TaskType: 1 } }, notify() {}, applyBootstrapSnapshot() {} },\n" +
            "  CF_UTILS: { escapeHtml(value) { return String(value ?? ''); } },\n" +
            "  addEventListener(type, handler) { listeners.set(type, handler); }, addLog() {}, showToast(message) { toasts.push(message); }, updateOperatorStatus() {}, requestAnimationFrame(callback) { if (callback) callback(); },\n" +
            "  setTimeout() { return 0; }, clearTimeout() {}, confirm() { return true; }\n" +
            "};\n" +
            "require(settingsPath);\n" +
            "select.value = 'bogus';\n" +
            "window.updateTaskType('bogus');\n" +
            "if (commands.length !== 0) throw new Error('unsupported task type was sent');\n" +
            "if (select.value !== '1') throw new Error('unsupported task type did not restore previous selection: ' + select.value);\n" +
            "if (!toasts.some((message) => String(message).includes('不支持的检测任务类型'))) throw new Error('unsupported task type toast missing');\n" +
            "select.value = '2';\n" +
            "window.updateTaskType('2');\n" +
            "if (commands.length !== 1 || commands[0].cmd !== 'set_task_type' || commands[0].value !== 2) throw new Error('valid task type command missing: ' + JSON.stringify(commands));\n" +
            "if (window.CF_STORE.state.settings.TaskType !== 2) throw new Error('optimistic task type state missing');\n" +
            "listeners.get('cf-command-error')?.({ detail: { cmd: 'set_task_type', requestId: 'task-request', message: 'failed' } });\n" +
            "if (select.value !== '1') throw new Error('failed task type command did not restore selection: ' + select.value);\n" +
            "if (window.CF_STORE.state.settings.TaskType !== 1) throw new Error('failed task type command did not restore state: ' + window.CF_STORE.state.settings.TaskType);\n";

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
