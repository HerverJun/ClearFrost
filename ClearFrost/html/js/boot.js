// ==========================================
// ClearFrost boot and generic shell actions
// ==========================================
(function () {
    "use strict";

    let windowDragging = false;

    function startDrag(event) {
        if (
            event?.target?.closest?.("button") ||
            event?.target?.closest?.("input") ||
            event?.target?.closest?.(".no-drag")
        ) {
            return;
        }
        windowDragging = true;
        window.sendCommand("start_drag");
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
        return !message || window.confirm(message);
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

        window.sendCommand("manual_release", payload);
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
                window.sendCommand(cmd, value === undefined ? null : value);
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

        document.addEventListener("change", (event) => {
            const commandElement = event.target.closest("[data-change-cmd]");
            if (commandElement) {
                window.sendCommand(commandElement.dataset.changeCmd, commandElement.value);
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

    document.addEventListener("DOMContentLoaded", () => {
        setupDelegatedActions();
        window.moveVisionControlsToSettings?.();
        window.initRoiInteractions?.();
        window.updatePlcAddressUi?.();
        window.updatePlcProtocolModeUi?.();
        window.renderRecentInspections?.();
        window.CF_RENDER?.renderAll?.();
        updateOperatorStatus();
        setTimeout(() => window.sendCommand("app_ready"), 500);
    });

    Object.assign(window, {
        closeManualReleaseModal,
        openManualReleaseModal,
        submitManualRelease,
        startDrag,
        toggleDrawer,
        updateOperatorStatus,
    });
})();
