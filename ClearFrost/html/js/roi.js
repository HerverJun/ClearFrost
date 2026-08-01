// ==========================================
// ClearFrost ROI canvas interactions
// ==========================================
(function () {
    "use strict";

    let roiCanvas = null;
    let isDrawingROI = false;
    let roiStartX = 0;
    let roiStartY = 0;
    let currentROIRect = null;
    let normalizedROIRect = null;
    const RoiBridgeErrorMessage = "ROI 通信失败，请刷新页面后重试";
    const RoiCommandPendingTtlMs = 30000;
    const pendingRoiCommands = new Map();

    function sendRoiCommand(rect, onSuccess, onFailure = null) {
        try {
            const requestId = window.sendCommand?.("update_roi", { rect });
            if (!requestId) {
                throw new Error("WebViewBridgeUnavailable");
            }
            registerPendingRoiCommand(requestId, onFailure);
            if (typeof onSuccess === "function") onSuccess(requestId);
            return requestId;
        } catch (error) {
            console.error("ROI command failed:", error);
            window.showToast?.(RoiBridgeErrorMessage, "error", 1800);
            window.addLog?.(RoiBridgeErrorMessage, "error");
            if (typeof onFailure === "function") onFailure(RoiBridgeErrorMessage);
            return "";
        }
    }

    function registerPendingRoiCommand(requestId, onFailure) {
        const id = String(requestId || "").trim();
        if (!id || typeof onFailure !== "function") return;
        const timeoutId = window.setTimeout?.(() => {
            pendingRoiCommands.delete(id);
        }, RoiCommandPendingTtlMs);
        pendingRoiCommands.set(id, { onFailure, timeoutId });
    }

    function takePendingRoiCommand(requestId) {
        const id = String(requestId || "").trim();
        if (!id) return null;
        const pending = pendingRoiCommands.get(id);
        if (!pending) return null;
        if (pending.timeoutId) window.clearTimeout?.(pending.timeoutId);
        pendingRoiCommands.delete(id);
        return pending;
    }

    function getRoiCommandErrorDetail(event) {
        const detail = event?.detail || {};
        const data = detail.data || {};
        const envelope = detail.envelope || {};
        return {
            cmd: detail.cmd || data.cmd || data.Cmd || "",
            message: detail.message || data.message || data.Message || RoiBridgeErrorMessage,
            requestId: String(detail.requestId || envelope.requestId || data.requestId || data.RequestId || "").trim(),
        };
    }

    function cloneNormalizedRoiRect() {
        return normalizedROIRect
            ? { x: normalizedROIRect.x, y: normalizedROIRect.y, w: normalizedROIRect.w, h: normalizedROIRect.h }
            : null;
    }

    function restoreNormalizedRoiRect(rect) {
        normalizedROIRect = rect
            ? { x: rect.x, y: rect.y, w: rect.w, h: rect.h }
            : null;
        currentROIRect = null;
        redrawROI();
    }

    function handleRoiCommandError(event) {
        const { cmd, message, requestId } = getRoiCommandErrorDetail(event);
        if (cmd !== "update_roi") return;
        const pending = takePendingRoiCommand(requestId);
        if (!pending || typeof pending.onFailure !== "function") return;
        pending.onFailure(message || RoiBridgeErrorMessage);
    }

    function initRoiInteractions() {
        roiCanvas = document.getElementById("roi-canvas");
        if (!roiCanvas) return;

        const img = document.getElementById("camera-view");
        const container = document.getElementById("camera-container");
        if (!container) return;

        function updateCanvasLayout() {
            if (!img || !roiCanvas) return;
            const previewFrame = window.CF_STATE?.previewFrame || {};
            const containerRect = container.getBoundingClientRect();
            const mapping = window.CF_COORDINATE_MAPPING?.calculateImageContentMapping({
                containerWidth: containerRect.width,
                containerHeight: containerRect.height,
                previewWidth: Number(previewFrame.previewWidth || img.naturalWidth || img.width || 1280),
                previewHeight: Number(previewFrame.previewHeight || img.naturalHeight || img.height || 720),
                sourceWidth: Number(previewFrame.sourceWidth || img.naturalWidth || img.width || 1280),
                sourceHeight: Number(previewFrame.sourceHeight || img.naturalHeight || img.height || 720),
            });
            if (!mapping?.valid) return;

            const imageRect = mapping.imageRect;
            roiCanvas.style.width = `${imageRect.width}px`;
            roiCanvas.style.height = `${imageRect.height}px`;
            roiCanvas.style.left = `${imageRect.x}px`;
            roiCanvas.style.top = `${imageRect.y}px`;
            roiCanvas.style.right = "auto";
            roiCanvas.style.bottom = "auto";
            roiCanvas.width = Math.max(1, Math.round(imageRect.width));
            roiCanvas.height = Math.max(1, Math.round(imageRect.height));
            redrawROI();
        }

        const resizeObserver = new ResizeObserver(() => requestAnimationFrame(updateCanvasLayout));
        resizeObserver.observe(container);
        img?.addEventListener("load", updateCanvasLayout);
        window.addEventListener("resize", updateCanvasLayout);
        setTimeout(updateCanvasLayout, 100);

        roiCanvas.addEventListener("mousedown", (event) => {
            isDrawingROI = true;
            const rect = roiCanvas.getBoundingClientRect();
            roiStartX = (event.clientX - rect.left) * (roiCanvas.width / Math.max(1, rect.width));
            roiStartY = (event.clientY - rect.top) * (roiCanvas.height / Math.max(1, rect.height));
        });

        roiCanvas.addEventListener("mousemove", (event) => {
            if (!isDrawingROI || !roiCanvas) return;
            const rect = roiCanvas.getBoundingClientRect();
            const currentX = (event.clientX - rect.left) * (roiCanvas.width / Math.max(1, rect.width));
            const currentY = (event.clientY - rect.top) * (roiCanvas.height / Math.max(1, rect.height));
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

        roiCanvas.addEventListener("mouseup", (event) => {
            if (!isDrawingROI || !roiCanvas) return;
            isDrawingROI = false;
            const rect = roiCanvas.getBoundingClientRect();
            const currentX = (event.clientX - rect.left) * (roiCanvas.width / Math.max(1, rect.width));
            const currentY = (event.clientY - rect.top) * (roiCanvas.height / Math.max(1, rect.height));
            const x = Math.min(roiStartX, currentX);
            const y = Math.min(roiStartY, currentY);
            const w = Math.abs(currentX - roiStartX);
            const h = Math.abs(currentY - roiStartY);

            if (w < 10 || h < 10) return;
            const normX = x / roiCanvas.width;
            const normY = y / roiCanvas.height;
            const normW = w / roiCanvas.width;
            const normH = h / roiCanvas.height;

            const previousRect = cloneNormalizedRoiRect();
            const requestId = sendRoiCommand([normX, normY, normW, normH], () => {
                window.addLog?.(`ROI Set: [${normX.toFixed(2)}, ${normY.toFixed(2)}, ${normW.toFixed(2)}, ${normH.toFixed(2)}]`);
            }, (message) => {
                restoreNormalizedRoiRect(previousRect);
                window.addLog?.(`ROI 设置失败: ${message || RoiBridgeErrorMessage}`, "error");
            });
            if (!requestId) return;
            normalizedROIRect = { x: normX, y: normY, w: normW, h: normH };
            currentROIRect = { x, y, w, h };
        });

        roiCanvas.addEventListener("mouseleave", () => {
            isDrawingROI = false;
        });
    }

    function clearRoi() {
        const canvas = document.getElementById("roi-canvas");
        const previousRect = cloneNormalizedRoiRect();
        if (canvas) canvas.getContext("2d").clearRect(0, 0, canvas.width, canvas.height);
        currentROIRect = null;
        normalizedROIRect = null;
        sendRoiCommand([0, 0, 0, 0], () => {
            window.addLog?.("ROI Cleared");
        }, (message) => {
            restoreNormalizedRoiRect(previousRect);
            window.addLog?.(`ROI 清除失败: ${message || RoiBridgeErrorMessage}`, "error");
        });
    }

    function redrawROI() {
        if (!roiCanvas) return;
        const ctx = roiCanvas.getContext("2d");
        ctx.clearRect(0, 0, roiCanvas.width, roiCanvas.height);
        if (normalizedROIRect) {
            currentROIRect = {
                x: normalizedROIRect.x * roiCanvas.width,
                y: normalizedROIRect.y * roiCanvas.height,
                w: normalizedROIRect.w * roiCanvas.width,
                h: normalizedROIRect.h * roiCanvas.height,
            };
        }
        if (!currentROIRect) return;
        ctx.strokeStyle = "#a4161a";
        ctx.lineWidth = 2;
        ctx.setLineDash([8, 4]);
        ctx.strokeRect(currentROIRect.x, currentROIRect.y, currentROIRect.w, currentROIRect.h);
        ctx.fillStyle = "rgba(164, 22, 26, 0.05)";
        ctx.fillRect(currentROIRect.x, currentROIRect.y, currentROIRect.w, currentROIRect.h);
    }

    function setRoi(rect) {
        if (!Array.isArray(rect) || rect.length !== 4) {
            normalizedROIRect = null;
            currentROIRect = null;
            redrawROI();
            return;
        }

        const x = Math.max(0, Math.min(1, Number(rect[0]) || 0));
        const y = Math.max(0, Math.min(1, Number(rect[1]) || 0));
        const w = Math.max(0, Math.min(1 - x, Number(rect[2]) || 0));
        const h = Math.max(0, Math.min(1 - y, Number(rect[3]) || 0));
        normalizedROIRect = w > 0.001 && h > 0.001 ? { x, y, w, h } : null;
        currentROIRect = null;
        redrawROI();
    }

    window.clearRoi = clearRoi;
    window.initRoiInteractions = initRoiInteractions;
    window.redrawROI = redrawROI;
    window.sendRoiCommand = sendRoiCommand;
    window.setRoi = setRoi;
    window.addEventListener("cf-command-error", handleRoiCommandError);
})();
