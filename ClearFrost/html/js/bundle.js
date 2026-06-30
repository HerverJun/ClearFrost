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

    function formatDevPayload(payload) {
        try {
            return JSON.stringify(payload);
        } catch {
            return payload?.cmd || "";
        }
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

        console.log(`[ClearFrost Dev] Mock command: ${formatDevPayload(payload)}`);
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

// ==========================================
// ClearFrost front-end state store
// ==========================================
(function () {
    "use strict";

    const MaxRecentInspections = 15;
    const subscribers = new Set();
    const pendingReasons = new Set();
    let renderScheduled = false;

    const state = window.CF_STATE || {
        stats: { total: 0, ok: 0, ng: 0 },
        inspection: {},
        health: {},
        recentInspections: [],
        settings: {},
        modelList: [],
        modelLabels: [],
        cameraList: [],
        activeCameraId: "",
        history: {
            dates: [],
            hours: [],
            images: [],
            detectionLogs: [],
            statistics: [],
        },
        metrics: {},
        previewFrameId: 0,
    };

    state.stats = state.stats || { total: 0, ok: 0, ng: 0 };
    state.inspection = state.inspection || {};
    state.health = state.health || {};
    state.recentInspections = state.recentInspections || [];
    state.history = state.history || {};
    window.CF_STATE = state;

    function pickValue(source, ...keys) {
        if (!source) return undefined;
        for (const key of keys) {
            if (Object.prototype.hasOwnProperty.call(source, key) && source[key] !== undefined && source[key] !== null) {
                return source[key];
            }
        }
        return undefined;
    }

    function escapeHtml(value) {
        return String(value ?? "")
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#39;");
    }

    const ErrorAdviceMap = Object.freeze({
        CaptureFrameFailed: "检查相机连接/曝光/触发线",
        NoBarcode: "检查 PLC 条码地址或扫码枪",
        BarcodeReadFailed: "检查 PLC 通讯、条码地址或扫码枪",
        PlcNotConnected: "检查 PLC 网络/IP/端口及通讯线",
        PlcWriteFailed: "检查 PLC 结果地址、写入权限或握手时序",
        PlcWriteException: "检查 PLC 通讯、地址配置或驱动状态",
        DetectionServiceError: "检查模型文件、GPU 推理环境或输入图像",
        DetectionCycleException: "检查检测规则、ROI、模型配置或运行日志",
        UnhandledDetectionException: "查看系统日志并联系维护人员",
    });

    const StageFallbackAdviceMap = Object.freeze({
        barcode: "检查 PLC 条码地址或扫码枪",
        capture: "检查相机连接/曝光/触发线",
        inference: "检查模型文件、GPU 推理环境或输入图像",
        roifilter: "检查 ROI 和检测规则配置",
        plcwrite: "检查 PLC 通讯、结果地址或握手时序",
        saveimage: "检查图像保存目录和磁盘空间",
        saverecord: "检查数据库文件和存储目录权限",
    });

    function cleanText(value) {
        return value === undefined || value === null ? "" : String(value).trim();
    }

    function getMappedAdvice(errorCode) {
        const normalizedCode = cleanText(errorCode);
        if (!normalizedCode) return { code: "", advice: "" };
        if (ErrorAdviceMap[normalizedCode]) {
            return { code: normalizedCode, advice: ErrorAdviceMap[normalizedCode] };
        }

        const lowerCode = normalizedCode.toLowerCase();
        const mappedCode = Object.keys(ErrorAdviceMap).find((key) => key.toLowerCase() === lowerCode);
        return mappedCode
            ? { code: mappedCode, advice: ErrorAdviceMap[mappedCode] }
            : { code: normalizedCode, advice: "" };
    }

    function resolveErrorAdvice(source) {
        const data = source?.inspection || source || {};
        const mapped = getMappedAdvice(
            cleanText(pickValue(data, "errorCode", "ErrorCode")) ||
            cleanText(pickValue(data, "barcodeError", "BarcodeError")),
        );
        const errorStage = cleanText(pickValue(data, "errorStage", "ErrorStage"));
        const stageAdvice = StageFallbackAdviceMap[errorStage.toLowerCase()] || "";
        return {
            code: mapped.code,
            stage: errorStage,
            message: cleanText(pickValue(data, "errorMessage", "ErrorMessage", "message", "Message")),
            advice: mapped.advice || stageAdvice,
        };
    }

    function formatErrorAdvice(source, options = {}) {
        const resolved = resolveErrorAdvice(source);
        if (!resolved.advice) return "";
        const prefix = options.prefix ?? "处理建议";
        const suffix = options.includeCode === false || !resolved.code ? "" : ` [${resolved.code}]`;
        return `${prefix}: ${resolved.advice}${suffix}`;
    }

    function normalizeHealthLevel(value) {
        if (value === 0 || value === "0" || value === "Ok") return "Ok";
        if (value === 1 || value === "1" || value === "Warning") return "Warning";
        if (value === 2 || value === "2" || value === "Critical") return "Critical";
        return value || "Unknown";
    }

    function normalizeInspection(payload) {
        const data = payload?.inspection || payload || {};
        return {
            inspectionId: pickValue(data, "inspectionId", "InspectionId"),
            triggerSource: pickValue(data, "triggerSource", "TriggerSource"),
            sourceLabel: pickValue(data, "sourceLabel", "SourceLabel"),
            triggerSeq: pickValue(data, "triggerSeq", "TriggerSeq"),
            resultSeq: pickValue(data, "resultSeq", "ResultSeq"),
            productBarcode: pickValue(data, "productBarcode", "ProductBarcode"),
            barcodeEnabled: pickValue(data, "barcodeEnabled", "BarcodeEnabled"),
            barcodeReadSucceeded: pickValue(data, "barcodeReadSucceeded", "BarcodeReadSucceeded"),
            barcodeError: pickValue(data, "barcodeError", "BarcodeError"),
            traceStatus: pickValue(data, "traceStatus", "TraceStatus"),
            currentStage: pickValue(data, "currentStage", "CurrentStage"),
            errorStage: pickValue(data, "errorStage", "ErrorStage"),
            errorCode: pickValue(data, "errorCode", "ErrorCode"),
            errorMessage: pickValue(data, "errorMessage", "ErrorMessage"),
            totalMs: pickValue(data, "totalMs", "TotalMs"),
            captureMs: pickValue(data, "captureMs", "CaptureMs"),
            inferenceMs: pickValue(data, "inferenceMs", "InferenceMs"),
            plcWriteMs: pickValue(data, "plcWriteMs", "PlcWriteMs"),
            handshakeStartMs: pickValue(data, "handshakeStartMs", "HandshakeStartMs"),
            plcResultWriteMs: pickValue(data, "plcResultWriteMs", "PlcResultWriteMs"),
            handshakeCompleteMs: pickValue(data, "handshakeCompleteMs", "HandshakeCompleteMs"),
            usedModelName: pickValue(data, "usedModelName", "UsedModelName"),
            wasFallback: pickValue(data, "wasFallback", "WasFallback"),
            fallbackAttemptCount: pickValue(data, "fallbackAttemptCount", "FallbackAttemptCount"),
            fallbackSkippedReason: pickValue(data, "fallbackSkippedReason", "FallbackSkippedReason"),
            imageQueuePending: pickValue(data, "imageQueuePending", "ImageQueuePending"),
            recordQueuePending: pickValue(data, "recordQueuePending", "RecordQueuePending"),
            actualCount: pickValue(data, "actualCount", "ActualCount", "targetCount"),
            isOk: pickValue(data, "isOk", "IsOk", "isQualified", "IsQualified"),
            message: pickValue(data, "message", "Message"),
            ruleSummary: pickValue(data, "ruleSummary", "RuleSummary"),
            rulePrimaryReason: pickValue(data, "rulePrimaryReason", "RulePrimaryReason"),
            ruleDetails: pickValue(data, "ruleDetails", "RuleDetails"),
        };
    }

    function notify(reason) {
        pendingReasons.add(reason || "state");
        if (renderScheduled) return;

        renderScheduled = true;
        window.requestAnimationFrame(() => {
            renderScheduled = false;
            const reasons = Array.from(pendingReasons);
            pendingReasons.clear();
            subscribers.forEach((subscriber) => subscriber(state, reasons));
        });
    }

    function subscribe(subscriber) {
        if (typeof subscriber !== "function") return () => {};
        subscribers.add(subscriber);
        return () => subscribers.delete(subscriber);
    }

    function rememberInspection(inspection) {
        if (!inspection.inspectionId && inspection.isOk === undefined) return;
        const list = state.recentInspections;
        const existingIndex = inspection.inspectionId ? list.findIndex((x) => x.inspectionId === inspection.inspectionId) : -1;
        const existing = existingIndex >= 0 ? list[existingIndex] : null;
        const item = {
            _renderKey: existing?._renderKey || (inspection.inspectionId ? `id:${inspection.inspectionId}` : `local:${Date.now()}:${Math.random().toString(36).slice(2, 8)}`),
            time: new Date().toLocaleTimeString(),
            ...inspection,
        };
        if (existingIndex >= 0) {
            list.splice(existingIndex, 1);
        }
        list.unshift(item);
        while (list.length > MaxRecentInspections) list.pop();
    }

    function applyInspectionUpdate(payload) {
        const incoming = normalizeInspection(payload);
        const cleanIncoming = Object.fromEntries(
            Object.entries(incoming).filter(([, value]) => value !== undefined),
        );
        const previous = state.inspection || {};
        const isNewInspection = cleanIncoming.inspectionId && cleanIncoming.inspectionId !== previous.inspectionId;
        const isStandaloneSource = cleanIncoming.sourceLabel && !cleanIncoming.inspectionId;
        const base = isNewInspection || isStandaloneSource ? {} : previous;

        state.inspection = { ...base, ...cleanIncoming };

        if (cleanIncoming.isOk !== undefined || cleanIncoming.currentStage || cleanIncoming.inspectionId) {
            rememberInspection(state.inspection);
        }

        notify("inspection");
    }

    function applyStatsUpdate(payload) {
        const data = typeof payload === "string" ? JSON.parse(payload) : (payload || {});
        if (data.total !== undefined) state.stats.total = data.total;
        if (data.ok !== undefined) state.stats.ok = data.ok;
        if (data.ng !== undefined) state.stats.ng = data.ng;
        notify("stats");
    }

    function applyHealthSnapshot(snapshot) {
        if (!snapshot) return;
        state.health = snapshot;
        notify("health");
    }

    function applyBootstrapSnapshot(snapshot) {
        if (!snapshot) return;

        const stats = snapshot.stats || snapshot.Stats;
        const health = snapshot.health || snapshot.Health;
        const config = snapshot.config || snapshot.Config;
        const models = snapshot.models || snapshot.Models;
        const modelLabels = snapshot.modelLabels || snapshot.ModelLabels;
        const cameras = snapshot.cameras || snapshot.Cameras;
        const activeCameraId = pickValue(snapshot, "activeCameraId", "ActiveCameraId");
        const storagePath = pickValue(snapshot, "storagePath", "StoragePath");

        if (config) state.settings = config;
        if (Array.isArray(models)) state.modelList = models;
        if (Array.isArray(modelLabels)) state.modelLabels = modelLabels;
        if (Array.isArray(cameras)) state.cameraList = cameras;
        if (activeCameraId !== undefined) state.activeCameraId = activeCameraId || "";
        if (storagePath !== undefined) state.storagePath = storagePath || "";
        if (stats) {
            state.stats.total = pickValue(stats, "total", "Total", "totalCount", "TotalCount") ?? state.stats.total;
            state.stats.ok = pickValue(stats, "ok", "Ok", "qualifiedCount", "QualifiedCount") ?? state.stats.ok;
            state.stats.ng = pickValue(stats, "ng", "Ng", "unqualifiedCount", "UnqualifiedCount") ?? state.stats.ng;
        }
        if (health) state.health = health;

        notify("bootstrap");
    }

    window.CF_UTILS = {
        pickValue,
        escapeHtml,
        normalizeHealthLevel,
        normalizeInspection,
    };

    window.CF_ERROR_ADVICE = {
        map: ErrorAdviceMap,
        resolve: resolveErrorAdvice,
        format: formatErrorAdvice,
    };

    window.CF_STORE = {
        state,
        subscribe,
        notify,
        applyInspectionUpdate,
        applyStatsUpdate,
        applyHealthSnapshot,
        applyBootstrapSnapshot,
    };
})();

// ==========================================
// ClearFrost main screen rendering
// ==========================================
(function () {
    "use strict";

    const { escapeHtml } = window.CF_UTILS;
    const store = window.CF_STORE;
    const errorAdvice = window.CF_ERROR_ADVICE;
    const domCache = new Map();
    const recentInspectionRows = new Map();
    const criticalAdviceLogKeys = new Set();
    const fallbackTelemetryLogKeys = new Set();
    const logBuffer = [];
    const detectionLogBuffer = [];
    const MaxLogEntries = 50;
    const MaxCriticalAdviceLogKeys = 120;
    const MaxFallbackTelemetryLogKeys = 120;
    const LogFlushIntervalMs = 300;
    let logFlushTimer = null;
    let detectionLogFlushTimer = null;
    let lastQueueAdviceKey = "";
    let resultOverlayTimer = null;
    let lastPreviewFrameId = 0;
    let openCameraCooldownUntil = 0;
    let openCameraUnlockTimer = null;
    let openCameraPending = false;
    let systemRunning = false;
    let systemBusy = false;
    let exitAppPending = false;
    let plcTriggerResetTimer = null;
    const FullRenderReasons = new Set(["bootstrap", "state"]);
    const KnownRenderReasons = new Set(["inspection", "stats", "health", "bootstrap", "state"]);
    const KeyLogPatterns = [
        /PLC/i,
        /Plc/i,
        /相机/,
        /Camera/i,
        /连接/,
        /断开/,
        /未连接/,
        /启动系统/,
        /停止检测/,
        /检测已/,
        /手动检测/,
        /强制放行/,
        /开启成功/,
        /开启异常/,
        /驱动缺失/,
        /启动诊断/,
        /队列/,
    ];

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
        if (!dot || (dot.dataset.cfState === state && dot.classList.contains(state))) return;
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

    function isFailedInspection(item) {
        return item?.isOk === false || item?.currentStage === "Failed";
    }

    function getInspectionAdvice(item, prefix = "处理建议", includeCode = false) {
        if (!isFailedInspection(item)) return "";
        return errorAdvice?.format?.(item, { prefix, includeCode }) || "";
    }

    function logCriticalInspectionAdvice(item) {
        const resolved = errorAdvice?.resolve?.(item);
        const hasErrorCode = Boolean(item?.errorCode);
        if (!resolved?.advice || (!isFailedInspection(item) && !hasErrorCode)) return;

        const key = [
            item?.inspectionId || "live",
            resolved.code || resolved.stage || "unknown",
            resolved.message || "",
        ].join("\u001f");
        if (criticalAdviceLogKeys.has(key)) return;

        criticalAdviceLogKeys.add(key);
        if (criticalAdviceLogKeys.size > MaxCriticalAdviceLogKeys) {
            const oldestKey = criticalAdviceLogKeys.values().next().value;
            if (oldestKey) criticalAdviceLogKeys.delete(oldestKey);
        }

        const idPart = item?.inspectionId ? `(${item.inspectionId})` : "";
        const codePart = resolved.code ? ` [${resolved.code}]` : "";
        addLog(`关键错误${idPart}: ${resolved.advice}${codePart}`, "error");
    }

    function toFiniteNumber(value) {
        const number = Number(value);
        return Number.isFinite(number) ? number : 0;
    }

    function getFallbackRatioText(list) {
        const inspections = Array.isArray(list)
            ? list.filter((item) => item && (item.inspectionId || item.isOk !== undefined))
            : [];
        if (inspections.length === 0) return "";

        const fallbackCount = inspections.filter((item) => item.wasFallback === true).length;
        const ratio = fallbackCount / inspections.length * 100;
        return `近${inspections.length}次 fallback ${fallbackCount}次 (${ratio.toFixed(1)}%)`;
    }

    function getFallbackBadge(inspection, recentInspections) {
        const attempts = Math.max(0, Math.trunc(toFiniteNumber(inspection?.fallbackAttemptCount)));
        const inferenceMs = inspection?.inferenceMs ?? "-";
        const ratioText = getFallbackRatioText(recentInspections);
        const ratioSuffix = ratioText ? `; ${ratioText}` : "";

        if (inspection?.wasFallback === true) {
            return {
                text: attempts > 1 ? `FALLBACK x${attempts}` : "FALLBACK",
                title: `fallback命中，模型: ${inspection.usedModelName || "-"}，推理: ${inferenceMs}ms${ratioSuffix}`,
            };
        }

        const skippedReason = String(inspection?.fallbackSkippedReason || "").trim();
        if (skippedReason && skippedReason !== "FallbackDisabled") {
            return {
                text: "FB SKIP",
                title: `fallback未命中或跳过: ${skippedReason}${ratioSuffix}`,
            };
        }

        return null;
    }

    function getPerformanceDetail(item) {
        const parts = [];
        const inferenceMs = item?.inferenceMs;
        if (inferenceMs !== undefined && inferenceMs !== null && inferenceMs !== "") {
            parts.push(`推理${inferenceMs}ms`);
        }

        const attempts = Math.max(0, Math.trunc(toFiniteNumber(item?.fallbackAttemptCount)));
        if (item?.wasFallback === true) {
            parts.push(`fallback ${attempts > 0 ? attempts : "?"}模型`);
        } else if (attempts > 1) {
            parts.push(`模型尝试${attempts}次`);
        }

        const skippedReason = String(item?.fallbackSkippedReason || "").trim();
        if (skippedReason && skippedReason !== "FallbackDisabled" && item?.wasFallback !== true) {
            parts.push(`FB:${skippedReason}`);
        }

        const imagePending = Math.max(0, Math.trunc(toFiniteNumber(item?.imageQueuePending)));
        const recordPending = Math.max(0, Math.trunc(toFiniteNumber(item?.recordQueuePending)));
        if (imagePending > 0 || recordPending > 0) {
            parts.push(`队列 I${imagePending}/R${recordPending}`);
        }

        return parts.join(" / ");
    }

    function logFallbackTelemetry(item, recentInspections) {
        if (item?.wasFallback !== true) return;

        const key = item.inspectionId || `${item.triggerSeq || "-"}:${item.resultSeq || "-"}:${item.usedModelName || "-"}`;
        if (fallbackTelemetryLogKeys.has(key)) return;

        fallbackTelemetryLogKeys.add(key);
        if (fallbackTelemetryLogKeys.size > MaxFallbackTelemetryLogKeys) {
            const oldestKey = fallbackTelemetryLogKeys.values().next().value;
            if (oldestKey) fallbackTelemetryLogKeys.delete(oldestKey);
        }

        const attempts = Math.max(0, Math.trunc(toFiniteNumber(item.fallbackAttemptCount)));
        const ratioText = getFallbackRatioText(recentInspections);
        const ratioSuffix = ratioText ? `，${ratioText}` : "";
        addDetectionLog(
            `Fallback命中: ${item.usedModelName || "-"}，尝试${attempts > 0 ? attempts : "-"}模型，推理${item.inferenceMs ?? "-"}ms${ratioSuffix}`,
            "warning",
        );
    }

    function getObjectSummaryFromMessage(message) {
        const parts = String(message || "").split("|").map((part) => part.trim()).filter(Boolean);
        return parts.find((part) => /^Found\s+\d+\s*:/i.test(part) || part.includes("未检测到目标")) || "";
    }

    function normalizeRuleDetails(details) {
        if (Array.isArray(details)) return details.filter(Boolean).map(String);
        if (typeof details === "string" && details.trim()) return [details.trim()];
        return [];
    }

    function getDetectionSummary(item) {
        const advice = getInspectionAdvice(item, "建议");
        if (advice) return advice;

        if (item?.isOk === false && item?.rulePrimaryReason) return item.rulePrimaryReason;

        const message = item?.message || item?.errorMessage || "";
        const objectPart = getObjectSummaryFromMessage(message);
        if (objectPart) return objectPart;
        if (item?.barcodeError) return item.barcodeError;
        if (item?.errorCode) return item.errorCode;
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

        const adviceMessage = getInspectionAdvice(inspection);
        const ruleReason = isOk === false ? inspection.rulePrimaryReason : "";
        const message = adviceMessage || ruleReason || inspection.message || (isOk === true ? "检测通过" : isOk === false ? "检测未通过" : "等待检测结果");
        setText("camera-result-text", message, "等待检测结果");
        setText("camera-total-ms", `${inspection.totalMs || 0}ms`, "0ms");
        setText("camera-target-count", inspection.actualCount ?? 0, "0");
        setText("camera-model", inspection.usedModelName, "-");
        setText("feed-model-name", inspection.usedModelName ? `MODEL ${inspection.usedModelName}` : "MODEL -", "MODEL -");
        const fallbackBadge = el("camera-fallback");
        const fallbackMeta = getFallbackBadge(inspection, state.recentInspections || []);
        if (fallbackBadge && fallbackMeta) {
            fallbackBadge.textContent = fallbackMeta.text;
            fallbackBadge.title = fallbackMeta.title;
        }
        toggleClass(fallbackBadge, "hidden", !fallbackMeta);
        logFallbackTelemetry(inspection, state.recentInspections || []);
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
            const objectSummary = item.isOk === false ? getObjectSummaryFromMessage(item.message || "") : "";
            const performanceDetail = getPerformanceDetail(item);
            const detail = [
                detectionSummary,
                objectSummary && objectSummary !== detectionSummary ? objectSummary : null,
                item.totalMs ? `${item.totalMs}ms` : null,
                performanceDetail,
            ].filter(Boolean).join(" / ");
            const ruleTitle = [
                item.ruleSummary,
                ...normalizeRuleDetails(item.ruleDetails),
            ].filter(Boolean).join("\n");
            const detailTitle = [
                detail,
                ruleTitle,
            ].filter(Boolean).join("\n");
            const key = item._renderKey || item.inspectionId || `${item.time}:${title}:${index}`;
            const signature = [
                statusClass,
                statusText,
                item.time || "",
                identity || "",
                title || "",
                detailTitle || detail || item.currentStage || "-",
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
                detailNode.title = detailTitle || detail || "";
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
        setText("stitch-pass-rate", `${yieldRate.toFixed(1)}%`, "0.0%");

    }

    function getHealthValue(health, camelName, pascalName) {
        return health?.[camelName] ?? health?.[pascalName];
    }

    function getQueuePressureItems(health) {
        const imagePending = Math.max(0, Math.trunc(toFiniteNumber(getHealthValue(health, "imageQueueLength", "ImageQueueLength"))));
        const imageCapacity = Math.max(0, Math.trunc(toFiniteNumber(getHealthValue(health, "imageQueueCapacity", "ImageQueueCapacity"))));
        const imagePendingBytes = Math.max(0, Math.trunc(toFiniteNumber(getHealthValue(health, "imageQueuePendingBytes", "ImageQueuePendingBytes"))));
        const imageMaxBufferedBytes = Math.max(0, Math.trunc(toFiniteNumber(getHealthValue(health, "imageQueueMaxBufferedBytes", "ImageQueueMaxBufferedBytes"))));
        const recordPending = Math.max(0, Math.trunc(toFiniteNumber(getHealthValue(health, "recordQueueLength", "RecordQueueLength"))));
        const recordCapacity = Math.max(0, Math.trunc(toFiniteNumber(getHealthValue(health, "recordQueueCapacity", "RecordQueueCapacity"))));
        const items = [];

        if (imageCapacity > 0 && imagePending * 4 >= imageCapacity * 3) {
            items.push(`图像${imagePending}/${imageCapacity}`);
        }
        if (imageMaxBufferedBytes > 0 && imagePendingBytes * 4 >= imageMaxBufferedBytes * 3) {
            items.push(`图像缓冲${formatBytesMb(imagePendingBytes)}/${formatBytesMb(imageMaxBufferedBytes)}`);
        }
        if (recordCapacity > 0 && recordPending * 4 >= recordCapacity * 3) {
            items.push(`记录${recordPending}/${recordCapacity}`);
        }

        return items;
    }

    function formatBytesMb(bytes) {
        return `${(bytes / 1024 / 1024).toFixed(1)}MB`;
    }

    function logQueuePressureAdvice(health) {
        const items = getQueuePressureItems(health);
        if (items.length === 0) {
            lastQueueAdviceKey = "";
            return;
        }

        const key = items.join("|");
        if (key === lastQueueAdviceKey) return;

        lastQueueAdviceKey = key;
        addLog(`队列压力偏高: ${items.join("，")}；建议检查磁盘/数据库写入速度或降低触发频率`, "warning");
    }

    function renderHealthSnapshot(state) {
        const health = state?.health || {};
        const cameraStatus = health.cameraStatus || health.CameraStatus || "";
        const plcStatus = health.plcStatus || health.PlcStatus || "";

        if (cameraStatus) {
            updateConnection("cam", /^(Open|Grabbing)$/i.test(String(cameraStatus)));
        }
        if (plcStatus) {
            updateConnection("plc", /^Connected/i.test(String(plcStatus)));
        }
        logQueuePressureAdvice(health);
    }

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
        const normalizedMessage = String(msg || "");
        const normalizedType = String(type || "").toLowerCase();
        if (normalizedType !== "error" && !KeyLogPatterns.some((pattern) => pattern.test(normalizedMessage))) return;
        logBuffer.push({ msg, type, time: new Date().toLocaleTimeString() });
        if (!logFlushTimer) {
            logFlushTimer = window.setTimeout(() => {
                logFlushTimer = null;
                flushBufferedLogs(
                    logBuffer,
                    "log-container",
                    (entryType) => `cf-key-log-row ${entryType || "info"}`,
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
        const normalizedType = String(type || "").toLowerCase();
        const indicator =
            normalizedType === "cam" || normalizedType === "camera"
                ? { root: "status-cam", dot: "status-cam-dot", text: "status-cam-text", label: "相机" }
                : normalizedType === "plc"
                    ? { root: "status-plc", dot: "status-plc-dot", text: "status-plc-text", label: "PLC" }
                    : null;

        if (indicator) {
            const connected = Boolean(isConnected);
            const statusText = connected ? "已连接" : "未连接";
            const root = el(indicator.root);
            setDotState(indicator.dot, connected ? "status-on" : "status-off");
            setText(indicator.text, statusText);
            toggleClass(root, "is-connected", connected);
            if (root) {
                root.setAttribute("aria-label", `${indicator.label}: ${statusText}`);
                root.title = `${indicator.label}${statusText}`;
            }
        }

        if (type === "cam" && !isConnected && openCameraPending && !systemBusy) {
            openCameraPending = false;
            setOpenCameraButtonBusy(false);
            if (openCameraUnlockTimer) {
                window.clearTimeout(openCameraUnlockTimer);
                openCameraUnlockTimer = null;
            }
        }

        if (type === "cam" && isConnected && openCameraPending && !systemBusy) {
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
        systemBusy = Boolean(isBusy);
        setStartSystemButtonState(systemRunning, systemBusy);
    }

    function setStartSystemButtonState(isRunning, isBusy = false) {
        systemRunning = Boolean(isRunning);
        systemBusy = Boolean(isBusy);
        const button = el("btn-open-camera");
        if (!button) return;
        button.disabled = systemBusy;
        button.classList.toggle("camera-open-pending", systemBusy);
        button.classList.toggle("is-running", systemRunning);
        button.setAttribute("aria-label", systemRunning ? "停止检测" : "启动系统");
        button.title = systemRunning ? "停止检测" : "启动系统";
        button.innerHTML = systemRunning
            ? `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M6 6h12v12H6z" />
                </svg>
                ${systemBusy ? "正在停止..." : "停止检测"}`
            : `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2"
                        d="M8 5v14l11-7-11-7Z" />
                </svg>
                ${systemBusy ? "正在启动..." : "启动系统 (Start System)"}`;
    }

    function requestStartSystem() {
        if (systemBusy) {
            showToast(systemRunning ? "正在停止检测，请稍候" : "系统正在启动中，请勿重复点击", "warning", 1200);
            return;
        }

        if (systemRunning) {
            setStartSystemButtonState(true, true);
            window.sendCommand("stop_system");
            addLog("停止检测指令已发送", "info");
            showToast("停止检测指令已发送", "info", 1200);
            return true;
        }

        const now = Date.now();
        if (now < openCameraCooldownUntil) {
            showToast("系统正在启动中，请勿重复点击", "warning", 1200);
            return;
        }

        openCameraCooldownUntil = now + 1500;
        openCameraPending = true;
        setOpenCameraButtonBusy(true);
        if (openCameraUnlockTimer) window.clearTimeout(openCameraUnlockTimer);
        openCameraUnlockTimer = window.setTimeout(() => {
            openCameraPending = false;
            if (!systemBusy) setOpenCameraButtonBusy(false);
            openCameraUnlockTimer = null;
        }, 1500);

        window.sendCommand("start_system");
        addLog("启动系统指令已发送", "info");
        showToast("启动系统指令已发送", "info", 1400);
        return true;
    }

    function requestOpenCamera() {
        return requestStartSystem();
    }

    function startSystem() {
        return requestStartSystem();
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
        logCriticalInspectionAdvice(window.CF_STATE?.inspection || payload);
    }

    function handleHealthSnapshot(snapshot) {
        store.applyHealthSnapshot(snapshot);
    }

    function flashPlcTrigger() {
        const dot = el("status-plc-trigger-dot");
        const root = el("status-plc-trigger");
        if (!dot) return;

        if (plcTriggerResetTimer) {
            window.clearTimeout(plcTriggerResetTimer);
            plcTriggerResetTimer = null;
        }

        dot.classList.remove("status-trigger-flash", "status-off", "status-on", "status-warning", "status-error");
        void dot.offsetWidth;
        dot.classList.add("status-trigger-flash");
        if (root) root.classList.add("is-triggering");
        setText("status-plc-trigger-text", "触发");
        if (root) {
            root.setAttribute("aria-label", "触发拍照: 已触发");
            root.title = "触发拍照已触发";
        }

        plcTriggerResetTimer = window.setTimeout(() => {
            dot.classList.remove("status-trigger-flash");
            dot.dataset.cfState = "";
            setDotState("status-plc-trigger-dot", "status-off");
            if (root) root.classList.remove("is-triggering");
            setText("status-plc-trigger-text", "待触发");
            if (root) {
                root.setAttribute("aria-label", "触发拍照: 待触发");
                root.title = "触发拍照待触发";
            }
            plcTriggerResetTimer = null;
        }, 650);
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
            case "cameraPreviewStatus":
                window.setCameraPreviewStatus?.(payload);
                break;
            case "cameraDirectConnectResult":
                window.receiveCameraDirectConnectResult?.(payload);
                break;
            case "showSettingsModal":
                window.openSettingsModal?.(payload.config || payload.Config || null);
                break;
            case "closeSettingsModal":
                window.closeSettingsModal?.();
                break;
            case "setRoi":
                window.setRoi?.(payload.rect || payload.Rect || null);
                break;
            case "serialPortsDetected":
                window.handleSerialPortsDetected?.(payload);
                break;
            case "setSystemRunning":
                setStartSystemButtonState(
                    payload.isRunning ?? payload.IsRunning,
                    payload.isBusy ?? payload.IsBusy ?? false,
                );
                break;
            default:
                if (window.__CF_DEV_MODE) console.debug("Unknown uiCommand:", data);
                break;
        }
    }

    function handleCommandDispatched(cmd) {
        switch (cmd) {
            case "manual_detect":
                addLog("手动检测按钮已点击", "info");
                showToast("手动检测已触发", "info", 1200);
                break;
            case "manual_release":
                addLog("强制放行申请已提交", "warning");
                showToast("强制放行申请已提交", "warning", 1200);
                break;
            default:
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
            inferenceMs: data.inspection?.inferenceMs ?? data.inferenceMs,
            actualCount: data.inspection?.actualCount ?? data.actualCount,
            usedModelName: data.inspection?.usedModelName ?? data.usedModelName,
            wasFallback: data.inspection?.wasFallback ?? data.wasFallback,
            fallbackAttemptCount: data.inspection?.fallbackAttemptCount ?? data.fallbackAttemptCount,
            fallbackSkippedReason: data.inspection?.fallbackSkippedReason ?? data.fallbackSkippedReason,
            imageQueuePending: data.inspection?.imageQueuePending ?? data.imageQueuePending,
            recordQueuePending: data.inspection?.recordQueuePending ?? data.recordQueuePending,
            handshakeStartMs: data.inspection?.handshakeStartMs ?? data.handshakeStartMs,
            plcResultWriteMs: data.inspection?.plcResultWriteMs ?? data.plcResultWriteMs,
            handshakeCompleteMs: data.inspection?.handshakeCompleteMs ?? data.handshakeCompleteMs,
            ruleSummary: data.inspection?.ruleSummary ?? data.ruleSummary,
            rulePrimaryReason: data.inspection?.rulePrimaryReason ?? data.rulePrimaryReason,
            ruleDetails: data.inspection?.ruleDetails ?? data.ruleDetails,
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
        requestStartSystem,
        startSystem,
        handleCommandDispatched,
        setStartSystemButtonState,
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

// ==========================================
// ClearFrost settings workspace
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const store = window.CF_STORE;

    const PLC_PROTOCOL_UI_HINTS = {
        Mitsubishi_MC_ASCII: {
            help: "三菱 MC ASCII：Hsl/Hao 支持 D/M/X/Y 等字地址；McpX 当前业务适配只支持 D 区。",
            placeholder: "例如 D100",
        },
        Mitsubishi_MC_Binary: {
            help: "三菱 MC Binary：Hsl/Hao 支持 D/M/X/Y 等字地址；McpX 当前业务适配只支持 D 区。",
            placeholder: "例如 D100",
        },
        Siemens_S7: {
            help: "西门子 S7：当前信号读写使用字/字节地址，支持 DB1.0、DB1.DBW0、M0、I0、Q0；位地址请勿用于这里。",
            placeholder: "例如 DB1.0 或 DB1.DBW0",
        },
        Modbus_TCP: {
            help: "Modbus TCP：使用 0-based 寄存器地址，例如 40001 或 0。",
            placeholder: "例如 40001",
        },
        Omron_Fins: {
            help: "欧姆龙 FINS：常用 D100 / CIO100(C100) / W100 / H100 / A100。",
            placeholder: "例如 D100",
        },
    };

    let PROJECT_PRESETS = {
        N5_remote: {
            name: "N5遥控器漏装视觉检测",
            PlcIp: "10.182.82.19",
            PlcPort: 2700,
            PlcTriggerAddress: "D100",
            PlcResultAddress: "D102",
            CameraSerialNumber: "5G087BAGAK00018",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "remote",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        N5_screw: {
            name: "N5螺钉视觉检测",
            PlcIp: "10.182.82.19",
            PlcPort: 3000,
            PlcTriggerAddress: "D90",
            PlcResultAddress: "D92",
            CameraSerialNumber: "EF59601AAK00030",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "screw",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        N6_remote: {
            name: "N6遥控器漏装视觉检测",
            PlcIp: "192.168.100.122",
            PlcPort: 5777,
            PlcTriggerAddress: "D6607",
            PlcResultAddress: "D6608",
            CameraSerialNumber: "AM01040AAK00040",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "remote",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        N6_screw: {
            name: "N6螺钉视觉检测",
            PlcIp: "10.182.82.3",
            PlcPort: 4300,
            PlcTriggerAddress: "D100",
            PlcResultAddress: "D102",
            CameraSerialNumber: "",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TargetLabel: "screw",
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        W5_screw: {
            name: "W5螺钉视觉检测",
            PlcIp: "192.168.22.44",
            PlcPort: 4999,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "EF59632AAK00291",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TargetLabel: "screw",
            TargetCount: 4,
            ExposureTime: 50000,
            Gain: 1.1,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
        W6_screw: {
            name: "W6螺钉视觉检测",
            PlcIp: "192.168.250.1",
            PlcPort: 5999,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "EF59632AAK00291",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TargetLabel: "screw",
            TargetCount: 4,
            ExposureTime: 3500,
            Gain: 1.5,
            PlcDriverProvider: "HaoCommunication",
            PlcProtocolMode: "Legacy",
            PlcTriggerDelayMs: 800,
            PlcPollingIntervalMs: 500,
            PlcOkValue: 1,
            PlcNgValue: 0,
            PlcTriggerSeqAddress: "D557",
            PlcResultSeqAddress: "D558",
            PlcVisionOnlineAddress: "D559",
            PlcVisionReadyAddress: "D560",
            PlcVisionBusyAddress: "D561",
            PlcInspectionDoneAddress: "D562",
            PlcErrorCodeAddress: "D563",
            PlcTraceSavedAddress: "D564",
            PlcHeartbeatAddress: "D565",
            PlcResetFaultAddress: "D566",
            BarcodeEnabled: false,
            BarcodeAddress: "D570",
            BarcodeWordLength: 16,
            BarcodeEncoding: "ASCII",
            BarcodeRequired: false,
            PlcSiemensCpuModel: "S1200",
            PlcSiemensRack: 0,
            PlcSiemensSlot: 2,
            EnableGpu: false,
            IndustrialRenderMode: true,
            MaxRetryCount: 1,
            RetryIntervalMs: 2000,
            StoragePath: "C:\\GreeVisionData",
            CameraManufacturer: "Huaray",
        },
    };

    let pendingProjectPresetId = "";

    function byId(id) {
        return document.getElementById(id);
    }

    function normalizeThresholdValue(value, fallback = 0) {
        const num = parseFloat(value);
        if (Number.isNaN(num)) return fallback;
        return Math.max(0, Math.min(1, num));
    }

    function setThresholdControl(inputId, legacySliderId, value, fallback = 0) {
        const normalized = normalizeThresholdValue(value, fallback);
        const input = byId(inputId);
        if (input) input.value = normalized.toFixed(2);

        const legacySlider = byId(legacySliderId);
        if (legacySlider) legacySlider.value = Math.round(normalized * 100);
        return normalized;
    }

    function readThresholdControl(inputId, legacySliderId, fallback) {
        const input = byId(inputId);
        if (input) return normalizeThresholdValue(input.value, fallback);

        const legacySlider = byId(legacySliderId);
        if (legacySlider) return normalizeThresholdValue(parseFloat(legacySlider.value) / 100, fallback);
        return fallback;
    }

    function escapeHtml(value) {
        return window.CF_UTILS?.escapeHtml
            ? window.CF_UTILS.escapeHtml(value)
            : String(value ?? "");
    }

    function makeRuleId() {
        return `rule-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
    }

    function createInspectionRule(type = "Count") {
        if (type === "OrderedLabels") {
            return {
                Id: makeRuleId(),
                Name: "顺序规则",
                Enabled: true,
                Type: "OrderedLabels",
                ExpectedLabels: [],
                SortBy: "CenterX",
                Direction: "LeftToRight",
                ExpectedCount: 0,
                MinConfidence: 0,
                AllowMissing: false,
                AllowDuplicate: false,
            };
        }

        if (type === "RelativePosition") {
            return {
                Id: makeRuleId(),
                Name: "位置规则",
                Enabled: true,
                Type: "RelativePosition",
                SubjectLabel: "",
                ReferenceLabel: "",
                Relation: "LeftOf",
                MinDistance: 0,
                MaxDistance: 0,
                MinConfidence: 0,
            };
        }

        return {
            Id: makeRuleId(),
            Name: "数量规则",
            Enabled: true,
            Type: "Count",
            Label: "",
            Operator: "Equal",
            Count: 1,
            MinConfidence: 0,
        };
    }

    function normalizeRuleLabels(value) {
        if (Array.isArray(value)) {
            return value.map((label) => String(label || "").trim()).filter(Boolean);
        }

        return String(value || "")
            .split(",")
            .map((label) => label.trim())
            .filter(Boolean);
    }

    function normalizeInspectionRule(rule) {
        const type = rule?.Type || rule?.type || "Count";
        const base = createInspectionRule(type);
        const normalized = {
            ...base,
            ...rule,
            Id: rule?.Id || rule?.id || base.Id,
            Name: rule?.Name ?? rule?.name ?? base.Name,
            Enabled: rule?.Enabled ?? rule?.enabled ?? true,
            Type: type,
            MinConfidence: normalizeThresholdValue(rule?.MinConfidence ?? rule?.minConfidence ?? base.MinConfidence, 0),
        };

        if (type === "OrderedLabels") {
            normalized.ExpectedLabels = normalizeRuleLabels(rule?.ExpectedLabels ?? rule?.expectedLabels);
            normalized.ExpectedCount = Math.max(0, parseInt(rule?.ExpectedCount ?? rule?.expectedCount ?? 0, 10) || 0);
            normalized.AllowMissing = !!(rule?.AllowMissing ?? rule?.allowMissing);
            normalized.AllowDuplicate = !!(rule?.AllowDuplicate ?? rule?.allowDuplicate);
            normalized.SortBy = rule?.SortBy || rule?.sortBy || "CenterX";
            normalized.Direction = rule?.Direction || rule?.direction || "LeftToRight";
            return normalized;
        }

        if (type === "RelativePosition") {
            normalized.SubjectLabel = String(rule?.SubjectLabel ?? rule?.subjectLabel ?? "").trim();
            normalized.ReferenceLabel = String(rule?.ReferenceLabel ?? rule?.referenceLabel ?? "").trim();
            normalized.Relation = rule?.Relation || rule?.relation || "LeftOf";
            normalized.MinDistance = Math.max(0, parseFloat(rule?.MinDistance ?? rule?.minDistance ?? 0) || 0);
            normalized.MaxDistance = Math.max(0, parseFloat(rule?.MaxDistance ?? rule?.maxDistance ?? 0) || 0);
            return normalized;
        }

        normalized.Label = String(rule?.Label ?? rule?.label ?? "").trim();
        normalized.Operator = rule?.Operator || rule?.operator || "Equal";
        normalized.Count = Math.max(0, parseInt(rule?.Count ?? rule?.count ?? 0, 10) || 0);
        return normalized;
    }

    function makeLegacyRuleSet(data) {
        if (data?.WireSequenceJudgeEnabled) {
            return {
                Version: 1,
                Mode: "All",
                FallbackTargetLabel: data.TargetLabel || "",
                FallbackTargetCount: Number.isFinite(Number(data.TargetCount)) ? Math.max(0, Number(data.TargetCount)) : 0,
                Rules: [{
                    ...createInspectionRule("OrderedLabels"),
                    Name: "端子线序",
                    ExpectedLabels: normalizeRuleLabels(data.WireSequenceExpectedLabels || "Wire_Brown,Wire_Black,Wire_Blue"),
                    SortBy: data.WireSequenceSortBy || "CenterX",
                    Direction: data.WireSequenceDirection || "LeftToRight",
                    ExpectedCount: data.WireSequenceExpectedCount || 0,
                    MinConfidence: data.WireSequenceMinConfidence || 0,
                    AllowMissing: !!data.WireSequenceAllowMissing,
                    AllowDuplicate: !!data.WireSequenceAllowDuplicate,
                }],
            };
        }

        return {
            Version: 1,
            Mode: "All",
            FallbackTargetLabel: data?.TargetLabel || "",
            FallbackTargetCount: Number.isFinite(Number(data?.TargetCount)) ? Math.max(0, Number(data.TargetCount)) : 0,
            Rules: [{
                ...createInspectionRule("Count"),
                Name: `${data?.TargetLabel || "目标"} 数量`,
                Label: data?.TargetLabel || "screw",
                Count: Number.isFinite(Number(data?.TargetCount)) ? Number(data.TargetCount) : 4,
            }],
        };
    }

    function normalizeInspectionRuleSet(raw, legacyData = {}) {
        let parsed = raw;
        if (typeof raw === "string" && raw.trim()) {
            try {
                parsed = JSON.parse(raw);
            } catch {
                parsed = null;
            }
        }

        if (!parsed || !Array.isArray(parsed.Rules || parsed.rules)) {
            parsed = makeLegacyRuleSet(legacyData);
        }

        const rules = (parsed.Rules || parsed.rules || []).map(normalizeInspectionRule);
        const fallbackLabel = parsed.FallbackTargetLabel ?? parsed.fallbackTargetLabel ?? "";
        const fallbackCount = Number(parsed.FallbackTargetCount ?? parsed.fallbackTargetCount ?? 0);
        return {
            Version: 1,
            Mode: "All",
            FallbackTargetLabel: String(fallbackLabel || "").trim(),
            FallbackTargetCount: Number.isFinite(fallbackCount) ? Math.max(0, Math.floor(fallbackCount)) : 0,
            Rules: rules.length ? rules : makeLegacyRuleSet(legacyData).Rules,
        };
    }

    function getCurrentRuleSet() {
        return store.state.inspectionRuleSet || normalizeInspectionRuleSet(store.state.settings?.InspectionRuleSetJson, store.state.settings || {});
    }

    function syncInspectionRuleJson() {
        const ruleSet = getCurrentRuleSet();
        const hidden = byId("cfg-inspection-rule-set-json");
        if (hidden) hidden.value = JSON.stringify(ruleSet);
        store.state.settings = { ...(store.state.settings || {}), InspectionRuleSetJson: JSON.stringify(ruleSet) };
        return ruleSet;
    }

    function updateRuleLabelOptions() {
        const datalist = byId("inspection-rule-label-options");
        if (!datalist) return;
        const labels = Array.isArray(store.state.modelLabels) ? store.state.modelLabels : [];
        datalist.innerHTML = labels
            .map((label) => `<option value="${escapeHtml(label)}"></option>`)
            .join("");
    }

    function ruleTypeLabel(type) {
        if (type === "OrderedLabels") return "顺序";
        if (type === "RelativePosition") return "位置";
        return "数量";
    }

    function ruleInputAttrs(index, field, extra = "") {
        return `data-input-action="updateInspectionRule" data-change-action="updateInspectionRule" data-pass-element="true" data-rule-index="${index}" data-rule-field="${field}" ${extra}`;
    }

    function renderCountRuleFields(rule, index) {
        return `
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">标签</label>
                <input value="${escapeHtml(rule.Label || "")}" list="inspection-rule-label-options"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-bold" placeholder="留空表示全部目标"
                    ${ruleInputAttrs(index, "Label")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">比较</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "Operator")}>
                    ${operatorOptions(rule.Operator)}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">数量</label>
                <input type="number" min="0" step="1" value="${escapeHtml(rule.Count ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "Count")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最低置信度</label>
                <input type="number" min="0" max="1" step="0.01" value="${escapeHtml(rule.MinConfidence ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinConfidence")}>
            </div>
        `;
    }

    function renderOrderedRuleFields(rule, index) {
        return `
            <div class="cf-plc-span-3">
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">期望标签顺序</label>
                <input value="${escapeHtml((rule.ExpectedLabels || []).join(","))}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    placeholder="Wire_Brown,Wire_Black,Wire_Blue"
                    ${ruleInputAttrs(index, "ExpectedLabels")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">排序字段</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "SortBy")}>
                    ${optionList(rule.SortBy, [["CenterX", "中心 X"], ["CenterY", "中心 Y"], ["TopY", "顶部 Y"], ["Confidence", "置信度"], ["Area", "面积"]])}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">排序方向</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "Direction")}>
                    ${optionList(rule.Direction, [["LeftToRight", "从左到右"], ["RightToLeft", "从右到左"], ["TopToBottom", "从上到下"], ["BottomToTop", "从下到上"], ["Ascending", "升序"], ["Descending", "降序"]])}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">期望数量</label>
                <input type="number" min="0" max="256" step="1" value="${escapeHtml(rule.ExpectedCount ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "ExpectedCount")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最低置信度</label>
                <input type="number" min="0" max="1" step="0.01" value="${escapeHtml(rule.MinConfidence ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinConfidence")}>
            </div>
            <label class="cf-plc-toggle">
                <input type="checkbox" class="accent-celadon-600 w-3.5 h-3.5 rounded" ${rule.AllowMissing ? "checked" : ""}
                    ${ruleInputAttrs(index, "AllowMissing")}>
                <span>允许缺失</span>
            </label>
            <label class="cf-plc-toggle">
                <input type="checkbox" class="accent-celadon-600 w-3.5 h-3.5 rounded" ${rule.AllowDuplicate ? "checked" : ""}
                    ${ruleInputAttrs(index, "AllowDuplicate")}>
                <span>允许重复</span>
            </label>
        `;
    }

    function renderPositionRuleFields(rule, index) {
        return `
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">主标签</label>
                <input value="${escapeHtml(rule.SubjectLabel || "")}" list="inspection-rule-label-options"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-bold"
                    ${ruleInputAttrs(index, "SubjectLabel")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">关系</label>
                <select class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono cursor-pointer"
                    ${ruleInputAttrs(index, "Relation")}>
                    ${optionList(rule.Relation, [["LeftOf", "在左侧"], ["RightOf", "在右侧"], ["Above", "在上方"], ["Below", "在下方"]])}
                </select>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">参考标签</label>
                <input value="${escapeHtml(rule.ReferenceLabel || "")}" list="inspection-rule-label-options"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-bold"
                    ${ruleInputAttrs(index, "ReferenceLabel")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最小间距(px)</label>
                <input type="number" min="0" step="1" value="${escapeHtml(rule.MinDistance ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinDistance")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最大间距(px)</label>
                <input type="number" min="0" step="1" value="${escapeHtml(rule.MaxDistance ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MaxDistance")}>
            </div>
            <div>
                <label class="text-[10px] font-bold text-ink-400 mb-1 block">最低置信度</label>
                <input type="number" min="0" max="1" step="0.01" value="${escapeHtml(rule.MinConfidence ?? 0)}"
                    class="w-full tech-input px-3 py-2 rounded-lg text-xs font-mono"
                    ${ruleInputAttrs(index, "MinConfidence")}>
            </div>
        `;
    }

    function optionList(current, pairs) {
        return pairs.map(([value, label]) =>
            `<option value="${value}" ${String(current) === value ? "selected" : ""}>${label}</option>`
        ).join("");
    }

    function operatorOptions(current) {
        return optionList(current, [
            ["Equal", "等于"],
            ["NotEqual", "不等于"],
            ["GreaterThan", "大于"],
            ["GreaterThanOrEqual", "大于等于"],
            ["LessThan", "小于"],
            ["LessThanOrEqual", "小于等于"],
        ]);
    }

    function renderInspectionRules() {
        updateRuleLabelOptions();
        const container = byId("inspection-rule-list");
        if (!container) return;
        const ruleSet = getCurrentRuleSet();
        const rules = ruleSet.Rules || [];
        if (!rules.length) {
            container.innerHTML = '<p class="text-[10px] text-ink-400">尚未配置规则。</p>';
            syncInspectionRuleJson();
            return;
        }

        container.innerHTML = rules.map((rule, index) => {
            const fields = rule.Type === "OrderedLabels"
                ? renderOrderedRuleFields(rule, index)
                : rule.Type === "RelativePosition"
                    ? renderPositionRuleFields(rule, index)
                    : renderCountRuleFields(rule, index);
            return `
                <article class="bg-white/80 border border-celadon-100 rounded-lg p-4 shadow-sm">
                    <div class="flex flex-wrap items-center justify-between gap-2 mb-3">
                        <div class="flex items-center gap-2">
                            <input type="checkbox" class="accent-celadon-600 w-3.5 h-3.5 rounded" ${rule.Enabled ? "checked" : ""}
                                ${ruleInputAttrs(index, "Enabled")}>
                            <span class="px-2 py-1 rounded bg-celadon-50 text-celadon-700 text-[10px] font-bold">${ruleTypeLabel(rule.Type)}</span>
                            <input value="${escapeHtml(rule.Name || "")}" class="tech-input px-2 py-1 rounded-lg text-xs font-bold"
                                ${ruleInputAttrs(index, "Name")} placeholder="规则名称">
                        </div>
                        <div class="flex gap-1">
                            <button type="button" data-action="moveInspectionRule" data-value='{"index":${index},"direction":-1}'
                                class="px-2 py-1 bg-porcelain-100 text-ink-500 hover:bg-celadon-50 text-[10px] font-bold rounded">上移</button>
                            <button type="button" data-action="moveInspectionRule" data-value='{"index":${index},"direction":1}'
                                class="px-2 py-1 bg-porcelain-100 text-ink-500 hover:bg-celadon-50 text-[10px] font-bold rounded">下移</button>
                            <button type="button" data-action="duplicateInspectionRule" data-value="${index}"
                                class="px-2 py-1 bg-blue-50 text-blue-600 hover:bg-blue-100 text-[10px] font-bold rounded">复制</button>
                            <button type="button" data-action="removeInspectionRule" data-value="${index}"
                                class="px-2 py-1 bg-red-50 text-red-600 hover:bg-red-100 text-[10px] font-bold rounded">删除</button>
                        </div>
                    </div>
                    <div class="cf-plc-grid cf-plc-grid-3">
                        ${fields}
                    </div>
                </article>
            `;
        }).join("");
        syncInspectionRuleJson();
    }

    function updateInspectionRule(element) {
        const index = parseInt(element?.dataset?.ruleIndex, 10);
        const field = element?.dataset?.ruleField;
        const rules = getCurrentRuleSet().Rules || [];
        if (!Number.isInteger(index) || !field || !rules[index]) return;

        let value = element.type === "checkbox" ? element.checked : element.value;
        if (["Count", "ExpectedCount"].includes(field)) value = Math.max(0, parseInt(value, 10) || 0);
        if (["MinConfidence"].includes(field)) value = normalizeThresholdValue(value, 0);
        if (["MinDistance", "MaxDistance"].includes(field)) value = Math.max(0, parseFloat(value) || 0);
        if (field === "ExpectedLabels") value = normalizeRuleLabels(value);
        rules[index][field] = value;
        syncInspectionRuleJson();
    }

    function addInspectionRule(type) {
        const ruleSet = getCurrentRuleSet();
        ruleSet.Rules.push(createInspectionRule(type || "Count"));
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function removeInspectionRule(index) {
        const ruleSet = getCurrentRuleSet();
        ruleSet.Rules.splice(parseInt(index, 10), 1);
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function duplicateInspectionRule(index) {
        const ruleSet = getCurrentRuleSet();
        const source = ruleSet.Rules[parseInt(index, 10)];
        if (!source) return;
        ruleSet.Rules.splice(parseInt(index, 10) + 1, 0, { ...JSON.parse(JSON.stringify(source)), Id: makeRuleId(), Name: `${source.Name || "规则"} 副本` });
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function moveInspectionRule(payload) {
        const ruleSet = getCurrentRuleSet();
        const index = parseInt(payload?.index, 10);
        const direction = parseInt(payload?.direction, 10);
        const nextIndex = index + direction;
        if (!ruleSet.Rules[index] || nextIndex < 0 || nextIndex >= ruleSet.Rules.length) return;
        const [item] = ruleSet.Rules.splice(index, 1);
        ruleSet.Rules.splice(nextIndex, 0, item);
        store.state.inspectionRuleSet = ruleSet;
        renderInspectionRules();
    }

    function validateInspectionRuleSettings() {
        const rules = getCurrentRuleSet().Rules || [];
        if (!rules.length) return "至少需要配置一条判定规则";
        const enabledRules = rules.filter((r) => r.Enabled !== false);
        if (!enabledRules.length) return "至少需要启用一条判定规则";
        for (const rule of enabledRules) {
            if (rule.Type === "OrderedLabels" && !normalizeRuleLabels(rule.ExpectedLabels).length) {
                return `规则“${rule.Name || "顺序规则"}”必须配置期望标签顺序`;
            }
            if (rule.Type === "RelativePosition" && (!rule.SubjectLabel || !rule.ReferenceLabel)) {
                return `规则“${rule.Name || "位置规则"}”必须配置主标签和参考标签`;
            }
        }
        return null;
    }

    function getCompactPlcAddress(value) {
        return String(value || "").trim().replace(/\s+/g, "").toUpperCase();
    }

    function updateSiemensRackSlotVisibility() {
        const protocol = byId("cfg-plc-protocol")?.value || "";
        const cpuModel = (byId("cfg-plc-siemens-cpu-model")?.value || "").toUpperCase();
        const group = byId("cfg-plc-siemens-rack-slot");
        const showRackSlot = protocol === "Siemens_S7" && (cpuModel === "S300" || cpuModel === "S400");
        if (group) group.classList.toggle("hidden", !showRackSlot);
    }

    function updateTriggerSourceUi() {
        const triggerSource = byId("cfg-trigger-source")?.value || "PLC";
        const serialSection = byId("cfg-serial-trigger-section");
        if (serialSection) serialSection.classList.toggle("hidden", triggerSource !== "SerialPhotoelectric");
    }

    function normalizeSerialPortName(value) {
        const raw = String(value || "").trim();
        if (!raw) return "";
        const match = raw.match(/\bCOM\d+\b/i);
        return match ? match[0].toUpperCase() : raw;
    }

    function ensureSerialPortOption(value, displayName) {
        const select = byId("cfg-serial-port");
        const portName = normalizeSerialPortName(value);
        if (!select || !portName) return;

        const existing = Array.from(select.options).find((opt) =>
            normalizeSerialPortName(opt.value) === portName
        );
        if (existing) {
            existing.value = portName;
            if (displayName) existing.text = displayName;
            return;
        }

        const option = document.createElement("option");
        option.value = portName;
        option.text = displayName || portName;
        select.add(option);
    }

    function handleSerialPortsDetected(data) {
        setSerialAutoDetectBusy(false);
        const select = byId("cfg-serial-port");
        if (!select) return;
        const rawPorts = data?.ports || data?.Ports || data || [];
        const ports = Array.isArray(rawPorts) ? rawPorts : [];
        const currentValue = normalizeSerialPortName(select.value);
        select.innerHTML = '<option value="">-- 请选择 COM 口 --</option>';
        let preferredValue = "";
        ports.forEach((port) => {
            const rawName = typeof port === "string" ? port : (port.name || port.Name || "");
            const displayName = typeof port === "string"
                ? port
                : (port.displayName || port.DisplayName || rawName);
            ensureSerialPortOption(rawName || displayName, displayName);
            const portName = normalizeSerialPortName(rawName || displayName);
            const isPreferred = typeof port === "object" && (port.isPreferred || port.IsPreferred);
            if (!preferredValue && (isPreferred || ports.length === 1)) {
                preferredValue = portName;
            }
        });
        if (currentValue) {
            ensureSerialPortOption(currentValue);
            select.value = currentValue;
        } else if (preferredValue) {
            select.value = preferredValue;
        }
        const selectedText = select.value ? `，已选择 ${select.value}` : "";
        window.showToast?.(`识别到 ${ports.length} 个串口${selectedText}`, ports.length ? "success" : "warning", 1400);
        window.addLog?.(`串口自动识别完成: ${ports.length} 个${selectedText}`, ports.length ? "success" : "warning");
    }

    let serialAutoDetectResetTimer = null;

    function setSerialAutoDetectBusy(isBusy) {
        const button = document.querySelector('[data-cmd="serial_auto_detect_ports"]');
        if (!button) return;

        if (serialAutoDetectResetTimer) {
            clearTimeout(serialAutoDetectResetTimer);
            serialAutoDetectResetTimer = null;
        }

        if (isBusy) {
            button.dataset.originalText = button.dataset.originalText || button.textContent.trim() || "自动识别";
            button.disabled = true;
            button.textContent = "识别中";
            serialAutoDetectResetTimer = window.setTimeout(() => setSerialAutoDetectBusy(false), 8000);
            return;
        }

        button.disabled = false;
        button.textContent = button.dataset.originalText || "自动识别";
    }

    function handleCommandDispatched(cmd) {
        if (cmd !== "serial_auto_detect_ports") return;
        setSerialAutoDetectBusy(true);
        window.showToast?.("正在识别串口...", "info", 1000);
        window.addLog?.("正在自动识别串口光电 COM 口...", "info");
    }

    function updatePlcProtocolModeUi() {
        const mode = byId("cfg-plc-protocol-mode")?.value || "Legacy";
        const handshakeOptions = byId("cfg-plc-handshake-options");
        if (handshakeOptions) handshakeOptions.classList.toggle("hidden", mode !== "HandshakeV1");
    }

    function syncDriverProviderOptions() {
        const protocolSelect = byId("cfg-plc-protocol");
        const driverSelect = byId("cfg-plc-driver-provider");
        if (!protocolSelect || !driverSelect) return;

        const isMitsubishi = (protocolSelect.value || "").startsWith("Mitsubishi");
        const mcpxOption = driverSelect.querySelector('option[value="McpX"]');
        if (mcpxOption) mcpxOption.disabled = !isMitsubishi;
        if (!isMitsubishi && driverSelect.value === "McpX") driverSelect.value = "HaoCommunication";
        updateSiemensRackSlotVisibility();
    }

    function updatePlcAddressUi() {
        const protocolSelect = byId("cfg-plc-protocol");
        const triggerInput = byId("cfg-plc-trigger");
        const resultInput = byId("cfg-plc-result");
        const helpEl = byId("cfg-plc-address-help");
        const siemensOptions = byId("cfg-plc-siemens-options");
        const protocol = protocolSelect?.value || "Mitsubishi_MC_ASCII";
        const hints = PLC_PROTOCOL_UI_HINTS[protocol] || PLC_PROTOCOL_UI_HINTS.Mitsubishi_MC_ASCII;

        if (triggerInput) triggerInput.placeholder = hints.placeholder;
        if (resultInput) resultInput.placeholder = hints.placeholder;
        if (helpEl) helpEl.textContent = hints.help;
        if (siemensOptions) siemensOptions.classList.toggle("hidden", protocol !== "Siemens_S7");

        syncDriverProviderOptions();
        updatePlcProtocolModeUi();
    }

    function validatePlcAddress(address, protocol, driver = "") {
        const compact = getCompactPlcAddress(address);
        if (!compact) return "地址不能为空";
        if (protocol.startsWith("Mitsubishi")) {
            if (driver === "McpX") {
                return /^(?:D)?\d+$/.test(compact) ? null : "McpX 当前业务适配仅支持三菱 D 区地址，例如 D100";
            }
            if (/^(?:D|M|S|T|C|R)\d+$/.test(compact)) return null;
            if (/^(?:X|Y)[0-9A-F]+$/.test(compact)) return null;
            if (/^(?:D|M|S|T|C|R|X|Y)[0-9A-F]+\.\d+$/.test(compact)) {
                return "当前信号读写使用字地址，不支持位地址";
            }
            return "三菱地址需为 D100、M100、X10 或 Y10 格式";
        }
        if (protocol === "Siemens_S7") {
            if (/^(M|I|Q|AI|AQ)\d+$/.test(compact)) return null;
            if (/^(?:[MIQ]\d+\.\d+|DB\d+\.(?:\d+|DBX\d+)\.\d+)$/.test(compact)) {
                return "当前信号读写使用字/字节地址，不支持 M0.0 或 DB1.0.0 这类位地址";
            }
            let match = compact.match(/^DB(\d+)\.(\d+)$/);
            if (match && Number(match[1]) >= 1 && Number(match[2]) >= 0) return null;
            match = compact.match(/^DB(\d+)\.DB[BWD](\d+)$/);
            if (match && Number(match[1]) >= 1 && Number(match[2]) >= 0) return null;
            return "西门子地址需为 DB1.0、DB1.DBW0、M0、I0 或 Q0 格式";
        }
        if (protocol === "Modbus_TCP") {
            return /^\d+$/.test(compact) ? null : "Modbus 地址需为数字";
        }
        if (protocol === "Omron_Fins") {
            if (/^(?:D|CIO|C|W|H|A)\d+$/.test(compact)) return null;
            if (/^(?:D|CIO|C|W|H|A)\d+\.\d+$/.test(compact)) {
                return "当前信号读写使用字地址，不支持位地址";
            }
            return "欧姆龙地址需为 D100、CIO100、W100、H100 或 A100 格式";
        }
        return "地址格式不符合当前 PLC 协议";
    }

    function validatePlcSettings() {
        const triggerSource = byId("cfg-trigger-source")?.value || "PLC";
        if (triggerSource !== "PLC") return null;

        const protocol = byId("cfg-plc-protocol")?.value || "";
        const driver = byId("cfg-plc-driver-provider")?.value || "";
        const mode = byId("cfg-plc-protocol-mode")?.value || "Legacy";
        const triggerAddress = byId("cfg-plc-trigger")?.value || "";
        const resultAddress = byId("cfg-plc-result")?.value || "";

        if (driver === "McpX" && !protocol.startsWith("Mitsubishi")) {
            return "仅三菱协议支持 McpX 驱动库";
        }

        const triggerError = validatePlcAddress(triggerAddress, protocol, driver);
        if (triggerError) return `触发地址无效: ${triggerError}`;
        const resultError = validatePlcAddress(resultAddress, protocol, driver);
        if (resultError) return `结果地址无效: ${resultError}`;

        if (mode === "HandshakeV1") {
            const handshakeFields = [
                ["TriggerSeq", "cfg-plc-trigger-seq"],
                ["ResultSeq", "cfg-plc-result-seq"],
                ["VisionOnline", "cfg-plc-vision-online"],
                ["VisionReady", "cfg-plc-vision-ready"],
                ["VisionBusy", "cfg-plc-vision-busy"],
                ["InspectionDone", "cfg-plc-inspection-done"],
                ["ErrorCode", "cfg-plc-error-code"],
                ["TraceSaved", "cfg-plc-trace-saved"],
                ["Heartbeat", "cfg-plc-heartbeat"],
                ["ResetFault", "cfg-plc-reset-fault"],
                ["TriggerAck", "cfg-plc-trigger-ack"],
                ["ResultValid", "cfg-plc-result-valid"],
                ["ResultAck", "cfg-plc-result-ack"],
            ];
            for (const [label, inputId] of handshakeFields) {
                const error = validatePlcAddress(byId(inputId)?.value || "", protocol, driver);
                if (error) return `${label} 地址无效: ${error}`;
            }
        }

        if (byId("cfg-barcode-enabled")?.checked) {
            const barcodeError = validatePlcAddress(byId("cfg-barcode-address")?.value || "", protocol, driver);
            if (barcodeError) return `条码地址无效: ${barcodeError}`;
        }

        return null;
    }

    function validateTriggerSettings() {
        const triggerSource = byId("cfg-trigger-source")?.value || "PLC";
        if (triggerSource !== "SerialPhotoelectric") return null;

        const portName = normalizeSerialPortName(byId("cfg-serial-port")?.value || "");
        if (!portName) {
            return "选择串口光电触发时，必须先选择 COM 口";
        }

        return null;
    }

    function activateSettingsTab(tabName) {
        document.querySelectorAll(".cf-settings-tab").forEach((btn) => {
            btn.classList.toggle("active", btn.dataset.settingsTab === tabName);
        });
        const panels = document.querySelectorAll("[data-settings-panel]");
        panels.forEach((panel) => {
            panel.classList.toggle("hidden", panel.dataset.settingsPanel !== tabName);
        });
        if (panels.length) return;

        const sectionMapping = {
            vision: ["vision"],
            camera: ["camera"],
        };
        const targetSections = sectionMapping[tabName] || [tabName];

        document.querySelectorAll("[data-settings-section]").forEach((section) => {
            const sectionName = section.dataset.settingsSection;
            const isActive = targetSections.includes(sectionName);
            section.classList.toggle("hidden", !isActive);
            if (isActive) {
                section.style.removeProperty("display");
            } else {
                section.style.setProperty("display", "none", "important");
            }
        });

        const content = document.querySelector("#settings-modal .cf-settings-content");
        if (content) {
            content.dataset.activeSettings = tabName;
        }
    }

    function syncSettingsChrome() {
        if (!document.body.classList.contains("cf-stitch-page")) return;
        const title = document.querySelector("#settings-modal .cf-ornate-header h3");
        if (title) title.textContent = "系统参数配置";
    }

    function moveVisionControlsToSettings() {
        const controls = byId("yolo-controls");
        const target = byId("settings-vision-controls");
        if (document.body.classList.contains("cf-stitch-page")) return;
        if (!controls || !target || controls.parentElement === target) return;
        target.appendChild(controls);
    }

    function applyMultiModelUiState(enabled) {
        const checkbox = byId("enable-multi-model");
        const statusText = byId("multi-model-status");
        const configSection = byId("multi-model-config");
        if (checkbox) checkbox.checked = enabled;
        if (statusText) {
            statusText.innerText = enabled ? "已启用" : "自动切换";
            statusText.classList.toggle("text-celadon-600", enabled);
            statusText.classList.toggle("font-bold", enabled);
            statusText.classList.toggle("text-ink-500", !enabled);
        }
        if (configSection) {
            configSection.classList.toggle("opacity-50", !enabled);
            configSection.classList.toggle("pointer-events-none", !enabled);
        }
    }

    function toggleMultiModel(enabled) {
        applyMultiModelUiState(enabled);
        bridge.sendCommand("toggle_multi_model", enabled);
        window.addLog?.(enabled ? "多模型自动切换已启用" : "多模型自动切换已禁用", enabled ? "success" : "info");
    }

    function populateSettings(config) {
        const data = typeof config === "string" ? JSON.parse(config) : (config || {});
        store.state.settings = data;

        const mapping = {
            StoragePath: "cfg-storage-path",
            TriggerSource: "cfg-trigger-source",
            SerialPhotoelectricPortName: "cfg-serial-port",
            SerialPhotoelectricBaudRate: "cfg-serial-baud",
            SerialPhotoelectricDebounceMs: "cfg-serial-debounce",
            SerialPhotoelectricTimeoutMs: "cfg-serial-timeout",
            PlcProtocol: "cfg-plc-protocol",
            PlcDriverProvider: "cfg-plc-driver-provider",
            PlcProtocolMode: "cfg-plc-protocol-mode",
            PlcIp: "cfg-plc-ip",
            PlcPort: "cfg-plc-port",
            PlcTriggerAddress: "cfg-plc-trigger",
            PlcResultAddress: "cfg-plc-result",
            PlcTriggerSeqAddress: "cfg-plc-trigger-seq",
            PlcResultSeqAddress: "cfg-plc-result-seq",
            PlcVisionOnlineAddress: "cfg-plc-vision-online",
            PlcVisionReadyAddress: "cfg-plc-vision-ready",
            PlcVisionBusyAddress: "cfg-plc-vision-busy",
            PlcInspectionDoneAddress: "cfg-plc-inspection-done",
            PlcErrorCodeAddress: "cfg-plc-error-code",
            PlcTraceSavedAddress: "cfg-plc-trace-saved",
            PlcHeartbeatAddress: "cfg-plc-heartbeat",
            PlcResetFaultAddress: "cfg-plc-reset-fault",
            PlcTriggerAckAddress: "cfg-plc-trigger-ack",
            PlcResultValidAddress: "cfg-plc-result-valid",
            PlcResultAckAddress: "cfg-plc-result-ack",
            PlcResultAckTimeoutMs: "cfg-plc-result-ack-timeout",
            PlcTriggerDelayMs: "cfg-plc-trigger-delay",
            PlcPollingIntervalMs: "cfg-plc-polling-interval",
            PlcOkValue: "cfg-plc-ok-value",
            PlcNgValue: "cfg-plc-ng-value",
            PlcSiemensCpuModel: "cfg-plc-siemens-cpu-model",
            PlcSiemensRack: "cfg-plc-siemens-rack",
            PlcSiemensSlot: "cfg-plc-siemens-slot",
            CameraName: "cfg-cam-name",
            CameraSerialNumber: "cfg-cam-serial",
            CameraManufacturer: "cfg-cam-manufacturer",
            CameraPixelFormat: "cfg-cam-pixel-format",
            ExposureTime: "cfg-cam-exposure",
            GainRaw: "cfg-cam-gain",
            MaxRetryCount: "cfg-logic-retry-count",
            RetryIntervalMs: "cfg-logic-retry-interval",
            EnableGpu: "cfg-yolo-gpu",
            GpuIndex: "cfg-yolo-gpu-index",
            IndustrialRenderMode: "cfg-industrial-render-mode",
            BarcodeEnabled: "cfg-barcode-enabled",
            BarcodeAddress: "cfg-barcode-address",
            BarcodeWordLength: "cfg-barcode-word-length",
            BarcodeEncoding: "cfg-barcode-encoding",
            BarcodeRequired: "cfg-barcode-required",
            CurrentOperatorId: "cfg-current-operator-id",
            CurrentOperatorRole: "cfg-current-operator-role",
        };

        for (const [propName, inputId] of Object.entries(mapping)) {
            if (data[propName] === undefined) continue;
            const input = byId(inputId);
            if (!input) continue;
            if (inputId === "cfg-serial-port" && data[propName]) {
                ensureSerialPortOption(data[propName]);
            }
            if (input.type === "checkbox") {
                input.checked = !!data[propName];
            } else {
                input.value = data[propName] ?? "";
            }
        }

        if (data.TaskType !== undefined && byId("task-type-select")) byId("task-type-select").value = String(data.TaskType);
        if (data.Confidence !== undefined) {
            setThresholdControl("conf-input", "conf-slider", data.Confidence);
        }
        if (data.IouThreshold !== undefined) {
            setThresholdControl("iou-input", "iou-slider", data.IouThreshold);
        }
        store.state.inspectionRuleSet = normalizeInspectionRuleSet(data.InspectionRuleSetJson, data);
        renderInspectionRules();
        const activeCamera = Array.isArray(data.Cameras)
            ? (data.Cameras.find((camera) => camera.Id === data.ActiveCameraId || camera.id === data.ActiveCameraId) ||
                data.Cameras.find((camera) => camera.IsEnabled || camera.isEnabled) ||
                data.Cameras[0])
            : null;
        const pixelFormat = data.CameraPixelFormat || activeCamera?.PixelFormat || activeCamera?.pixelFormat || "Auto";
        if (byId("cfg-cam-pixel-format")) byId("cfg-cam-pixel-format").value = pixelFormat;
        if (data.EnableMultiModelFallback !== undefined) applyMultiModelUiState(!!data.EnableMultiModelFallback);
        if (data.BarcodeEnabled !== undefined) {
            store.state.inspection = { ...store.state.inspection, barcodeEnabled: !!data.BarcodeEnabled };
            store.notify("inspection");
        }
        updatePlcAddressUi();
        updateTriggerSourceUi();
        window.updateOperatorStatus?.();
        if (store.state.modelList?.length) {
            selectModelOption(
                byId("model-select"),
                modelSelectionValueFromReference(data.CurrentModelReference, data.CurrentModelFileName));
            selectModelOption(
                byId("auxiliary1-select"),
                modelSelectionValueFromReference(data.Auxiliary1ModelReference, data.Auxiliary1ModelPath));
            selectModelOption(
                byId("auxiliary2-select"),
                modelSelectionValueFromReference(data.Auxiliary2ModelReference, data.Auxiliary2ModelPath));
        }
    }

    function initSettings(config) {
        populateSettings(config);
        window.addLog?.("系统配置已加载", "success");
    }

    function updateStoragePath(path) {
        const input = byId("cfg-storage-path");
        if (input) input.value = path || "";
    }

    function getPresetDisplayName(presetId, preset) {
        return String(preset?.name || preset?.Name || preset?.CameraName || presetId || "").trim();
    }

    function getProjectPresetNameInput() {
        return byId("project-preset-name");
    }

    function updateProjectPresetSelect(selectedId = "") {
        const select = byId("project-preset-select");
        if (!select) return;

        const currentValue = selectedId || select.value || "";
        select.innerHTML = "";

        const emptyOption = document.createElement("option");
        emptyOption.value = "";
        emptyOption.text = "-- 选择预设项目（可选）--";
        select.add(emptyOption);

        Object.entries(PROJECT_PRESETS)
            .sort((left, right) => getPresetDisplayName(left[0], left[1]).localeCompare(getPresetDisplayName(right[0], right[1]), "zh-CN"))
            .forEach(([presetId, preset]) => {
                const option = document.createElement("option");
                option.value = presetId;
                option.text = getPresetDisplayName(presetId, preset);
                select.add(option);
            });

        if (currentValue && PROJECT_PRESETS[currentValue]) {
            select.value = currentValue;
        }
    }

    function syncProjectPresetName() {
        const select = byId("project-preset-select");
        const input = getProjectPresetNameInput();
        if (!select || !input) return;

        const preset = PROJECT_PRESETS[select.value];
        input.value = preset ? getPresetDisplayName(select.value, preset) : "";
    }

    function handleProjectPresets(data) {
        const presets = data?.presets || data?.Presets || data || {};
        PROJECT_PRESETS = presets && typeof presets === "object" && !Array.isArray(presets) ? presets : {};
        const selectedId = pendingProjectPresetId;
        pendingProjectPresetId = "";
        updateProjectPresetSelect(selectedId);
        syncProjectPresetName();

        const pathLabel = byId("project-preset-path");
        const path = data?.path || data?.Path || "";
        if (pathLabel) pathLabel.textContent = path ? `预设文件: ${path}` : "";
    }

    function makeProjectPresetId(name) {
        const base = String(name || "")
            .trim()
            .replace(/\s+/g, "_")
            .replace(/[^\w\u4e00-\u9fa5-]+/g, "_")
            .replace(/_+/g, "_")
            .replace(/^_+|_+$/g, "")
            .slice(0, 48);
        return `${base || "preset"}_${Date.now().toString(36)}`;
    }

    function findProjectPresetIdByName(name) {
        const normalized = String(name || "").trim();
        return Object.entries(PROJECT_PRESETS).find(([, preset]) => getPresetDisplayName("", preset) === normalized)?.[0] || "";
    }

    function saveProjectPresetAsNew() {
        const input = getProjectPresetNameInput();
        const name = (input?.value || prompt("请输入新预设名称") || "").trim();
        if (!name) {
            alert("请输入预设名称");
            return;
        }

        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
            return;
        }
        const triggerError = validateTriggerSettings();
        if (triggerError) {
            alert(triggerError);
            return;
        }
        const sequenceError = validateInspectionRuleSettings();
        if (sequenceError) {
            alert(sequenceError);
            return;
        }

        let presetId = findProjectPresetIdByName(name);
        if (presetId && !confirm(`已存在同名预设“${name}”，是否覆盖？`)) {
            return;
        }

        if (!presetId) presetId = makeProjectPresetId(name);
        const preset = collectSettingsData();
        preset.name = name;
        pendingProjectPresetId = presetId;
        bridge.sendCommand("save_project_preset", { id: presetId, name, preset });
    }

    function updateSelectedProjectPreset() {
        const select = byId("project-preset-select");
        const presetId = select?.value || "";
        if (!presetId || !PROJECT_PRESETS[presetId]) {
            alert("请先选择要更新的预设");
            return;
        }

        const input = getProjectPresetNameInput();
        const name = (input?.value || getPresetDisplayName(presetId, PROJECT_PRESETS[presetId])).trim();
        if (!name) {
            alert("请输入预设名称");
            return;
        }

        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
            return;
        }
        const triggerError = validateTriggerSettings();
        if (triggerError) {
            alert(triggerError);
            return;
        }
        const sequenceError = validateInspectionRuleSettings();
        if (sequenceError) {
            alert(sequenceError);
            return;
        }

        const preset = collectSettingsData();
        preset.name = name;
        pendingProjectPresetId = presetId;
        bridge.sendCommand("save_project_preset", { id: presetId, name, preset });
    }

    function deleteSelectedProjectPreset() {
        const select = byId("project-preset-select");
        const presetId = select?.value || "";
        if (!presetId || !PROJECT_PRESETS[presetId]) {
            alert("请先选择要删除的预设");
            return;
        }

        const name = getPresetDisplayName(presetId, PROJECT_PRESETS[presetId]);
        if (!confirm(`确认删除预设“${name}”？`)) return;

        bridge.sendCommand("delete_project_preset", presetId);
        pendingProjectPresetId = "";
        if (getProjectPresetNameInput()) getProjectPresetNameInput().value = "";
        updateProjectPresetSelect("");
    }

    function exportConfigMigration() {
        bridge.sendCommand("export_config_migration");
    }

    function importConfigMigration() {
        bridge.sendCommand("import_config_migration");
    }

    function collectSettingsData() {
        const fieldMapping = {
            "cfg-storage-path": "StoragePath",
            "cfg-trigger-source": "TriggerSource",
            "cfg-serial-port": "SerialPhotoelectricPortName",
            "cfg-serial-baud": "SerialPhotoelectricBaudRate",
            "cfg-serial-debounce": "SerialPhotoelectricDebounceMs",
            "cfg-serial-timeout": "SerialPhotoelectricTimeoutMs",
            "cfg-plc-protocol": "PlcProtocol",
            "cfg-plc-driver-provider": "PlcDriverProvider",
            "cfg-plc-protocol-mode": "PlcProtocolMode",
            "cfg-plc-ip": "PlcIp",
            "cfg-plc-port": "PlcPort",
            "cfg-plc-trigger": "PlcTriggerAddress",
            "cfg-plc-result": "PlcResultAddress",
            "cfg-plc-trigger-seq": "PlcTriggerSeqAddress",
            "cfg-plc-result-seq": "PlcResultSeqAddress",
            "cfg-plc-vision-online": "PlcVisionOnlineAddress",
            "cfg-plc-vision-ready": "PlcVisionReadyAddress",
            "cfg-plc-vision-busy": "PlcVisionBusyAddress",
            "cfg-plc-inspection-done": "PlcInspectionDoneAddress",
            "cfg-plc-error-code": "PlcErrorCodeAddress",
            "cfg-plc-trace-saved": "PlcTraceSavedAddress",
            "cfg-plc-heartbeat": "PlcHeartbeatAddress",
            "cfg-plc-reset-fault": "PlcResetFaultAddress",
            "cfg-plc-trigger-ack": "PlcTriggerAckAddress",
            "cfg-plc-result-valid": "PlcResultValidAddress",
            "cfg-plc-result-ack": "PlcResultAckAddress",
            "cfg-plc-result-ack-timeout": "PlcResultAckTimeoutMs",
            "cfg-plc-trigger-delay": "PlcTriggerDelayMs",
            "cfg-plc-polling-interval": "PlcPollingIntervalMs",
            "cfg-plc-ok-value": "PlcOkValue",
            "cfg-plc-ng-value": "PlcNgValue",
            "cfg-plc-siemens-cpu-model": "PlcSiemensCpuModel",
            "cfg-plc-siemens-rack": "PlcSiemensRack",
            "cfg-plc-siemens-slot": "PlcSiemensSlot",
            "cfg-cam-name": "CameraName",
            "cfg-cam-serial": "CameraSerialNumber",
            "cfg-cam-manufacturer": "CameraManufacturer",
            "cfg-cam-pixel-format": "CameraPixelFormat",
            "cfg-cam-exposure": "ExposureTime",
            "cfg-cam-gain": "GainRaw",
            "cfg-logic-retry-count": "MaxRetryCount",
            "cfg-logic-retry-interval": "RetryIntervalMs",
            "cfg-yolo-gpu": "EnableGpu",
            "cfg-yolo-gpu-index": "GpuIndex",
            "cfg-industrial-render-mode": "IndustrialRenderMode",
            "cfg-barcode-enabled": "BarcodeEnabled",
            "cfg-barcode-address": "BarcodeAddress",
            "cfg-barcode-word-length": "BarcodeWordLength",
            "cfg-barcode-encoding": "BarcodeEncoding",
            "cfg-barcode-required": "BarcodeRequired",
            "cfg-current-operator-id": "CurrentOperatorId",
            "cfg-current-operator-role": "CurrentOperatorRole",
        };
        const numericFields = new Set([
            "PlcPort", "PlcTriggerDelayMs", "PlcPollingIntervalMs", "PlcOkValue", "PlcNgValue",
            "PlcResultAckTimeoutMs",
            "PlcSiemensRack", "PlcSiemensSlot", "ExposureTime", "GainRaw",
            "MaxRetryCount", "RetryIntervalMs", "GpuIndex", "BarcodeWordLength",
            "SerialPhotoelectricBaudRate", "SerialPhotoelectricDebounceMs", "SerialPhotoelectricTimeoutMs",
        ]);
        const data = {};

        for (const [inputId, propName] of Object.entries(fieldMapping)) {
            const input = byId(inputId);
            if (!input) continue;
            if (input.type === "checkbox") {
                data[propName] = input.checked;
            } else if (numericFields.has(propName) || input.type === "number") {
                const numVal = parseFloat(input.value);
                data[propName] = Number.isNaN(numVal) ? 0 : numVal;
            } else if (propName === "SerialPhotoelectricPortName") {
                data[propName] = normalizeSerialPortName(input.value);
            } else {
                data[propName] = input.value || "";
            }
        }
        if (byId("task-type-select")) data.TaskType = parseInt(byId("task-type-select").value, 10);
        data.Confidence = readThresholdControl("conf-input", "conf-slider", 0.5);
        data.IouThreshold = readThresholdControl("iou-input", "iou-slider", 0.45);
        data.InspectionRuleSetJson = JSON.stringify(getCurrentRuleSet());

        return data;
    }

    function saveSettings() {
        const plcError = validatePlcSettings();
        if (plcError) {
            alert(plcError);
            return;
        }
        const triggerError = validateTriggerSettings();
        if (triggerError) {
            alert(triggerError);
            return;
        }
        const sequenceError = validateInspectionRuleSettings();
        if (sequenceError) {
            alert(sequenceError);
            return;
        }

        const data = collectSettingsData();
        store.state.settings = { ...(store.state.settings || {}), ...data };
        window.updateOperatorStatus?.();
        bridge.sendCommand("save_settings", data);
    }

    function selectModelOption(select, preferredValue, fallbackValue = "") {
        if (!select) return;
        const preferred = String(preferredValue || "").trim();
        const fallback = String(fallbackValue || "").trim();
        const options = Array.from(select.options);
        if (preferred && options.some((option) => option.value === preferred)) {
            select.value = preferred;
            return;
        }
        const preferredFileMatch = preferred ? options.find((option) => option.dataset.fileName === preferred) : null;
        if (preferredFileMatch) {
            select.value = preferredFileMatch.value;
            return;
        }
        if (fallback && options.some((option) => option.value === fallback)) {
            select.value = fallback;
            return;
        }
        const fallbackFileMatch = fallback ? options.find((option) => option.dataset.fileName === fallback) : null;
        if (fallbackFileMatch) {
            select.value = fallbackFileMatch.value;
            return;
        }
        select.selectedIndex = options.length ? 0 : -1;
    }

    function encodeModelIdentityPart(value) {
        const bytes = encodeURIComponent(String(value || "")).replace(/%([0-9A-F]{2})/g, (_, hex) =>
            String.fromCharCode(parseInt(hex, 16)));
        return btoa(bytes).replace(/=+$/g, "").replace(/\+/g, "-").replace(/\//g, "_");
    }

    function modelSelectionValueFromReference(reference, legacyValue = "") {
        if (!reference) return String(legacyValue || "").trim();
        const type = String(reference.Type || reference.type || "");
        const modelId = reference.ModelId || reference.modelId || "";
        const version = reference.Version || reference.version || "";
        const sha256 = String(reference.Sha256 || reference.sha256 || "").trim().toLowerCase();
        const legacyFileName = reference.LegacyFileName || reference.legacyFileName || legacyValue || "";
        if ((type === "ApprovedPackage" || type === "1") && modelId && version && sha256) {
            return `approved:${encodeModelIdentityPart(modelId)}:${encodeModelIdentityPart(version)}:${sha256}`;
        }
        if ((type === "LegacyOnnx" || type === "2") && legacyFileName) {
            return `legacy:${encodeModelIdentityPart(legacyFileName)}:${sha256}`;
        }
        return String(legacyValue || "").trim();
    }

    function normalizeModelOption(item) {
        if (typeof item === "string") {
            return { value: item, text: item, fileName: item };
        }
        const value = String(item?.value || item?.Value || "").trim();
        const text = String(item?.text || item?.Text || item?.fileName || item?.FileName || value).trim();
        const fileName = String(item?.fileName || item?.FileName || text).trim();
        return { value, text, fileName };
    }

    function initModelList(files, notifyBackend = false) {
        const rawModels = Array.isArray(files) ? files : (files?.models || files?.Models || []);
        const models = rawModels.map(normalizeModelOption).filter((model) => model.value);
        store.state.modelList = models;
        const select = byId("model-select");
        if (!select) return;

        const settings = store.state.settings || {};
        const previousPrimary = select.value;
        const previousAux1 = byId("auxiliary1-select")?.value || "";
        const previousAux2 = byId("auxiliary2-select")?.value || "";

        select.innerHTML = "";
        if (!models.length) {
            const option = document.createElement("option");
            option.text = "未找到可用模型";
            option.value = "";
            select.add(option);
            return;
        }

        models.forEach((model) => {
            const option = document.createElement("option");
            option.value = model.value;
            option.text = model.text;
            option.dataset.fileName = model.fileName;
            select.add(option);
        });
        selectModelOption(
            select,
            modelSelectionValueFromReference(settings.CurrentModelReference, settings.CurrentModelFileName),
            previousPrimary);

        ["auxiliary1-select", "auxiliary2-select"].forEach((id) => {
            const auxSelect = byId(id);
            if (!auxSelect) return;
            auxSelect.innerHTML = '<option value="">不使用</option>';
            models.forEach((model) => {
                const option = document.createElement("option");
                option.value = model.value;
                option.text = model.text;
                option.dataset.fileName = model.fileName;
                auxSelect.add(option);
            });
        });

        selectModelOption(
            byId("auxiliary1-select"),
            modelSelectionValueFromReference(settings.Auxiliary1ModelReference, settings.Auxiliary1ModelPath),
            previousAux1);
        selectModelOption(
            byId("auxiliary2-select"),
            modelSelectionValueFromReference(settings.Auxiliary2ModelReference, settings.Auxiliary2ModelPath),
            previousAux2);
        if (notifyBackend) bridge.sendCommand("change_model", select.value);
        window.addLog?.(`成功加载 ${models.length} 个模型`, "info");
    }

    function openSettingsModal(config) {
        if (config) populateSettings(config);
        byId("settings-modal")?.classList.remove("hidden");
        syncSettingsChrome();
        activateSettingsTab("vision");
        bridge.sendCommand("get_project_presets");
        bridge.sendCommand("open_settings");
    }

    function openSettingsFromBackend(config) {
        if (config) populateSettings(config);
        byId("settings-modal")?.classList.remove("hidden");
        syncSettingsChrome();
        activateSettingsTab("vision");
    }

    function closeSettingsModal() {
        byId("settings-modal")?.classList.add("hidden");
    }

    function updateConfidence(val) {
        const fallback = store.state.settings?.Confidence ?? 0.5;
        const rawValue = byId("conf-input") ? val : parseFloat(val) / 100;
        const value = setThresholdControl("conf-input", "conf-slider", rawValue, fallback);
        store.state.settings = { ...(store.state.settings || {}), Confidence: value };
        bridge.sendCommand("set_confidence", value);
    }

    function updateIou(val) {
        const fallback = store.state.settings?.IouThreshold ?? 0.45;
        const rawValue = byId("iou-input") ? val : parseFloat(val) / 100;
        const value = setThresholdControl("iou-input", "iou-slider", rawValue, fallback);
        store.state.settings = { ...(store.state.settings || {}), IouThreshold: value };
        bridge.sendCommand("set_iou", value);
    }

    function updateTaskType(val) {
        const taskType = parseInt(val, 10);
        bridge.sendCommand("set_task_type", taskType);
    }

    function loadProjectPreset(presetId) {
        if (!presetId) {
            syncProjectPresetName();
            return;
        }

        const preset = PROJECT_PRESETS[presetId];
        if (!preset) {
            window.addLog?.(`未找到预设配置: ${presetId}`, "error");
            return;
        }

        const textAssignments = {
            "cfg-trigger-source": preset.TriggerSource ?? "PLC",
            "cfg-serial-port": preset.SerialPhotoelectricPortName ?? "",
            "cfg-serial-baud": preset.SerialPhotoelectricBaudRate ?? 9600,
            "cfg-serial-debounce": preset.SerialPhotoelectricDebounceMs ?? 50,
            "cfg-serial-timeout": preset.SerialPhotoelectricTimeoutMs ?? 1000,
            "cfg-plc-ip": preset.PlcIp,
            "cfg-plc-port": preset.PlcPort,
            "cfg-plc-trigger": preset.PlcTriggerAddress,
            "cfg-plc-result": preset.PlcResultAddress,
            "cfg-plc-protocol": preset.PlcProtocol,
            "cfg-plc-trigger-delay": preset.PlcTriggerDelayMs ?? 800,
            "cfg-plc-polling-interval": preset.PlcPollingIntervalMs ?? 500,
            "cfg-plc-ok-value": preset.PlcOkValue ?? 1,
            "cfg-plc-ng-value": preset.PlcNgValue ?? 0,
            "cfg-plc-driver-provider": preset.PlcDriverProvider ?? "HaoCommunication",
            "cfg-plc-protocol-mode": preset.PlcProtocolMode ?? "Legacy",
            "cfg-plc-trigger-seq": preset.PlcTriggerSeqAddress ?? "D557",
            "cfg-plc-result-seq": preset.PlcResultSeqAddress ?? "D558",
            "cfg-plc-vision-online": preset.PlcVisionOnlineAddress ?? "D559",
            "cfg-plc-vision-ready": preset.PlcVisionReadyAddress ?? "D560",
            "cfg-plc-vision-busy": preset.PlcVisionBusyAddress ?? "D561",
            "cfg-plc-inspection-done": preset.PlcInspectionDoneAddress ?? "D562",
            "cfg-plc-error-code": preset.PlcErrorCodeAddress ?? "D563",
            "cfg-plc-trace-saved": preset.PlcTraceSavedAddress ?? "D564",
            "cfg-plc-heartbeat": preset.PlcHeartbeatAddress ?? "D565",
            "cfg-plc-reset-fault": preset.PlcResetFaultAddress ?? "D566",
            "cfg-plc-trigger-ack": preset.PlcTriggerAckAddress ?? "D567",
            "cfg-plc-result-valid": preset.PlcResultValidAddress ?? "D568",
            "cfg-plc-result-ack": preset.PlcResultAckAddress ?? "D569",
            "cfg-plc-result-ack-timeout": preset.PlcResultAckTimeoutMs ?? 2000,
            "cfg-plc-siemens-cpu-model": preset.PlcSiemensCpuModel ?? "S1200",
            "cfg-plc-siemens-rack": preset.PlcSiemensRack ?? 0,
            "cfg-plc-siemens-slot": preset.PlcSiemensSlot ?? 2,
            "cfg-barcode-address": preset.BarcodeAddress ?? "D570",
            "cfg-barcode-word-length": preset.BarcodeWordLength ?? 16,
            "cfg-barcode-encoding": preset.BarcodeEncoding ?? "ASCII",
            "cfg-cam-name": getPresetDisplayName(presetId, preset),
            "cfg-cam-serial": preset.CameraSerialNumber,
            "cfg-cam-manufacturer": preset.CameraManufacturer ?? "Huaray",
            "cfg-cam-pixel-format": preset.CameraPixelFormat ?? preset.PixelFormat ?? "Auto",
            "cfg-cam-exposure": preset.ExposureTime,
            "cfg-cam-gain": preset.GainRaw ?? preset.Gain ?? 1.1,
            "cfg-logic-retry-count": preset.MaxRetryCount ?? 1,
            "cfg-logic-retry-interval": preset.RetryIntervalMs ?? 2000,
            "cfg-yolo-gpu-index": preset.GpuIndex ?? 0,
            "cfg-storage-path": preset.StoragePath ?? "C:\\GreeVisionData",
        };
        Object.entries(textAssignments).forEach(([id, value]) => {
            const input = byId(id);
            if (id === "cfg-serial-port" && value) ensureSerialPortOption(value);
            if (input) input.value = value;
        });

        const checkboxAssignments = {
            "cfg-barcode-enabled": preset.BarcodeEnabled ?? false,
            "cfg-barcode-required": preset.BarcodeRequired ?? false,
            "cfg-yolo-gpu": preset.EnableGpu ?? false,
            "cfg-industrial-render-mode": preset.IndustrialRenderMode ?? true,
        };
        Object.entries(checkboxAssignments).forEach(([id, value]) => {
            const cb = byId(id);
            if (cb) cb.checked = value;
        });

        updatePlcAddressUi();
        updatePlcProtocolModeUi();
        updateSiemensRackSlotVisibility();
        updateTriggerSourceUi();
        store.state.inspectionRuleSet = normalizeInspectionRuleSet(preset.InspectionRuleSetJson, preset);
        renderInspectionRules();
        syncProjectPresetName();
        window.addLog?.(`已加载预设: ${getPresetDisplayName(presetId, preset)}`, "success");
    }

    function handleConfigSnapshot(data) {
        const config = data?.config || data?.Config || data;
        if (data?.storagePath || data?.StoragePath) updateStoragePath(data.storagePath || data.StoragePath);
        if (config) populateSettings(config);
        if (data?.open || data?.Open) openSettingsFromBackend(config);
    }

    function handleBootstrapSnapshot(data) {
        store.applyBootstrapSnapshot(data);
        if (data?.config || data?.Config) populateSettings(data.config || data.Config);
        if (data?.storagePath || data?.StoragePath) updateStoragePath(data.storagePath || data.StoragePath);
        const models = data?.models || data?.Models;
        if (Array.isArray(models)) initModelList(models, false);
        const cameras = data?.cameras || data?.Cameras;
        if (Array.isArray(cameras) && typeof window.receiveCameraList === "function") {
            window.receiveCameraList({
                cameras,
                activeId: data.activeCameraId || data.ActiveCameraId || "",
            });
        }
    }

    function collectDataset() {
        const btn = byId("btn-collect-dataset");
        const resultDiv = byId("dataset-collect-result");
        if (!btn) return;

        btn.disabled = true;
        btn.textContent = "收集中，请稍候...";
        resultDiv.classList.add("hidden");

        bridge.sendCommand("collect_dataset");
    }

    function handleDatasetCollectResult(data) {
        const btn = byId("btn-collect-dataset");
        const resultDiv = byId("dataset-collect-result");
        if (!btn || !resultDiv) return;

        btn.disabled = false;
        btn.textContent = "一键收集训练数据集";

        if (data?.success) {
            resultDiv.className = "mt-2 text-[10px] text-green-600";
            resultDiv.textContent = `✅ 收集完成！共 ${data.totalCopied} 张（NG ${data.failCopied} / OK ${data.passCopied}），已保存至：${data.outputDirectory}`;
        } else {
            resultDiv.className = "mt-2 text-[10px] text-red-500";
            resultDiv.textContent = `❌ 收集失败：${data?.message || "未知错误"}`;
        }
        resultDiv.classList.remove("hidden");
    }

    Object.assign(window, {
        activateSettingsTab,
        applyMultiModelUiState,
        closeSettingsModal,
        deleteSelectedProjectPreset,
        exportConfigMigration,
        handleProjectPresets,
        importConfigMigration,
        initModelList,
        initSettings,
        loadProjectPreset,
        moveVisionControlsToSettings,
        openSettingsModal,
        populateSettings,
        saveSettings,
        saveProjectPresetAsNew,
        syncDriverProviderOptions,
        syncProjectPresetName,
        toggleMultiModel,
        updateSelectedProjectPreset,
        updateConfidence,
        updateIou,
        addInspectionRule,
        duplicateInspectionRule,
        moveInspectionRule,
        removeInspectionRule,
        renderInspectionRules,
        updateInspectionRule,
        updatePlcAddressUi,
        updatePlcProtocolModeUi,
        updateSiemensRackSlotVisibility,
        updateStoragePath,
        updateTaskType,
        collectDataset,
        handleDatasetCollectResult,
        handleCommandDispatched,
        handleSerialPortsDetected,
        updateTriggerSourceUi,
    });

    bridge.registerMessageHandler("bootstrapSnapshot", handleBootstrapSnapshot);
    bridge.registerMessageHandler("configSnapshot", handleConfigSnapshot);
    bridge.registerMessageHandler("modelList", (data) => initModelList(data?.models || data?.Models || data || [], false));
    bridge.registerMessageHandler("modelLabels", (data) => {
        store.state.modelLabels = data?.labels || data?.Labels || data || [];
        updateRuleLabelOptions();
    });
    bridge.registerMessageHandler("projectPresets", handleProjectPresets);
    bridge.registerMessageHandler("datasetCollectResult", handleDatasetCollectResult);
    bridge.registerMessageHandler("serialPortsDetected", handleSerialPortsDetected);
})();

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

    function byId(id) {
        return document.getElementById(id);
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
        const pixelFormat = byId("cfg-cam-pixel-format")?.value || "Auto";
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
        setSuperSearchFeedback();
        directConnectPending = null;
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

        bridge.sendCommand("direct_connect_camera", {
            serialNumber: camera.serialNumber || "",
            ip: camera.ip || "",
            manufacturer: camera.manufacturer || "Huaray",
            model: camera.model || camera.userDefinedName || "Camera",
        });
        window.addLog?.(`正在直连相机: ${camera.serialNumber || camera.model || "-"}`, "info");
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
            exposureTime: parseFloat(byId("cfg-cam-exposure")?.value) || 50000,
            gain: parseFloat(byId("cfg-cam-gain")?.value) || 1.0,
        };
    }

    function requestCameraPreviewFrame() {
        setCameraPreviewStatus({ isBusy: true, message: "正在连接相机并获取画面..." });
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
})();

// ==========================================
// ClearFrost history, logs and gallery
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const { escapeHtml } = window.CF_UTILS;
    const errorAdvice = window.CF_ERROR_ADVICE;
    const TRACE_DEFAULT_PAGE_SIZE = 100;
    let tracePagerState = createTracePagerState();
    let activeTraceRecord = null;

    function byId(id) {
        return document.getElementById(id);
    }

    function createTracePagerState() {
        return {
            pages: [],
            pageIndex: -1,
            pageSize: TRACE_DEFAULT_PAGE_SIZE,
            pendingRequestId: "",
            lastHandledRequestId: "",
            pendingDirection: "",
        };
    }

    function resetTracePagerState() {
        tracePagerState = createTracePagerState();
        updateTracePaginationUi();
    }

    function getActiveTracePage() {
        if (tracePagerState.pageIndex < 0) return null;
        return tracePagerState.pages[tracePagerState.pageIndex] || null;
    }

    function toBoolean(value) {
        if (typeof value === "boolean") return value;
        if (typeof value === "number") return value !== 0;
        if (typeof value === "string") {
            const normalized = value.trim().toLowerCase();
            return normalized === "true" || normalized === "1";
        }
        return Boolean(value);
    }

    function toNullableNumber(value) {
        if (value === undefined || value === null || value === "") return null;
        const numberValue = Number(value);
        return Number.isFinite(numberValue) ? numberValue : null;
    }

    function setTraceLoadingState(isLoading) {
        const grid = byId("ng-image-grid");
        const prevButton = byId("trace-prev-page");
        const nextButton = byId("trace-next-page");

        if (prevButton) prevButton.disabled = isLoading || tracePagerState.pageIndex <= 0;
        if (nextButton) nextButton.disabled = isLoading || (!getActiveTracePage()?.hasMore && tracePagerState.pageIndex + 1 >= tracePagerState.pages.length);

        if (!isLoading || !grid) return;
        grid.innerHTML = `
            <div class="cf-trace-empty">
                <div class="flex flex-col items-center gap-3">
                    <div class="animate-spin rounded-full h-8 w-8 border-b-2 border-celadon-500"></div>
                    <span>正在加载追溯页...</span>
                </div>
            </div>`;
    }

    function updateTracePaginationUi() {
        const badge = byId("gallery-count");
        const status = byId("trace-page-status");
        const prevButton = byId("trace-prev-page");
        const nextButton = byId("trace-next-page");
        const activePage = getActiveTracePage();
        const pageNumber = tracePagerState.pageIndex >= 0 ? tracePagerState.pageIndex + 1 : 0;
        const recordCount = activePage?.records?.length || 0;
        const cachedNextPage = tracePagerState.pageIndex + 1 < tracePagerState.pages.length;
        const canGoPrev = tracePagerState.pageIndex > 0;
        const canGoNext = cachedNextPage || Boolean(activePage?.hasMore);

        if (badge) {
            badge.textContent = pageNumber > 0 ? `${recordCount} 条 · 第 ${pageNumber} 页` : "0 条";
        }

        if (status) {
            if (!activePage) {
                status.textContent = "等待查询";
            } else {
                status.textContent = `第 ${pageNumber} 页 · ${recordCount} 条${activePage.hasMore ? " · 还有下一页" : cachedNextPage ? " · 已缓存下一页" : ""}`;
            }
        }

        if (prevButton) prevButton.disabled = !canGoPrev;
        if (nextButton) nextButton.disabled = !canGoNext;
    }

    function renderTracePage(page) {
        const grid = byId("ng-image-grid");
        if (!grid) return;

        grid.innerHTML = "";
        const activePage = page || getActiveTracePage();
        const records = activePage?.records || [];
        const pageNumber = tracePagerState.pageIndex >= 0 ? tracePagerState.pageIndex + 1 : 0;
        const badge = byId("gallery-count");

        if (badge) {
            badge.textContent = pageNumber > 0 ? `${records.length} 条 · 第 ${pageNumber} 页` : "0 条";
        }

        if (!records.length) {
            grid.innerHTML = '<div class="cf-trace-empty">此时间段未发现异常图片记录</div>';
            updateTracePaginationUi();
            return;
        }

        const fragment = document.createDocumentFragment();
        for (const record of records) {
            const url = record.thumbnailUrl || record.displayImageUrl || "";
            const card = document.createElement("div");
            const resultText = record.isQualified ? "OK" : "NG";
            const resultClass = record.isQualified ? "ok" : "ng";
            const reviewLabel = record.hasRenderedImage ? "复查图" : "无复查图";
            const model = record.modelVersion || record.modelName || "-";
            const adviceText = getTraceAdviceText(record, "建议");
            const adviceMarkup = adviceText
                ? `<p class="cf-trace-advice">${escapeHtml(adviceText)}</p>`
                : "";
            const imageMarkup = url
                ? `<img src="${url}" loading="lazy" decoding="async" alt="${escapeHtml(record.inspectionId)}">`
                : `<div class="cf-trace-thumb-missing">无图像</div>`;
            card.className = "cf-trace-card";
            card.innerHTML = `<div class="cf-trace-thumb">
                    ${imageMarkup}
                    <span class="${resultClass}">${escapeHtml(resultText)}</span>
                    <em>${escapeHtml(reviewLabel)}</em>
                </div>
                <div class="cf-trace-card-body">
                    <div>
                        <p>${escapeHtml(record.productBarcode || "-")}</p>
                        <p>${escapeHtml(record.timestamp || "-")}</p>
                        <p>ID: ${escapeHtml(record.inspectionId || "-")}</p>
                        <p>模型: ${escapeHtml(model)}</p>
                        <p>相机: ${escapeHtml(record.cameraId || "-")}</p>
                        ${adviceMarkup}
                    </div>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8"
                            d="M12 3 2.7 20h18.6L12 3Zm0 6v5m0 3h.01" />
                    </svg>
                </div>
                <button type="button">查看详情</button>`;
            card.onclick = () => openTraceViewer(record, "rendered");
            fragment.appendChild(card);
        }

        grid.appendChild(fragment);
        updateTracePaginationUi();
    }

    function normalizeTracePage(data) {
        const rawRecords = Array.isArray(data) ? data : (data?.records || data?.Records || data?.images || data?.Images || []);
        return {
            records: rawRecords.map(normalizeTraceRecord),
            hasMore: toBoolean(pickTraceValue(data, "hasMore", "HasMore")),
            pageSize: Number(pickTraceValue(data, "pageSize", "PageSize")) || TRACE_DEFAULT_PAGE_SIZE,
            nextCursorTimestamp: pickTraceValue(data, "nextCursorTimestamp", "NextCursorTimestamp") || "",
            nextCursorId: toNullableNumber(pickTraceValue(data, "nextCursorId", "NextCursorId")),
        };
    }

    function requestTracePage(direction = "initial") {
        syncTraceControls();
        const date = byId("gallery-date-picker")?.value || window.currentNGDate;
        const hour = byId("trace-hour-select")?.value || window.currentNGHour || "";
        if (!date) return;

        window.currentNGDate = date;
        window.currentNGHour = hour;

        const activePage = getActiveTracePage();
        const payload = {
            date,
            hour,
            pageSize: tracePagerState.pageSize,
        };

        if (direction === "next" && activePage?.nextCursorTimestamp && activePage?.nextCursorId !== null && activePage?.nextCursorId !== undefined) {
            payload.afterTimestamp = activePage.nextCursorTimestamp;
            payload.afterId = activePage.nextCursorId;
        }

        setTraceLoadingState(true);
        tracePagerState.pendingDirection = direction;
        tracePagerState.pendingRequestId = bridge.sendCommand("get_ng_images", payload);
        updateTracePaginationUi();
    }

    function loadPreviousTracePage() {
        if (tracePagerState.pageIndex <= 0) return;
        tracePagerState.pageIndex -= 1;
        renderTracePage();
    }

    function loadNextTracePage() {
        const cachedNextPage = tracePagerState.pages[tracePagerState.pageIndex + 1];
        if (cachedNextPage) {
            tracePagerState.pageIndex += 1;
            renderTracePage();
            return;
        }

        const activePage = getActiveTracePage();
        if (!activePage?.hasMore) return;
        requestTracePage("next");
    }

    function syncLogHistoryChrome() {
        if (!document.body.classList.contains("cf-stitch-page")) return;
        const title = document.querySelector("#log-history-modal .cf-ornate-header h3");
        if (title) title.textContent = "检测日志 (Detection Logs)";
    }

    function openLogHistoryModal() {
        byId("log-history-modal")?.classList.remove("hidden");
        syncLogHistoryChrome();
        bridge.sendCommand("get_detection_logs");
    }

    function syncTraceControls() {
        const dateInput = byId("gallery-date-picker");
        const dateSlot = byId("trace-date-slot");
        if (dateInput && dateSlot && dateInput.parentElement !== dateSlot) {
            dateSlot.appendChild(dateInput);
        }
    }

    function closeLogHistoryModal() {
        byId("log-history-modal")?.classList.add("hidden");
    }

    function openGalleryModal() {
        byId("gallery-modal")?.classList.remove("hidden");
        syncTraceControls();
        resetTracePagerState();
        const badge = byId("gallery-count");
        if (badge) badge.textContent = "0 张";
        bridge.sendCommand("get_ng_dates");
    }

    function closeGalleryModal() {
        byId("gallery-modal")?.classList.add("hidden");
    }

    function openStatisticsHistoryModal() {
        byId("statistics-history-modal")?.classList.remove("hidden");
        requestStatisticsHistory(30);
    }

    function closeStatisticsHistoryModal() {
        byId("statistics-history-modal")?.classList.add("hidden");
    }

    function requestStatisticsHistory(days) {
        const table = byId("statistics-history-table");
        if (table) table.innerHTML = '<tr><td colspan="5" class="text-center py-8">加载中...</td></tr>';
        bridge.sendCommand("get_statistics_history", days);
    }

    function closeImageViewer() {
        byId("image-viewer")?.classList.add("hidden");
    }

    function receiveStatisticsHistory(data) {
        const records = Array.isArray(data) ? data : (data?.records || data?.Records || []);
        const tbody = byId("statistics-history-table");
        if (!tbody) return;

        if (!records.length) {
            tbody.innerHTML = '<tr><td colspan="5" class="px-4 py-10 text-center text-slate-400 italic">暂无历史数据</td></tr>';
            return;
        }

        tbody.innerHTML = records.map((item, index) => {
            const isToday = index === 0;
            const rowClass = isToday ? "bg-celadon-50/50" : "hover:bg-slate-50";
            const dateLabel = isToday ? `${escapeHtml(item.date)} <span class="text-[9px] text-celadon-600 font-bold">(今日)</span>` : escapeHtml(item.date);
            const rate = Number(item.rate || 0);
            const rateColor = rate >= 95 ? "text-bamboo-600" : rate >= 80 ? "text-gamboge-500" : "text-rouge-600";
            return `
                <tr class="${rowClass} transition-colors">
                    <td class="px-4 py-3 font-medium text-slate-700">${dateLabel}</td>
                    <td class="px-4 py-3 text-center font-mono font-bold text-slate-600">${item.total || 0}</td>
                    <td class="px-4 py-3 text-center font-mono text-bamboo-600">${item.ok || 0}</td>
                    <td class="px-4 py-3 text-center font-mono text-rouge-600">${item.ng || 0}</td>
                    <td class="px-4 py-3 text-center font-mono font-bold ${rateColor}">${rate.toFixed(1)}%</td>
                </tr>
            `;
        }).join("");
    }

    function updateDetectionLogTable(data) {
        if (data === undefined) {
            bridge.sendCommand("get_detection_logs");
            return;
        }
        const logs = Array.isArray(data) ? data : (data?.logs || data?.Logs || []);
        const tbody = byId("log-history-table");
        const badge = byId("log-count-badge");
        if (!tbody) return;

        if (!logs.length) {
            tbody.innerHTML = '<tr><td colspan="3" class="px-4 py-10 text-center text-slate-400 italic">暂无检测日志</td></tr>';
            if (badge) badge.textContent = "0 条";
            return;
        }

        if (badge) badge.textContent = `${logs.length} 条`;
        tbody.innerHTML = logs.slice(0, 500).map((log) => {
            const result = String(log.result || "");
            const isNg = result.includes("不合格") || result.includes("NG");
            const resultClass = isNg
                ? "bg-rouge-50 text-rouge-600 border-rouge-200"
                : "bg-bamboo-50 text-bamboo-600 border-bamboo-200";
            const details = String(log.details || "");
            return `
                <tr class="hover:bg-slate-50 transition-colors">
                    <td class="px-4 py-3 font-mono text-slate-600 whitespace-nowrap">${escapeHtml(log.time || "-")}</td>
                    <td class="px-4 py-3 text-center">
                        <span class="inline-block px-2 py-0.5 rounded-full text-[10px] font-bold border ${resultClass}">
                            ${escapeHtml(result || "-")}
                        </span>
                    </td>
                    <td class="px-4 py-3 text-slate-500 max-w-md whitespace-normal break-words leading-snug" title="${escapeHtml(details)}">
                        ${escapeHtml(details || "-")}
                    </td>
                </tr>
            `;
        }).join("");
    }

    function buildAuditQuery() {
        return {
            startTime: byId("audit-start-time")?.value || "",
            endTime: byId("audit-end-time")?.value || "",
            operation: byId("audit-operation-filter")?.value || "",
            operatorId: byId("audit-operator-filter")?.value || "",
            status: byId("audit-status-filter")?.value || "",
            limit: 500,
        };
    }

    function openAuditModal() {
        byId("audit-modal")?.classList.remove("hidden");
        queryAuditRecords();
    }

    function closeAuditModal() {
        byId("audit-modal")?.classList.add("hidden");
    }

    function setAuditError(message) {
        const node = byId("audit-error");
        if (!node) return;
        node.textContent = message || "";
        node.classList.toggle("hidden", !message);
    }

    function queryAuditRecords() {
        setAuditError("");
        const tbody = byId("audit-table");
        if (tbody) {
            tbody.innerHTML = '<tr><td colspan="8" class="px-4 py-10 text-center text-slate-400 italic">Loading audit records...</td></tr>';
        }
        bridge.sendCommand("query_audit_records", buildAuditQuery());
    }

    function exportAuditRecords() {
        setAuditError("");
        bridge.sendCommand("export_audit_records", buildAuditQuery());
    }

    function updateAuditRecords(data) {
        const records = Array.isArray(data) ? data : (data?.records || data?.Records || []);
        const error = data?.error || data?.Error || "";
        const tbody = byId("audit-table");
        const badge = byId("audit-count-badge");
        if (!tbody) return;

        if (error) {
            setAuditError(error);
        }

        if (badge) badge.textContent = `${records.length} rows`;
        if (!records.length) {
            tbody.innerHTML = '<tr><td colspan="8" class="px-4 py-10 text-center text-slate-400 italic">No audit records matched.</td></tr>';
            return;
        }

        tbody.innerHTML = records.map((record) => {
            const status = String(record.status || "");
            const statusClass = status === "Failed" || status === "Denied"
                ? "bg-rouge-50 text-rouge-600 border-rouge-200"
                : "bg-bamboo-50 text-bamboo-600 border-bamboo-200";
            return `
                <tr class="hover:bg-slate-50 transition-colors">
                    <td class="px-3 py-3 whitespace-nowrap">${escapeHtml(record.timestamp || "-")}</td>
                    <td class="px-3 py-3">${escapeHtml(record.operation || "-")}</td>
                    <td class="px-3 py-3"><span class="inline-block px-2 py-0.5 rounded-full text-[10px] font-bold border ${statusClass}">${escapeHtml(status || "-")}</span></td>
                    <td class="px-3 py-3">${escapeHtml(record.operatorId || "-")}</td>
                    <td class="px-3 py-3">${escapeHtml(record.role || "-")}</td>
                    <td class="px-3 py-3">${escapeHtml(record.inspectionId || "-")}</td>
                    <td class="px-3 py-3 max-w-md whitespace-normal break-words">${escapeHtml(record.details || record.reason || "-")}</td>
                    <td class="px-3 py-3 max-w-xs whitespace-normal break-words">${escapeHtml(record.failureBlocker || "-")}</td>
                </tr>
            `;
        }).join("");
    }

    function updateAuditExport(data) {
        const error = data?.error || data?.Error || "";
        const path = data?.path || data?.Path || "";
        if (error) {
            setAuditError(error);
            return;
        }

        const node = byId("audit-export-path");
        if (node) node.textContent = path ? `Exported: ${path}` : "";
        window.showToast?.("Audit CSV exported", "success", 1600);
    }

    function updateNGDates(data) {
        if (data === undefined) {
            bridge.sendCommand("get_ng_dates");
            return;
        }
        const dates = Array.isArray(data) ? data : (data?.dates || data?.Dates || []);
        const list = byId("ng-date-list");
        if (!list) return;
        list.innerHTML = "";

        if (!dates.length) {
            list.innerHTML = '<div class="text-[10px] text-ink-300 p-4 text-center italic font-serif opacity-50">暂无历史存根</div>';
            if (byId("ng-hour-list")) byId("ng-hour-list").innerHTML = "";
            if (byId("ng-image-grid")) byId("ng-image-grid").innerHTML = '<div class="cf-trace-empty">此时间段未发现异常图片记录</div>';
            resetTracePagerState();
            return;
        }

        dates.forEach((date) => {
            const div = document.createElement("div");
            div.className = "p-2.5 hover:bg-celadon-50 hover:text-celadon-700 cursor-pointer rounded-xl text-[11px] text-ink-500 font-bold transition-[background-color,border-color,color,box-shadow] border border-transparent hover:border-celadon-100 mb-1";
            div.innerText = date;
            div.onclick = () => {
                Array.from(list.children).forEach((child) => {
                    child.className = "p-2.5 hover:bg-celadon-50 hover:text-celadon-700 cursor-pointer rounded-xl text-[11px] text-ink-500 font-bold transition-[background-color,border-color,color,box-shadow] border border-transparent hover:border-celadon-100 mb-1";
                });
                div.className = "p-2.5 bg-celadon-50 text-celadon-700 cursor-pointer rounded-xl text-[11px] font-black transition-[background-color,border-color,color,box-shadow] shadow-sm border border-celadon-200 mb-1";
                window.currentNGDate = date;
                window.currentNGHour = "";
                resetTracePagerState();
                if (byId("ng-hour-list")) byId("ng-hour-list").innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 opacity-50 font-serif">读取中...</div>';
                if (byId("ng-image-grid")) byId("ng-image-grid").innerHTML = "";
                bridge.sendCommand("get_ng_hours", date);
            };
            list.appendChild(div);
        });

        list.firstElementChild?.click?.();
    }

    function updateNGHours(data) {
        const hours = Array.isArray(data) ? data : (data?.hours || data?.Hours || []);
        const list = byId("ng-hour-list");
        if (!list) return;
        list.innerHTML = "";
        const hourSelect = byId("trace-hour-select");
        if (hourSelect) {
            hourSelect.innerHTML = "";
            delete hourSelect.dataset.synced;
        }

        if (!hours.length) {
            if (hourSelect) hourSelect.innerHTML = '<option value="">全部时段</option>';
            list.innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 font-serif opacity-50">无时段数据</div>';
            if (byId("ng-image-grid")) byId("ng-image-grid").innerHTML = '<div class="cf-trace-empty">此时间段未发现异常图片记录</div>';
            resetTracePagerState();
            return;
        }

        hours.forEach((hour) => {
            if (hourSelect) {
                const option = document.createElement("option");
                option.value = hour;
                option.textContent = `${hour}:00 - ${String(Number(hour) + 1).padStart(2, "0")}:00`;
                hourSelect.appendChild(option);
            }
            const div = document.createElement("div");
            div.className = "px-4 py-2 bg-white/60 border border-slate-100 rounded-xl text-[11px] cursor-pointer hover:bg-white hover:text-celadon-600 hover:border-celadon-200 transition-[background-color,border-color,color,box-shadow] font-bold text-ink-500 shadow-sm flex items-center justify-between group";
            div.innerHTML = `<span>${escapeHtml(hour)}:00 时段</span><span class="opacity-0 group-hover:opacity-100">›</span>`;
            div.onclick = () => {
                Array.from(list.children).forEach((child) => {
                    child.className = "px-4 py-2 bg-white/60 border border-slate-100 rounded-xl text-[11px] cursor-pointer hover:bg-white hover:text-celadon-600 hover:border-celadon-200 transition-[background-color,border-color,color,box-shadow] font-bold text-ink-500 shadow-sm flex items-center justify-between group";
                });
                div.className = "px-4 py-2 bg-celadon-600 border-celadon-600 text-white rounded-xl text-[11px] cursor-pointer transition-[background-color,border-color,color,box-shadow] font-bold shadow-md flex items-center justify-between";
                window.currentNGHour = hour;
                resetTracePagerState();
                if (byId("ng-image-grid")) {
                    byId("ng-image-grid").innerHTML = '<div class="col-span-full h-full flex flex-col items-center justify-center py-20 text-ink-300 opacity-50"><div class="animate-spin rounded-full h-8 w-8 border-b-2 border-celadon-500 mb-4"></div><span class="text-xs font-serif italic">正在索引影像档案...</span></div>';
                }
                requestTracePage("initial");
            };
            list.appendChild(div);
        });

        if (!window.currentNGHour) {
            list.firstElementChild?.click?.();
        }
    }

    function selectTraceHour(hour) {
        window.currentNGHour = hour || byId("trace-hour-select")?.value || window.currentNGHour;
    }

    function searchTraceImages() {
        syncTraceControls();
        const date = byId("gallery-date-picker")?.value || window.currentNGDate;
        const hour = byId("trace-hour-select")?.value || window.currentNGHour || "";
        if (date) window.currentNGDate = date;
        window.currentNGHour = hour;
        resetTracePagerState();
        requestTracePage("initial");
    }

    function pickTraceValue(source, ...keys) {
        if (!source) return "";
        for (const key of keys) {
            if (source[key] !== undefined && source[key] !== null) return source[key];
        }
        return "";
    }

    function normalizeTraceRecord(item) {
        if (typeof item === "string") {
            const baseUrl = `http://ng-images.local/Unqualified/${window.currentNGDate}/${window.currentNGHour}/`;
            const url = baseUrl + encodeURIComponent(item);
            return {
                filename: item,
                inspectionId: String(item).replace(/\.[^.]+$/, ""),
                timestamp: `${window.currentNGDate || "-"} ${window.currentNGHour ? `${window.currentNGHour}:00` : "--:--"}`,
                isQualified: false,
                imageUrl: url,
                renderedImageUrl: "",
                thumbnailUrl: url,
                displayImageUrl: url,
                hasRenderedImage: false,
                missingRenderedImage: true,
            };
        }

        const record = item || {};
        const imageUrl = pickTraceValue(record, "imageUrl", "ImageUrl");
        const renderedImageUrl = pickTraceValue(record, "renderedImageUrl", "RenderedImageUrl");
        const missingRenderedImageValue = pickTraceValue(record, "missingRenderedImage", "MissingRenderedImage");
        const hasRenderedImageValue = pickTraceValue(record, "hasRenderedImage", "HasRenderedImage");
        const hasRenderedImage = hasRenderedImageValue !== ""
            ? toBoolean(hasRenderedImageValue)
            : Boolean(renderedImageUrl) && !toBoolean(missingRenderedImageValue);
        return {
            inspectionId: pickTraceValue(record, "inspectionId", "InspectionId") || "-",
            productBarcode: pickTraceValue(record, "productBarcode", "ProductBarcode") || "-",
            timestamp: pickTraceValue(record, "timestamp", "Timestamp") || "-",
            isQualified: toBoolean(pickTraceValue(record, "isQualified", "IsQualified")),
            modelVersion: pickTraceValue(record, "modelVersion", "ModelVersion") || "",
            modelName: pickTraceValue(record, "modelName", "ModelName") || "-",
            cameraId: pickTraceValue(record, "cameraId", "CameraId") || "-",
            errorStage: pickTraceValue(record, "errorStage", "ErrorStage") || "",
            errorCode: pickTraceValue(record, "errorCode", "ErrorCode") || "",
            errorMessage: pickTraceValue(record, "errorMessage", "ErrorMessage") || "",
            imagePath: pickTraceValue(record, "imagePath", "ImagePath") || "",
            renderedImagePath: pickTraceValue(record, "renderedImagePath", "RenderedImagePath") || "",
            imageUrl,
            renderedImageUrl,
            thumbnailUrl: pickTraceValue(record, "thumbnailUrl", "ThumbnailUrl") || renderedImageUrl || imageUrl,
            displayImageUrl: pickTraceValue(record, "displayImageUrl", "DisplayImageUrl") || renderedImageUrl || imageUrl,
            hasRenderedImage,
            missingRenderedImage: hasRenderedImageValue !== "" ? !toBoolean(hasRenderedImageValue) : !renderedImageUrl || toBoolean(missingRenderedImageValue),
        };
    }

    function getTraceAdviceText(record, prefix = "处理建议") {
        if (!record || record.isQualified) return "";
        return errorAdvice?.format?.(record, { prefix, includeCode: false }) || "";
    }

    function getCurrentRuleSetJson() {
        const state = window.CF_STORE?.state || window.CF_STATE || {};
        if (state.inspectionRuleSet) {
            return JSON.stringify(state.inspectionRuleSet);
        }

        const hiddenValue = byId("cfg-inspection-rule-set-json")?.value || "";
        if (hiddenValue.trim()) return hiddenValue;

        const settings = state.settings || {};
        return settings.InspectionRuleSetJson || settings.inspectionRuleSetJson || "";
    }

    function setHistoryRulePreviewStatus(payload) {
        const statusNode = byId("history-rule-preview-status");
        const button = byId("viewer-info")?.querySelector('[data-trace-action="rule-preview"]');
        const status = String(payload?.status || "").toLowerCase();
        const isRunning = status === "running";

        if (button) {
            const canPreview = button.dataset.canPreview !== "false";
            button.disabled = !canPreview || isRunning;
            button.textContent = isRunning ? "复判中..." : "当前规则复判";
        }

        if (!statusNode) return;

        statusNode.classList.remove("hidden", "pending", "ok", "ng", "error");
        if (!status) {
            statusNode.classList.add("hidden");
            statusNode.textContent = "";
            return;
        }

        if (isRunning) {
            statusNode.classList.add("pending");
            statusNode.textContent = payload?.message || "正在用当前规则复判...";
            return;
        }

        if (status === "failed") {
            statusNode.classList.add("error");
            statusNode.textContent = payload?.message || "复判失败";
            return;
        }

        const isOk = payload?.isQualified === true || String(payload?.result || "").toUpperCase() === "OK";
        const summary = payload?.rulePrimaryReason || payload?.summary || payload?.message || "-";
        const actualCount = payload?.actualCount ?? payload?.ActualCount;
        const elapsed = payload?.totalMs ?? payload?.TotalMs;
        const detail = [
            `当前规则 ${isOk ? "OK" : "NG"}`,
            summary,
            actualCount !== undefined ? `检出 ${actualCount}` : null,
            elapsed !== undefined ? `${elapsed}ms` : null,
        ].filter(Boolean).join(" · ");
        statusNode.classList.add(isOk ? "ok" : "ng");
        statusNode.textContent = detail;
        statusNode.title = detail;
    }

    function runHistoryRulePreview(record) {
        const normalized = normalizeTraceRecord(record || activeTraceRecord || {});
        const imagePath = normalized.imagePath || normalized.imageUrl || "";
        const renderedImagePath = normalized.renderedImagePath || normalized.renderedImageUrl || "";
        if (!imagePath && !renderedImagePath) {
            window.showToast?.("历史图路径不存在，无法复判", "warning", 1800);
            setHistoryRulePreviewStatus({ status: "failed", message: "历史图路径不存在" });
            return;
        }

        setHistoryRulePreviewStatus({
            status: "running",
            inspectionId: normalized.inspectionId,
            message: "正在用当前规则复判历史图...",
        });

        bridge.sendCommand("run_history_rule_preview", {
            inspectionId: normalized.inspectionId || "",
            timestamp: normalized.timestamp || "",
            imagePath,
            renderedImagePath,
            ruleSetJson: getCurrentRuleSetJson(),
        });
    }

    function updateHistoryRulePreview(data) {
        const payload = data || {};
        const incomingId = payload.inspectionId || payload.InspectionId || "";
        const activeId = activeTraceRecord?.inspectionId || "";
        const isActiveViewer = !incomingId || !activeId || incomingId === activeId;
        const status = String(payload.status || payload.Status || "").toLowerCase();

        if (isActiveViewer) {
            setHistoryRulePreviewStatus(payload);
        }

        if (status === "completed") {
            const ok = payload.isQualified === true || String(payload.result || "").toUpperCase() === "OK";
            window.showToast?.(`历史图复判: ${ok ? "OK" : "NG"}`, ok ? "success" : "warning", 1800);
        } else if (status === "failed") {
            window.showToast?.(payload.message || "历史图复判失败", "warning", 2200);
        }
    }

    function openTraceViewer(record, mode = "rendered") {
        const normalized = normalizeTraceRecord(record);
        activeTraceRecord = normalized;
        const viewer = byId("image-viewer");
        const img = byId("viewer-img");
        const info = byId("viewer-info");
        const reviewUrl = normalized.renderedImageUrl || "";
        const originalUrl = normalized.imageUrl || "";
        const activeMode = mode === "original" && originalUrl ? "original" : (reviewUrl ? "rendered" : "original");
        const activeUrl = activeMode === "original" ? originalUrl : (reviewUrl || originalUrl);

        if (img) {
            img.src = activeUrl;
            img.alt = normalized.inspectionId || "trace image";
        }

        if (info) {
            const statusText = normalized.hasRenderedImage ? "复查图" : "无复查图";
            const canRulePreview = Boolean(normalized.imagePath || normalized.renderedImagePath || originalUrl || reviewUrl);
            const adviceText = getTraceAdviceText(normalized);
            info.innerHTML = `
                <div class="cf-trace-viewer-toolbar">
                    <div class="cf-trace-viewer-meta">
                        <strong>${escapeHtml(normalized.inspectionId)}</strong>
                        <span>${escapeHtml(normalized.timestamp)}</span>
                        <em>${escapeHtml(statusText)}</em>
                    </div>
                    <div class="cf-trace-viewer-actions">
                        <button type="button" data-trace-mode="rendered" ${reviewUrl ? "" : "disabled"} class="${activeMode === "rendered" ? "active" : ""}">复查图</button>
                        <button type="button" data-trace-mode="original" ${originalUrl ? "" : "disabled"} class="${activeMode === "original" ? "active" : ""}">训练原图</button>
                        <button type="button" data-trace-action="rule-preview" data-can-preview="${canRulePreview ? "true" : "false"}" ${canRulePreview ? "" : "disabled"}>当前规则复判</button>
                    </div>
                </div>
                <div class="cf-trace-preview-status error ${adviceText ? "" : "hidden"}">${escapeHtml(adviceText)}</div>
                <div id="history-rule-preview-status" class="cf-trace-preview-status hidden"></div>`;

            info.querySelector('[data-trace-mode="rendered"]')?.addEventListener("click", (event) => {
                event.stopPropagation();
                openTraceViewer(normalized, "rendered");
            });
            info.querySelector('[data-trace-mode="original"]')?.addEventListener("click", (event) => {
                event.stopPropagation();
                openTraceViewer(normalized, "original");
            });
            info.querySelector('[data-trace-action="rule-preview"]')?.addEventListener("click", (event) => {
                event.stopPropagation();
                runHistoryRulePreview(normalized);
            });
        }

        viewer?.classList.remove("hidden");
    }

    function updateNGImages(data, message) {
        const requestId = message?.requestId || data?.requestId || data?.RequestId || "";
        if (
            requestId &&
            (
                requestId !== tracePagerState.pendingRequestId ||
                requestId === tracePagerState.lastHandledRequestId
            )
        ) {
            return;
        }

        const page = normalizeTracePage(data);
        if (tracePagerState.pendingDirection === "next" && tracePagerState.pageIndex >= 0) {
            tracePagerState.pages = tracePagerState.pages.slice(0, tracePagerState.pageIndex + 1);
            tracePagerState.pages.push(page);
            tracePagerState.pageIndex = tracePagerState.pages.length - 1;
        } else {
            tracePagerState.pages = [page];
            tracePagerState.pageIndex = 0;
        }

        tracePagerState.pageSize = page.pageSize || tracePagerState.pageSize;
        tracePagerState.lastHandledRequestId = requestId || tracePagerState.lastHandledRequestId;
        tracePagerState.pendingDirection = "";
        renderTracePage(page);
    }

    Object.assign(window, {
        closeGalleryModal,
        closeImageViewer,
        closeAuditModal,
        closeLogHistoryModal,
        closeStatisticsHistoryModal,
        exportAuditRecords,
        openGalleryModal,
        openAuditModal,
        openLogHistoryModal,
        openStatisticsHistoryModal,
        loadNextTracePage,
        loadPreviousTracePage,
        queryAuditRecords,
        receiveStatisticsHistory,
        requestStatisticsHistory,
        runHistoryRulePreview,
        searchTraceImages,
        selectTraceHour,
        updateDetectionLogTable,
        updateAuditExport,
        updateAuditRecords,
        updateNGDates,
        updateNGHours,
        updateNGImages,
        updateHistoryRulePreview,
    });

    bridge.registerMessageHandler("statisticsHistory", receiveStatisticsHistory);
    bridge.registerMessageHandler("detectionLogTable", updateDetectionLogTable);
    bridge.registerMessageHandler("auditRecords", updateAuditRecords);
    bridge.registerMessageHandler("auditExport", updateAuditExport);
    bridge.registerMessageHandler("historyDates", updateNGDates);
    bridge.registerMessageHandler("historyHours", updateNGHours);
    bridge.registerMessageHandler("historyImages", updateNGImages);
    bridge.registerMessageHandler("historyRulePreview", updateHistoryRulePreview);
})();

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
        const operatorId = String(settings.CurrentOperatorId || "").trim() || "未设置";
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

        const operatorId = String(settings.CurrentOperatorId || "").trim() || "未设置";
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

// ==========================================
// ClearFrost legacy app.js facade
// ==========================================
// Runtime logic now lives in:
// bridge.js, state.js, render-main.js, settings.js, camera.js, roi.js, history.js, boot.js.
// This file is intentionally kept as a compatibility placeholder for older
// WebView2 deployments or cached HTML that still references js/app.js.
(function () {
    "use strict";
    window.CF_LEGACY_APP_FACADE_LOADED = true;
})();

// ==========================================
// ClearFrost legacy ui.js facade
// ==========================================
// UI behavior has been split into focused static modules. Keep this file so
// existing packaging and WebView2 cache assumptions continue to hold.
(function () {
    "use strict";
    window.CF_LEGACY_UI_FACADE_LOADED = true;
})();
