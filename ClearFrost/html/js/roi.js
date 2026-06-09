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

    function initRoiInteractions() {
        roiCanvas = document.getElementById("roi-canvas");
        if (!roiCanvas) return;

        const img = document.getElementById("camera-view");
        const container = document.getElementById("camera-container");
        if (!container) return;

        function updateCanvasLayout() {
            if (!img || !roiCanvas) return;
            const imageWidth = img.naturalWidth || img.width || 1280;
            const imageHeight = img.naturalHeight || img.height || 720;
            if (imageWidth === 0) return;

            const containerRect = container.getBoundingClientRect();
            const containerRatio = containerRect.width / containerRect.height;
            const imageRatio = imageWidth / imageHeight;
            let renderedWidth;
            let renderedHeight;
            let offsetX;
            let offsetY;

            if (containerRatio > imageRatio) {
                renderedHeight = containerRect.height;
                renderedWidth = containerRect.height * imageRatio;
                offsetX = (containerRect.width - renderedWidth) / 2;
                offsetY = 0;
            } else {
                renderedWidth = containerRect.width;
                renderedHeight = containerRect.width / imageRatio;
                offsetX = 0;
                offsetY = (containerRect.height - renderedHeight) / 2;
            }

            roiCanvas.style.width = `${renderedWidth}px`;
            roiCanvas.style.height = `${renderedHeight}px`;
            roiCanvas.style.left = `${offsetX}px`;
            roiCanvas.style.top = `${offsetY}px`;
            roiCanvas.width = renderedWidth;
            roiCanvas.height = renderedHeight;
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
            roiStartX = event.clientX - rect.left;
            roiStartY = event.clientY - rect.top;
        });

        roiCanvas.addEventListener("mousemove", (event) => {
            if (!isDrawingROI || !roiCanvas) return;
            const rect = roiCanvas.getBoundingClientRect();
            const currentX = event.clientX - rect.left;
            const currentY = event.clientY - rect.top;
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
            const currentX = event.clientX - rect.left;
            const currentY = event.clientY - rect.top;
            const x = Math.min(roiStartX, currentX);
            const y = Math.min(roiStartY, currentY);
            const w = Math.abs(currentX - roiStartX);
            const h = Math.abs(currentY - roiStartY);

            if (w < 10 || h < 10) return;
            const normX = x / roiCanvas.width;
            const normY = y / roiCanvas.height;
            const normW = w / roiCanvas.width;
            const normH = h / roiCanvas.height;

            window.sendCommand("update_roi", { rect: [normX, normY, normW, normH] });
            window.addLog?.(`ROI Set: [${normX.toFixed(2)}, ${normY.toFixed(2)}, ${normW.toFixed(2)}, ${normH.toFixed(2)}]`);
            normalizedROIRect = { x: normX, y: normY, w: normW, h: normH };
            currentROIRect = { x, y, w, h };
        });

        roiCanvas.addEventListener("mouseleave", () => {
            isDrawingROI = false;
        });
    }

    function clearRoi() {
        const canvas = document.getElementById("roi-canvas");
        if (canvas) canvas.getContext("2d").clearRect(0, 0, canvas.width, canvas.height);
        currentROIRect = null;
        normalizedROIRect = null;
        window.sendCommand("update_roi", { rect: [0, 0, 0, 0] });
        window.addLog?.("ROI Cleared");
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
    window.setRoi = setRoi;
})();
