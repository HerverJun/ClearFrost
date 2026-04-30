// ==========================================
// ClearFrost UI Logic (ui.js)
// ==========================================

// Global state variables for UI
let roiCanvas = null;
let roiCtx = null;
let isDrawingROI = false;
let roiStartX = 0;
let roiStartY = 0;
// 存储当前 ROI 坐标用于持久显示 (相对于 canvas 的像素坐标)
let currentROIRect = null; // { x, y, w, h }
let windowDragging = false;
let dragOffset = { x: 0, y: 0 };

// --- Window Dragging ---
function startDrag(e) {
    if (
        e.target.closest("button") ||
        e.target.closest("input") ||
        e.target.closest(".no-drag")
    )
        return;
    windowDragging = true;
    dragOffset.x = e.screenX - window.screenX;
    dragOffset.y = e.screenY - window.screenY;
    sendCommand("start_drag");
}
window.startDrag = startDrag;

document.addEventListener("mouseup", () => {
    if (windowDragging) {
        windowDragging = false;
        // Drag end is handled natively by C#, no message needed
    }
});

document.addEventListener("mousemove", (e) => {
    // Native drag handled by C# often, but here for completeness if needed
});

// --- Drawer & Dock ---

function toggleDrawer(panelId) {
    const panel = document.getElementById(panelId);
    if (!panel) return;

    const isLeft = panelId === "left-panel";
    const isOpen = panel.classList.contains("drawer-open");

    if (isOpen) {
        panel.classList.remove("drawer-open");
        panel.classList.add(
            isLeft ? "drawer-closed-left" : "drawer-closed-right",
        );
        // Show floating button
        const floatBtn = document.getElementById(
            isLeft ? "float-btn-left" : "float-btn-right",
        );
        if (floatBtn) {
            floatBtn.classList.remove("pointer-events-none", "opacity-0");
            floatBtn.classList.add("opacity-100");
        }
    } else {
        panel.classList.remove(
            isLeft ? "drawer-closed-left" : "drawer-closed-right",
        );
        panel.classList.add("drawer-open");
        // Hide floating button
        const floatBtn = document.getElementById(
            isLeft ? "float-btn-left" : "float-btn-right",
        );
        if (floatBtn) {
            floatBtn.classList.add("pointer-events-none", "opacity-0");
            floatBtn.classList.remove("opacity-100");
        }
    }
}
window.toggleDrawer = toggleDrawer;

function toggleDock() {
    const dock = document.getElementById("bottom-dock");
    const arrow = document.getElementById("dock-arrow");
    const trigger = document.getElementById("dock-trigger-container");

    if (!dock) return;

    const isOpen = dock.classList.contains("dock-open");

    if (isOpen) {
        // 收起工具栏 → 显示呼出按钮
        dock.classList.remove("dock-open");
        dock.classList.add("dock-closed");
        if (arrow) arrow.classList.remove("rotate-180");
        if (trigger) {
            trigger.classList.remove("opacity-0", "pointer-events-none");
            trigger.classList.add("opacity-100", "pointer-events-auto");
        }
    } else {
        // 展开工具栏 → 隐藏呼出按钮
        dock.classList.remove("dock-closed");
        dock.classList.add("dock-open");
        if (arrow) arrow.classList.add("rotate-180");
        if (trigger) {
            trigger.classList.remove("opacity-100", "pointer-events-auto");
            trigger.classList.add("opacity-0", "pointer-events-none");
        }
    }
}
window.toggleDock = toggleDock;

// --- Vision Modes ---


function toggleMultiModel(enabled) {
    applyMultiModelUiState(enabled);

    sendCommand("toggle_multi_model", enabled);
    addLog(
        enabled ? "✓ 多模型自动切换已启用" : "多模型自动切换已禁用",
        enabled ? "success" : "info",
    );
}
window.toggleMultiModel = toggleMultiModel;

function applyMultiModelUiState(enabled) {
    const checkbox = document.getElementById("enable-multi-model");
    const statusText = document.getElementById("multi-model-status");
    const configSection = document.getElementById("multi-model-config");

    // Update checkbox state
    if (checkbox) checkbox.checked = enabled;

    // Update status text
    if (statusText) {
        statusText.innerText = enabled ? "已启用" : "自动切换";
        if (enabled) {
            statusText.classList.add("text-celadon-600", "font-bold");
            statusText.classList.remove("text-ink-500");
        } else {
            statusText.classList.remove("text-celadon-600", "font-bold");
            statusText.classList.add("text-ink-500");
        }
    }

    // Enable/disable auxiliary model configuration section
    if (configSection) {
        if (enabled) {
            configSection.classList.remove("opacity-50", "pointer-events-none");
        } else {
            configSection.classList.add("opacity-50", "pointer-events-none");
        }
    }
}

// --- Camera Management ---

