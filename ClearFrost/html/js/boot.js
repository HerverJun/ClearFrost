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

    function setupDelegatedActions() {
        document.addEventListener("click", (event) => {
            const commandElement = event.target.closest("[data-cmd]");
            if (commandElement) {
                const cmd = commandElement.dataset.cmd;
                if (!cmd || !confirmIfNeeded(commandElement)) return;
                const value = parseDatasetValue(commandElement.dataset.value);
                window.sendCommand(cmd, value === undefined ? null : value);
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
        window.CF_RENDER?.initCharts?.();
        window.CF_RENDER?.renderAll?.();
        setTimeout(() => window.sendCommand("app_ready"), 500);
    });

    Object.assign(window, {
        startDrag,
        toggleDrawer,
    });
})();
