// ==========================================
// ClearFrost WebView2 bridge
// ==========================================
(function () {
    "use strict";

    const handlers = window.__CF_MSG_HANDLERS || {};
    let requestSeq = 0;

    function nextRequestId() {
        requestSeq += 1;
        return `cf-${Date.now().toString(36)}-${requestSeq.toString(36)}`;
    }

    function parseMessage(data) {
        if (!data) return null;
        if (typeof data === "string") {
            try {
                return JSON.parse(data);
            } catch (error) {
                console.error("ClearFrost message parse failed:", error);
                return null;
            }
        }
        return data;
    }

    function sendCommand(cmd, value = null) {
        const payload = {
            cmd,
            value,
            requestId: nextRequestId(),
            timestamp: Date.now(),
        };

        if (window.chrome?.webview) {
            window.chrome.webview.postMessage(payload);
            if (window.__CF_DEV_MODE && typeof window.addLog === "function") {
                window.addLog(`CMD: ${cmd}`, "info");
            }
            return payload.requestId;
        }

        console.log("[ClearFrost Dev] Mock command:", payload);
        if (typeof window.addLog === "function") {
            window.addLog(`[Mock] Sent: ${cmd}`, "warning");
        }
        return payload.requestId;
    }

    function registerMessageHandler(type, handler) {
        if (!type || typeof handler !== "function") return;
        handlers[type] = handler;
    }

    function dispatchBackendMessage(raw) {
        const message = parseMessage(raw);
        if (!message || !message.type) return;

        const handler = handlers[message.type];
        if (typeof handler !== "function") {
            if (window.__CF_DEV_MODE) {
                console.debug("[ClearFrost] Unhandled backend message:", message.type, message);
            }
            return;
        }

        try {
            handler(message.data, message);
        } catch (error) {
            console.error(`ClearFrost handler failed: ${message.type}`, error);
            if (typeof window.addLog === "function") {
                window.addLog(`消息处理失败: ${message.type}`, "error");
            }
        }
    }

    if (window.chrome?.webview && !window.__CF_WEBVIEW_MSG_BOUND) {
        window.__CF_WEBVIEW_MSG_BOUND = true;
        window.chrome.webview.addEventListener("message", (event) => {
            dispatchBackendMessage(event.data);
        });
    }

    window.__CF_MSG_HANDLERS = handlers;
    window.CF_BRIDGE = {
        sendCommand,
        registerMessageHandler,
        dispatchBackendMessage,
    };
    window.sendCommand = sendCommand;
})();
