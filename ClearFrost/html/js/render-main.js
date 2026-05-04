// ==========================================
// ClearFrost main screen rendering
// ==========================================
(function () {
    "use strict";

    const { pickValue, escapeHtml, normalizeHealthLevel } = window.CF_UTILS;
    const store = window.CF_STORE;
    const domCache = new Map();
    const logBuffer = [];
    const detectionLogBuffer = [];
    const MaxLogEntries = 50;
    const LogFlushIntervalMs = 300;
    const ChartUpdateIntervalMs = 250;
    let logFlushTimer = null;
    let detectionLogFlushTimer = null;
    let resultOverlayTimer = null;
    let lastPreviewFrameId = 0;
    let lastStatsChartTick = 0;
    let openCameraCooldownUntil = 0;
    let openCameraUnlockTimer = null;
    let openCameraPending = false;
    let exitAppPending = false;

    window.statsChart = window.statsChart || null;

    function el(id) {
        if (!id) return null;
        if (!domCache.has(id)) {
            domCache.set(id, document.getElementById(id));
        }
        return domCache.get(id);
    }

    function setText(id, value, fallback = "-") {
        const node = el(id);
        if (!node) return;
        const text = value === undefined || value === null || value === "" ? fallback : String(value);
        if (node.textContent !== text) node.textContent = text;
        if ("title" in node && node.title !== text) node.title = text;
    }

    function toggleClass(node, className, force) {
        if (!node) return;
        node.classList.toggle(className, force);
    }

    function setDotState(dotId, state) {
        const dot = el(dotId);
        if (!dot || dot.dataset.cfState === state) return;
        dot.dataset.cfState = state;
        dot.classList.remove("status-on", "status-off", "status-warning", "status-error", "bg-slate-300", "bg-emerald-500");
        dot.classList.add(state);
    }

    function getBarcodeState(inspection) {
        if (inspection.barcodeEnabled === false) return "disabled";
        const barcode = inspection.productBarcode;
        const succeeded = inspection.barcodeReadSucceeded;
        const error = inspection.barcodeError || inspection.errorCode;
        if (succeeded === false || error === "BarcodeReadFailed" || error === "NoBarcode") return "failed";
        if (barcode) return "success";
        if (succeeded === null || succeeded === undefined) return "waiting";
        return "disabled";
    }

    function renderBarcode(inspection) {
        const card = el("cf-barcode-card");
        if (!card) return;
        const state = getBarcodeState(inspection);
        if (card.dataset.cfState !== state) {
            card.dataset.cfState = state;
            card.classList.remove("barcode-state-disabled", "barcode-state-waiting", "barcode-state-success", "barcode-state-failed");
            card.classList.add(`barcode-state-${state}`);
        }

        const labels = {
            disabled: "未启用",
            waiting: "等待触发",
            success: "读取成功",
            failed: "读取失败",
        };
        setText("cf-barcode-status", labels[state], "等待触发");
        setText("cf-barcode-value", inspection.productBarcode, "-");
        setText("cf-barcode-error", inspection.barcodeError || (state === "waiting" ? "条码来自 PLC，上电后等待首件触发" : ""), "");
    }

    function renderInspectionContext(state) {
        const inspection = state.inspection || {};
        setText("ctx-inspection-id", inspection.inspectionId);
        setText("ctx-trigger-source", inspection.triggerSource);
        setText("ctx-trigger-seq", inspection.triggerSeq);
        setText("ctx-result-seq", inspection.resultSeq);
        setText("ctx-trace-status", inspection.traceStatus, "Unknown");
        setText("ctx-current-stage", inspection.currentStage, "Idle");
        setText("ctx-error-code", inspection.errorCode);
        setText("ctx-total-ms", inspection.totalMs, "0");
        setText("ctx-capture-ms", inspection.captureMs, "0");
        setText("ctx-inference-ms", inspection.inferenceMs, "0");
        setText("ctx-plc-write-ms", inspection.plcWriteMs, "0");
        setText("camera-phase", inspection.currentStage, "IDLE");
        setText("feed-sn", getTraceIdentityLabel(inspection), "条码: -");
        const isStandaloneSource = !!inspection.sourceLabel && !inspection.inspectionId;
        setText(
            "feed-trigger-seq",
            isStandaloneSource
                ? "LOCAL"
                : `T${inspection.triggerSeq ?? "-"} / R${inspection.resultSeq ?? "-"}`,
            "T- / R-",
        );
        renderBarcode(inspection);
    }

    function getTraceIdentityLabel(item) {
        if (item?.productBarcode) return `SN: ${item.productBarcode}`;
        if (item?.sourceLabel) return item.sourceLabel;
        if (item?.barcodeEnabled === true) {
            return item?.barcodeReadSucceeded === false ? "条码未读取" : "等待条码";
        }
        if (item?.inspectionId) return `ID: ${item.inspectionId}`;
        return "条码: -";
    }

    function getDetectionSummary(item) {
        const message = item?.message || item?.errorMessage || "";
        const parts = String(message).split("|").map((part) => part.trim()).filter(Boolean);
        const objectPart = parts.find((part) => /^Found\s+\d+\s*:/i.test(part) || part.includes("未检测到目标"));
        if (objectPart) return objectPart;
        if (item?.barcodeError) return item.barcodeError;
        if (item?.actualCount !== undefined && item?.actualCount !== null) return `检出 ${item.actualCount}`;
        return item?.currentStage || "-";
    }

    function renderCameraResult(state) {
        const inspection = state.inspection || {};
        const isOk = inspection.isOk;
        const pill = el("camera-result-pill");
        if (pill) {
            const className = isOk === true ? "result-ok" : isOk === false ? "result-ng" : "result-idle";
            const text = isOk === true ? "OK" : isOk === false ? "NG" : "WAIT";
            if (pill.dataset.cfResult !== className) {
                pill.dataset.cfResult = className;
                pill.classList.remove("result-idle", "result-ok", "result-ng");
                pill.classList.add(className);
            }
            if (pill.textContent !== text) pill.textContent = text;
        }

        const message = inspection.message || (isOk === true ? "检测通过" : isOk === false ? "检测未通过" : "等待检测结果");
        setText("camera-result-text", message, "等待检测结果");
        setText("camera-total-ms", `${inspection.totalMs || 0}ms`, "0ms");
        setText("camera-target-count", inspection.actualCount ?? 0, "0");
        setText("camera-model", inspection.usedModelName, "-");
        setText("feed-model-name", inspection.usedModelName ? `MODEL ${inspection.usedModelName}` : "MODEL -", "MODEL -");
        toggleClass(el("camera-fallback"), "hidden", !inspection.wasFallback);
    }

    function renderRecentInspections(state = window.CF_STATE) {
        const container = el("inspection-flow-list");
        if (!container) return;
        const list = state.recentInspections || [];
        if (!list.length) {
            const empty = '<div class="cf-empty-state">等待第一条检测流水...</div>';
            if (container.innerHTML !== empty) container.innerHTML = empty;
            return;
        }

        container.innerHTML = list.map((item) => {
            const isOk = item.isOk === true;
            const statusClass = isOk ? "ok" : item.isOk === false ? "ng" : "run";
            const statusText = isOk ? "OK" : item.isOk === false ? "NG" : "RUN";
            const identity = getTraceIdentityLabel(item);
            const title = item.productBarcode || item.sourceLabel || item.inspectionId || item.barcodeError || "-";
            const detectionSummary = getDetectionSummary(item);
            const detail = [
                detectionSummary,
                item.totalMs ? `${item.totalMs}ms` : null,
            ].filter(Boolean).join(" / ");

            return `<div class="cf-flow-row ${statusClass}">
                <span class="cf-flow-rail"></span>
                <div class="cf-flow-head">
                    <span class="cf-flow-time">${escapeHtml(item.time)}</span>
                    <span class="cf-flow-status">${statusText}</span>
                </div>
                <div class="cf-flow-sn" title="${escapeHtml(title)}">${escapeHtml(identity)}</div>
                <div class="cf-flow-detail" title="${escapeHtml(detail)}">${escapeHtml(detail || item.currentStage || "-")}</div>
            </div>`;
        }).join("");
    }

    function renderRecentNg(state) {
        const inspection = state.inspection || {};
        if (inspection.isOk !== false) return;
        setText("recent-ng-title", getTraceIdentityLabel(inspection) || "NG 样本");
        setText("recent-ng-detail", `${inspection.errorCode || "NG"} / ${inspection.totalMs || 0}ms / ${inspection.usedModelName || "-"}`);
    }

    function renderStats(state) {
        const stats = state.stats || { total: 0, ok: 0, ng: 0 };
        setText("val-total", stats.total, "0");
        setText("val-ok", stats.ok, "0");
        setText("val-ng", stats.ng, "0");
        const yieldRate = stats.total > 0 ? (stats.ok / stats.total * 100) : 0;
        const defectRate = stats.total > 0 ? (stats.ng / stats.total * 100) : 0;
        setText("val-defect-rate", `${defectRate.toFixed(1)}%`, "0.0%");
        setText("stitch-pass-rate", `${yieldRate.toFixed(1)}%`, "0.0%");
        const progress = el("yield-progress");
        if (progress) {
            const width = `${Math.max(0, Math.min(100, yieldRate)).toFixed(1)}%`;
            if (progress.style.width !== width) progress.style.width = width;
        }

        if (window.statsChart?.data?.datasets?.[0]) {
            const now = Date.now();
            if (now - lastStatsChartTick >= ChartUpdateIntervalMs) {
                window.statsChart.data.datasets[0].data = [stats.ok || 0, stats.ng || 0];
                window.statsChart.update("none");
                lastStatsChartTick = now;
            }
        }
    }

    function renderHealthSnapshot(state) {
        const snapshot = state.health;
        if (!snapshot) return;
        const level = normalizeHealthLevel(pickValue(snapshot, "healthLevel", "HealthLevel"));
        const camera = pickValue(snapshot, "cameraStatus", "CameraStatus");
        const plc = pickValue(snapshot, "plcStatus", "PlcStatus");
        const model = pickValue(snapshot, "modelStatus", "ModelStatus");
        const storage = pickValue(snapshot, "storageStatus", "StorageStatus");
        const database = pickValue(snapshot, "databaseStatus", "DatabaseStatus");
        const imageQueue = pickValue(snapshot, "imageQueueLength", "ImageQueueLength") || 0;
        const recordQueue = pickValue(snapshot, "recordQueueLength", "RecordQueueLength") || 0;
        const errors = pickValue(snapshot, "recentErrors", "RecentErrors") || [];
        const recentError = errors.length ? errors[errors.length - 1] : null;

        setText("health-overall-text", level.toUpperCase(), "HEALTH");
        setDotState("health-overall-dot", level === "Critical" ? "status-error" : level === "Warning" ? "status-warning" : "status-on");
        setText("health-camera", camera);
        setText("health-plc", plc);
        setText("health-model", model);
        setText("health-storage", storage);
        setText("health-database", database);
        setText("health-queue", `${imageQueue}/${recordQueue}`);
        setText("health-error", recentError ? `${recentError.Source || recentError.source || "Error"}: ${recentError.Message || recentError.message || ""}` : "无最近错误");

        setText("header-camera-text", camera ? `CAM ${camera}` : "CAMERA", "CAMERA");
        setText("header-plc-text", plc ? `PLC ${plc}` : "PLC", "PLC");
        setText("header-model-text", model ? `MODEL ${model}` : "MODEL", "MODEL");
        setText("header-storage-text", storage ? `STORE ${storage}` : "STORAGE", "STORAGE");
        setDotState("header-status-model", model && !String(model).includes("NotLoaded") ? "status-on" : "status-off");
        setDotState("header-status-storage", storage && !String(storage).includes("Error") ? "status-on" : "status-error");
    }

    function renderAll(state) {
        renderInspectionContext(state);
        renderCameraResult(state);
        renderRecentInspections(state);
        renderRecentNg(state);
        renderStats(state);
        renderHealthSnapshot(state);
    }

    function flushBufferedLogs(buffer, containerId, classFactory, formatter) {
        const container = el(containerId);
        if (!container || buffer.length === 0) return;

        const fragment = document.createDocumentFragment();
        for (let index = buffer.length - 1; index >= 0; index--) {
            const entry = buffer[index];
            const div = document.createElement("div");
            div.className = classFactory(entry.type);
            div.innerText = formatter(entry);
            fragment.appendChild(div);
        }

        container.prepend(fragment);
        buffer.length = 0;

        while (container.children.length > MaxLogEntries) {
            container.lastChild.remove();
        }
    }

    function addLog(msg, type = "info") {
        logBuffer.push({ msg, type, time: new Date().toLocaleTimeString() });
        if (!logFlushTimer) {
            logFlushTimer = window.setTimeout(() => {
                logFlushTimer = null;
                flushBufferedLogs(
                    logBuffer,
                    "log-container",
                    (entryType) => "p-1 font-mono text-[10px] border-l-2 " +
                        (entryType === "error" ? "border-vermilion text-vermilion bg-vermilion/5" : "border-celadon-300 text-ink-500 hover:bg-slate-50"),
                    (entry) => `${entry.time} ${entry.msg}`,
                );
            }, LogFlushIntervalMs);
        }
    }

    function addDetectionLog(msg, type = "normal") {
        detectionLogBuffer.push({ msg, type, time: new Date().toLocaleTimeString() });
        if (!detectionLogFlushTimer) {
            detectionLogFlushTimer = window.setTimeout(() => {
                detectionLogFlushTimer = null;
                flushBufferedLogs(
                    detectionLogBuffer,
                    "detection-log-container",
                    () => "pl-2 border-l border-slate-100 text-ink-600 py-1 hover:bg-slate-50 transition-colors font-mono text-[10px]",
                    (entry) => `[${entry.time}] ${entry.msg}`,
                );
            }, LogFlushIntervalMs);
        }
    }

    function clearLogs() {
        logBuffer.length = 0;
        if (logFlushTimer) window.clearTimeout(logFlushTimer);
        logFlushTimer = null;
        const container = el("log-container");
        if (container) container.innerHTML = "";
    }

    function clearDetectionLogs() {
        detectionLogBuffer.length = 0;
        if (detectionLogFlushTimer) window.clearTimeout(detectionLogFlushTimer);
        detectionLogFlushTimer = null;
        const container = el("detection-log-container");
        if (container) container.innerHTML = "";
    }

    function showToast(message, type = "info", durationMs = 1400) {
        if (!message) return;
        let container = el("cf-toast-container");
        if (!container) {
            container = document.createElement("div");
            container.id = "cf-toast-container";
            container.className = "cf-toast-container";
            document.body.appendChild(container);
            domCache.set("cf-toast-container", container);
        }

        const toast = document.createElement("div");
        toast.className = `cf-toast cf-toast-${type}`;
        toast.textContent = message;
        container.appendChild(toast);
        window.requestAnimationFrame(() => toast.classList.add("cf-toast-show"));
        window.setTimeout(() => {
            toast.classList.remove("cf-toast-show");
            window.setTimeout(() => toast.remove(), 220);
        }, durationMs);
    }

    function updateResult(isOk) {
        const overlay = el("result-overlay");
        if (!overlay) return;
        overlay.classList.remove("hidden");
        if (resultOverlayTimer) window.clearTimeout(resultOverlayTimer);
        overlay.style.animation = "none";
        void overlay.offsetHeight;
        overlay.style.animation = null;

        if (isOk) {
            overlay.innerText = "OK";
            overlay.className = "stitch-result-overlay stitch-result-ok";
        } else {
            overlay.innerText = "NG";
            overlay.className = "stitch-result-overlay stitch-result-ng";
        }

        resultOverlayTimer = window.setTimeout(() => {
            overlay.classList.add("hidden");
            resultOverlayTimer = null;
        }, 1200);
    }

    function updateConnection(type, isConnected) {
        const dotId = type === "cam" ? "header-status-cam" : "header-status-plc";
        setDotState(dotId, isConnected ? "status-on" : "status-off");
        if (type === "cam") setText("header-camera-text", isConnected ? "CAM OPEN" : "CAM CLOSED");
        if (type === "plc") setText("header-plc-text", isConnected ? "PLC ONLINE" : "PLC OFFLINE");

        if (type === "cam" && !isConnected && openCameraPending) {
            openCameraPending = false;
            setOpenCameraButtonBusy(false);
            if (openCameraUnlockTimer) {
                window.clearTimeout(openCameraUnlockTimer);
                openCameraUnlockTimer = null;
            }
        }

        if (type === "cam" && isConnected && openCameraPending) {
            openCameraPending = false;
            setOpenCameraButtonBusy(false);
            if (openCameraUnlockTimer) {
                window.clearTimeout(openCameraUnlockTimer);
                openCameraUnlockTimer = null;
            }
            showToast("相机连接成功", "success", 1300);
        }
    }

    function setWindowButtonsBusy(isBusy) {
        ["btn-minimize-app", "btn-toggle-maximize", "btn-exit-app"].forEach((id) => {
            const button = el(id);
            if (!button) return;
            button.disabled = isBusy;
            button.classList.toggle("opacity-60", isBusy);
            button.classList.toggle("cursor-wait", isBusy);
        });
    }

    function requestExitApp() {
        if (exitAppPending) return;
        if (!confirm("确认退出系统？")) return;
        exitAppPending = true;
        setWindowButtonsBusy(true);
        addLog("已发送 exit_app 指令，正在等待安全退出...", "info");
        showToast("正在安全退出...", "info", 1500);
        window.sendCommand("exit_app");
    }

    function setOpenCameraButtonBusy(isBusy) {
        const button = el("btn-open-camera");
        if (!button) return;
        button.disabled = isBusy;
        button.classList.toggle("camera-open-pending", isBusy);
    }

    function requestOpenCamera() {
        const now = Date.now();
        if (now < openCameraCooldownUntil) {
            showToast("相机正在打开中，请勿重复点击", "warning", 1200);
            return;
        }

        openCameraCooldownUntil = now + 1500;
        openCameraPending = true;
        setOpenCameraButtonBusy(true);
        if (openCameraUnlockTimer) window.clearTimeout(openCameraUnlockTimer);
        openCameraUnlockTimer = window.setTimeout(() => {
            openCameraPending = false;
            setOpenCameraButtonBusy(false);
            openCameraUnlockTimer = null;
        }, 1500);

        window.sendCommand("open_camera");
        showToast("打开相机指令已发送", "info", 1200);
        return true;
    }

    function startSystem() {
        if (requestOpenCamera()) {
            showToast("启动系统指令已发送", "info", 1400);
        }
    }

    function updatePreviewImage({ url, base64, frameId }) {
        const image = el("camera-view");
        if (!image) return;

        const normalizedFrameId = Number(frameId || Date.now());
        if (normalizedFrameId < lastPreviewFrameId) return;
        lastPreviewFrameId = normalizedFrameId;
        window.CF_STATE.previewFrameId = normalizedFrameId;

        const src = url || (base64 ? (String(base64).startsWith("data:image") ? base64 : `data:image/jpeg;base64,${base64}`) : "");
        if (!src || image.src === src) return;

        image.onload = () => {
            image.onload = null;
            image.onerror = null;
            window.requestAnimationFrame(() => {
                if (typeof window.redrawROI === "function") window.redrawROI();
            });
        };
        image.onerror = () => {
            image.onload = null;
            image.onerror = null;
        };
        image.src = src;
    }

    function updateImage(base64) {
        updatePreviewImage({ base64, frameId: Date.now() });
    }

    function updateImageUrl(url) {
        updatePreviewImage({ url, frameId: Date.now() });
    }

    function updateInferenceMetrics(metrics) {
        const data = typeof metrics === "string" ? JSON.parse(metrics) : metrics;
        if (!data) return;
        window.CF_STATE.metrics = data;
    }

    function updateStatus(data) {
        try {
            store.applyStatsUpdate(data);
        } catch (error) {
            console.error("Status Update Error:", error);
            addLog("Status Parse Error", "error");
        }
    }

    function handleInspectionUpdate(payload) {
        store.applyInspectionUpdate(payload);
    }

    function handleHealthSnapshot(snapshot) {
        store.applyHealthSnapshot(snapshot);
    }

    function flashPlcTrigger() {
        const trigger = el("header-status-trigger");
        if (!trigger) return;
        trigger.classList.remove("status-trigger-flash");
        void trigger.offsetWidth;
        trigger.classList.add("status-trigger-flash");
        trigger.addEventListener("animationend", () => trigger.classList.remove("status-trigger-flash"), { once: true });
    }

    function updateCameraName(name) {
        window.CF_STATE.cameraName = name || "未配置";
    }

    function handleUiCommand(data) {
        const action = data?.action || data?.Action;
        const payload = data?.payload || data?.Payload || {};
        switch (action) {
            case "alert":
                alert(payload.message || payload.Message || "");
                break;
            case "toast":
                showToast(payload.message || payload.Message, payload.type || payload.Type || "info", payload.durationMs || payload.DurationMs || 1400);
                break;
            case "showPasswordModal":
                window.showPasswordModal?.();
                break;
            case "closePasswordModal":
                window.closePasswordModal?.();
                break;
            case "showSettingsModal":
                window.openSettingsModal?.(payload.config || payload.Config || null);
                break;
            case "closeSettingsModal":
                window.closeSettingsModal?.();
                break;
            default:
                if (window.__CF_DEV_MODE) console.debug("Unknown uiCommand:", data);
                break;
        }
    }

    function handleDetectionFrame(data) {
        if (!data) return;
        if (typeof data.isOk === "boolean") updateResult(data.isOk);
        if (data.stats) updateStatus(data.stats);
        if (data.log?.message) addDetectionLog(data.log.message, data.log.type);
        if (data.metrics) updateInferenceMetrics(data.metrics);
        handleInspectionUpdate({
            ...(data.inspection || {}),
            isOk: data.isOk,
            message: data.log?.message,
            totalMs: data.inspection?.totalMs ?? data.totalMs,
            actualCount: data.inspection?.actualCount ?? data.actualCount,
            usedModelName: data.inspection?.usedModelName ?? data.usedModelName,
            wasFallback: data.inspection?.wasFallback ?? data.wasFallback,
            sourceLabel: data.inspection?.sourceLabel ?? data.sourceLabel,
            currentStage: data.inspection?.currentStage ?? "Completed",
        });
    }

    function receiveDetectionResult(result) {
        if (!result) return;
        updateResult(result.IsPass);
        if (result.ResultImageBase64) updateImage(result.ResultImageBase64);
        addDetectionLog(`${result.IsPass ? "通过" : "未通过"} - ${result.Message} (${result.ProcessingTimeMs.toFixed(1)}ms)`);
    }

    function initCharts() {
        const statsCanvas = el("statsChart");
        if (statsCanvas && window.Chart && !window.statsChart) {
            window.statsChart = new Chart(statsCanvas, {
                type: "doughnut",
                data: {
                    labels: ["OK", "NG"],
                    datasets: [{
                        data: [0, 0],
                        backgroundColor: ["#10b981", "#ef4444"],
                        borderColor: ["rgba(16, 185, 129, 0.75)", "rgba(239, 68, 68, 0.75)"],
                        borderWidth: 1,
                    }],
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    animation: false,
                    cutout: "70%",
                    plugins: { legend: { display: false } },
                },
            });
        }
    }

    store.subscribe(renderAll);

    Object.assign(window, {
        addLog,
        addDetectionLog,
        clearLogs,
        clearDetectionLogs,
        escapeHtml,
        flashPlcTrigger,
        handleInspectionUpdate,
        renderHealthSnapshot: handleHealthSnapshot,
        renderInspectionContext: () => renderInspectionContext(window.CF_STATE),
        renderRecentInspections: () => renderRecentInspections(window.CF_STATE),
        requestExitApp,
        requestOpenCamera,
        startSystem,
        setDotState,
        setText,
        showToast,
        updateCameraName,
        updateConnection,
        updateImage,
        updateImageUrl,
        updateInferenceMetrics,
        updateResult,
        updateStatus,
        receiveDetectionResult,
    });

    window.CF_RENDER = {
        initCharts,
        updatePreviewImage,
        renderAll: () => renderAll(window.CF_STATE),
    };

    const bridge = window.CF_BRIDGE;
    bridge.registerMessageHandler("updateStatus", updateStatus);
    bridge.registerMessageHandler("updateResult", (data) => updateResult(Boolean(data?.isOk)));
    bridge.registerMessageHandler("updateConnection", (data) => data && updateConnection(data.type, data.isConnected));
    bridge.registerMessageHandler("log", (data) => data && addLog(data.message, data.type));
    bridge.registerMessageHandler("detectionLog", (data) => data && addDetectionLog(data.message, data.type));
    bridge.registerMessageHandler("inferenceMetrics", updateInferenceMetrics);
    bridge.registerMessageHandler("previewFrame", updatePreviewImage);
    bridge.registerMessageHandler("flashPlcTrigger", flashPlcTrigger);
    bridge.registerMessageHandler("updateCameraName", (data) => updateCameraName(data?.name ?? data));
    bridge.registerMessageHandler("inspectionUpdate", handleInspectionUpdate);
    bridge.registerMessageHandler("healthSnapshot", handleHealthSnapshot);
    bridge.registerMessageHandler("detectionFrame", handleDetectionFrame);
    bridge.registerMessageHandler("uiCommand", handleUiCommand);
})();
