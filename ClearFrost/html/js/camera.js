// ==========================================
// ClearFrost camera management
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const store = window.CF_STORE;
    const { escapeHtml } = window.CF_UTILS;
    let discoveredCameras = [];
    let directConnectPending = null;
    let pendingCameraSwitch = null;
    let pendingCameraSearchRequestId = "";
    let pendingCameraPreviewRequestId = "";
    let pendingCameraMutationRequestId = "";
    let cameraMutationResetTimer = null;
    const CameraBridgeErrorMessage = "相机操作通信失败，请刷新页面后重试";
    const CameraSearchBridgeErrorMessage = "相机搜索通信失败，请刷新页面后重试";
    const CameraPreviewBridgeErrorMessage = "相机预览通信失败，请刷新页面后重试";
    const CameraDirectConnectBridgeErrorMessage = "相机直连通信失败，请刷新页面后重试";
    const CameraMutationPendingTtlMs = 30000;
    const CameraMutationTimeoutMessage = "相机配置操作等待超时，请稍后重试";

    function byId(id) {
        return document.getElementById(id);
    }

    function readFiniteNumberInput(id, fallback) {
        const raw = String(byId(id)?.value ?? "").trim();
        if (!raw) return fallback;

        const value = Number(raw);
        return Number.isFinite(value) ? value : fallback;
    }

    function sendCameraCommand(cmd, value = null, onFailure = null, failureMessage = CameraBridgeErrorMessage) {
        try {
            const requestId = bridge?.sendCommand?.(cmd, value);
            if (!requestId) {
                throw new Error("WebViewBridgeUnavailable");
            }
            return requestId;
        } catch (error) {
            console.error(`Camera command failed: ${cmd}`, error);
            window.showToast?.(failureMessage, "error", 1800);
            if (typeof onFailure === "function") onFailure(error);
            return "";
        }
    }

    function getCameraCommandErrorDetail(event) {
        const detail = event?.detail || {};
        const data = detail.data || {};
        const envelope = detail.envelope || {};
        return {
            cmd: detail.cmd || data.cmd || data.Cmd || "",
            message: detail.message || data.message || data.Message || CameraBridgeErrorMessage,
            requestId: String(detail.requestId || envelope.requestId || data.requestId || data.RequestId || "").trim(),
        };
    }

    function isMatchingCameraRequest(requestId, pendingRequestId) {
        const incoming = String(requestId || "").trim();
        const pending = String(pendingRequestId || "").trim();
        return !pending || !incoming || incoming === pending;
    }

    function setCameraMutationPending(isPending, action = "") {
        if (cameraMutationResetTimer) {
            clearTimeout(cameraMutationResetTimer);
            cameraMutationResetTimer = null;
        }

        document.querySelectorAll('[data-action="addNewCamera"], [data-action="deleteCurrentCamera"]').forEach((button) => {
            button.disabled = Boolean(isPending);
            button.classList.toggle("opacity-70", Boolean(isPending));
            button.classList.toggle("cursor-wait", Boolean(isPending));
            button.dataset.pendingCameraMutation = isPending ? action : "";
            if (isPending) {
                button.setAttribute("aria-busy", "true");
            } else {
                button.removeAttribute("aria-busy");
            }
        });

        if (!isPending) return;

        cameraMutationResetTimer = setTimeout(() => {
            pendingCameraMutationRequestId = "";
            setCameraMutationPending(false);
            window.showToast?.(CameraMutationTimeoutMessage, "warning", 2200);
            window.addLog?.(CameraMutationTimeoutMessage, "warning");
        }, CameraMutationPendingTtlMs);
    }

    function clearCameraMutationPending() {
        pendingCameraMutationRequestId = "";
        setCameraMutationPending(false);
    }

    function handleCameraCommandError(event) {
        const { cmd, message, requestId } = getCameraCommandErrorDetail(event);
        if (!cmd) return;

        if ((cmd === "search_huaray_cameras" || cmd === "super_search_cameras_hik") &&
            isMatchingCameraRequest(requestId, pendingCameraSearchRequestId)) {
            pendingCameraSearchRequestId = "";
            byId("super-search-loading")?.classList.add("hidden");
            setSuperSearchFeedback(message || CameraSearchBridgeErrorMessage, "error");
            return;
        }

        if (cmd === "direct_connect_camera" &&
            isMatchingCameraRequest(requestId, directConnectPending?.requestId || "")) {
            clearDirectConnectButtons(false);
            setSuperSearchFeedback(message || CameraDirectConnectBridgeErrorMessage, "error");
            directConnectPending = null;
            return;
        }

        if (cmd === "capture_camera_preview" &&
            isMatchingCameraRequest(requestId, pendingCameraPreviewRequestId)) {
            pendingCameraPreviewRequestId = "";
            setCameraPreviewStatus({ isBusy: false, message: message || CameraPreviewBridgeErrorMessage, type: "error" });
            return;
        }

        if ((cmd === "add_camera" || cmd === "delete_camera") &&
            isMatchingCameraRequest(requestId, pendingCameraMutationRequestId)) {
            clearCameraMutationPending();
            window.showToast?.(message || CameraBridgeErrorMessage, "error", 2200);
            return;
        }

        if (cmd === "switch_camera" &&
            isMatchingCameraRequest(requestId, pendingCameraSwitch?.requestId || "")) {
            const previousId = pendingCameraSwitch?.previousId || "";
            pendingCameraSwitch = null;
            setCameraSelection(previousId);
            window.showToast?.(message || CameraBridgeErrorMessage, "error", 1800);
        }
    }

    function getSuperSearchFeedback() {
        let feedback = byId("super-search-feedback");
        if (feedback) return feedback;

        const results = byId("super-search-results");
        const empty = byId("super-search-empty");
        const loading = byId("super-search-loading");
        const parent = results?.parentElement || empty?.parentElement || loading?.parentElement;
        if (!parent) return null;

        feedback = document.createElement("div");
        feedback.id = "super-search-feedback";
        feedback.className = "hidden";
        parent.insertBefore(feedback, results || empty || null);
        return feedback;
    }

    function setSuperSearchFeedback(message = "", type = "info") {
        const feedback = getSuperSearchFeedback();
        if (!feedback) return;

        if (!message) {
            feedback.textContent = "";
            feedback.className = "hidden";
            return;
        }

        const palette = {
            success: "bg-emerald-50 border-emerald-200 text-emerald-700",
            error: "bg-red-50 border-red-200 text-red-700",
            warning: "bg-amber-50 border-amber-200 text-amber-700",
            info: "bg-sky-50 border-sky-200 text-sky-700",
        };
        feedback.textContent = message;
        feedback.className = `mb-3 rounded-xl border px-4 py-3 text-sm font-semibold ${palette[type] || palette.info}`;
    }

    function setDirectConnectButtonsPending(index) {
        document.querySelectorAll("[data-direct-camera-index]").forEach((button) => {
            const isTarget = Number(button.dataset.directCameraIndex) === index;
            button.disabled = true;
            button.textContent = isTarget ? "连接中..." : "连接";
            button.classList.toggle("opacity-70", isTarget);
            button.classList.toggle("cursor-wait", isTarget);
            button.classList.toggle("opacity-50", !isTarget);
        });
    }

    function clearDirectConnectButtons(success) {
        document.querySelectorAll("[data-direct-camera-index]").forEach((button) => {
            const isTarget = directConnectPending && Number(button.dataset.directCameraIndex) === directConnectPending.index;
            button.disabled = Boolean(success && isTarget);
            button.textContent = success && isTarget ? "已添加" : "连接";
            button.classList.remove("opacity-70", "cursor-wait", "opacity-50");
            button.classList.toggle("opacity-80", Boolean(success && isTarget));
        });
    }

    function setCameraForm(camera) {
        if (!camera) return;
        const fields = {
            "cfg-cam-name": camera.displayName || "",
            "cfg-cam-manufacturer": camera.manufacturer || "Huaray",
            "cfg-cam-pixel-format": camera.pixelFormat || "Auto",
            "cfg-cam-serial": camera.serialNumber || "",
            "cfg-cam-exposure": camera.exposureTime ?? "",
            "cfg-cam-gain": camera.gain ?? "",
        };
        Object.entries(fields).forEach(([id, value]) => {
            const input = byId(id);
            if (input) input.value = value;
        });
    }

    function findCameraById(id) {
        return (window.cameraList || []).find((item) => item.id === id);
    }

    function setCameraSelection(id) {
        const normalizedId = String(id || "");
        const select = byId("cfg-cam-select");
        if (select) select.value = normalizedId;
        window.activeCameraId = normalizedId;
        store.state.activeCameraId = normalizedId;
        setCameraForm(findCameraById(normalizedId));
    }

    function receiveCameraList(data) {
        try {
            const cameras = data?.cameras || data?.Cameras || [];
            const activeId = data?.activeId || data?.ActiveId || data?.activeCameraId || "";
            store.state.cameraList = cameras;
            store.state.activeCameraId = activeId;
            window.cameraList = cameras;
            window.activeCameraId = activeId;
            pendingCameraSwitch = null;
            clearCameraMutationPending();

            const select = byId("cfg-cam-select");
            if (select) {
                select.innerHTML = "";
                if (!cameras.length) {
                    select.innerHTML = '<option value="">无可用相机</option>';
                } else {
                    cameras.forEach((camera) => {
                        const option = document.createElement("option");
                        option.value = camera.id;
                        option.textContent = camera.displayName || camera.id;
                        if (camera.id === activeId) option.selected = true;
                        select.appendChild(option);
                    });
                }
            }

            const activeCamera = cameras.find((camera) => camera.id === activeId);
            setCameraForm(activeCamera);
        } catch (error) {
            console.error("receiveCameraList error:", error);
        }
    }

    function onCameraSelected(cameraId) {
        const select = byId("cfg-cam-select");
        const id = cameraId || select?.value || "";
        const previousId = window.activeCameraId || store.state.activeCameraId || "";
        setCameraSelection(id);
        if (!id) return;

        pendingCameraSwitch = { previousId, nextId: id, requestId: "" };
        const requestId = sendCameraCommand("switch_camera", id, () => {
            setCameraSelection(previousId);
            pendingCameraSwitch = null;
        });
        if (requestId && pendingCameraSwitch) pendingCameraSwitch.requestId = requestId;
    }

    function addNewCamera() {
        if (pendingCameraMutationRequestId) {
            window.showToast?.("相机配置操作进行中，请稍候", "warning", 1400);
            return;
        }

        const displayName = byId("cfg-cam-name")?.value || `相机 ${(window.cameraList?.length || 0) + 1}`;
        const manufacturer = byId("cfg-cam-manufacturer")?.value || "Huaray";
        const pixelFormat = byId("cfg-cam-pixel-format")?.value || "Auto";
        const serialNumber = byId("cfg-cam-serial")?.value || "";
        const exposureTime = readFiniteNumberInput("cfg-cam-exposure", 50000);
        const gain = readFiniteNumberInput("cfg-cam-gain", 1.0);

        if (!serialNumber) {
            alert("请输入相机序列号");
            return;
        }

        const requestId = sendCameraCommand("add_camera", {
            displayName,
            manufacturer,
            pixelFormat,
            serialNumber,
            exposureTime,
            gain,
        }, () => {
            clearCameraMutationPending();
        });
        if (requestId) {
            pendingCameraMutationRequestId = requestId;
            setCameraMutationPending(true, "add");
            window.addLog?.(`正在添加/更新相机: ${displayName}...`, "info");
        }
    }

    function deleteCurrentCamera() {
        if (pendingCameraMutationRequestId) {
            window.showToast?.("相机配置操作进行中，请稍候", "warning", 1400);
            return;
        }

        const select = byId("cfg-cam-select");
        if (!select?.value) {
            window.showToast?.("请先选择要删除的相机", "warning", 1400);
            return;
        }

        const cameraId = select.value;
        const camera = findCameraById(cameraId);
        const cameraLabel = camera?.displayName || camera?.serialNumber || cameraId;
        if (typeof window.confirm === "function" && !window.confirm(`确定删除相机“${cameraLabel}”？`)) return;

        const requestId = sendCameraCommand("delete_camera", cameraId, () => {
            clearCameraMutationPending();
        });
        if (requestId) {
            pendingCameraMutationRequestId = requestId;
            setCameraMutationPending(true, "delete");
            window.addLog?.(`正在删除相机: ${cameraLabel}...`, "warning");
        }
    }

    function searchCamerasHuaray() {
        const modal = byId("super-search-modal");
        const loading = byId("super-search-loading");
        const results = byId("super-search-results");
        const empty = byId("super-search-empty");
        if (!modal) return;

        modal.classList.remove("hidden");
        loading?.classList.remove("hidden");
        results?.classList.add("hidden");
        empty?.classList.add("hidden");
        setSuperSearchFeedback();
        directConnectPending = null;
        pendingCameraSearchRequestId = "";
        if (results) results.innerHTML = "";
        const requestId = sendCameraCommand("search_huaray_cameras", null, () => {
            pendingCameraSearchRequestId = "";
            loading?.classList.add("hidden");
            setSuperSearchFeedback(CameraSearchBridgeErrorMessage, "error");
        }, CameraSearchBridgeErrorMessage);
        if (requestId) pendingCameraSearchRequestId = requestId;
    }

    const superSearchCameras = searchCamerasHuaray;

    function closeSuperSearchModal() {
        byId("super-search-modal")?.classList.add("hidden");
    }

    function receiveSuperSearchResult(data) {
        const cameras = data?.cameras || data?.Cameras || [];
        discoveredCameras = cameras;
        pendingCameraSearchRequestId = "";
        const loading = byId("super-search-loading");
        const results = byId("super-search-results");
        const empty = byId("super-search-empty");

        loading?.classList.add("hidden");
        if (!cameras.length) {
            empty?.classList.remove("hidden");
            return;
        }

        if (!results) return;
        results.classList.remove("hidden");
        results.innerHTML = cameras.map((camera, index) => `
            <div class="bg-gradient-to-r from-slate-50 to-slate-100 rounded-xl p-4 border border-slate-200 hover:shadow-md transition-all">
                <div class="flex items-center justify-between gap-3">
                    <div class="min-w-0">
                        <div class="flex items-center gap-2 mb-1">
                            <span class="text-sm font-bold text-slate-700 truncate">${escapeHtml(camera.userDefinedName || camera.model || "未命名相机")}</span>
                            <span class="px-2 py-0.5 text-[10px] font-semibold rounded-full bg-indigo-100 text-indigo-600">${escapeHtml(camera.manufacturer || "-")}</span>
                        </div>
                        <div class="text-xs text-slate-500 space-y-0.5">
                            <div><span class="font-medium">序列号:</span> ${escapeHtml(camera.serialNumber || "-")}</div>
                            <div><span class="font-medium">IP:</span> ${escapeHtml(camera.ip || "-")}</div>
                        </div>
                    </div>
                    <button class="px-3 py-1.5 text-xs font-bold rounded-lg bg-celadon-600 text-white hover:bg-celadon-700"
                        data-direct-camera-index="${index}">连接</button>
                </div>
            </div>
        `).join("");
    }

    function directConnectCamera(cameraOrSerial, ip, manufacturer, model, index = null) {
        const camera = typeof cameraOrSerial === "object"
            ? cameraOrSerial
            : { serialNumber: cameraOrSerial, ip, manufacturer, model };
        const pendingIndex = Number.isInteger(index)
            ? index
            : discoveredCameras.findIndex((item) => item?.serialNumber && item.serialNumber === camera.serialNumber);
        const cameraLabel = camera.serialNumber || camera.model || camera.userDefinedName || "-";
        directConnectPending = {
            index: pendingIndex,
            serialNumber: camera.serialNumber || "",
        };
        if (pendingIndex >= 0) setDirectConnectButtonsPending(pendingIndex);
        setSuperSearchFeedback(`正在添加相机 ${cameraLabel}，请稍候...`, "info");
        window.showToast?.("正在添加相机配置...", "info", 1200);

        const requestId = sendCameraCommand("direct_connect_camera", {
            serialNumber: camera.serialNumber || "",
            ip: camera.ip || "",
            manufacturer: camera.manufacturer || "Huaray",
            model: camera.model || camera.userDefinedName || "Camera",
        }, () => {
            clearDirectConnectButtons(false);
            setSuperSearchFeedback(CameraDirectConnectBridgeErrorMessage, "error");
            directConnectPending = null;
        }, CameraDirectConnectBridgeErrorMessage);
        if (requestId && directConnectPending) directConnectPending.requestId = requestId;
        if (requestId) window.addLog?.(`正在直连相机: ${camera.serialNumber || camera.model || "-"}`, "info");
    }

    function receiveCameraDirectConnectResult(data) {
        const success = Boolean(data?.success ?? data?.Success);
        const message = data?.message || data?.Message || (success ? "相机已添加" : "相机连接失败");
        clearDirectConnectButtons(success);
        setSuperSearchFeedback(message, success ? "success" : "error");
        window.showToast?.(message, success ? "success" : "error", success ? 1800 : 2600);
        directConnectPending = null;
    }

    function setCameraPreviewStatus({ isBusy = false, message = "", type = "info" } = {}) {
        const button = byId("btn-camera-preview-frame");
        const status = byId("camera-preview-status");
        const box = status?.closest(".cf-camera-preview-box");
        const hasFrame = Boolean(byId("camera-preview-image")?.src);

        if (button) {
            button.disabled = Boolean(isBusy);
            button.textContent = isBusy ? "获取中..." : "获取单帧";
            button.classList.toggle("opacity-70", Boolean(isBusy));
            button.classList.toggle("cursor-wait", Boolean(isBusy));
        }

        if (status && message) {
            status.textContent = message;
            status.classList.toggle("text-red-600", type === "error");
        }

        if (box && !hasFrame) {
            box.classList.remove("has-frame");
        }
    }

    function collectCameraPreviewPayload() {
        return {
            cameraId: byId("cfg-cam-select")?.value || window.activeCameraId || "",
            displayName: byId("cfg-cam-name")?.value || "",
            manufacturer: byId("cfg-cam-manufacturer")?.value || "Huaray",
            pixelFormat: byId("cfg-cam-pixel-format")?.value || "Auto",
            serialNumber: byId("cfg-cam-serial")?.value || "",
            exposureTime: readFiniteNumberInput("cfg-cam-exposure", 50000),
            gain: readFiniteNumberInput("cfg-cam-gain", 1.0),
        };
    }

    function requestCameraPreviewFrame() {
        setCameraPreviewStatus({ isBusy: true, message: "正在连接相机并获取画面..." });
        pendingCameraPreviewRequestId = "";
        const requestId = sendCameraCommand("capture_camera_preview", collectCameraPreviewPayload(), () => {
            pendingCameraPreviewRequestId = "";
            setCameraPreviewStatus({ isBusy: false, message: CameraPreviewBridgeErrorMessage, type: "error" });
        }, CameraPreviewBridgeErrorMessage);
        if (requestId) pendingCameraPreviewRequestId = requestId;
    }

    function receiveCameraPreviewFrame(data) {
        const image = byId("camera-preview-image");
        const status = byId("camera-preview-status");
        const box = image?.closest(".cf-camera-preview-box");
        if (!image) return;

        const base64 = data?.base64 || data?.Base64 || "";
        const url = data?.url || data?.Url || "";
        const src = url || (base64 ? (String(base64).startsWith("data:image") ? base64 : `data:image/jpeg;base64,${base64}`) : "");
        if (!src) return;
        pendingCameraPreviewRequestId = "";

        image.onload = () => {
            image.classList.remove("hidden");
            box?.classList.add("has-frame");
            if (status) status.textContent = "预览已更新";
            setCameraPreviewStatus({ isBusy: false });
        };
        image.onerror = () => {
            setCameraPreviewStatus({ isBusy: false, message: "预览画面加载失败", type: "error" });
        };
        image.src = src;
    }

    function searchCamerasHik() {
        const modal = byId("super-search-modal");
        const results = byId("super-search-results");
        const empty = byId("super-search-empty");
        const loading = byId("super-search-loading");
        modal?.classList.remove("hidden");
        if (results) {
            results.innerHTML = "";
            results.classList.add("hidden");
        }
        empty?.classList.add("hidden");
        loading?.classList.remove("hidden");
        setSuperSearchFeedback();
        pendingCameraSearchRequestId = "";
        const requestId = sendCameraCommand("super_search_cameras_hik", null, () => {
            pendingCameraSearchRequestId = "";
            loading?.classList.add("hidden");
            setSuperSearchFeedback(CameraSearchBridgeErrorMessage, "error");
        }, CameraSearchBridgeErrorMessage);
        if (requestId) pendingCameraSearchRequestId = requestId;
    }

    document.addEventListener("click", (event) => {
        const button = event.target.closest("[data-direct-camera-index]");
        if (!button) return;
        const index = Number(button.dataset.directCameraIndex);
        const camera = discoveredCameras[index];
        if (camera) directConnectCamera(camera, undefined, undefined, undefined, index);
    });

    Object.assign(window, {
        addNewCamera,
        closeSuperSearchModal,
        deleteCurrentCamera,
        directConnectCamera,
        onCameraSelected,
        requestCameraPreviewFrame,
        receiveCameraList,
        receiveCameraDirectConnectResult,
        receiveCameraPreviewFrame,
        receiveSuperSearchResult,
        searchCamerasHuaray,
        searchCamerasHik,
        setCameraPreviewStatus,
        superSearchCameras,
    });

    bridge.registerMessageHandler("cameraList", receiveCameraList);
    bridge.registerMessageHandler("cameraPreviewFrame", receiveCameraPreviewFrame);
    bridge.registerMessageHandler("discoveredCameras", receiveSuperSearchResult);
    window.addEventListener("cf-command-error", handleCameraCommandError);
})();