function onCameraSelected(cameraId) {
    // cameraId can be passed directly from onchange="onCameraSelected(this.value)"
    // or we fallback to reading from the select element
    const select = document.getElementById("cfg-cam-select");
    const id = cameraId || (select ? select.value : "");
    window.activeCameraId = id;

    if (!window.cameraList) window.cameraList = [];
    const cam = window.cameraList.find((c) => c.id === id);
    if (cam) {
        const nameEl = document.getElementById("cfg-cam-name");
        const manufacturerEl = document.getElementById("cfg-cam-manufacturer");
        const serialEl = document.getElementById("cfg-cam-serial");
        const expEl = document.getElementById("cfg-cam-exposure");
        const gainEl = document.getElementById("cfg-cam-gain");
        if (nameEl) nameEl.value = cam.displayName || "";
        if (manufacturerEl) manufacturerEl.value = cam.manufacturer || "Huaray";
        if (serialEl) serialEl.value = cam.serialNumber || "";
        if (expEl) expEl.value = cam.exposureTime || "";
        if (gainEl) gainEl.value = cam.gain || "";
    }

    // Notify backend of camera switch
    if (id) sendCommand("switch_camera", id);
}
window.onCameraSelected = onCameraSelected;

function addNewCamera() {
    // 收集表单中的相机配置信息
    const displayName =
        document.getElementById("cfg-cam-name")?.value ||
        `相机 ${(window.cameraList?.length || 0) + 1}`;
    const manufacturer =
        document.getElementById("cfg-cam-manufacturer")?.value || "Huaray";
    const serialNumber = document.getElementById("cfg-cam-serial")?.value || "";
    const exposureTime =
        parseFloat(document.getElementById("cfg-cam-exposure")?.value) || 50000;
    const gain =
        parseFloat(document.getElementById("cfg-cam-gain")?.value) || 1.0;

    if (!serialNumber) {
        alert("请输入相机序列号");
        return;
    }

    const camData = {
        displayName: displayName,
        manufacturer: manufacturer,
        serialNumber: serialNumber,
        exposureTime: exposureTime,
        gain: gain,
    };

    sendCommand("add_camera", camData);
    addLog(`正在添加/更新相机: ${displayName}...`, "info");
}
window.addNewCamera = addNewCamera;

function deleteCurrentCamera() {
    const select = document.getElementById("cfg-cam-select");
    if (!select || !select.value) return;
    window.chrome.webview.postMessage(
        JSON.stringify({
            cmd: "delete_camera",
            value: select.value,
        }),
    );
}
window.deleteCurrentCamera = deleteCurrentCamera;

// --- Super Search Camera ---
function superSearchCameras() {
    const modal = document.getElementById("super-search-modal");
    const loading = document.getElementById("super-search-loading");
    const results = document.getElementById("super-search-results");
    const empty = document.getElementById("super-search-empty");

    if (!modal) return;

    // 显示弹窗和加载状态
    modal.classList.remove("hidden");
    loading.classList.remove("hidden");
    results.classList.add("hidden");
    empty.classList.add("hidden");
    results.innerHTML = "";

    // 发送搜索命令
    sendCommand("super_search_cameras");
}
window.superSearchCameras = superSearchCameras;

function closeSuperSearchModal() {
    const modal = document.getElementById("super-search-modal");
    if (modal) modal.classList.add("hidden");
}
window.closeSuperSearchModal = closeSuperSearchModal;

// 接收超级搜索结果
function receiveSuperSearchResult(data) {
    const loading = document.getElementById("super-search-loading");
    const results = document.getElementById("super-search-results");
    const empty = document.getElementById("super-search-empty");

    loading.classList.add("hidden");

    if (!data || !data.cameras || data.cameras.length === 0) {
        empty.classList.remove("hidden");
        return;
    }

    results.classList.remove("hidden");
    results.innerHTML = data.cameras
        .map(
            (cam) => `
        <div class="bg-gradient-to-r from-slate-50 to-slate-100 rounded-xl p-4 border border-slate-200 hover:shadow-md transition-all">
            <div class="flex items-center justify-between">
                <div class="flex-1">
                    <div class="flex items-center gap-2 mb-1">
                        <span class="text-sm font-bold text-slate-700">${cam.userDefinedName || cam.model || "未命名相机"}</span>
                        <span class="px-2 py-0.5 text-[10px] font-semibold rounded-full bg-indigo-100 text-indigo-600">${cam.manufacturer}</span>
                    </div>
                    <div class="text-xs text-slate-500 space-y-0.5">
                        <div><span class="font-medium">序列号:</span> ${cam.serialNumber}</div>
                        <div><span class="font-medium">型号:</span> ${cam.model || "-"}</div>
                        <div><span class="font-medium">接口:</span> ${cam.interfaceType || "-"}</div>
                    </div>
                </div>
                <button onclick="directConnectCamera('${cam.serialNumber}', '${cam.manufacturer}', '${cam.model || ""}', '${cam.userDefinedName || ""}')"
                    class="px-4 py-2 bg-gradient-to-r from-green-500 to-emerald-500 text-white text-sm font-semibold rounded-lg hover:shadow-lg hover:scale-105 transition-all flex items-center gap-1">
                    <svg xmlns="http://www.w3.org/2000/svg" class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 10V3L4 14h7v7l9-11h-7z" />
                    </svg>
                    连接
                </button>
            </div>
        </div>
    `,
        )
        .join("");
}
window.receiveSuperSearchResult = receiveSuperSearchResult;

