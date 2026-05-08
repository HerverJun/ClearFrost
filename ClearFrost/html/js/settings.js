// ==========================================
// ClearFrost settings workspace
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const store = window.CF_STORE;

    const PLC_PROTOCOL_UI_HINTS = {
        Mitsubishi_MC_ASCII: {
            help: "三菱 MC ASCII：常用 D100 / M100 / X10 / Y10；地址不带协议前缀。",
            placeholder: "例如 D100",
        },
        Mitsubishi_MC_Binary: {
            help: "三菱 MC Binary：常用 D100 / M100 / X10 / Y10；地址不带协议前缀。",
            placeholder: "例如 D100",
        },
        Siemens_S7: {
            help: "西门子 S7：DB 地址使用 DB1.0 格式；M/I/Q 可用 M0 / I0 / Q0。",
            placeholder: "例如 DB1.0",
        },
        Modbus_TCP: {
            help: "Modbus TCP：使用 0-based 寄存器地址，例如 40001 或 0。",
            placeholder: "例如 40001",
        },
        Omron_Fins: {
            help: "欧姆龙 FINS：常用 D100 / CIO100 / W100 / H100。",
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
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
    };

    let pendingProjectPresetId = "";

    function byId(id) {
        return document.getElementById(id);
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

    function validatePlcAddress(address, protocol) {
        const compact = getCompactPlcAddress(address);
        if (!compact) return "地址不能为空";
        if (protocol === "Siemens_S7") {
            if (/^(M|I|Q)\d+(\.\d+)?$/.test(compact)) return null;
            const match = compact.match(/^DB(\d+)\.(\d+)$/);
            if (match && Number(match[1]) >= 1 && Number(match[2]) >= 0) return null;
            return "西门子地址需为 DB1.0、M0、I0 或 Q0 格式";
        }
        if (protocol === "Modbus_TCP") {
            return /^\d+$/.test(compact) ? null : "Modbus 地址需为数字";
        }
        if (/^(D|M|X|Y|CIO|W|H)\d+$/i.test(compact)) return null;
        return "地址格式不符合当前 PLC 协议";
    }

    function validatePlcSettings() {
        const protocol = byId("cfg-plc-protocol")?.value || "";
        const driver = byId("cfg-plc-driver-provider")?.value || "";
        const mode = byId("cfg-plc-protocol-mode")?.value || "Legacy";
        const triggerAddress = byId("cfg-plc-trigger")?.value || "";
        const resultAddress = byId("cfg-plc-result")?.value || "";

        if (driver === "McpX" && !protocol.startsWith("Mitsubishi")) {
            return "仅三菱协议支持 McpX 驱动库";
        }

        const triggerError = validatePlcAddress(triggerAddress, protocol);
        if (triggerError) return `触发地址无效: ${triggerError}`;
        const resultError = validatePlcAddress(resultAddress, protocol);
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
                const error = validatePlcAddress(byId(inputId)?.value || "", protocol);
                if (error) return `${label} 地址无效: ${error}`;
            }
        }

        if (byId("cfg-barcode-enabled")?.checked) {
            const barcodeError = validatePlcAddress(byId("cfg-barcode-address")?.value || "", protocol);
            if (barcodeError) return `条码地址无效: ${barcodeError}`;
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
            ExposureTime: "cfg-cam-exposure",
            GainRaw: "cfg-cam-gain",
            TargetLabel: "cfg-logic-target-label",
            TargetCount: "cfg-logic-target-count",
            MaxRetryCount: "cfg-logic-retry-count",
            RetryIntervalMs: "cfg-logic-retry-interval",
            EnableGpu: "cfg-yolo-gpu",
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
            if (input.type === "checkbox") {
                input.checked = !!data[propName];
            } else {
                input.value = data[propName] ?? "";
            }
        }

        if (data.TaskType !== undefined && byId("task-type-select")) byId("task-type-select").value = String(data.TaskType);
        if (data.Confidence !== undefined) {
            const percent = Math.round(Number(data.Confidence) * 100);
            if (byId("conf-slider")) byId("conf-slider").value = percent;
            if (byId("conf-value")) byId("conf-value").textContent = Number(data.Confidence).toFixed(2);
        }
        if (data.IouThreshold !== undefined) {
            const percent = Math.round(Number(data.IouThreshold) * 100);
            if (byId("iou-slider")) byId("iou-slider").value = percent;
            if (byId("iou-value")) byId("iou-value").textContent = Number(data.IouThreshold).toFixed(2);
        }
        if (data.EnableMultiModelFallback !== undefined) applyMultiModelUiState(!!data.EnableMultiModelFallback);
        if (data.BarcodeEnabled !== undefined) {
            store.state.inspection = { ...store.state.inspection, barcodeEnabled: !!data.BarcodeEnabled };
            store.notify("inspection");
        }
        updatePlcAddressUi();
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

    function collectSettingsData() {
        const fieldMapping = {
            "cfg-storage-path": "StoragePath",
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
            "cfg-cam-exposure": "ExposureTime",
            "cfg-cam-gain": "GainRaw",
            "cfg-logic-target-label": "TargetLabel",
            "cfg-logic-target-count": "TargetCount",
            "cfg-logic-retry-count": "MaxRetryCount",
            "cfg-logic-retry-interval": "RetryIntervalMs",
            "cfg-yolo-gpu": "EnableGpu",
            "cfg-industrial-render-mode": "IndustrialRenderMode",
            "cfg-barcode-enabled": "BarcodeEnabled",
            "cfg-barcode-address": "BarcodeAddress",
            "cfg-barcode-word-length": "BarcodeWordLength",
            "cfg-barcode-encoding": "BarcodeEncoding",
            "cfg-barcode-required": "BarcodeRequired",
        };
        const numericFields = new Set([
            "PlcPort", "PlcTriggerDelayMs", "PlcPollingIntervalMs", "PlcOkValue", "PlcNgValue",
            "PlcSiemensRack", "PlcSiemensSlot", "ExposureTime", "GainRaw", "TargetCount",
            "MaxRetryCount", "RetryIntervalMs", "BarcodeWordLength",
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
            } else {
                data[propName] = input.value || "";
            }
        }

        if (byId("task-type-select")) data.TaskType = parseInt(byId("task-type-select").value, 10);
        if (byId("conf-slider")) data.Confidence = Math.max(0, Math.min(1, parseFloat(byId("conf-slider").value) / 100));
        if (byId("iou-slider")) data.IouThreshold = Math.max(0, Math.min(1, parseFloat(byId("iou-slider").value) / 100));

        return data;
    }

    function saveSettings() {
        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
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
        const value = parseFloat(val) / 100;
        if (byId("conf-value")) byId("conf-value").innerText = value.toFixed(2);
        bridge.sendCommand("set_confidence", value);
    }

    function updateIou(val) {
        const value = parseFloat(val) / 100;
        if (byId("iou-value")) byId("iou-value").innerText = value.toFixed(2);
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
            "cfg-cam-exposure": preset.ExposureTime,
            "cfg-cam-gain": preset.GainRaw ?? preset.Gain ?? 1.1,
            "cfg-logic-target-label": preset.TargetLabel,
            "cfg-logic-target-count": preset.TargetCount,
            "cfg-logic-retry-count": preset.MaxRetryCount ?? 1,
            "cfg-logic-retry-interval": preset.RetryIntervalMs ?? 2000,
            "cfg-storage-path": preset.StoragePath ?? "C:\\GreeVisionData",
        };
        Object.entries(textAssignments).forEach(([id, value]) => {
            const input = byId(id);
            if (input) input.value = value;
        });

        const checkboxAssignments = {
            "cfg-barcode-enabled": preset.BarcodeEnabled ?? false,
            "cfg-barcode-required": preset.BarcodeRequired ?? false,
            "cfg-yolo-gpu": preset.EnableGpu ?? false,
            "cfg-industrial-render-mode": preset.IndustrialRenderMode ?? true,
        };
        Object.entries(checkboxAssignments).forEach(([id, value]) => {
            const cb = byId(id);
            if (cb) cb.checked = value;
        });

        updatePlcAddressUi();
        updatePlcProtocolModeUi();
        updateSiemensRackSlotVisibility();
        syncProjectPresetName();
        window.addLog?.(`已加载预设: ${getPresetDisplayName(presetId, preset)}`, "success");
    }

    function openPasswordModal() {
        byId("password-modal")?.classList.remove("hidden");
        const input = byId("admin-password");
        if (input) {
            input.value = "";
            input.focus();
        }
    }

    function closePasswordModal() {
        byId("password-modal")?.classList.add("hidden");
    }

    function verifyPassword() {
        const input = byId("admin-password");
        const pwd = input?.value || "";
        bridge.sendCommand("verify_password", pwd);
        if (input) input.value = "";
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
            window.receiveCameraList({ cameras, activeId: data.activeCameraId || data.ActiveCameraId || "" });
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

    Object.assign(window, {
        activateSettingsTab,
        applyMultiModelUiState,
        closePasswordModal,
        closeSettingsModal,
        deleteSelectedProjectPreset,
        handleProjectPresets,
        initModelList,
        initSettings,
        loadProjectPreset,
        moveVisionControlsToSettings,
        openPasswordModal,
        openSettingsModal,
        populateSettings,
        saveSettings,
        saveProjectPresetAsNew,
        showPasswordModal: openPasswordModal,
        syncDriverProviderOptions,
        syncProjectPresetName,
        toggleMultiModel,
        updateSelectedProjectPreset,
        updateConfidence,
        updateIou,
        updatePlcAddressUi,
        updatePlcProtocolModeUi,
        updateSiemensRackSlotVisibility,
        updateStoragePath,
        updateTaskType,
        verifyPassword,
        collectDataset,
        handleDatasetCollectResult,
    });

    bridge.registerMessageHandler("bootstrapSnapshot", handleBootstrapSnapshot);
    bridge.registerMessageHandler("configSnapshot", handleConfigSnapshot);
    bridge.registerMessageHandler("modelList", (data) => initModelList(data?.models || data?.Models || data || [], false));
    bridge.registerMessageHandler("projectPresets", handleProjectPresets);
    bridge.registerMessageHandler("datasetCollectResult", handleDatasetCollectResult);
})();
