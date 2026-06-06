// ==========================================
// ClearFrost boot and generic shell actions
// ==========================================
(function () {
    "use strict";

    let windowDragging = false;

    function cleanText(value) {
        return value === undefined || value === null ? "" : String(value).trim();
    }

    function getOperatorSession() {
        return window.CF_STORE?.state?.operatorSession || {};
    }

    function pickSessionText(session, ...keys) {
        for (const key of keys) {
            const value = cleanText(session?.[key]);
            if (value) return value;
        }
        return "";
    }

    function renderOperatorSession(session) {
        const current = session || getOperatorSession();
        const operatorName = pickSessionText(current, "operatorName", "OperatorName") || "未登录";
        const role = pickSessionText(current, "role", "Role", "operatorRole", "OperatorRole") || "Operator";
        const shiftName = pickSessionText(current, "shiftName", "ShiftName") || "班次";
        const chip = document.getElementById("operator-session-chip");
        const name = document.getElementById("operator-chip-name");
        const shift = document.getElementById("operator-chip-shift");
        if (name) name.textContent = operatorName;
        if (shift) shift.textContent = `${shiftName} / ${role}`;
        if (chip) {
            chip.title = `操作员: ${operatorName}\n班次: ${shiftName}\n角色: ${role}`;
            chip.classList.toggle("is-signed-in", Boolean(current.isSignedIn || current.IsSignedIn));
        }
    }

    function setSelectValue(select, value, fallback) {
        if (!select) return;
        const normalized = cleanText(value);
        const hasOption = Array.from(select.options).some((option) => option.value === normalized);
        select.value = hasOption ? normalized : fallback;
    }

    function openOperatorSessionModal() {
        const modal = document.getElementById("operator-session-modal");
        if (!modal) return;

        const current = getOperatorSession();
        const signedIn = Boolean(current.isSignedIn || current.IsSignedIn);
        const operatorName = pickSessionText(current, "operatorName", "OperatorName");
        const role = pickSessionText(current, "role", "Role", "operatorRole", "OperatorRole") || "Operator";
        const shiftName = pickSessionText(current, "shiftName", "ShiftName");
        const nameInput = document.getElementById("operator-session-name");
        const roleSelect = document.getElementById("operator-session-role");
        const shiftSelect = document.getElementById("operator-session-shift");
        const currentLabel = document.getElementById("operator-session-current");
        const signOutButton = document.getElementById("operator-session-signout");

        if (nameInput) {
            nameInput.value = signedIn ? operatorName : "";
        }
        setSelectValue(roleSelect, role, "Operator");
        setSelectValue(shiftSelect, shiftName, "");
        if (currentLabel) {
            currentLabel.textContent = `${operatorName || "未登录"} / ${shiftName || "班次"} / ${role}`;
        }
        if (signOutButton) {
            signOutButton.disabled = !signedIn;
        }

        modal.classList.remove("hidden");
        setTimeout(() => nameInput?.focus?.(), 0);
    }

    function closeOperatorSessionModal() {
        document.getElementById("operator-session-modal")?.classList.add("hidden");
    }

    function signInOperator() {
        openOperatorSessionModal();
    }

    function submitOperatorSessionForm(event) {
        event?.preventDefault?.();
        const operatorName = cleanText(document.getElementById("operator-session-name")?.value);
        if (!operatorName) {
            window.addLog?.("操作员工号/姓名不能为空", "warning");
            document.getElementById("operator-session-name")?.focus?.();
            return;
        }

        window.sendCommand("operator_sign_in", {
            operatorName,
            role: cleanText(document.getElementById("operator-session-role")?.value) || "Operator",
            shiftName: cleanText(document.getElementById("operator-session-shift")?.value),
        });
        closeOperatorSessionModal();
    }

    function signOutOperator() {
        window.sendCommand("operator_sign_out");
        closeOperatorSessionModal();
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

    function collectPromptPayload(element) {
        const message = element.dataset.prompt;
        if (!message) return undefined;

        const raw = window.prompt(message, element.dataset.promptDefault || "");
        if (raw === null) return null;

        const value = raw.trim();
        if (element.dataset.promptRequired === "true" && !value) {
            window.showToast?.("必须填写操作原因", "warning", 1800);
            return null;
        }

        const key = element.dataset.promptKey || "value";
        return { [key]: value };
    }

    function setupDelegatedActions() {
        document.addEventListener("click", (event) => {
            const commandElement = event.target.closest("[data-cmd]");
            if (commandElement) {
                const cmd = commandElement.dataset.cmd;
                if (!cmd || !confirmIfNeeded(commandElement)) return;
                let value = parseDatasetValue(commandElement.dataset.value);
                const promptPayload = collectPromptPayload(commandElement);
                if (promptPayload === null) return;
                if (promptPayload !== undefined) {
                    value = value && typeof value === "object" && !Array.isArray(value)
                        ? { ...value, ...promptPayload }
                        : promptPayload;
                }
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

    function setupOperatorSessionModal() {
        const modal = document.getElementById("operator-session-modal");
        document.getElementById("operator-session-form")?.addEventListener("submit", submitOperatorSessionForm);
        modal?.addEventListener("click", (event) => {
            if (event.target === modal) closeOperatorSessionModal();
        });
        document.addEventListener("keydown", (event) => {
            if (event.key === "Escape" && modal && !modal.classList.contains("hidden")) {
                closeOperatorSessionModal();
            }
        });
    }

    document.addEventListener("mouseup", () => {
        windowDragging = false;
    });

    document.addEventListener("DOMContentLoaded", () => {
        setupDelegatedActions();
        setupOperatorSessionModal();
        window.moveVisionControlsToSettings?.();
        window.initRoiInteractions?.();
        window.updatePlcAddressUi?.();
        window.updatePlcProtocolModeUi?.();
        window.renderRecentInspections?.();
        window.CF_RENDER?.renderAll?.();
        renderOperatorSession();
        setTimeout(() => window.sendCommand("app_ready"), 500);
    });

    window.CF_BRIDGE?.registerMessageHandler?.("operatorSession", (session) => {
        window.CF_STORE?.applyOperatorSession?.(session);
        renderOperatorSession(session);
    });

    Object.assign(window, {
        startDrag,
        toggleDrawer,
        openOperatorSessionModal,
        closeOperatorSessionModal,
        signInOperator,
        signOutOperator,
        submitOperatorSessionForm,
        renderOperatorSession,
    });
})();