// 直接连接相机（无序列号过滤）
function directConnectCamera(
    serialNumber,
    manufacturer,
    model,
    userDefinedName,
) {
    sendCommand("direct_connect_camera", {
        serialNumber: serialNumber,
        manufacturer: manufacturer,
        model: model,
        userDefinedName: userDefinedName,
    });
    closeSuperSearchModal();
}
window.directConnectCamera = directConnectCamera;


// --- YOLO Parameter Controls ---

function updateConfidence(val) {
    const value = parseFloat(val) / 100;
    const display = document.getElementById("conf-value");
    if (display) display.innerText = value.toFixed(2);
    sendCommand("set_confidence", value);
}
window.updateConfidence = updateConfidence;

function updateIou(val) {
    const value = parseFloat(val) / 100;
    const display = document.getElementById("iou-value");
    if (display) display.innerText = value.toFixed(2);
    sendCommand("set_iou", value);
}
window.updateIou = updateIou;

function updateTaskType(val) {
    const taskType = parseInt(val, 10);
    sendCommand("set_task_type", taskType);
    const taskNames = {
        0: "分类 (Classify)",
        1: "目标检测 (Detect)",
        3: "实例分割 (Segment)",
        5: "姿态估计 (Pose)",
        6: "旋转框检测 (OBB)",
    };
    addLog(`任务类型已设置为: ${taskNames[taskType] || taskType}`);
}
window.updateTaskType = updateTaskType;


// --- Modals ---

function openSettingsModal(config) {
    document.getElementById("settings-modal").classList.remove("hidden");
    // 如果后端传入了配置数据（密码验证通过后），直接填充设置
    // 否则，发送 open_settings 命令触发密码验证流程
    if (config) {
        populateSettings(config);
    } else {
        sendCommand("open_settings");
    }
}
window.openSettingsModal = openSettingsModal;

function closeSettingsModal() {
    document.getElementById("settings-modal").classList.add("hidden");
}
window.closeSettingsModal = closeSettingsModal;

const PLC_PROTOCOL_UI_HINTS = {
    Mitsubishi_MC_ASCII: {
        placeholderTrigger: "555 或 D555",
        placeholderResult: "556 或 D556",
        helpText: "三菱统一按 D 区保存；输入纯数字会自动规范成 D 区地址。",
    },
    Mitsubishi_MC_Binary: {
        placeholderTrigger: "555 或 D555",
        placeholderResult: "556 或 D556",
        helpText: "三菱统一按 D 区保存；输入纯数字会自动规范成 D 区地址。",
    },
    Siemens_S7: {
        placeholderTrigger: "DB100.0",
        placeholderResult: "DB100.2",
        helpText:
            "西门子首版仅支持 DB 字地址，例如 DB100.0；请确认 PLC 已开放 PUT/GET，且联动 DB 关闭 optimized block access。",
    },
    Modbus_TCP: {
        placeholderTrigger: "100",
        placeholderResult: "102",
        helpText: "Modbus 仅支持纯数字寄存器地址。",
    },
    Omron_Fins: {
        placeholderTrigger: "100 或 D100",
        placeholderResult: "102 或 D102",
        helpText: "欧姆龙统一按 D 区保存；输入纯数字会自动规范成 D 区地址。",
    },
};

function getCompactPlcAddress(value) {
    return (value || "").trim().replace(/\s+/g, "").toUpperCase();
}

function updateSiemensRackSlotVisibility() {
    const protocol = document.getElementById("cfg-plc-protocol")?.value || "";
    const cpuModel = (
        document.getElementById("cfg-plc-siemens-cpu-model")?.value || ""
    ).toUpperCase();
    const rackSlotGroup = document.getElementById("cfg-plc-siemens-rack-slot");
    if (!rackSlotGroup) return;

    const showRackSlot =
        protocol === "Siemens_S7" && (cpuModel === "S300" || cpuModel === "S400");
    rackSlotGroup.classList.toggle("hidden", !showRackSlot);
}

