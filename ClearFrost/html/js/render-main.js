// ==========================================
// ClearFrost main screen rendering
// ==========================================
(function () {
    "use strict";

    const { escapeHtml } = window.CF_UTILS;
    const store = window.CF_STORE;
    const domCache = new Map();
    const recentInspectionRows = new Map();
    const logBuffer = [];
    const detectionLogBuffer = [];
    const MaxLogEntries = 50;
    const LogFlushIntervalMs = 300;
    let logFlushTimer = null;
    let detectionLogFlushTimer = null;
    let resultOverlayTimer = null;
    let lastPreviewFrameId = 0;
    let openCameraCooldownUntil = 0;
    let openCameraUnlockTimer = null;
    let openCameraPending = false;
    let exitAppPending = false;
    const FullRenderReasons = new Set(["bootstrap", "state"]);
    const KnownRenderReasons = new Set(["inspection", "stats", "health", "bootstrap", "state"]);

    function shouldRenderFull(reasons) {
        if (!Array.isArray(reasons) || reasons.length === 0) return true;
        return reasons.some((reason) => FullRenderReasons.has(reason) || !KnownRenderReasons.has(reason));
    }

    function hasRenderReason(reasons, reason) {
        return shouldRenderFull(reasons) || reasons.includes(reason);
    }

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

    function renderInspectionContext(state) {
        const inspection = state.inspection || {};
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
            if (container.dataset.cfEmpty !== "true") {
                container.textContent = "";
                recentInspectionRows.clear();
                const empty = document.createElement("div");
                empty.className = "cf-empty-state";
                empty.textContent = "等待第一条检测流水...";
                container.appendChild(empty);
                container.dataset.cfEmpty = "true";
            }
            return;
        }

        container.dataset.cfEmpty = "false";
        const usedNodes = new Set();
        list.forEach((item, index) => {
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
            const key = item._renderKey || item.inspectionId || `${item.time}:${title}:${index}`;
            const signature = [
                statusClass,
                statusText,
                item.time || "",
                identity || "",
                title || "",
                detail || item.currentStage || "-",
            ].join("\u001f");

            let row = recentInspectionRows.get(String(key));
            if (!row) {
                row = createInspectionRow();
                recentInspectionRows.set(String(key), row);
            }
            usedNodes.add(row);

            if (row.dataset.cfSignature !== signature) {
                row.dataset.cfSignature = signature;
                row.className = `cf-flow-row ${statusClass}`;
                row.querySelector(".cf-flow-time").textContent = item.time || "";
                row.querySelector(".cf-flow-status").textContent = statusText;
                const sn = row.querySelector(".cf-flow-sn");
                sn.textContent = identity || "";
                sn.title = title || "";
                const detailNode = row.querySelector(".cf-flow-detail");
                detailNode.textContent = detail || item.currentStage || "-";
                detailNode.title = detail || "";
            }
            row.dataset.cfKey = String(key);

            const current = container.children[index];
            if (current !== row) {
                container.insertBefore(row, current || null);
            }
        });

        Array.from(container.children).forEach((child) => {
            if (!usedNodes.has(child)) {
                if (child.dataset.cfKey) recentInspectionRows.delete(child.dataset.cfKey);
                child.remove();
            }
        });
    }

    function createInspectionRow() {
        const row = document.createElement("div");
        row.className = "cf-flow-row run";

        const rail = document.createElement("span");
        rail.className = "cf-flow-rail";
        row.appendChild(rail);

        const head = document.createElement("div");
        head.className = "cf-flow-head";

        const time = document.createElement("span");
        time.className = "cf-flow-time";
        head.appendChild(time);

        const status = document.createElement("span");
        status.className = "cf-flow-status";
        head.appendChild(status);
        row.appendChild(head);

        const sn = document.createElement("div");
        sn.className = "cf-flow-sn";
        row.appendChild(sn);

        const detail = document.createElement("div");
        detail.className = "cf-flow-detail";
        row.appendChild(detail);

        return row;
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

    }

    function renderHealthSnapshot() {}

    function renderAll(state, reasons = []) {
        if (hasRenderReason(reasons, "inspection")) {
            renderInspectionContext(state);
            renderCameraResult(state);
            renderRecentInspections(state);
        }
        if (hasRenderReason(reasons, "stats")) {
            renderStats(state);
        }
        if (hasRenderReason(reasons, "health")) {
            renderHealthSnapshot(state);
        }
    }

    function flushBufferedLogs(buffer, containerId, classFactory, formatter) {
        if (buffer.length === 0) return;
        const container = el(containerId);
        if (!container) {
            buffer.length = 0;
            return;
        }

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
        if (!el("log-container")) return;
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
        if (!el("detection-log-container")) return;
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

    function initCharts() {}

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
