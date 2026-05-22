// ==========================================
// ClearFrost camera management
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const store = window.CF_STORE;
    const { escapeHtml } = window.CF_UTILS;
    let discoveredCameras = [];

    function byId(id) {
        return document.getElementById(id);
    }

    function setCameraForm(camera) {
        if (!camera) return;
        const fields = {
            "cfg-cam-name": camera.displayName || "",
            "cfg-cam-manufacturer": camera.manufacturer || "Huaray",
            "cfg-cam-pixel-format": camera.pixelFormat || "Mono8",
            "cfg-cam-serial": camera.serialNumber || "",
            "cfg-cam-exposure": camera.exposureTime || "",
            "cfg-cam-gain": camera.gain || "",
        };
        Object.entries(fields).forEach(([id, value]) => {
            const input = byId(id);
            if (input) input.value = value;
        });
    }

    function receiveCameraList(data) {
        try {
            const cameras = data?.cameras || data?.Cameras || [];
            const activeId = data?.activeId || data?.ActiveId || data?.activeCameraId || "";
            store.state.cameraList = cameras;
            store.state.activeCameraId = activeId;
            window.cameraList = cameras;
            window.activeCameraId = activeId;

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
        window.activeCameraId = id;
        store.state.activeCameraId = id;

        const camera = (window.cameraList || []).find((item) => item.id === id);
        setCameraForm(camera);
        if (id) bridge.sendCommand("switch_camera", id);
    }

    function addNewCamera() {
        const displayName = byId("cfg-cam-name")?.value || `相机 ${(window.cameraList?.length || 0) + 1}`;
        const manufacturer = byId("cfg-cam-manufacturer")?.value || "Huaray";
        const pixelFormat = byId("cfg-cam-pixel-format")?.value || "Mono8";
        const serialNumber = byId("cfg-cam-serial")?.value || "";
        const exposureTime = parseFloat(byId("cfg-cam-exposure")?.value) || 50000;
        const gain = parseFloat(byId("cfg-cam-gain")?.value) || 1.0;

        if (!serialNumber) {
            alert("请输入相机序列号");
            return;
        }

        bridge.sendCommand("add_camera", {
            displayName,
            manufacturer,
            pixelFormat,
            serialNumber,
            exposureTime,
            gain,
        });
        window.addLog?.(`正在添加/更新相机: ${displayName}...`, "info");
    }

    function deleteCurrentCamera() {
        const select = byId("cfg-cam-select");
        if (!select?.value) return;
        bridge.sendCommand("delete_camera", select.value);
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
        if (results) results.innerHTML = "";
        bridge.sendCommand("search_huaray_cameras");
    }

    const superSearchCameras = searchCamerasHuaray;

    function closeSuperSearchModal() {
        byId("super-search-modal")?.classList.add("hidden");
    }

    function receiveSuperSearchResult(data) {
        const cameras = data?.cameras || data?.Cameras || [];
        discoveredCameras = cameras;
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

    function directConnectCamera(cameraOrSerial, ip, manufacturer, model) {
        const camera = typeof cameraOrSerial === "object"
            ? cameraOrSerial
            : { serialNumber: cameraOrSerial, ip, manufacturer, model };
        bridge.sendCommand("direct_connect_camera", {
            serialNumber: camera.serialNumber || "",
            ip: camera.ip || "",
            manufacturer: camera.manufacturer || "Huaray",
            model: camera.model || camera.userDefinedName || "Camera",
        });
        window.addLog?.(`正在直连相机: ${camera.serialNumber || camera.model || "-"}`, "info");
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
            pixelFormat: byId("cfg-cam-pixel-format")?.value || "Mono8",
            serialNumber: byId("cfg-cam-serial")?.value || "",
            exposureTime: parseFloat(byId("cfg-cam-exposure")?.value) || 50000,
            gain: parseFloat(byId("cfg-cam-gain")?.value) || 1.0,
        };
    }

    function requestCameraPreviewFrame() {
        setCameraPreviewStatus({ isBusy: true, message: "正在打开相机并获取画面..." });
        bridge.sendCommand("capture_camera_preview", collectCameraPreviewPayload());
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
        bridge.sendCommand("super_search_cameras_hik");
    }

    document.addEventListener("click", (event) => {
        const button = event.target.closest("[data-direct-camera-index]");
        if (!button) return;
        const index = Number(button.dataset.directCameraIndex);
        const camera = discoveredCameras[index];
        if (camera) directConnectCamera(camera);
    });

    Object.assign(window, {
        addNewCamera,
        closeSuperSearchModal,
        deleteCurrentCamera,
        directConnectCamera,
        onCameraSelected,
        requestCameraPreviewFrame,
        receiveCameraList,
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
})();