function updatePlcAddressUi() {
    const protocolSelect = document.getElementById("cfg-plc-protocol");
    const triggerInput = document.getElementById("cfg-plc-trigger");
    const resultInput = document.getElementById("cfg-plc-result");
    const barcodeInput = document.getElementById("cfg-plc-barcode-address");
    const helpEl = document.getElementById("cfg-plc-address-help");
    const siemensOptions = document.getElementById("cfg-plc-siemens-options");
    const protocol = protocolSelect?.value || "Mitsubishi_MC_ASCII";
    const hints =
        PLC_PROTOCOL_UI_HINTS[protocol] || PLC_PROTOCOL_UI_HINTS.Mitsubishi_MC_ASCII;

    if (triggerInput) triggerInput.placeholder = hints.placeholderTrigger;
    if (resultInput) resultInput.placeholder = hints.placeholderResult;
    if (barcodeInput) {
        barcodeInput.placeholder = protocol === "Siemens_S7" ? "DB15.DBB2" : hints.placeholderTrigger;
    }
    if (helpEl) helpEl.textContent = hints.helpText;
    if (siemensOptions) {
        siemensOptions.classList.toggle("hidden", protocol !== "Siemens_S7");
    }

    syncDriverProviderOptions();
    updateSiemensRackSlotVisibility();
}
window.updatePlcAddressUi = updatePlcAddressUi;
window.updateSiemensRackSlotVisibility = updateSiemensRackSlotVisibility;

function validatePlcAddress(address, protocol) {
    const compact = getCompactPlcAddress(address);
    if (!compact) {
        return "PLC 地址不能为空";
    }

    if (protocol === "Mitsubishi_MC_ASCII" || protocol === "Mitsubishi_MC_Binary") {
        if (/^\d+$/.test(compact) || /^D\d+$/.test(compact)) return null;
        return "三菱地址仅支持 D 区，例如 555 或 D555";
    }

    if (protocol === "Siemens_S7") {
        const match = compact.match(/^DB(\d+)\.(\d+)$/);
        if (!match) {
            return "西门子首版仅支持 DB 字地址，例如 DB100.0";
        }

        return null;
    }

    if (protocol === "Modbus_TCP") {
        if (/^\d+$/.test(compact)) return null;
        return "Modbus 地址仅支持纯数字寄存器地址，例如 100";
    }

    if (protocol === "Omron_Fins") {
        if (/^\d+$/.test(compact) || /^D\d+$/.test(compact)) return null;
        return "欧姆龙地址仅支持 D 区，例如 100 或 D100";
    }

    return null;
}

function validatePlcByteAddress(address, protocol) {
    const compact = getCompactPlcAddress(address);
    if (!compact) {
        return "PLC 字节地址不能为空";
    }

    if (protocol === "Siemens_S7") {
        if (/^DB\d+\.(DBB)?\d+$/.test(compact)) return null;
        return "西门子条码地址支持 DB15.DBB2 或 DB15.2";
    }

    return validatePlcAddress(address, protocol);
}

function validatePlcSettings() {
    const protocol = document.getElementById("cfg-plc-protocol")?.value || "";
    const driver = document.getElementById("cfg-plc-driver-provider")?.value || "";
    const triggerAddress = document.getElementById("cfg-plc-trigger")?.value || "";
    const resultAddress = document.getElementById("cfg-plc-result")?.value || "";
    const barcodeEnabled = !!document.getElementById("cfg-plc-barcode-enabled")?.checked;
    const barcodeAddress = document.getElementById("cfg-plc-barcode-address")?.value || "";
    const barcodeLength = parseInt(document.getElementById("cfg-plc-barcode-length")?.value || "0", 10);

    if (driver === "McpX" && !protocol.startsWith("Mitsubishi")) {
        return "仅三菱协议支持 McpX 驱动库";
    }

    const triggerError = validatePlcAddress(triggerAddress, protocol);
    if (triggerError) {
        return `触发地址无效: ${triggerError}`;
    }

    const resultError = validatePlcAddress(resultAddress, protocol);
    if (resultError) {
        return `结果地址无效: ${resultError}`;
    }

    if (barcodeEnabled) {
        const barcodeError = validatePlcByteAddress(barcodeAddress, protocol);
        if (barcodeError) {
            return `条码地址无效: ${barcodeError}`;
        }

        if (!Number.isFinite(barcodeLength) || barcodeLength < 1 || barcodeLength > 256) {
            return "条码长度必须在 1 到 256 之间";
        }
    }

    return null;
}

