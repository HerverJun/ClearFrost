// ==========================================
// ClearFrost boot and generic shell actions
// ==========================================
(function () {
    "use strict";

    let windowDragging = false;
    const ShellBridgeErrorMessage = "界面通信失败，请刷新页面后重试";
    const ShellCommandPendingTtlMs = 30000;
    const pendingShellCommandFailures = new Map();

    function sendShellCommand(cmd, value = null, onFailure = null, failureMessage = ShellBridgeErrorMessage) {
        try {
            const requestId = window.sendCommand?.(cmd, value);
            if (!requestId) {
                throw new Error("WebViewBridgeUnavailable");
            }
            registerPendingShellCommandFailure(requestId, cmd, onFailure);
            return requestId;
        } catch (error) {
            console.error(`Shell command failed: ${cmd}`, error);
            window.showToast?.(failureMessage, "error", 1800);
            window.addLog?.(`${failureMessage}: ${cmd}`, "error");
            if (typeof onFailure === "function") onFailure(error);
            return "";
        }
    }

    function registerPendingShellCommandFailure(requestId, cmd, onFailure) {
        const id = String(requestId || "").trim();
        if (!id || typeof onFailure !== "function") return;
        const timeoutId = window.setTimeout?.(() => {
            pendingShellCommandFailures.delete(id);
        }, ShellCommandPendingTtlMs);
        pendingShellCommandFailures.set(id, { cmd, onFailure, timeoutId });
    }

    function takePendingShellCommandFailure(requestId, cmd = "") {
        const id = String(requestId || "").trim();
        if (!id) return null;
        const pending = pendingShellCommandFailures.get(id);
        if (!pending) return null;
        if (cmd && pending.cmd && pending.cmd !== cmd) return null;
        if (pending.timeoutId) window.clearTimeout?.(pending.timeoutId);
        pendingShellCommandFailures.delete(id);
        return pending;
    }

    function getShellCommandErrorDetail(event) {
        const detail = event?.detail || {};
        const data = detail.data || {};
        const envelope = detail.envelope || {};
        return {
            cmd: detail.cmd || data.cmd || data.Cmd || "",
            message: detail.message || data.message || data.Message || ShellBridgeErrorMessage,
            errorCode: detail.errorCode || data.errorCode || data.ErrorCode || "CommandError",
            requestId: String(detail.requestId || envelope.requestId || data.requestId || data.RequestId || "").trim(),
        };
    }

    function handleShellCommandError(event) {
        const { cmd, message, errorCode, requestId } = getShellCommandErrorDetail(event);
        const pending = takePendingShellCommandFailure(requestId, cmd);
        if (!pending || typeof pending.onFailure !== "function") return;

        const error = new Error(message || ShellBridgeErrorMessage);
        error.name = errorCode || "CommandError";
        pending.onFailure(error);
    }

    function getChangeCommandValue(element) {
        if (!element) return "";
        if (element.type === "checkbox") return !!element.checked;
        return element.value ?? "";
    }

    function restoreChangeCommandValue(element, value) {
        if (!element) return;
        if (element.type === "checkbox") {
            element.checked = !!value;
            return;
        }
        element.value = value ?? "";
    }

    function captureChangeCommandBaseline(event) {
        const commandElement = event.target.closest("[data-change-cmd]");
        if (!commandElement) return;
        commandElement.dataset.previousValue = String(getChangeCommandValue(commandElement) ?? "");
        if (commandElement.dataset.confirmedValue === undefined) {
            commandElement.dataset.confirmedValue = commandElement.dataset.previousValue;
        }
    }

    function startDrag(event) {
        if (
            event?.target?.closest?.("button") ||
            event?.target?.closest?.("input") ||
            event?.target?.closest?.(".no-drag")
        ) {
            return;
        }
        windowDragging = true;
        sendShellCommand("start_drag");
    }

    function toggleDrawer(panelId) {
        const panel = document.getElementById(panelId);
        if (!panel) return;
        const isLeft = panelId === "left-panel";
        const isOpen = panel.classList.contains("drawer-open");
        const floatBtn = document.getElementById(isLeft ? "float-btn-left" : "float-btn-right");

        if (isOpen) {
            panel.classList.remove("drawer-open");
            panel.classList.add(isLeft ? "drawer-closed-left" : "drawer-closed-right");
            if (floatBtn) {
                floatBtn.classList.remove("pointer-events-none", "opacity-0");
                floatBtn.classList.add("opacity-100");
            }
            return;
        }

        panel.classList.remove(isLeft ? "drawer-closed-left" : "drawer-closed-right");
        panel.classList.add("drawer-open");
        if (floatBtn) {
            floatBtn.classList.add("pointer-events-none", "opacity-0");
            floatBtn.classList.remove("opacity-100");
        }
    }

    function parseDatasetValue(rawValue) {
        if (rawValue === undefined) return undefined;
        try {
            return JSON.parse(rawValue);
        } catch {
            return rawValue;
        }
    }

    function getElementPayload(element, prefix) {
        if (element.dataset[`${prefix}NoValue`] === "true") return undefined;
        const propName = element.dataset[`${prefix}Prop`];
        if (propName) return element[propName];
        const value = parseDatasetValue(element.dataset[`${prefix}Value`] ?? element.dataset.value);
        return value === undefined ? element.value : value;
    }

    function callWindowAction(actionName, element, payload) {
        const action = window[actionName];
        if (typeof action !== "function") {
            console.warn(`ClearFrost action not found: ${actionName}`);
            return;
        }
        if (element.dataset.passElement === "true") {
            action(element);
            return;
        }
        if (payload === undefined) {
            action();
            return;
        }
        action(payload);
    }

    function confirmIfNeeded(element) {
        const message = element.dataset.confirm;
        if (!message) return true;
        if (typeof window.confirm !== "function") {
            const warning = "当前环境无法显示确认框，操作已取消";
            window.showToast?.(warning, "warning", 2200);
            window.addLog?.(warning, "warning");
            return false;
        }
        return window.confirm(message);
    }

    function getCurrentSettings() {
        return window.CF_STORE?.state?.settings || {};
    }

    function getCurrentInspection() {
        return window.CF_STORE?.state?.inspection || {};
    }

    function getRoleLabel(role) {
        switch (role) {
            case "Engineer":
                return "工程师";
            case "ShiftLead":
                return "班组长";
            default:
                return "操作员";
        }
    }

    function updateOperatorStatus() {
        const settings = getCurrentSettings();
        const operatorId = String(settings.CurrentOperatorId || "").trim() || "本机操作";
        const role = String(settings.CurrentOperatorRole || "Operator");
        const roleLabel = getRoleLabel(role);

        const idNode = document.getElementById("operator-status-id");
        const roleNode = document.getElementById("operator-status-role");
        if (idNode) idNode.textContent = operatorId;
        if (roleNode) roleNode.textContent = roleLabel;
    }

    function openManualReleaseModal() {
        updateOperatorStatus();
        const settings = getCurrentSettings();
        const inspection = getCurrentInspection();
        const modal = document.getElementById("manual-release-modal");
        if (!modal) return;

        const operatorId = String(settings.CurrentOperatorId || "").trim() || "本机操作";
        const role = String(settings.CurrentOperatorRole || "Operator");
        const inspectionId = String(inspection.inspectionId || inspection.InspectionId || "").trim() || "-";
        const requestId = `manual-release-${Date.now().toString(36)}`;

        const setText = (id, value) => {
            const node = document.getElementById(id);
            if (node) node.textContent = value;
        };

        setText("manual-release-operator-id", operatorId);
        setText("manual-release-operator-role", getRoleLabel(role));
        setText("manual-release-inspection-id", inspectionId);
        setText("manual-release-request-id", `请求号: ${requestId}`);
        modal.dataset.requestId = requestId;
        modal.dataset.inspectionId = inspectionId === "-" ? "" : inspectionId;

        const reason = document.getElementById("manual-release-reason");
        const token = document.getElementById("manual-release-token");
        if (reason) reason.value = "";
        if (token) token.value = "";
        modal.classList.remove("hidden");
        window.requestAnimationFrame(() => reason?.focus());
    }

    function closeManualReleaseModal() {
        document.getElementById("manual-release-modal")?.classList.add("hidden");
    }

    function submitManualRelease() {
        const modal = document.getElementById("manual-release-modal");
        if (!modal) return;

        const reason = String(document.getElementById("manual-release-reason")?.value || "").trim();
        const confirmationToken = String(document.getElementById("manual-release-token")?.value || "").trim();
        if (reason.length < 6) {
            window.showToast?.("手动放行原因过短", "error", 1400);
            window.addLog?.("手动放行已取消: 原因不足", "warning");
            return;
        }

        if (!confirmationToken) {
            window.showToast?.("请填写确认令牌", "error", 1400);
            return;
        }

        const payload = {
            requestId: modal.dataset.requestId || `manual-release-${Date.now().toString(36)}`,
            reason,
            confirmationToken,
            inspectionId: modal.dataset.inspectionId || "",
        };

        const requestId = sendShellCommand("manual_release", payload, (error) => {
            modal.classList.remove("hidden");
            window.addLog?.(`强制放行提交失败: ${error?.message || ShellBridgeErrorMessage}`, "error");
            document.getElementById("manual-release-token")?.focus();
        });
        if (!requestId) return;
        window.handleCommandDispatched?.("manual_release", modal);
        closeManualReleaseModal();
    }

    function setupDelegatedActions() {
        document.addEventListener("click", (event) => {
            const commandElement = event.target.closest("[data-cmd]");
            if (commandElement) {
                const cmd = commandElement.dataset.cmd;
                if (!cmd || !confirmIfNeeded(commandElement)) return;
                const value = parseDatasetValue(commandElement.dataset.value);
                const requestId = sendShellCommand(cmd, value === undefined ? null : value);
                if (!requestId) return;
                window.handleCommandDispatched?.(cmd, commandElement);
                return;
            }

            const actionElement = event.target.closest("[data-action]");
            if (actionElement) {
                const actionName = actionElement.dataset.action;
                if (!actionName || !confirmIfNeeded(actionElement)) return;
                callWindowAction(actionName, actionElement, parseDatasetValue(actionElement.dataset.value));
            }
        });

        document.addEventListener("pointerdown", captureChangeCommandBaseline);

        document.addEventListener("focusin", captureChangeCommandBaseline);

        document.addEventListener("change", (event) => {
            const commandElement = event.target.closest("[data-change-cmd]");
            if (commandElement) {
                const cmd = commandElement.dataset.changeCmd;
                const previousValue = commandElement.dataset.confirmedValue ?? commandElement.dataset.previousValue ?? "";
                const nextValue = getChangeCommandValue(commandElement);
                let requestId = "";
                requestId = sendShellCommand(cmd, nextValue, (error) => {
                    if (
                        requestId &&
                        commandElement.dataset.pendingChangeRequestId &&
                        commandElement.dataset.pendingChangeRequestId !== requestId
                    ) {
                        return;
                    }

                    restoreChangeCommandValue(commandElement, previousValue);
                    commandElement.dataset.confirmedValue = String(previousValue ?? "");
                    if (requestId && commandElement.dataset.pendingChangeRequestId === requestId) {
                        delete commandElement.dataset.pendingChangeRequestId;
                        const message = error?.message || ShellBridgeErrorMessage;
                        window.showToast?.(message, "error", 2200);
                        window.addLog?.(message, "error");
                    }
                });
                if (!requestId) return;
                commandElement.dataset.pendingChangeRequestId = requestId;
                commandElement.dataset.confirmedValue = String(nextValue ?? "");
                return;
            }

            const actionElement = event.target.closest("[data-change-action]");
            if (actionElement) {
                callWindowAction(
                    actionElement.dataset.changeAction,
                    actionElement,
                    getElementPayload(actionElement, "change"),
                );
            }
        });

        document.addEventListener("input", (event) => {
            const actionElement = event.target.closest("[data-input-action]");
            if (!actionElement) return;
            callWindowAction(
                actionElement.dataset.inputAction,
                actionElement,
                getElementPayload(actionElement, "input"),
            );
        });

        document.addEventListener("keydown", (event) => {
            const actionElement = event.target.closest("[data-key-action]");
            if (!actionElement) return;
            const expectedKey = actionElement.dataset.key || "Enter";
            if (event.key !== expectedKey) return;
            event.preventDefault();
            callWindowAction(actionElement.dataset.keyAction, actionElement);
        });
    }

    document.addEventListener("mouseup", () => {
        windowDragging = false;
    });

    window.addEventListener("cf-command-error", handleShellCommandError);

    document.addEventListener("DOMContentLoaded", () => {
        setupDelegatedActions();
        window.moveVisionControlsToSettings?.();
        window.initRoiInteractions?.();
        window.updatePlcAddressUi?.();
        window.updatePlcProtocolModeUi?.();
        window.renderRecentInspections?.();
        window.CF_RENDER?.renderAll?.();
        updateOperatorStatus();
        setTimeout(() => sendShellCommand("app_ready"), 500);
    });

    Object.assign(window, {
        closeManualReleaseModal,
        openManualReleaseModal,
        submitManualRelease,
        sendShellCommand,
        startDrag,
        toggleDrawer,
        updateOperatorStatus,
    });
})();
