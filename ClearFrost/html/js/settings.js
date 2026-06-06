// ==========================================
// ClearFrost settings workspace
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const store = window.CF_STORE;

    const PLC_PROTOCOL_UI_HINTS = {
        Mitsubishi_MC_ASCII: {
            help: "三菱 MC ASCII：Hsl/Hao 支持 D/M/X/Y 等字地址；McpX 当前业务适配只支持 D 区。",
            placeholder: "例如 D100",
        },
        Mitsubishi_MC_Binary: {
            help: "三菱 MC Binary：Hsl/Hao 支持 D/M/X/Y 等字地址；McpX 当前业务适配只支持 D 区。",
            placeholder: "例如 D100",
        },
        Siemens_S7: {
            help: "西门子 S7：当前信号读写使用字/字节地址，支持 DB1.0、DB1.DBW0、M0、I0、Q0；位地址请勿用于这里。",
            placeholder: "例如 DB1.0 或 DB1.DBW0",
        },
        Modbus_TCP: {
            help: "Modbus TCP：使用 0-based 寄存器地址，例如 40001 或 0。",
            placeholder: "例如 40001",
        },
        Omron_Fins: {
            help: "欧姆龙 FINS：常用 D100 / CIO100(C100) / W100 / H100 / A100。",
            placeholder: "例如 D100",
        },
    };

    let PROJECT_PRESETS = {
        N5_remote: {
            name: "N5遥控器漏装视觉检测",
            PlcIp: "10.182.82.19",
            PlcPort: 2700,
            PlcTriggerAddress: "D100",
            PlcResultAddress: "D102",
            CameraSerialNumber: "5G087BAGAK00018",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "remote",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            PlcWriteRetryCount: 1,
            PlcWriteRetryIntervalMs: 200,
            RequireOperatorForProductionStart: true,
            OperatorSessionMaxHours: 12,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        N5_screw: {
            name: "N5螺钉视觉检测",
            PlcIp: "10.182.82.19",
            PlcPort: 3000,
            PlcTriggerAddress: "D90",
            PlcResultAddress: "D92",
            CameraSerialNumber: "EF59601AAK00030",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "screw",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            PlcWriteRetryCount: 1,
            PlcWriteRetryIntervalMs: 200,
            RequireOperatorForProductionStart: true,
            OperatorSessionMaxHours: 12,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        N6_remote: {
            name: "N6遥控器漏装视觉检测",
            PlcIp: "192.168.100.122",
            PlcPort: 5777,
            PlcTriggerAddress: "D6607",
            PlcResultAddress: "D6608",
            CameraSerialNumber: "AM01040AAK00040",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "remote",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            PlcWriteRetryCount: 1,
            PlcWriteRetryIntervalMs: 200,
            RequireOperatorForProductionStart: true,
            OperatorSessionMaxHours: 12,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        N6_screw: {
            name: "N6螺钉视觉检测",
            PlcIp: "10.182.82.3",
            PlcPort: 4300,
            PlcTriggerAddress: "D100",
            PlcResultAddress: "D102",
            CameraSerialNumber: "",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "screw",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            PlcWriteRetryCount: 1,
            PlcWriteRetryIntervalMs: 200,
            RequireOperatorForProductionStart: true,
            OperatorSessionMaxHours: 12,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        W5_screw: {
            name: "W5螺钉视觉检测",
            PlcIp: "192.168.22.44",
            PlcPort: 4999,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "EF59632AAK00291",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TargetLabel: "screw",
            TargetCount: 4,
            ExposureTime: 50000,
            Gain: 1.1,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            PlcWriteRetryCount: 1,
            PlcWriteRetryIntervalMs: 200,
            RequireOperatorForProductionStart: true,
            OperatorSessionMaxHours: 12,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        W6_screw: {
            name: "W6螺钉视觉检测",
            PlcIp: "192.168.250.1",
            PlcPort: 5999,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "EF59632AAK00291",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TargetLabel: "screw",
            TargetCount: 4,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            PlcWriteRetryCount: 1,
            PlcWriteRetryIntervalMs: 200,
            RequireOperatorForProductionStart: true,
            OperatorSessionMaxHours: 12,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
    };

    let pendingProjectPresetId = "";

    function byId(id) {
        return document.getElementById(id);
    }

    function normalizeThresholdValue(value, fallback = 0) {
        const num = parseFloat(value);
        if (Number.isNaN(num)) return fallback;
        return Math.max(0, Math.min(1, num));
    }

    function setThresholdControl(inputId, legacySliderId, value, fallback = 0) {
        const normalized = normalizeThresholdValue(value, fallback);
        const input = byId(inputId);
        if (input) input.value = normalized.toFixed(2);

        const legacySlider = byId(legacySliderId);
        if (legacySlider) legacySlider.value = Math.round(normalized * 100);
        return normalized;
    }

    function readThresholdControl(inputId, legacySliderId, fallback) {
        const input = byId(inputId);
        if (input) return normalizeThresholdValue(input.value, fallback);

        const legacySlider = byId(legacySliderId);
        if (legacySlider) return normalizeThresholdValue(parseFloat(legacySlider.value) / 100, fallback);
        return fallback;
    }

    function escapeHtml(value) {
        return window.CF_UTILS?.escapeHtml
            ? window.CF_UTILS.escapeHtml(value)
            : String(value ?? "");
    }

    function makeRuleId() {
        return `rule-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    }

    function createInspectionRule(type = "Count") {
        if (type === "OrderedLabels") {
            return {
                Id: makeRuleId(),
                Name: "顺序规则",
                Enabled: true,
                Type: "OrderedLabels",
                ExpectedLabels: [],
                SortBy: "CenterX",
                Direction: "LeftToRight",
                ExpectedCount: 0,
                MinConfidence: 0,
                AllowMissing: false,
                AllowDuplicate: false,
            };
        }

        if (type === "RelativePosition") {
            return {
                Id: makeRuleId(),
                Name: "位置规则",
                Enabled: true,
                Type: "RelativePosition",
                SubjectLabel: "",
                ReferenceLabel: "",
                Relation: "LeftOf",
                MinDistance: 0,
                MaxDistance: 0,
                MinConfidence: 0,
            };
        }

        return {
            Id: makeRuleId(),
            Name: "数量规则",
            Enabled: true,
            Type: "Count",
            Label: "",
            Operator: "Equal",
            Count: 1,
            MinConfidence: 0,
        };
    }

    function normalizeRuleLabels(value) {
        if (Array.isArray(value)) {
            return value.map((label) => String(label || "").trim()).filter(Boolean);
        }

        return String(value || "")
            .split(",")
            .map((label) => label.trim())
            .filter(Boolean);
    }

    function normalizeInspectionRule(rule) {
        const type = rule?.Type || rule?.type || "Count";
        const base = createInspectionRule(type);
        const normalized = {
            ...base,
            ...rule,
            Id: rule?.Id || rule?.id || base.Id,
            Name: rule?.Name ?? rule?.name ?? base.Name,
            Enabled: rule?.Enabled ?? rule?.enabled ?? true,
            Type: type,
            MinConfidence: normalizeThresholdValue(rule?.MinConfidence ?? rule?.minConfidence ?? base.MinConfidence, 0),
        };

        if (type === "OrderedLabels") {
            normalized.ExpectedLabels = normalizeRuleLabels(rule?.ExpectedLabels ?? rule?.expectedLabels);
            normalized.ExpectedCount = Math.max(0, parseInt(rule?.ExpectedCount ?? rule?.expectedCount ?? 0, 10) || 0);
            normalized.AllowMissing = !!(rule?.AllowMissing ?? rule?.allowMissing);
            normalized.AllowDuplicate = !!(rule?.AllowDuplicate ?? rule?.allowDuplicate);
            normalized.SortBy = rule?.SortBy || rule?.sortBy || "CenterX";
            normalized.Direction = rule?.Direction || rule?.direction || "LeftToRight";
            return normalized;
        }

        if (type === "RelativePosition") {
            normalized.SubjectLabel = String(rule?.SubjectLabel ?? rule?.subjectLabel ?? "").trim();
            normalized.ReferenceLabel = String(rule?.ReferenceLabel ?? rule?.referenceLabel ?? "").trim();
            normalized.Relation = rule?.Relation || rule?.relation || "LeftOf";
            normalized.MinDistance = Math.max(0, parseFloat(rule?.MinDistance ?? rule?.minDistance ?? 0) || 0);
            normalized.MaxDistance = Math.max(0, parseFloat(rule?.MaxDistance ?? rule?.maxDistance ?? 0) || 0);
            return normalized;
        }

        normalized.Label = String(rule?.Label ?? rule?.label ?? "").trim();
        normalized.Operator = rule?.Operator || rule?.operator || "Equal";
        normalized.Count = Math.max(0, parseInt(rule?.Count ?? rule?.count ?? 0, 10) || 0);
        return normalized;
    }

    function makeLegacyRuleSet(data) {
        if (data?.WireSequenceJudgeEnabled) {
            return {
                Version: 1,
                Mode: "All",
                FallbackTargetLabel: data.TargetLabel || "",
                FallbackTargetCount: Number.isFinite(Number(data.TargetCount)) ? Math.max(0, Number(data.TargetCount)) : 0,
                Rules: [{
                    ...createInspectionRule("OrderedLabels"),
                    Name: "端子线序",
                    ExpectedLabels: normalizeRuleLabels(data.WireSequenceExpectedLabels || "Wire_Brown,Wire_Black,Wire_Blue"),
                    SortBy: data.WireSequenceSortBy || "CenterX",
                    Direction: data.WireSequenceDirection || "LeftToRight",
                    ExpectedCount: data.WireSequenceExpectedCount || 0,
                    MinConfidence: data.WireSequenceMinConfidence || 0,
                    AllowMissing: !!data.WireSequenceAllowMissing,
                    AllowDuplicate: !!data.WireSequenceAllowDuplicate,
                }],
            };
        }

        return {
            Version: 1,
            Mode: "All",
            FallbackTargetLabel: data?.TargetLabel || "",
            FallbackTargetCount: Number.isFinite(Number(data?.TargetCount)) ? Math.max(0, Number(data.TargetCount)) : 0,
            Rules: [{
                ...createInspectionRule("Count"),
                Name: `${data?.TargetLabel || "目标"} 数量`,
                Label: data?.TargetLabel || "screw",
                Count: Number.isFinite(Number(data?.TargetCount)) ? Number(data.TargetCount) : 4,
            }],
        };
    }

    function normalizeInspectionRuleSet(raw, legacyData = {}) {
        let parsed = raw;
        if (typeof raw === "string" && raw.trim()) {
            try {
                parsed = JSON.parse(raw);
            } catch {
                parsed = null;
            }
        }

        if (!parsed || !Array.isArray(parsed.Rules || parsed.rules)) {
            parsed = makeLegacyRuleSet(legacyData);
        }

        const rules = (parsed.Rules || parsed.rules || []).map(normalizeInspectionRule);
        const fallbackLabel = parsed.FallbackTargetLabel ?? parsed.fallbackTargetLabel ?? "";
        const fallbackCount = Number(parsed.FallbackTargetCount ?? parsed.fallbackTargetCount ?? 0);
        return {
            Version: 1,
            Mode: "All",
            FallbackTargetLabel: String(fallbackLabel || "").trim(),
            FallbackTargetCount: Number.isFinite(fallbackCount) ? Math.max(0, Math.floor(fallbackCount)) : 0,
            Rules: rules.length ? rules : makeLegacyRuleSet(legacyData).Rules,
        };
    }

    function getCurrentRuleSet() {
        return store.state.inspectionRuleSet || normalizeInspectionRuleSet(store.state.settings?.InspectionRuleSetJson, store.state.settings || {});
    }

    function syncInspectionRuleJson() {
        const ruleSet = getCurrentRuleSet();
        const hidden = byId("cfg-inspection-rule-set-json");
        if (hidden) hidden.value = JSON.stringify(ruleSet);
        store.state.settings = { ...(store.state.settings || {}), InspectionRuleSetJson: JSON.stringify(ruleSet) };
        return ruleSet;
    }

    function updateRuleLabelOptions() {
        const datalist = byId("inspection-rule-label-options");
        if (!datalist) return;
        const labels = Array.isArray(store.state.modelLabels) ? store.state.modelLabels : [];
        datalist.innerHTML = labels
            .map((label) => `<option value="${escapeHtml(label)}"></option>`)
            .join("");
    }

    function ruleTypeLabel(type) {
        if (type === "OrderedLabels") return "顺序";
        if (type === "RelativePosition") return "位置";
        return "数量";
    }

    function ruleInputAttrs(index, field, extra = "") {
        return `data-input-action="updateInspectionRule" data-change-action="updateInspectionRule" data-pass-element="true" data-rule-index="${index}" data-rule-field="${field}" ${extra}`;
    }

    function renderCountRuleFields(rule, index) {
        return `
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">标签</label>
                <input value="${escapeHtml(rule.Label || "")}" list="inspection-rule-label-options"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-bold" placeholder="留空表示全部目标"
                    ${ruleInputAttrs(index, "Label")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">比较</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "Operator")}>
                    ${operatorOptions(rule.Operator)}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">数量</label>
                <input type="number" min="0" step="1" value="${escapeHtml(rule.Count ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "Count")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最低置信度</label>
                <input type="number" min="0" max="1" step="0.01" value="${escapeHtml(rule.MinConfidence ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinConfidence")}>
            </div>
        `;
    }

    function renderOrderedRuleFields(rule, index) {
        return `
            <div class="cf-plc-span-3">
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">期望标签顺序</label>
                <input value="${escapeHtml((rule.ExpectedLabels || []).join(","))}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    placeholder="Wire_Brown,Wire_Black,Wire_Blue"
                    ${ruleInputAttrs(index, "ExpectedLabels")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">排序字段</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "SortBy")}>
                    ${optionList(rule.SortBy, [["CenterX", "中心 X"], ["CenterY", "中心 Y"], ["TopY", "顶部 Y"], ["Confidence", "置信度"], ["Area", "面积"]])}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">排序方向</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "Direction")}>
                    ${optionList(rule.Direction, [["LeftToRight", "从左到右"], ["RightToLeft", "从右到左"], ["TopToBottom", "从上到下"], ["BottomToTop", "从下到上"], ["Ascending", "升序"], ["Descending", "降序"]])}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">期望数量</label>
                <input type="number" min="0" max="256" step="1" value="${escapeHtml(rule.ExpectedCount ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "ExpectedCount")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最低置信度</label>
                <input type="number" min="0" max="1" step="0.01" value="${escapeHtml(rule.MinConfidence ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinConfidence")}>
            </div>
            <label class="cf-plc-toggle">
                <input type="checkbox" class="accent-celadon-600 w-3.5 h-3.5 rounded" ${rule.AllowMissing ? "checked" : ""}
                    ${ruleInputAttrs(index, "AllowMissing")}>
                <span>允许缺失</span>
            </label>
            <label class="cf-plc-toggle">
                <input type="checkbox" class="accent-celadon-600 w-3.5 h-3.5 rounded" ${rule.AllowDuplicate ? "checked" : ""}
                    ${ruleInputAttrs(index, "AllowDuplicate")}>
                <span>允许重复</span>
            </label>
        `;
    }

    function renderPositionRuleFields(rule, index) {
        return `
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">主标签</label>
                <input value="${escapeHtml(rule.SubjectLabel || "")}" list="inspection-rule-label-options"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-bold"
                    ${ruleInputAttrs(index, "SubjectLabel")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">关系</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "Relation")}>
                    ${optionList(rule.Relation, [["LeftOf", "在左侧"], ["RightOf", "在右侧"], ["Above", "在上方"], ["Below", "在下方"]])}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">参考标签</label>
                <input value="${escapeHtml(rule.ReferenceLabel || "")}" list="inspection-rule-label-options"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-bold"
                    ${ruleInputAttrs(index, "ReferenceLabel")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最小间距(px)</label>
                <input type="number" min="0" step="1" value="${escapeHtml(rule.MinDistance ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinDistance")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最大间距(px)</label>
                <input type="number" min="0" step="1" value="${escapeHtml(rule.MaxDistance ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MaxDistance")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最低置信度</label>
                <input type="number" min="0" max="1" step="0.01" value="${escapeHtml(rule.MinConfidence ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinConfidence")}>
            </div>
        `;
    }

    function optionList(current, pairs) {
        return pairs.map(([value, label]) =>
            `<option value="${value}" ${String(current) === value ? "selected" : ""}>${label}</option>`
        ).join("");
    }

    function operatorOptions(current) {
        return optionList(current, [
            ["Equal", "等于"],
            ["NotEqual", "不等于"],
            ["GreaterThan", "大于"],
            ["GreaterThanOrEqual", "大于等于"],
            ["LessThan", "小于"],
            ["LessThanOrEqual", "小于等于"],
        ]);
    }

    function renderInspectionRules() {
        updateRuleLabelOptions();
        const container = byId("inspection-rule-list");
        if (!container) return;
        const ruleSet = getCurrentRuleSet();
        const rules = ruleSet.Rules || [];
        if (!rules.length) {
            container.innerHTML = '<p class="text-[10px] text-ink-400">尚未配置规则。</p>';
            syncInspectionRuleJson();
            return;
        }

        container.innerHTML = rules.map((rule, index) => {
            const fields = rule.Type === "OrderedLabels"
                ? renderOrderedRuleFields(rule, index)
                : rule.Type === "RelativePosition"
                    ? renderPositionRuleFields(rule, index)
                    : renderCountRuleFields(rule, index);
            return `
                <article class="bg-white/80 border border-celadon-100 rounded-lg p-4 shadow-sm">
                    <div class="flex flex-wrap items-center justify-between gap-2 mb-3">
                        <div class="flex items-center gap-2">
                            <input type="checkbox" class="accent-celadon-600 w-3.5 h-3.5 rounded" ${rule.Enabled ? "checked" : ""}
                                ${ruleInputAttrs(index, "Enabled")}>
                            <span class="px-2 py-1 rounded bg-celadon-50 text-celadon-700 text-[10px] font-bold">${ruleTypeLabel(rule.Type)}</span>
                            <input value="${escapeHtml(rule.Name || "")}" class="tech-input px-2 py-1 rounded-lg text-xs font-bold"
                                ${ruleInputAttrs(index, "Name")} placeholder="规则名称">
                        </div>
                        <div class="flex gap-1">
                            <button type="button" data-action="moveInspectionRule" data-value='{"index":${index},"direction":-1}'
                                class="px-2 py-1 bg-porcelain-100 text-ink-500 hover:bg-celadon-50 text-[10px] font-bold rounded">上移</button>
                            <button type="button" data-action="moveInspectionRule" data-value='{"index":${index},"direction":1}'
                                class="px-2 py-1 bg-porcelain-100 text-ink-500 hover:bg-celadon-50 text-[10px] font-bold rounded">下移</button>
                            <button type="button" data-action="duplicateInspectionRule" data-value="${index}"
                                class="px-2 py-1 bg-blue-50 text-blue-600 hover:bg-blue-100 text-[10px] font-bold rounded">复制</button>
                            <button type="button" data-action="removeInspectionRule" data-value="${index}"
                                class="px-2 py-1 bg-red-50 text-red-600 hover:bg-red-100 text-[10px] font-bold rounded">删除</button>
                        </div>
                    </div>
                    <div class="cf-plc-grid cf-plc-grid-3">
                        ${fields}
                    </div>
                </article>
            `;
        }).join("");
        syncInspectionRuleJson();
    }

    function updateInspectionRule(element) {
        const index = parseInt(element?.dataset?.ruleIndex, 10);
        const field = element?.dataset?.ruleField;
        const rules = getCurrentRuleSet().Rules || [];
        if (!Number.isInteger(index) || !field || !rules[index]) return;

        let value = element.type === "checkbox" ? element.checked : element.value;
        if (["Count", "ExpectedCount"].includes(field)) value = Math.max(0, parseInt(value, 10) || 0);
        if (["MinConfidence"].includes(field)) value = normalizeThresholdValue(value, 0);
        if (["MinDistance", "MaxDistance"].includes(field)) value = Math.max(0, parseFloat(value) || 0);
        if (field === "ExpectedLabels") value = normalizeRuleLabels(value);
        rules[index][field] = value;
        syncInspectionRuleJson();
    }

    function addInspectionRule(type) {
        const ruleSet = getCurrentRuleSet();
        ruleSet.Rules.push(createInspectionRule(type || "Count"));
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function removeInspectionRule(index) {
        const ruleSet = getCurrentRuleSet();
        ruleSet.Rules.splice(parseInt(index, 10), 1);
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function duplicateInspectionRule(index) {
        const ruleSet = getCurrentRuleSet();
        const source = ruleSet.Rules[parseInt(index, 10)];
        if (!source) return;
        ruleSet.Rules.splice(parseInt(index, 10) + 1, 0, { ...JSON.parse(JSON.stringify(source)), Id: makeRuleId(), Name: `${source.Name || "规则"} 副本` });
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function moveInspectionRule(payload) {
        const ruleSet = getCurrentRuleSet();
        const index = parseInt(payload?.index, 10);
        const direction = parseInt(payload?.direction, 10);
        const nextIndex = index + direction;
        if (!ruleSet.Rules[index] || nextIndex < 0 || nextIndex >= ruleSet.Rules.length) return;
        const [item] = ruleSet.Rules.splice(index, 1);
        ruleSet.Rules.splice(nextIndex, 0, item);
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function validateInspectionRuleSettings() {
        const rules = getCurrentRuleSet().Rules || [];
        if (!rules.length) return "至少需要配置一条判定规则";
        const enabledRules = rules.filter((r) => r.Enabled !== false);
        if (!enabledRules.length) return "至少需要启用一条判定规则";
        for (const rule of enabledRules) {
            if (rule.Type === "OrderedLabels" && !normalizeRuleLabels(rule.ExpectedLabels).length) {
                return `规则“${rule.Name || "顺序规则"}”必须配置期望标签顺序`;
            }
            if (rule.Type === "RelativePosition" && (!rule.SubjectLabel || !rule.ReferenceLabel)) {
                return `规则“${rule.Name || "位置规则"}”必须配置主标签和参考标签`;
            }
        }
        return null;
    }

    function getCompactPlcAddress(value) {
        return String(value || "").trim().replace(/\s+/g, "").toUpperCase();
    }

    function updateSiemensRackSlotVisibility() {
        const protocol = byId("cfg-plc-protocol")?.value || "";
        const cpuModel = (byId("cfg-plc-siemens-cpu-model")?.value || "").toUpperCase();
        const group = byId("cfg-plc-siemens-rack-slot");
        const showRackSlot = protocol === "Siemens_S7" && (cpuModel === "S300" || cpuModel === "S400");
        if (group) group.classList.toggle("hidden", !showRackSlot);
    }

    function updateTriggerSourceUi() {
        const triggerSource = byId("cfg-trigger-source")?.value || "PLC";
        const serialSection = byId("cfg-serial-trigger-section");
        if (serialSection) serialSection.classList.toggle("hidden", triggerSource !== "SerialPhotoelectric");
    }

    function normalizeSerialPortName(value) {
        const raw = String(value || "").trim();
        if (!raw) return "";
        const match = raw.match(/\bCOM\d+\b/i);
        return match ? match[0].toUpperCase() : raw;
    }

    function ensureSerialPortOption(value, displayName) {
        const select = byId("cfg-serial-port");
        const portName = normalizeSerialPortName(value);
        if (!select || !portName) return;

        const existing = Array.from(select.options).find((opt) =>
            normalizeSerialPortName(opt.value) === portName
        );
        if (existing) {
            existing.value = portName;
            if (displayName) existing.text = displayName;
            return;
        }

        const option = document.createElement("option");
        option.value = portName;
        option.text = displayName || portName;
        select.add(option);
    }

    function handleSerialPortsDetected(data) {
        setSerialAutoDetectBusy(false);
        const select = byId("cfg-serial-port");
        if (!select) return;
        const rawPorts = data?.ports || data?.Ports || data || [];
        const ports = Array.isArray(rawPorts) ? rawPorts : [];
        const currentValue = normalizeSerialPortName(select.value);
        select.innerHTML = '<option value="">-- 请选择 COM 口 --</option>';
        let preferredValue = "";
        ports.forEach((port) => {
            const rawName = typeof port === "string" ? port : (port.name || port.Name || "");
            const displayName = typeof port === "string"
                ? port
                : (port.displayName || port.DisplayName || rawName);
            ensureSerialPortOption(rawName || displayName, displayName);
            const portName = normalizeSerialPortName(rawName || displayName);
            const isPreferred = typeof port === "object" && (port.isPreferred || port.IsPreferred);
            if (!preferredValue && (isPreferred || ports.length === 1)) {
                preferredValue = portName;
            }
        });
        if (currentValue) {
            ensureSerialPortOption(currentValue);
            select.value = currentValue;
        } else if (preferredValue) {
            select.value = preferredValue;
        }
        const selectedText = select.value ? `，已选择 ${select.value}` : "";
        window.showToast?.(`识别到 ${ports.length} 个串口${selectedText}`, ports.length ? "success" : "warning", 1400);
        window.addLog?.(`串口自动识别完成: ${ports.length} 个${selectedText}`, ports.length ? "success" : "warning");
    }

    let serialAutoDetectResetTimer = null;

    function setSerialAutoDetectBusy(isBusy) {
        const button = document.querySelector('[data-cmd="serial_auto_detect_ports"]');
        if (!button) return;

        if (serialAutoDetectResetTimer) {
            clearTimeout(serialAutoDetectResetTimer);
            serialAutoDetectResetTimer = null;
        }

        if (isBusy) {
            button.dataset.originalText = button.dataset.originalText || button.textContent.trim() || "自动识别";
            button.disabled = true;
            button.textContent = "识别中";
            serialAutoDetectResetTimer = window.setTimeout(() => setSerialAutoDetectBusy(false), 8000);
            return;
        }

        button.disabled = false;
        button.textContent = button.dataset.originalText || "自动识别";
    }

    function handleCommandDispatched(cmd) {
        if (cmd !== "serial_auto_detect_ports") return;
        setSerialAutoDetectBusy(true);
        window.showToast?.("正在识别串口...", "info", 1000);
        window.addLog?.("正在自动识别串口光电 COM 口...", "info");
    }

    function updatePlcProtocolModeUi() {
        const mode = byId("cfg-plc-protocol-mode")?.value || "Legacy";
        const handshakeOptions = byId("cfg-plc-handshake-options");
        if (handshakeOptions) handshakeOptions.classList.toggle("hidden", mode !== "HandshakeV1");
    }

    function syncDriverProviderOptions() {
        const protocolSelect = byId("cfg-plc-protocol");
        const driverSelect = byId("cfg-plc-driver-provider");
        if (!protocolSelect || !driverSelect) return;

        const isMitsubishi = (protocolSelect.value || "").startsWith("Mitsubishi");
        const mcpxOption = driverSelect.querySelector('option[value="McpX"]');
        if (mcpxOption) mcpxOption.disabled = !isMitsubishi;
        if (!isMitsubishi && driverSelect.value === "McpX") driverSelect.value = "HaoCommunication";
        updateSiemensRackSlotVisibility();
    }

    function updatePlcAddressUi() {
        const protocolSelect = byId("cfg-plc-protocol");
        const triggerInput = byId("cfg-plc-trigger");
        const resultInput = byId("cfg-plc-result");
        const helpEl = byId("cfg-plc-address-help");
        const siemensOptions = byId("cfg-plc-siemens-options");
        const protocol = protocolSelect?.value || "Mitsubishi_MC_ASCII";
        const hints = PLC_PROTOCOL_UI_HINTS[protocol] || PLC_PROTOCOL_UI_HINTS.Mitsubishi_MC_ASCII;

        if (triggerInput) triggerInput.placeholder = hints.placeholder;
        if (resultInput) resultInput.placeholder = hints.placeholder;
        if (helpEl) helpEl.textContent = hints.help;
        if (siemensOptions) siemensOptions.classList.toggle("hidden", protocol !== "Siemens_S7");

        syncDriverProviderOptions();
        updatePlcProtocolModeUi();
    }

    function validatePlcAddress(address, protocol, driver = "") {
        const compact = getCompactPlcAddress(address);
        if (!compact) return "地址不能为空";
        if (protocol.startsWith("Mitsubishi")) {
            if (driver === "McpX") {
                return /^(?:D)?\d+$/.test(compact) ? null : "McpX 当前业务适配仅支持三菱 D 区地址，例如 D100";
            }
            if (/^(?:D|M|S|T|C|R)\d+$/.test(compact)) return null;
            if (/^(?:X|Y)[0-9A-F]+$/.test(compact)) return null;
            if (/^(?:D|M|S|T|C|R|X|Y)[0-9A-F]+\.\d+$/.test(compact)) {
                return "当前信号读写使用字地址，不支持位地址";
            }
            return "三菱地址需为 D100、M100、X10 或 Y10 格式";
        }
        if (protocol === "Siemens_S7") {
            if (/^(M|I|Q|AI|AQ)\d+$/.test(compact)) return null;
            if (/^(?:[MIQ]\d+\.\d+|DB\d+\.(?:\d+|DBX\d+)\.\d+)$/.test(compact)) {
                return "当前信号读写使用字/字节地址，不支持 M0.0 或 DB1.0.0 这类位地址";
            }
            let match = compact.match(/^DB(\d+)\.(\d+)$/);
            if (match && Number(match[1]) >= 1 && Number(match[2]) >= 0) return null;
            match = compact.match(/^DB(\d+)\.DB[BWD](\d+)$/);
            if (match && Number(match[1]) >= 1 && Number(match[2]) >= 0) return null;
            return "西门子地址需为 DB1.0、DB1.DBW0、M0、I0 或 Q0 格式";
        }
        if (protocol === "Modbus_TCP") {
            return /^\d+$/.test(compact) ? null : "Modbus 地址需为数字";
        }
        if (protocol === "Omron_Fins") {
            if (/^(?:D|CIO|C|W|H|A)\d+$/.test(compact)) return null;
            if (/^(?:D|CIO|C|W|H|A)\d+\.\d+$/.test(compact)) {
                return "当前信号读写使用字地址，不支持位地址";
            }
            return "欧姆龙地址需为 D100、CIO100、W100、H100 或 A100 格式";
        }
        return "地址格式不符合当前 PLC 协议";
    }

    function validatePlcSettings() {
        const triggerSource = byId("cfg-trigger-source")?.value || "PLC";
        if (triggerSource !== "PLC") return null;

        const protocol = byId("cfg-plc-protocol")?.value || "";
        const driver = byId("cfg-plc-driver-provider")?.value || "";
        const mode = byId("cfg-plc-protocol-mode")?.value || "Legacy";
        const triggerAddress = byId("cfg-plc-trigger")?.value || "";
        const resultAddress = byId("cfg-plc-result")?.value || "";

        if (driver === "McpX" && !protocol.startsWith("Mitsubishi")) {
            return "仅三菱协议支持 McpX 驱动库";
        }

        const triggerError = validatePlcAddress(triggerAddress, protocol, driver);
        if (triggerError) return `触发地址无效: ${triggerError}`;
        const resultError = validatePlcAddress(resultAddress, protocol, driver);
        if (resultError) return `结果地址无效: ${resultError}`;

        if (mode === "HandshakeV1") {
            const handshakeFields = [
                ["TriggerSeq", "cfg-plc-trigger-seq"],
                ["ResultSeq", "cfg-plc-result-seq"],
                ["VisionOnline", "cfg-plc-vision-online"],
                ["VisionReady", "cfg-plc-vision-ready"],
                ["VisionBusy", "cfg-plc-vision-busy"],
                ["InspectionDone", "cfg-plc-inspection-done"],
                ["ErrorCode", "cfg-plc-error-code"],
                ["TraceSaved", "cfg-plc-trace-saved"],
                ["Heartbeat", "cfg-plc-heartbeat"],
                ["ResetFault", "cfg-plc-reset-fault"],
            ];
            for (const [label, inputId] of handshakeFields) {
                const error = validatePlcAddress(byId(inputId)?.value || "", protocol, driver);
                if (error) return `${label} 地址无效: ${error}`;
            }
        }

        if (byId("cfg-barcode-enabled")?.checked) {
            const barcodeError = validatePlcAddress(byId("cfg-barcode-address")?.value || "", protocol, driver);
            if (barcodeError) return `条码地址无效: ${barcodeError}`;
        }

        return null;
    }

    function validateTriggerSettings() {
        const triggerSource = byId("cfg-trigger-source")?.value || "PLC";
        if (triggerSource !== "SerialPhotoelectric") return null;

        const portName = normalizeSerialPortName(byId("cfg-serial-port")?.value || "");
        if (!portName) {
            return "选择串口光电触发时，必须先选择 COM 口";
        }

        return null;
    }

    function activateSettingsTab(tabName) {
        document.querySelectorAll(".cf-settings-tab").forEach((btn) => {
            btn.classList.toggle("active", btn.dataset.settingsTab === tabName);
        });
        const panels = document.querySelectorAll("[data-settings-panel]");
        panels.forEach((panel) => {
            panel.classList.toggle("hidden", panel.dataset.settingsPanel !== tabName);
        });
        if (panels.length) return;

        const sectionMapping = {
            vision: ["vision"],
            camera: ["camera"],
        };
        const targetSections = sectionMapping[tabName] || [tabName];

        document.querySelectorAll("[data-settings-section]").forEach((section) => {
            const sectionName = section.dataset.settingsSection;
            const isActive = targetSections.includes(sectionName);
            section.classList.toggle("hidden", !isActive);
            if (isActive) {
                section.style.removeProperty("display");
            } else {
                section.style.setProperty("display", "none", "important");
            }
        });

        const content = document.querySelector("#settings-modal .cf-settings-content");
        if (content) {
            content.dataset.activeSettings = tabName;
        }
    }

    function syncSettingsChrome() {
        if (!document.body.classList.contains("cf-stitch-page")) return;
        const title = document.querySelector("#settings-modal .cf-ornate-header h3");
        if (title) title.textContent = "系统参数配置";
    }

    function moveVisionControlsToSettings() {
        const controls = byId("yolo-controls");
        const target = byId("settings-vision-controls");
        if (document.body.classList.contains("cf-stitch-page")) return;
        if (!controls || !target || controls.parentElement === target) return;
        target.appendChild(controls);
    }

    function applyMultiModelUiState(enabled) {
        const checkbox = byId("enable-multi-model");
        const statusText = byId("multi-model-status");
        const configSection = byId("multi-model-config");
        if (checkbox) checkbox.checked = enabled;
        if (statusText) {
            statusText.innerText = enabled ? "已启用" : "自动切换";
            statusText.classList.toggle("text-celadon-600", enabled);
            statusText.classList.toggle("font-bold", enabled);
            statusText.classList.toggle("text-ink-500", !enabled);
        }
        if (configSection) {
            configSection.classList.toggle("opacity-50", !enabled);
            configSection.classList.toggle("pointer-events-none", !enabled);
        }
    }

    function toggleMultiModel(enabled) {
        applyMultiModelUiState(enabled);
        bridge.sendCommand("toggle_multi_model", enabled);
        window.addLog?.(enabled ? "多模型自动切换已启用" : "多模型自动切换已禁用", enabled ? "success" : "info");
    }

    function populateSettings(config) {
        const data = typeof config === "string" ? JSON.parse(config) : (config || {});
        store.state.settings = data;

        const mapping = {
            StoragePath: "cfg-storage-path",
            DataRetentionEnabled: "cfg-retention-enabled",
            RequireOperatorForProductionStart: "cfg-require-operator-production",
            OperatorSessionMaxHours: "cfg-operator-session-max-hours",
            ImageRetentionDays: "cfg-image-retention-days",
            LogRetentionDays: "cfg-log-retention-days",
            AuditLogRetentionDays: "cfg-audit-retention-days",
            ReportRetentionDays: "cfg-report-retention-days",
            TraceRecordRetentionDays: "cfg-trace-record-retention-days",
            TriggerSource: "cfg-trigger-source",
            SerialPhotoelectricPortName: "cfg-serial-port",
            SerialPhotoelectricBaudRate: "cfg-serial-baud",
            SerialPhotoelectricDebounceMs: "cfg-serial-debounce",
            SerialPhotoelectricTimeoutMs: "cfg-serial-timeout",
            PlcProtocol: "cfg-plc-protocol",
            PlcDriverProvider: "cfg-plc-driver-provider",
            PlcProtocolMode: "cfg-plc-protocol-mode",
            PlcIp: "cfg-plc-ip",
            PlcPort: "cfg-plc-port",
            PlcTriggerAddress: "cfg-plc-trigger",
            PlcResultAddress: "cfg-plc-result",
            PlcTriggerSeqAddress: "cfg-plc-trigger-seq",
            PlcResultSeqAddress: "cfg-plc-result-seq",
            PlcVisionOnlineAddress: "cfg-plc-vision-online",
            PlcVisionReadyAddress: "cfg-plc-vision-ready",
            PlcVisionBusyAddress: "cfg-plc-vision-busy",
            PlcInspectionDoneAddress: "cfg-plc-inspection-done",
            PlcErrorCodeAddress: "cfg-plc-error-code",
            PlcTraceSavedAddress: "cfg-plc-trace-saved",
            PlcHeartbeatAddress: "cfg-plc-heartbeat",
            PlcResetFaultAddress: "cfg-plc-reset-fault",
            PlcTriggerDelayMs: "cfg-plc-trigger-delay",
            PlcPollingIntervalMs: "cfg-plc-polling-interval",
            PlcOkValue: "cfg-plc-ok-value",
            PlcNgValue: "cfg-plc-ng-value",
            PlcSiemensCpuModel: "cfg-plc-siemens-cpu-model",
            PlcSiemensRack: "cfg-plc-siemens-rack",
            PlcSiemensSlot: "cfg-plc-siemens-slot",
            CameraName: "cfg-cam-name",
            CameraSerialNumber: "cfg-cam-serial",
            CameraManufacturer: "cfg-cam-manufacturer",
            CameraPixelFormat: "cfg-cam-pixel-format",
            ExposureTime: "cfg-cam-exposure",
            GainRaw: "cfg-cam-gain",
            MaxRetryCount: "cfg-logic-retry-count",
            RetryIntervalMs: "cfg-logic-retry-interval",
            PlcWriteRetryCount: "cfg-plc-write-retry-count",
            PlcWriteRetryIntervalMs: "cfg-plc-write-retry-interval",
            InspectionCycleSlaEnabled: "cfg-cycle-sla-enabled",
            InspectionCycleWarningMs: "cfg-cycle-warning-ms",
            InspectionCycleCriticalMs: "cfg-cycle-critical-ms",
            InspectionCycleMinSamples: "cfg-cycle-min-samples",
            QualityYieldSlaEnabled: "cfg-yield-sla-enabled",
            QualityYieldWarningPercent: "cfg-yield-warning-percent",
            QualityYieldCriticalPercent: "cfg-yield-critical-percent",
            QualityYieldMinSamples: "cfg-yield-min-samples",
            ConsecutiveNgAlarmEnabled: "cfg-consecutive-ng-enabled",
            ConsecutiveNgWarningCount: "cfg-consecutive-ng-warning",
            ConsecutiveNgCriticalCount: "cfg-consecutive-ng-critical",
            EnableGpu: "cfg-yolo-gpu",
            GpuIndex: "cfg-yolo-gpu-index",
            IndustrialRenderMode: "cfg-industrial-render-mode",
            BarcodeEnabled: "cfg-barcode-enabled",
            BarcodeAddress: "cfg-barcode-address",
            BarcodeWordLength: "cfg-barcode-word-length",
            BarcodeEncoding: "cfg-barcode-encoding",
            BarcodeRequired: "cfg-barcode-required",
        };

        for (const [propName, inputId] of Object.entries(mapping)) {
            if (data[propName] === undefined) continue;
            const input = byId(inputId);
            if (!input) continue;
            if (inputId === "cfg-serial-port" && data[propName]) {
                ensureSerialPortOption(data[propName]);
            }
            if (input.type === "checkbox") {
                input.checked = !!data[propName];
            } else {
                input.value = data[propName] ?? "";
            }
        }

        if (data.TaskType !== undefined && byId("task-type-select")) byId("task-type-select").value = String(data.TaskType);
        if (data.Confidence !== undefined) {
            setThresholdControl("conf-input", "conf-slider", data.Confidence);
        }
        if (data.IouThreshold !== undefined) {
            setThresholdControl("iou-input", "iou-slider", data.IouThreshold);
        }
        store.state.inspectionRuleSet = normalizeInspectionRuleSet(data.InspectionRuleSetJson, data);
        renderInspectionRules();
        const activeCamera = Array.isArray(data.Cameras)
            ? (data.Cameras.find((camera) => camera.Id === data.ActiveCameraId || camera.id === data.ActiveCameraId) ||
                data.Cameras.find((camera) => camera.IsEnabled || camera.isEnabled) ||
                data.Cameras[0])
            : null;
        const pixelFormat = data.CameraPixelFormat || activeCamera?.PixelFormat || activeCamera?.pixelFormat || "Auto";
        if (byId("cfg-cam-pixel-format")) byId("cfg-cam-pixel-format").value = pixelFormat;
        if (data.EnableMultiModelFallback !== undefined) applyMultiModelUiState(!!data.EnableMultiModelFallback);
        if (data.BarcodeEnabled !== undefined) {
            store.state.inspection = { ...store.state.inspection, barcodeEnabled: !!data.BarcodeEnabled };
            store.notify("inspection");
        }
        updatePlcAddressUi();
        updateTriggerSourceUi();
        if (store.state.modelList?.length) {
            selectModelOption(byId("model-select"), data.CurrentModelFileName);
            selectModelOption(byId("auxiliary1-select"), data.Auxiliary1ModelPath);
            selectModelOption(byId("auxiliary2-select"), data.Auxiliary2ModelPath);
        }
    }

    function initSettings(config) {
        populateSettings(config);
        window.addLog?.("系统配置已加载", "success");
    }

    function updateStoragePath(path) {
        const input = byId("cfg-storage-path");
        if (input) input.value = path || "";
    }

    function getPresetDisplayName(presetId, preset) {
        return String(preset?.name || preset?.Name || preset?.CameraName || presetId || "").trim();
    }

    function getProjectPresetNameInput() {
        return byId("project-preset-name");
    }

    function updateProjectPresetSelect(selectedId = "") {
        const select = byId("project-preset-select");
        if (!select) return;

        const currentValue = selectedId || select.value || "";
        select.innerHTML = "";

        const emptyOption = document.createElement("option");
        emptyOption.value = "";
        emptyOption.text = "-- 选择预设项目（可选）--";
        select.add(emptyOption);

        Object.entries(PROJECT_PRESETS)
            .sort((left, right) => getPresetDisplayName(left[0], left[1]).localeCompare(getPresetDisplayName(right[0], right[1]), "zh-CN"))
            .forEach(([presetId, preset]) => {
                const option = document.createElement("option");
                option.value = presetId;
                option.text = getPresetDisplayName(presetId, preset);
                select.add(option);
            });

        if (currentValue && PROJECT_PRESETS[currentValue]) {
            select.value = currentValue;
        }
    }

    function syncProjectPresetName() {
        const select = byId("project-preset-select");
        const input = getProjectPresetNameInput();
        if (!select || !input) return;

        const preset = PROJECT_PRESETS[select.value];
        input.value = preset ? getPresetDisplayName(select.value, preset) : "";
    }

    function handleProjectPresets(data) {
        const presets = data?.presets || data?.Presets || data || {};
        PROJECT_PRESETS = presets && typeof presets === "object" && !Array.isArray(presets) ? presets : {};
        const selectedId = pendingProjectPresetId;
        pendingProjectPresetId = "";
        updateProjectPresetSelect(selectedId);
        syncProjectPresetName();

        const pathLabel = byId("project-preset-path");
        const path = data?.path || data?.Path || "";
        if (pathLabel) pathLabel.textContent = path ? `预设文件: ${path}` : "";
    }

    function makeProjectPresetId(name) {
        const base = String(name || "")
            .trim()
            .replace(/\s+/g, "_")
            .replace(/[^\w\u4e00-\u9fa5-]+/g, "_")
            .replace(/_+/g, "_")
            .replace(/^_+|_+$/g, "")
            .slice(0, 48);
        return `${base || "preset"}_${Date.now().toString(36)}`;
    }

    function findProjectPresetIdByName(name) {
        const normalized = String(name || "").trim();
        return Object.entries(PROJECT_PRESETS).find(([, preset]) => getPresetDisplayName("", preset) === normalized)?.[0] || "";
    }

    function saveProjectPresetAsNew() {
        const input = getProjectPresetNameInput();
        const name = (input?.value || prompt("请输入新预设名称") || "").trim();
        if (!name) {
            alert("请输入预设名称");
            return;
        }

        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
            return;
        }
        const triggerError = validateTriggerSettings();
        if (triggerError) {
            alert(triggerError);
            return;
        }
        const sequenceError = validateInspectionRuleSettings();
        if (sequenceError) {
            alert(sequenceError);
            return;
        }

        let presetId = findProjectPresetIdByName(name);
        if (presetId && !confirm(`已存在同名预设“${name}”，是否覆盖？`)) {
            return;
        }

        if (!presetId) presetId = makeProjectPresetId(name);
        const preset = collectSettingsData();
        preset.name = name;
        pendingProjectPresetId = presetId;
        bridge.sendCommand("save_project_preset", { id: presetId, name, preset });
    }

    function updateSelectedProjectPreset() {
        const select = byId("project-preset-select");
        const presetId = select?.value || "";
        if (!presetId || !PROJECT_PRESETS[presetId]) {
            alert("请先选择要更新的预设");
            return;
        }

        const input = getProjectPresetNameInput();
        const name = (input?.value || getPresetDisplayName(presetId, PROJECT_PRESETS[presetId])).trim();
        if (!name) {
            alert("请输入预设名称");
            return;
        }

        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
            return;
        }
        const triggerError = validateTriggerSettings();
        if (triggerError) {
            alert(triggerError);
            return;
        }
        const sequenceError = validateInspectionRuleSettings();
        if (sequenceError) {
            alert(sequenceError);
            return;
        }

        const preset = collectSettingsData();
        preset.name = name;
        pendingProjectPresetId = presetId;
        bridge.sendCommand("save_project_preset", { id: presetId, name, preset });
    }

    function deleteSelectedProjectPreset() {
        const select = byId("project-preset-select");
        const presetId = select?.value || "";
        if (!presetId || !PROJECT_PRESETS[presetId]) {
            alert("请先选择要删除的预设");
            return;
        }

        const name = getPresetDisplayName(presetId, PROJECT_PRESETS[presetId]);
        if (!confirm(`确认删除预设“${name}”？`)) return;

        bridge.sendCommand("delete_project_preset", presetId);
        pendingProjectPresetId = "";
        if (getProjectPresetNameInput()) getProjectPresetNameInput().value = "";
        updateProjectPresetSelect("");
    }

    function exportConfigMigration() {
        bridge.sendCommand("export_config_migration");
    }

    function importConfigMigration() {
        bridge.sendCommand("import_config_migration");
    }

    function setModelPackageImportButtonBusy(isBusy) {
        const btn = byId("btn-import-model-package");
        if (!btn) return;
        btn.disabled = !!isBusy;
        btn.innerHTML = isBusy
            ? "等待导入..."
            : `<svg class="w-4 h-4" viewBox="0 0 24 24" fill="none" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M12 3v12m0-12 4 4m-4-4-4 4M5 15v3a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-3" />
                </svg>
                导入 ONNX 并生成模型包`;
    }

    function importModelPackage() {
        const resultDiv = byId("model-package-import-result");
        setModelPackageImportButtonBusy(false);

        if (resultDiv) {
            resultDiv.className = "mt-2 text-[10px] text-ink-400";
            resultDiv.textContent = "模型包导入入口已从核心版本隐藏；请走单独维护流程评审。";
            resultDiv.classList.remove("hidden");
        }

        window.addLog?.("模型包导入入口已隐藏，未执行导入。", "warning");
    }

    function collectSettingsData() {
        const fieldMapping = {
            "cfg-storage-path": "StoragePath",
            "cfg-retention-enabled": "DataRetentionEnabled",
            "cfg-require-operator-production": "RequireOperatorForProductionStart",
            "cfg-operator-session-max-hours": "OperatorSessionMaxHours",
            "cfg-image-retention-days": "ImageRetentionDays",
            "cfg-log-retention-days": "LogRetentionDays",
            "cfg-audit-retention-days": "AuditLogRetentionDays",
            "cfg-report-retention-days": "ReportRetentionDays",
            "cfg-trace-record-retention-days": "TraceRecordRetentionDays",
            "cfg-trigger-source": "TriggerSource",
            "cfg-serial-port": "SerialPhotoelectricPortName",
            "cfg-serial-baud": "SerialPhotoelectricBaudRate",
            "cfg-serial-debounce": "SerialPhotoelectricDebounceMs",
            "cfg-serial-timeout": "SerialPhotoelectricTimeoutMs",
            "cfg-plc-protocol": "PlcProtocol",
            "cfg-plc-driver-provider": "PlcDriverProvider",
            "cfg-plc-protocol-mode": "PlcProtocolMode",
            "cfg-plc-ip": "PlcIp",
            "cfg-plc-port": "PlcPort",
            "cfg-plc-trigger": "PlcTriggerAddress",
            "cfg-plc-result": "PlcResultAddress",
            "cfg-plc-trigger-seq": "PlcTriggerSeqAddress",
            "cfg-plc-result-seq": "PlcResultSeqAddress",
            "cfg-plc-vision-online": "PlcVisionOnlineAddress",
            "cfg-plc-vision-ready": "PlcVisionReadyAddress",
            "cfg-plc-vision-busy": "PlcVisionBusyAddress",
            "cfg-plc-inspection-done": "PlcInspectionDoneAddress",
            "cfg-plc-error-code": "PlcErrorCodeAddress",
            "cfg-plc-trace-saved": "PlcTraceSavedAddress",
            "cfg-plc-heartbeat": "PlcHeartbeatAddress",
            "cfg-plc-reset-fault": "PlcResetFaultAddress",
            "cfg-plc-trigger-delay": "PlcTriggerDelayMs",
            "cfg-plc-polling-interval": "PlcPollingIntervalMs",
            "cfg-plc-ok-value": "PlcOkValue",
            "cfg-plc-ng-value": "PlcNgValue",
            "cfg-plc-siemens-cpu-model": "PlcSiemensCpuModel",
            "cfg-plc-siemens-rack": "PlcSiemensRack",
            "cfg-plc-siemens-slot": "PlcSiemensSlot",
            "cfg-cam-name": "CameraName",
            "cfg-cam-serial": "CameraSerialNumber",
            "cfg-cam-manufacturer": "CameraManufacturer",
            "cfg-cam-pixel-format": "CameraPixelFormat",
            "cfg-cam-exposure": "ExposureTime",
            "cfg-cam-gain": "GainRaw",
            "cfg-logic-retry-count": "MaxRetryCount",
            "cfg-logic-retry-interval": "RetryIntervalMs",
            "cfg-plc-write-retry-count": "PlcWriteRetryCount",
            "cfg-plc-write-retry-interval": "PlcWriteRetryIntervalMs",
            "cfg-cycle-sla-enabled": "InspectionCycleSlaEnabled",
            "cfg-cycle-warning-ms": "InspectionCycleWarningMs",
            "cfg-cycle-critical-ms": "InspectionCycleCriticalMs",
            "cfg-cycle-min-samples": "InspectionCycleMinSamples",
            "cfg-yield-sla-enabled": "QualityYieldSlaEnabled",
            "cfg-yield-warning-percent": "QualityYieldWarningPercent",
            "cfg-yield-critical-percent": "QualityYieldCriticalPercent",
            "cfg-yield-min-samples": "QualityYieldMinSamples",
            "cfg-consecutive-ng-enabled": "ConsecutiveNgAlarmEnabled",
            "cfg-consecutive-ng-warning": "ConsecutiveNgWarningCount",
            "cfg-consecutive-ng-critical": "ConsecutiveNgCriticalCount",
            "cfg-yolo-gpu": "EnableGpu",
            "cfg-yolo-gpu-index": "GpuIndex",
            "cfg-industrial-render-mode": "IndustrialRenderMode",
            "cfg-barcode-enabled": "BarcodeEnabled",
            "cfg-barcode-address": "BarcodeAddress",
            "cfg-barcode-word-length": "BarcodeWordLength",
            "cfg-barcode-encoding": "BarcodeEncoding",
            "cfg-barcode-required": "BarcodeRequired",
        };
        const numericFields = new Set([
            "PlcPort", "PlcTriggerDelayMs", "PlcPollingIntervalMs", "PlcOkValue", "PlcNgValue",
            "PlcSiemensRack", "PlcSiemensSlot", "ExposureTime", "GainRaw",
            "MaxRetryCount", "RetryIntervalMs", "PlcWriteRetryCount", "PlcWriteRetryIntervalMs", "GpuIndex", "BarcodeWordLength",
            "ImageRetentionDays", "LogRetentionDays", "AuditLogRetentionDays",
            "ReportRetentionDays", "TraceRecordRetentionDays", "OperatorSessionMaxHours",
            "InspectionCycleWarningMs", "InspectionCycleCriticalMs", "InspectionCycleMinSamples",
            "QualityYieldWarningPercent", "QualityYieldCriticalPercent", "QualityYieldMinSamples",
            "ConsecutiveNgWarningCount", "ConsecutiveNgCriticalCount",
            "SerialPhotoelectricBaudRate", "SerialPhotoelectricDebounceMs", "SerialPhotoelectricTimeoutMs",
        ]);
        const data = {};

        for (const [inputId, propName] of Object.entries(fieldMapping)) {
            const input = byId(inputId);
            if (!input) continue;
            if (input.type === "checkbox") {
                data[propName] = input.checked;
            } else if (numericFields.has(propName) || input.type === "number") {
                const numVal = parseFloat(input.value);
                data[propName] = Number.isNaN(numVal) ? 0 : numVal;
            } else if (propName === "SerialPhotoelectricPortName") {
                data[propName] = normalizeSerialPortName(input.value);
            } else {
                data[propName] = input.value || "";
            }
        }
        if (byId("task-type-select")) data.TaskType = parseInt(byId("task-type-select").value, 10);
        data.Confidence = readThresholdControl("conf-input", "conf-slider", 0.5);
        data.IouThreshold = readThresholdControl("iou-input", "iou-slider", 0.45);
        data.InspectionRuleSetJson = JSON.stringify(getCurrentRuleSet());

        return data;
    }

    function saveSettings() {
        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
            return;
        }
        const triggerError = validateTriggerSettings();
        if (triggerError) {
            alert(triggerError);
            return;
        }
        const sequenceError = validateInspectionRuleSettings();
        if (sequenceError) {
            alert(sequenceError);
            return;
        }

        const data = collectSettingsData();
        bridge.sendCommand("save_settings", data);
    }

    function selectModelOption(select, preferredValue, fallbackValue = "") {
        if (!select) return;
        const preferred = String(preferredValue || "").trim();
        const fallback = String(fallbackValue || "").trim();
        const options = Array.from(select.options);
        if (preferred && options.some((option) => option.value === preferred)) {
            select.value = preferred;
            return;
        }
        if (fallback && options.some((option) => option.value === fallback)) {
            select.value = fallback;
            return;
        }
        select.selectedIndex = options.length ? 0 : -1;
    }

    function initModelList(files, notifyBackend = false) {
        const models = Array.isArray(files) ? files : (files?.models || files?.Models || []);
        store.state.modelList = models;
        const select = byId("model-select");
        if (!select) return;

        const settings = store.state.settings || {};
        const previousPrimary = select.value;
        const previousAux1 = byId("auxiliary1-select")?.value || "";
        const previousAux2 = byId("auxiliary2-select")?.value || "";

        select.innerHTML = "";
        if (!models.length) {
            const option = document.createElement("option");
            option.text = "未找到可用模型";
            option.value = "";
            select.add(option);
            return;
        }

        models.forEach((fileName) => {
            const option = document.createElement("option");
            option.value = fileName;
            option.text = fileName;
            select.add(option);
        });
        selectModelOption(select, settings.CurrentModelFileName, previousPrimary);

        ["auxiliary1-select", "auxiliary2-select"].forEach((id) => {
            const auxSelect = byId(id);
            if (!auxSelect) return;
            auxSelect.innerHTML = '<option value="">不使用</option>';
            models.forEach((fileName) => {
                const option = document.createElement("option");
                option.value = fileName;
                option.text = fileName;
                auxSelect.add(option);
            });
        });

        selectModelOption(byId("auxiliary1-select"), settings.Auxiliary1ModelPath, previousAux1);
        selectModelOption(byId("auxiliary2-select"), settings.Auxiliary2ModelPath, previousAux2);
        if (notifyBackend) bridge.sendCommand("change_model", select.value);
        window.addLog?.(`成功加载 ${models.length} 个模型`, "info");
    }

    function openSettingsModal(config) {
        if (config) populateSettings(config);
        byId("settings-modal")?.classList.remove("hidden");
        syncSettingsChrome();
        activateSettingsTab("vision");
        bridge.sendCommand("get_project_presets");
        bridge.sendCommand("open_settings");
    }

    function openSettingsFromBackend(config) {
        if (config) populateSettings(config);
        byId("settings-modal")?.classList.remove("hidden");
        syncSettingsChrome();
        activateSettingsTab("vision");
    }

    function closeSettingsModal() {
        byId("settings-modal")?.classList.add("hidden");
    }

    function updateConfidence(val) {
        const fallback = store.state.settings?.Confidence ?? 0.5;
        const rawValue = byId("conf-input") ? val : parseFloat(val) / 100;
        const value = setThresholdControl("conf-input", "conf-slider", rawValue, fallback);
        store.state.settings = { ...(store.state.settings || {}), Confidence: value };
        bridge.sendCommand("set_confidence", value);
    }

    function updateIou(val) {
        const fallback = store.state.settings?.IouThreshold ?? 0.45;
        const rawValue = byId("iou-input") ? val : parseFloat(val) / 100;
        const value = setThresholdControl("iou-input", "iou-slider", rawValue, fallback);
        store.state.settings = { ...(store.state.settings || {}), IouThreshold: value };
        bridge.sendCommand("set_iou", value);
    }

    function updateTaskType(val) {
        const taskType = parseInt(val, 10);
        bridge.sendCommand("set_task_type", taskType);
    }

    function loadProjectPreset(presetId) {
        if (!presetId) {
            syncProjectPresetName();
            return;
        }

        const preset = PROJECT_PRESETS[presetId];
        if (!preset) {
            window.addLog?.(`未找到预设配置: ${presetId}`, "error");
            return;
        }

        const textAssignments = {
            "cfg-trigger-source": preset.TriggerSource ?? "PLC",
            "cfg-serial-port": preset.SerialPhotoelectricPortName ?? "",
            "cfg-serial-baud": preset.SerialPhotoelectricBaudRate ?? 9600,
            "cfg-serial-debounce": preset.SerialPhotoelectricDebounceMs ?? 50,
            "cfg-serial-timeout": preset.SerialPhotoelectricTimeoutMs ?? 1000,
            "cfg-plc-ip": preset.PlcIp,
            "cfg-plc-port": preset.PlcPort,
            "cfg-plc-trigger": preset.PlcTriggerAddress,
            "cfg-plc-result": preset.PlcResultAddress,
            "cfg-plc-protocol": preset.PlcProtocol,
            "cfg-plc-trigger-delay": preset.PlcTriggerDelayMs ?? 800,
            "cfg-plc-polling-interval": preset.PlcPollingIntervalMs ?? 500,
            "cfg-plc-ok-value": preset.PlcOkValue ?? 1,
            "cfg-plc-ng-value": preset.PlcNgValue ?? 0,
            "cfg-plc-driver-provider": preset.PlcDriverProvider ?? "HaoCommunication",
            "cfg-plc-protocol-mode": preset.PlcProtocolMode ?? "Legacy",
            "cfg-plc-trigger-seq": preset.PlcTriggerSeqAddress ?? "D557",
            "cfg-plc-result-seq": preset.PlcResultSeqAddress ?? "D558",
            "cfg-plc-vision-online": preset.PlcVisionOnlineAddress ?? "D559",
            "cfg-plc-vision-ready": preset.PlcVisionReadyAddress ?? "D560",
            "cfg-plc-vision-busy": preset.PlcVisionBusyAddress ?? "D561",
            "cfg-plc-inspection-done": preset.PlcInspectionDoneAddress ?? "D562",
            "cfg-plc-error-code": preset.PlcErrorCodeAddress ?? "D563",
            "cfg-plc-trace-saved": preset.PlcTraceSavedAddress ?? "D564",
            "cfg-plc-heartbeat": preset.PlcHeartbeatAddress ?? "D565",
            "cfg-plc-reset-fault": preset.PlcResetFaultAddress ?? "D566",
            "cfg-plc-siemens-cpu-model": preset.PlcSiemensCpuModel ?? "S1200",
            "cfg-plc-siemens-rack": preset.PlcSiemensRack ?? 0,
            "cfg-plc-siemens-slot": preset.PlcSiemensSlot ?? 2,
            "cfg-barcode-address": preset.BarcodeAddress ?? "D570",
            "cfg-barcode-word-length": preset.BarcodeWordLength ?? 16,
            "cfg-barcode-encoding": preset.BarcodeEncoding ?? "ASCII",
            "cfg-cam-name": getPresetDisplayName(presetId, preset),
            "cfg-cam-serial": preset.CameraSerialNumber,
            "cfg-cam-manufacturer": preset.CameraManufacturer ?? "Huaray",
            "cfg-cam-pixel-format": preset.CameraPixelFormat ?? preset.PixelFormat ?? "Auto",
            "cfg-cam-exposure": preset.ExposureTime,
            "cfg-cam-gain": preset.GainRaw ?? preset.Gain ?? 1.1,
            "cfg-logic-retry-count": preset.MaxRetryCount ?? 1,
            "cfg-logic-retry-interval": preset.RetryIntervalMs ?? 2000,
            "cfg-plc-write-retry-count": preset.PlcWriteRetryCount ?? 1,
            "cfg-plc-write-retry-interval": preset.PlcWriteRetryIntervalMs ?? 200,
            "cfg-operator-session-max-hours": preset.OperatorSessionMaxHours ?? 12,
            "cfg-cycle-warning-ms": preset.InspectionCycleWarningMs ?? 1500,
            "cfg-cycle-critical-ms": preset.InspectionCycleCriticalMs ?? 3000,
            "cfg-cycle-min-samples": preset.InspectionCycleMinSamples ?? 10,
            "cfg-yield-warning-percent": preset.QualityYieldWarningPercent ?? 95.0,
            "cfg-yield-critical-percent": preset.QualityYieldCriticalPercent ?? 90.0,
            "cfg-yield-min-samples": preset.QualityYieldMinSamples ?? 30,
            "cfg-consecutive-ng-warning": preset.ConsecutiveNgWarningCount ?? 3,
            "cfg-consecutive-ng-critical": preset.ConsecutiveNgCriticalCount ?? 5,
            "cfg-yolo-gpu-index": preset.GpuIndex ?? 0,
            "cfg-storage-path": preset.StoragePath ?? "C:\\GreeVisionData",
            "cfg-image-retention-days": preset.ImageRetentionDays ?? 30,
            "cfg-log-retention-days": preset.LogRetentionDays ?? 180,
            "cfg-audit-retention-days": preset.AuditLogRetentionDays ?? 365,
            "cfg-report-retention-days": preset.ReportRetentionDays ?? 365,
            "cfg-trace-record-retention-days": preset.TraceRecordRetentionDays ?? 365,
        };
        Object.entries(textAssignments).forEach(([id, value]) => {
            const input = byId(id);
            if (id === "cfg-serial-port" && value) ensureSerialPortOption(value);
            if (input) input.value = value;
        });

        const checkboxAssignments = {
            "cfg-barcode-enabled": preset.BarcodeEnabled ?? false,
            "cfg-barcode-required": preset.BarcodeRequired ?? false,
            "cfg-yolo-gpu": preset.EnableGpu ?? false,
            "cfg-industrial-render-mode": preset.IndustrialRenderMode ?? true,
            "cfg-cycle-sla-enabled": preset.InspectionCycleSlaEnabled ?? true,
            "cfg-yield-sla-enabled": preset.QualityYieldSlaEnabled ?? true,
            "cfg-consecutive-ng-enabled": preset.ConsecutiveNgAlarmEnabled ?? true,
            "cfg-retention-enabled": preset.DataRetentionEnabled ?? false,
            "cfg-require-operator-production": preset.RequireOperatorForProductionStart ?? true,
        };
        Object.entries(checkboxAssignments).forEach(([id, value]) => {
            const cb = byId(id);
            if (cb) cb.checked = value;
        });

        updatePlcAddressUi();
        updatePlcProtocolModeUi();
        updateSiemensRackSlotVisibility();
        updateTriggerSourceUi();
        store.state.inspectionRuleSet = normalizeInspectionRuleSet(preset.InspectionRuleSetJson, preset);
        renderInspectionRules();
        syncProjectPresetName();
        window.addLog?.(`已加载预设: ${getPresetDisplayName(presetId, preset)}`, "success");
    }

    function handleConfigSnapshot(data) {
        const config = data?.config || data?.Config || data;
        if (data?.storagePath || data?.StoragePath) updateStoragePath(data.storagePath || data.StoragePath);
        if (config) populateSettings(config);
        if (data?.open || data?.Open) openSettingsFromBackend(config);
    }

    function handleBootstrapSnapshot(data) {
        store.applyBootstrapSnapshot(data);
        if (data?.config || data?.Config) populateSettings(data.config || data.Config);
        if (data?.storagePath || data?.StoragePath) updateStoragePath(data.storagePath || data.StoragePath);
        const models = data?.models || data?.Models;
        if (Array.isArray(models)) initModelList(models, false);
        const cameras = data?.cameras || data?.Cameras;
        if (Array.isArray(cameras) && typeof window.receiveCameraList === "function") {
            window.receiveCameraList({
                cameras,
                activeId: data.activeCameraId || data.ActiveCameraId || "",
            });
        }
    }

    function collectDataset() {
        const btn = byId("btn-collect-dataset");
        const resultDiv = byId("dataset-collect-result");
        if (!btn) return;

        btn.disabled = true;
        btn.textContent = "收集中，请稍候...";
        resultDiv.classList.add("hidden");

        bridge.sendCommand("collect_dataset");
    }

    function handleDatasetCollectResult(data) {
        const btn = byId("btn-collect-dataset");
        const resultDiv = byId("dataset-collect-result");
        if (!btn || !resultDiv) return;

        btn.disabled = false;
        btn.textContent = "一键收集训练数据集";

        if (data?.success) {
            resultDiv.className = "mt-2 text-[10px] text-green-600";
            resultDiv.textContent = `✅ 收集完成！共 ${data.totalCopied} 张（NG ${data.failCopied} / OK ${data.passCopied}），已保存至：${data.outputDirectory}`;
        } else {
            resultDiv.className = "mt-2 text-[10px] text-red-500";
            resultDiv.textContent = `❌ 收集失败：${data?.message || "未知错误"}`;
        }
        resultDiv.classList.remove("hidden");
    }

    function handleModelPackageImportResult(data) {
        const resultDiv = byId("model-package-import-result");
        setModelPackageImportButtonBusy(false);

        if (!resultDiv) return;

        if (data?.success) {
            resultDiv.className = "mt-2 text-[10px] text-green-600";
            resultDiv.textContent = `✅ 模型包已导入：${data.modelId || "-"}，版本 ${data.version || "-"}。${data.modelFileName ? `当前模型：${data.modelFileName}` : ""}`;
        } else {
            resultDiv.className = "mt-2 text-[10px] text-red-500";
            resultDiv.textContent = `❌ 模型包导入失败：${data?.message || "未知错误"}`;
        }

        resultDiv.classList.remove("hidden");
    }

    Object.assign(window, {
        activateSettingsTab,
        applyMultiModelUiState,
        closeSettingsModal,
        deleteSelectedProjectPreset,
        exportConfigMigration,
        handleProjectPresets,
        importModelPackage,
        importConfigMigration,
        initModelList,
        initSettings,
        loadProjectPreset,
        moveVisionControlsToSettings,
        openSettingsModal,
        populateSettings,
        saveSettings,
        saveProjectPresetAsNew,
        syncDriverProviderOptions,
        syncProjectPresetName,
        toggleMultiModel,
        updateSelectedProjectPreset,
        updateConfidence,
        updateIou,
        addInspectionRule,
        duplicateInspectionRule,
        moveInspectionRule,
        removeInspectionRule,
        renderInspectionRules,
        updateInspectionRule,
        updatePlcAddressUi,
        updatePlcProtocolModeUi,
        updateSiemensRackSlotVisibility,
        updateStoragePath,
        updateTaskType,
        collectDataset,
        handleDatasetCollectResult,
        handleModelPackageImportResult,
        handleCommandDispatched,
        handleSerialPortsDetected,
        updateTriggerSourceUi,
    });

    bridge.registerMessageHandler("bootstrapSnapshot", handleBootstrapSnapshot);
    bridge.registerMessageHandler("configSnapshot", handleConfigSnapshot);
    bridge.registerMessageHandler("modelList", (data) => initModelList(data?.models || data?.Models || data || [], false));
    bridge.registerMessageHandler("modelLabels", (data) => {
        store.state.modelLabels = data?.labels || data?.Labels || data || [];
        updateRuleLabelOptions();
    });
    bridge.registerMessageHandler("projectPresets", handleProjectPresets);
    bridge.registerMessageHandler("datasetCollectResult", handleDatasetCollectResult);
    bridge.registerMessageHandler("modelPackageImportResult", handleModelPackageImportResult);
    bridge.registerMessageHandler("serialPortsDetected", handleSerialPortsDetected);
})();