// Populate settings from backend config object
function populateSettings(data) {
    // 映射后端属性名到前端input id
    const mapping = {
        StoragePath: "cfg-storage-path",
        PlcProtocol: "cfg-plc-protocol",
        PlcDriverProvider: "cfg-plc-driver-provider",
        PlcIp: "cfg-plc-ip",
        PlcPort: "cfg-plc-port",
        PlcTriggerAddress: "cfg-plc-trigger",
        PlcResultAddress: "cfg-plc-result",
        EnablePlcBarcodeReading: "cfg-plc-barcode-enabled",
        PlcBarcodeAddress: "cfg-plc-barcode-address",
        PlcBarcodeLength: "cfg-plc-barcode-length",
        PlcBarcodeRequired: "cfg-plc-barcode-required",
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
    };
    for (const key in data) {
        const inputId = mapping[key];
        if (!inputId) continue;
        const el = document.getElementById(inputId);
        if (el) {
            if (el.type === "checkbox") el.checked = !!data[key];
            else el.value = data[key] ?? "";
        }
    }
    if (data.TaskType !== undefined) {
        const taskTypeSelect = document.getElementById("task-type-select");
        if (taskTypeSelect) taskTypeSelect.value = data.TaskType.toString();
    }
    if (data.EnableMultiModelFallback !== undefined) {
        applyMultiModelUiState(!!data.EnableMultiModelFallback);
    }
    updatePlcAddressUi();
}
window.populateSettings = populateSettings;

function syncDriverProviderOptions() {
    const protocolSelect = document.getElementById("cfg-plc-protocol");
    const driverSelect = document.getElementById("cfg-plc-driver-provider");
    if (!protocolSelect || !driverSelect) return;

    const isMitsubishi = (protocolSelect.value || "").startsWith("Mitsubishi");
    const mcpxOption = driverSelect.querySelector('option[value="McpX"]');

    if (mcpxOption) {
        mcpxOption.disabled = !isMitsubishi;
    }

    if (!isMitsubishi && driverSelect.value === "McpX") {
        driverSelect.value = "Hsl";
    }

    updateSiemensRackSlotVisibility();
}
window.syncDriverProviderOptions = syncDriverProviderOptions;

function initSettings(config) {
    const data = typeof config === "string" ? JSON.parse(config) : config;
    populateSettings(data);
    addLog("系统配置已加载", "success");
}
window.initSettings = initSettings;

function saveSettings() {
    const plcError = validatePlcSettings();
    if (plcError) {
        alert(plcError);
        return;
    }

    // 显式映射: 前端 input ID -> AppConfig 属性名
    const fieldMapping = {
        "cfg-storage-path": "StoragePath",
        "cfg-plc-protocol": "PlcProtocol",
        "cfg-plc-driver-provider": "PlcDriverProvider",
        "cfg-plc-ip": "PlcIp",
        "cfg-plc-port": "PlcPort",
        "cfg-plc-trigger": "PlcTriggerAddress",
        "cfg-plc-result": "PlcResultAddress",
        "cfg-plc-barcode-enabled": "EnablePlcBarcodeReading",
        "cfg-plc-barcode-address": "PlcBarcodeAddress",
        "cfg-plc-barcode-length": "PlcBarcodeLength",
        "cfg-plc-barcode-required": "PlcBarcodeRequired",
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
    };

    const data = {};
    const numericFields = [
        "PlcPort",
        "PlcTriggerDelayMs",
        "PlcPollingIntervalMs",
        "PlcBarcodeLength",
        "PlcOkValue",
        "PlcNgValue",
        "PlcSiemensRack",
        "PlcSiemensSlot",
        "ExposureTime",
        "GainRaw",
        "TargetCount",
        "MaxRetryCount",
        "RetryIntervalMs",
    ];

    for (const [inputId, propName] of Object.entries(fieldMapping)) {
        const el = document.getElementById(inputId);
        if (!el) continue;

        if (el.type === "checkbox") {
            data[propName] = el.checked;
        } else if (numericFields.includes(propName) || el.type === "number") {
            const numVal = parseFloat(el.value);
            data[propName] = isNaN(numVal) ? 0 : numVal;
        } else {
            data[propName] = el.value || "";
        }
    }

    // Task Type
    const tt = document.getElementById("task-type-select");
    if (tt) data["TaskType"] = parseInt(tt.value);

    sendCommand("save_settings", data);
}
window.saveSettings = saveSettings;

// ===== 项目预设配置（填充现有表单字段）=====
const PROJECT_PRESETS = {
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
    },
    W5_screw: {
        name: "W5螺钉视觉检测",
        PlcIp: "192.168.22.44",
        PlcPort: 4999,
        PlcTriggerAddress: "D555",
        PlcResultAddress: "D556",
        CameraSerialNumber: "EF59632AAK00074",
        PlcProtocol: "Mitsubishi_MC_Binary",
        TargetLabel: "screw",
        TargetCount: 4,
        ExposureTime: 3500,
        Gain: 1.5,
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
    },
    S7_barcode_aircon: {
        name: "S7空调条码视觉检测",
        PlcIp: "10.182.90.211",
        PlcPort: 102,
        PlcTriggerAddress: "DB1.555",
        PlcResultAddress: "DB1.556",
        PlcProtocol: "Siemens_S7",
        PlcDriverProvider: "Hsl",
        PlcSiemensCpuModel: "S1500",
        EnablePlcBarcodeReading: true,
        PlcBarcodeAddress: "DB15.DBB2",
        PlcBarcodeLength: 13,
        PlcBarcodeRequired: true,
        CameraSerialNumber: "",
        TargetLabel: "screw",
        TargetCount: 4,
        ExposureTime: 3500,
        Gain: 1.5,
    },
};

