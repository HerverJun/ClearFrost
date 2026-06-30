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
            cleanText(pickValue(data, "barcodeError", "BarcodeError")) ||
            cleanText(pickValue(data, "terminalHandshakeErrorCode", "TerminalHandshakeErrorCode")),
        );
        const errorStage = cleanText(pickValue(data, "errorStage", "ErrorStage"));
        const stageAdvice = StageFallbackAdviceMap[errorStage.toLowerCase()] || "";
        return {
            code: mapped.code,
            stage: errorStage,
            message: cleanText(pickValue(data, "errorMessage", "ErrorMessage", "message", "Message")) ||
                cleanText(pickValue(data, "terminalHandshakeMessage", "TerminalHandshakeMessage")),
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
            terminalHandshakeAttempted: pickValue(data, "terminalHandshakeAttempted", "TerminalHandshakeAttempted"),
            terminalHandshakeSucceeded: pickValue(data, "terminalHandshakeSucceeded", "TerminalHandshakeSucceeded"),
            terminalHandshakeErrorCode: pickValue(data, "terminalHandshakeErrorCode", "TerminalHandshakeErrorCode"),
            terminalHandshakeSignalName: pickValue(data, "terminalHandshakeSignalName", "TerminalHandshakeSignalName"),
            terminalHandshakeAddress: pickValue(data, "terminalHandshakeAddress", "TerminalHandshakeAddress"),
            terminalHandshakeMessage: pickValue(data, "terminalHandshakeMessage", "TerminalHandshakeMessage"),
            cycleSucceeded: pickValue(data, "cycleSucceeded", "CycleSucceeded"),
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