/**
 * 加载项目预设配置到现有表单字段
 */
function loadProjectPreset(presetId) {
    if (!presetId) return;

    const preset = PROJECT_PRESETS[presetId];
    if (!preset) {
        addLog(`未找到预设配置: ${presetId}`, "error");
        return;
    }

    // 填充 PLC 配置（现有字段）
    const plcIp = document.getElementById("cfg-plc-ip");
    const plcPort = document.getElementById("cfg-plc-port");
    const plcTrigger = document.getElementById("cfg-plc-trigger");
    const plcResult = document.getElementById("cfg-plc-result");
    const plcProtocol = document.getElementById("cfg-plc-protocol");
    const plcDriverProvider = document.getElementById("cfg-plc-driver-provider");
    const plcSiemensCpuModel = document.getElementById("cfg-plc-siemens-cpu-model");
    const plcBarcodeEnabled = document.getElementById("cfg-plc-barcode-enabled");
    const plcBarcodeAddress = document.getElementById("cfg-plc-barcode-address");
    const plcBarcodeLength = document.getElementById("cfg-plc-barcode-length");
    const plcBarcodeRequired = document.getElementById("cfg-plc-barcode-required");
    const plcTriggerDelay = document.getElementById("cfg-plc-trigger-delay");
    const plcPollingInterval = document.getElementById(
        "cfg-plc-polling-interval",
    );

    if (plcIp) plcIp.value = preset.PlcIp;
    if (plcPort) plcPort.value = preset.PlcPort;
    if (plcTrigger) plcTrigger.value = preset.PlcTriggerAddress;
    if (plcResult) plcResult.value = preset.PlcResultAddress;
    if (plcProtocol) plcProtocol.value = preset.PlcProtocol;
    if (plcDriverProvider && preset.PlcDriverProvider)
        plcDriverProvider.value = preset.PlcDriverProvider;
    if (plcSiemensCpuModel && preset.PlcSiemensCpuModel)
        plcSiemensCpuModel.value = preset.PlcSiemensCpuModel;
    if (plcBarcodeEnabled)
        plcBarcodeEnabled.checked = !!preset.EnablePlcBarcodeReading;
    if (plcBarcodeAddress)
        plcBarcodeAddress.value = preset.PlcBarcodeAddress || "DB15.DBB2";
    if (plcBarcodeLength)
        plcBarcodeLength.value = preset.PlcBarcodeLength || 13;
    if (plcBarcodeRequired)
        plcBarcodeRequired.checked = preset.PlcBarcodeRequired !== false;
    if (plcTriggerDelay)
        plcTriggerDelay.value = preset.PlcTriggerDelayMs || 800;
    if (plcPollingInterval)
        plcPollingInterval.value = preset.PlcPollingIntervalMs || 500;
    updatePlcAddressUi();

    // 填充相机配置（现有字段）
    const camName = document.getElementById("cfg-cam-name");
    const camSerialNumber = document.getElementById("cfg-cam-serial");
    const camManufacturer = document.getElementById("cfg-cam-manufacturer");
    const camExposure = document.getElementById("cfg-cam-exposure");
    const camGain = document.getElementById("cfg-cam-gain");

    if (camName) camName.value = preset.name; // 项目名称 -> 显示名称
    if (camSerialNumber) camSerialNumber.value = preset.CameraSerialNumber;
    if (camManufacturer) camManufacturer.value = "Huaray"; // 所有项目均为华睿相机
    if (camExposure) camExposure.value = preset.ExposureTime;
    if (camGain) camGain.value = preset.Gain || 1.1; // 填充增益，默认为1.1

    // 填充检测逻辑配置（现有字段）
    const targetLabel = document.getElementById("cfg-logic-target-label");
    const targetCount = document.getElementById("cfg-logic-target-count");

    if (targetLabel) targetLabel.value = preset.TargetLabel;
    if (targetCount) targetCount.value = preset.TargetCount;

    addLog(`✓ 已加载预设: ${preset.name}`, "success");
    // showMiniToast(`已加载预设: ${preset.name}`, 'success');
}
window.loadProjectPreset = loadProjectPreset;

function openPasswordModal() {
    document.getElementById("password-modal").classList.remove("hidden");
    const el = document.getElementById("admin-password");
    if (el) {
        el.value = "";
        el.focus();
    }
}
window.openPasswordModal = openPasswordModal;
window.showPasswordModal = openPasswordModal; // Alias for HTML onclick

function checkPassword() {
    // Renamed from verifyPassword to match some usages, or ensure consistency
    const pwd = document.getElementById("admin-password").value;
    sendCommand("verify_password", pwd);
    document.getElementById("admin-password").value = "";
}
window.verifyPassword = checkPassword; // Alias

function closePasswordModal() {
    document.getElementById("password-modal").classList.add("hidden");
}
window.closePasswordModal = closePasswordModal;

function openLogHistoryModal() {
    document.getElementById("log-history-modal").classList.remove("hidden");
    sendCommand("get_detection_logs");
}
window.openLogHistoryModal = openLogHistoryModal;
window.closeLogHistoryModal = () =>
    document.getElementById("log-history-modal").classList.add("hidden");

function openGalleryModal() {
    document.getElementById("gallery-modal").classList.remove("hidden");
    sendCommand("get_ng_dates");
}
window.openGalleryModal = openGalleryModal;
window.closeGalleryModal = () =>
    document.getElementById("gallery-modal").classList.add("hidden");

function openStatisticsHistoryModal() {
    document
        .getElementById("statistics-history-modal")
        .classList.remove("hidden");
    requestStatisticsHistory(30);
}
window.openStatisticsHistoryModal = openStatisticsHistoryModal;
window.closeStatisticsHistoryModal = () =>
    document.getElementById("statistics-history-modal").classList.add("hidden");

function requestStatisticsHistory(days) {
    if (days) {
        document.querySelectorAll(".stat-tab").forEach((b) => {
            b.classList.remove("bg-celadon-100", "text-celadon-700");
            b.classList.add("text-slate-500", "hover:bg-slate-50");
        });
        // Find button logic omitted for brevity
    }
    document.getElementById("statistics-history-table").innerHTML =
        '<tr><td colspan="5" class="text-center py-8">加载中...</td></tr>';
    sendCommand("get_statistics_history", days);
}
window.requestStatisticsHistory = requestStatisticsHistory;

window.closeImageViewer = () =>
    document.getElementById("image-viewer").classList.add("hidden");


// --- ROI Canvas Logic ---

function initRoiInteractions() {
    roiCanvas = document.getElementById("roi-canvas");
    if (!roiCanvas) return;

    const img = document.getElementById("camera-view");
    const container = document.getElementById("camera-container");

    function updateCanvasLayout() {
        if (!img) return;
        const iW = img.naturalWidth || img.width || 1280;
        const iH = img.naturalHeight || img.height || 720;
        if (iW === 0) return;

        const containerRect = container.getBoundingClientRect();
        const containerW = containerRect.width;
        const containerH = containerRect.height;
        const containerRatio = containerW / containerH;
        const imgRatio = iW / iH;

        let renderedW, renderedH, offsetX, offsetY;

        if (containerRatio > imgRatio) {
            renderedH = containerH;
            renderedW = containerH * imgRatio;
            offsetX = (containerW - renderedW) / 2;
            offsetY = 0;
        } else {
            renderedW = containerW;
            renderedH = containerW / imgRatio;
            offsetX = 0;
            offsetY = (containerH - renderedH) / 2;
        }

        roiCanvas.style.width = `${renderedW}px`;
        roiCanvas.style.height = `${renderedH}px`;
        roiCanvas.style.left = `${offsetX}px`;
        roiCanvas.style.top = `${offsetY}px`;
        roiCanvas.width = renderedW;
        roiCanvas.height = renderedH;
    }

    const resizeObserver = new ResizeObserver(() =>
        requestAnimationFrame(updateCanvasLayout),
    );
    if (container) resizeObserver.observe(container);
    if (img) img.addEventListener("load", updateCanvasLayout);
    window.addEventListener("resize", updateCanvasLayout);
    setTimeout(updateCanvasLayout, 100);

    roiCanvas.addEventListener("mousedown", (e) => {
        isDrawingROI = true;
        const rect = roiCanvas.getBoundingClientRect();
        roiStartX = e.clientX - rect.left;
        roiStartY = e.clientY - rect.top;
    });

    roiCanvas.addEventListener("mousemove", (e) => {
        if (!isDrawingROI) return;
        const rect = roiCanvas.getBoundingClientRect();
        const currentX = e.clientX - rect.left;
        const currentY = e.clientY - rect.top;
        const ctx = roiCanvas.getContext("2d");
        ctx.clearRect(0, 0, roiCanvas.width, roiCanvas.height);

        const width = currentX - roiStartX;
        const height = currentY - roiStartY;
        ctx.strokeStyle = "#a4161a";
        ctx.lineWidth = 2;
        ctx.setLineDash([8, 4]);
        ctx.strokeRect(roiStartX, roiStartY, width, height);
        ctx.fillStyle = "rgba(164, 22, 26, 0.05)";
        ctx.fillRect(roiStartX, roiStartY, width, height);
    });

    roiCanvas.addEventListener("mouseup", (e) => {
        if (!isDrawingROI) return;
        isDrawingROI = false;
        const rect = roiCanvas.getBoundingClientRect();
        const currentX = e.clientX - rect.left;
        const currentY = e.clientY - rect.top;
        let x = Math.min(roiStartX, currentX);
        let y = Math.min(roiStartY, currentY);
        let w = Math.abs(currentX - roiStartX);
        let h = Math.abs(currentY - roiStartY);

        if (w < 10 || h < 10) return;
        const normX = x / roiCanvas.width;
        const normY = y / roiCanvas.height;
        const normW = w / roiCanvas.width;
        const normH = h / roiCanvas.height;

        sendCommand("update_roi", { rect: [normX, normY, normW, normH] });
        addLog(
            `ROI Set: [${normX.toFixed(2)}, ${normY.toFixed(2)}, ${normW.toFixed(2)}, ${normH.toFixed(2)}]`,
        );

        // 保存当前 ROI 坐标用于持久显示
        currentROIRect = { x, y, w, h };
    });

    roiCanvas.addEventListener("mouseleave", () => {
        isDrawingROI = false;
    });
}

function clearRoi() {
    const canvas = document.getElementById("roi-canvas");
    if (canvas) {
        const ctx = canvas.getContext("2d");
        ctx.clearRect(0, 0, canvas.width, canvas.height);
    }
    currentROIRect = null; // 清除存储的 ROI
    sendCommand("update_roi", { rect: [0, 0, 0, 0] });
    addLog("ROI Cleared");
}
window.clearRoi = clearRoi;

// 重绘 ROI 矩形（用于在图像更新后保持显示）
function redrawROI() {
    if (!currentROIRect || !roiCanvas) return;
    const ctx = roiCanvas.getContext("2d");
    ctx.clearRect(0, 0, roiCanvas.width, roiCanvas.height);
    ctx.strokeStyle = "#a4161a";
    ctx.lineWidth = 2;
    ctx.setLineDash([8, 4]);
    ctx.strokeRect(
        currentROIRect.x,
        currentROIRect.y,
        currentROIRect.w,
        currentROIRect.h,
    );
    ctx.fillStyle = "rgba(164, 22, 26, 0.05)";
    ctx.fillRect(
        currentROIRect.x,
        currentROIRect.y,
        currentROIRect.w,
        currentROIRect.h,
    );
}
window.redrawROI = redrawROI;

// --- Initalization ---

document.addEventListener("DOMContentLoaded", () => {
    // initialize ROI
    initRoiInteractions();
    updatePlcAddressUi();

    // initialize File Input

    // Initialize Charts
    const ctx = document.getElementById("inferenceChart");
    if (ctx && window.Chart) {
        window.inferenceChart = new Chart(ctx, {
            type: "line",
            data: {
                labels: Array(60).fill(""),
                datasets: [
                    {
                        label: "FPS",
                        data: Array(60).fill(0),
                        borderColor: "#06b6d4",
                        borderWidth: 1,
                        tension: 0.4,
                        pointRadius: 0,
                    },
                ],
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                plugins: { legend: { display: false } },
                scales: {
                    x: { display: false },
                    y: { display: true, beginAtZero: true, suggestedMax: 30 },
                },
            },
        });
    }

    const statsCtx = document.getElementById("statsChart");
    if (statsCtx && window.Chart) {
        window.statsChart = new Chart(statsCtx, {
            type: "doughnut",
            data: {
                labels: ["OK", "NG"],
                datasets: [
                    {
                        data: [0, 0],
                        backgroundColor: ["#22c55e", "#ef4444"],
                        borderWidth: 0,
                    },
                ],
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: "70%",
                plugins: { legend: { display: false } },
            },
        });
    }

    // Signal Ready
    setTimeout(() => sendCommand("app_ready"), 500);
});

// --- Cropper Modal Logic (Legacy) ---

// --- Hikvision Super Search Stub ---
function searchCamerasHik() {
    const modal = document.getElementById("super-search-modal");
    const resultsContainer = document.getElementById("super-search-results");
    const emptyState = document.getElementById("super-search-empty");
    const loadingState = document.getElementById("super-search-loading");
    const modalTitle = modal ? modal.querySelector("h3") : null;

    if (modal) modal.classList.remove("hidden");
    if (resultsContainer) {
        resultsContainer.innerHTML = "";
        resultsContainer.classList.add("hidden");
    }
    if (emptyState) emptyState.classList.add("hidden");
    if (loadingState) loadingState.classList.remove("hidden");

    // Update title to indicate HIK search
    if (modalTitle)
        modalTitle.innerHTML = `
        <svg xmlns="http://www.w3.org/2000/svg" class="h-6 w-6 text-indigo-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
        </svg>
        相机搜索结果
    `;

    // Send command
    sendCommand("super_search_cameras_hik");
}
window.searchCamerasHik = searchCamerasHik;
