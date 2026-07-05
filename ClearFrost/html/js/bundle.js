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
        connections: {},
        activeCameraId: "",
        history: {
            dates: [],
            hours: [],
            images: [],
            detectionLogs: [],
            statistics: [],
        },
        replay: {
            dataset: {},
            runs: {},
            approval: {},
            datasets: [],
            evidence: [],
            integrity: {},
        },
        manualReview: {
            records: [],
            lastResponse: {},
        },
        fieldDebug: {},
        visionDebug: {},
        maintenanceAdviceHistory: [],
        diagnosticPackage: {},
        diagnosticPackageHistory: [],
        fieldHandoffReport: {},
        fieldHandoffReportHistory: [],
        metrics: {},
        previewFrameId: 0,
        previewFrame: {},
    };

    state.stats = state.stats || { total: 0, ok: 0, ng: 0 };
    state.inspection = state.inspection || {};
    state.health = state.health || {};
    state.recentInspections = state.recentInspections || [];
    state.connections = state.connections || {};
    state.history = state.history || {};
    state.replay = state.replay || { dataset: {}, runs: {}, approval: {} };
    state.replay.dataset = state.replay.dataset || {};
    state.replay.runs = state.replay.runs || {};
    state.replay.approval = state.replay.approval || {};
    state.replay.datasets = state.replay.datasets || [];
    state.replay.evidence = state.replay.evidence || [];
    state.replay.integrity = state.replay.integrity || {};
    state.manualReview = state.manualReview || { records: [], lastResponse: {} };
    state.manualReview.records = state.manualReview.records || [];
    state.manualReview.lastResponse = state.manualReview.lastResponse || {};
    state.fieldDebug = state.fieldDebug || {};
    state.visionDebug = state.visionDebug || {};
    state.maintenanceAdviceHistory = state.maintenanceAdviceHistory || [];
    state.diagnosticPackage = state.diagnosticPackage || {};
    state.diagnosticPackageHistory = state.diagnosticPackageHistory || [];
    state.fieldHandoffReport = state.fieldHandoffReport || {};
    state.fieldHandoffReportHistory = state.fieldHandoffReportHistory || [];
    state.previewFrame = state.previewFrame || {};
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
        PlcNotConnected: "PLC 未连接：请检查 PLC IP、端口和网线。",
        PlcWriteFailed: "检查 PLC 结果地址、写入权限或握手时序",
        PlcWriteException: "检查 PLC 通讯、地址配置或驱动状态",
        DetectionServiceError: "检查模型文件、GPU 推理环境或输入图像",
        DetectionCycleException: "检查检测规则、ROI、模型配置或运行日志",
        UnhandledDetectionException: "查看系统日志并联系维护人员",
        ReplayEvidenceGateMissing: "当前模型未完成上线验证，请联系工程师完成模型验证，或切换回已验证模型。",
        ReplayEvidencePackageRequired: "当前模型未完成上线验证，请联系工程师完成模型验证，或切换回已验证模型。",
        PrimaryModelReferenceEmpty: "模型未加载：请先在左侧选择主模型。",
        ModelNotLoaded: "模型未加载：请先在左侧选择主模型。",
        CameraNotReady: "相机未启动：请点击右下角“启动系统”，或检查相机网线/电源。",
        StartupBlocked: "当前还不能生产：请先处理诊断中心列出的待处理问题。",
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

    function normalizeReplayMessage(payload) {
        const data = payload?.replay || payload || {};
        const metrics = pickValue(data, "metrics", "Metrics") || {};
        const rejectionReasons = pickValue(data, "rejectionReasons", "RejectionReasons", "reasons", "Reasons") || [];
        return {
            datasetId: pickValue(data, "datasetId", "DatasetId"),
            datasetHash: pickValue(data, "datasetHash", "DatasetHash"),
            runId: pickValue(data, "runId", "RunId"),
            status: pickValue(data, "status", "Status"),
            phase: pickValue(data, "phase", "Phase"),
            completedSamples: pickValue(data, "completedSamples", "CompletedSamples"),
            totalSamples: pickValue(data, "totalSamples", "TotalSamples"),
            message: pickValue(data, "message", "Message"),
            errorCode: pickValue(data, "errorCode", "ErrorCode"),
            succeeded: pickValue(data, "succeeded", "Succeeded"),
            approvalAvailable: pickValue(data, "approvalAvailable", "ApprovalAvailable"),
            rejectionReasons: Array.isArray(rejectionReasons) ? rejectionReasons : [String(rejectionReasons || "")].filter(Boolean),
            reportJsonPath: pickValue(data, "reportJsonPath", "ReportJsonPath"),
            reportCsvPath: pickValue(data, "reportCsvPath", "ReportCsvPath"),
            reportHash: pickValue(data, "reportHash", "ReportHash"),
            policyHash: pickValue(data, "policyHash", "PolicyHash"),
            recipeHash: pickValue(data, "recipeHash", "RecipeHash"),
            ruleSetHash: pickValue(data, "ruleSetHash", "RuleSetHash"),
            evidenceId: pickValue(data, "evidenceId", "EvidenceId"),
            evidenceHash: pickValue(data, "evidenceHash", "EvidenceHash"),
            datasets: pickValue(data, "datasets", "Datasets"),
            runs: pickValue(data, "runs", "Runs"),
            evidence: pickValue(data, "evidence", "Evidence"),
            integrityStatus: pickValue(data, "status", "Status"),
            findings: pickValue(data, "findings", "Findings"),
            metrics: {
                sampleCount: pickValue(metrics, "sampleCount", "SampleCount"),
                candidateNewMissedDetectionCount: pickValue(metrics, "candidateNewMissedDetectionCount", "CandidateNewMissedDetectionCount"),
                candidateFixedMissedDetectionCount: pickValue(metrics, "candidateFixedMissedDetectionCount", "CandidateFixedMissedDetectionCount"),
                candidateNewFalseRejectCount: pickValue(metrics, "candidateNewFalseRejectCount", "CandidateNewFalseRejectCount"),
                candidateFixedFalseRejectCount: pickValue(metrics, "candidateFixedFalseRejectCount", "CandidateFixedFalseRejectCount"),
                changedDecisionCount: pickValue(metrics, "changedDecisionCount", "ChangedDecisionCount"),
                baselineAccuracy: pickValue(metrics, "baselineAccuracy", "BaselineAccuracy"),
                candidateAccuracy: pickValue(metrics, "candidateAccuracy", "CandidateAccuracy"),
                baselineP95ElapsedMs: pickValue(metrics, "baselineP95ElapsedMs", "BaselineP95ElapsedMs"),
                candidateP95ElapsedMs: pickValue(metrics, "candidateP95ElapsedMs", "CandidateP95ElapsedMs"),
            },
        };
    }

    function normalizeManualReviewMessage(payload) {
        const data = payload?.manualReview || payload || {};
        const record = pickValue(data, "record", "Record") || {};
        return {
            succeeded: pickValue(data, "succeeded", "Succeeded"),
            errorCode: pickValue(data, "errorCode", "ErrorCode"),
            message: pickValue(data, "message", "Message"),
            detectionRecordId: pickValue(data, "detectionRecordId", "DetectionRecordId") || pickValue(record, "detectionRecordId", "DetectionRecordId"),
            inspectionId: pickValue(data, "inspectionId", "InspectionId") || pickValue(record, "inspectionId", "InspectionId"),
            reviewStatus: pickValue(data, "reviewStatus", "ReviewStatus"),
            groundTruth: pickValue(data, "groundTruth", "GroundTruth") || pickValue(record, "groundTruth", "GroundTruth"),
            revision: pickValue(data, "revision", "Revision") || pickValue(record, "revision", "Revision"),
            reviewerId: pickValue(data, "reviewerId", "ReviewerId") || pickValue(record, "reviewerId", "ReviewerId"),
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

    function applyReplayUpdate(payload) {
        const replay = normalizeReplayMessage(payload);
        const cleanReplay = Object.fromEntries(
            Object.entries(replay).filter(([, value]) => value !== undefined),
        );

        if (cleanReplay.datasetId || cleanReplay.datasetHash) {
            state.replay.dataset = { ...state.replay.dataset, ...cleanReplay };
        }

        if (Array.isArray(cleanReplay.datasets)) {
            state.replay.datasets = cleanReplay.datasets;
        }

        if (Array.isArray(cleanReplay.runs)) {
            cleanReplay.runs.forEach((run) => {
                const runId = pickValue(run, "runId", "RunId");
                if (runId) {
                    state.replay.runs[runId] = {
                        ...(state.replay.runs[runId] || {}),
                        ...normalizeReplayMessage(run),
                    };
                }
            });
        }

        if (Array.isArray(cleanReplay.evidence)) {
            state.replay.evidence = cleanReplay.evidence;
        }

        if (cleanReplay.integrityStatus || cleanReplay.findings) {
            state.replay.integrity = {
                status: cleanReplay.integrityStatus || state.replay.integrity.status || "",
                findings: cleanReplay.findings || state.replay.integrity.findings || [],
            };
        }

        if (cleanReplay.runId) {
            state.replay.runs[cleanReplay.runId] = {
                ...(state.replay.runs[cleanReplay.runId] || {}),
                ...cleanReplay,
            };
            state.replay.currentRunId = cleanReplay.runId;
        }

        if (
            cleanReplay.approvalAvailable !== undefined ||
            cleanReplay.rejectionReasons ||
            cleanReplay.succeeded !== undefined ||
            cleanReplay.evidenceId ||
            cleanReplay.errorCode
        ) {
            state.replay.approval = {
                ...state.replay.approval,
                ...cleanReplay,
                available: cleanReplay.approvalAvailable !== undefined
                    ? cleanReplay.approvalAvailable
                    : state.replay.approval.available,
                rejectionReasons: cleanReplay.rejectionReasons || state.replay.approval.rejectionReasons || [],
            };
        }

        notify("replay");
    }

    function applyManualReviewUpdate(payload) {
        const records = pickValue(payload, "records", "Records");
        if (Array.isArray(records)) {
            state.manualReview.records = records.map(normalizeManualReviewMessage);
        } else {
            state.manualReview.lastResponse = normalizeManualReviewMessage(payload);
        }

        notify("manualReview");
    }

    function applyHealthSnapshot(snapshot) {
        if (!snapshot) return;
        state.health = snapshot;
        const adviceHistory = pickValue(snapshot, "maintenanceAdviceHistory", "MaintenanceAdviceHistory");
        if (Array.isArray(adviceHistory)) {
            state.maintenanceAdviceHistory = adviceHistory;
        }
        notify("health");
    }

    function applyFieldDebugResult(payload) {
        if (!payload) return;
        state.fieldDebug = {
            ...(state.fieldDebug || {}),
            ...payload,
        };
        notify("fieldDebug");
    }

    function applyVisionDebugResult(payload) {
        if (!payload) return;
        const status = pickValue(payload, "status", "Status") || "";
        const records = pickValue(payload, "records", "Records");
        const snapshot = pickValue(payload, "snapshot", "Snapshot");
        state.visionDebug = {
            ...(state.visionDebug || {}),
            ...payload,
            status,
            records: Array.isArray(records) ? records : (state.visionDebug?.records || []),
            snapshot: snapshot || state.visionDebug?.snapshot || null,
        };
        notify("visionDebug");
    }

    function applyDiagnosticPackageExportResult(payload) {
        if (!payload) return;
        state.diagnosticPackage = {
            ...(state.diagnosticPackage || {}),
            ...payload,
        };
        notify("fieldDebug");
    }

    function applyDiagnosticPackageHistoryResult(payload) {
        if (!payload) return;
        const packages = pickValue(payload, "packages", "Packages");
        state.diagnosticPackageHistory = Array.isArray(packages) ? packages : [];
        state.diagnosticPackageHistoryStatus = {
            succeeded: pickValue(payload, "succeeded", "Succeeded"),
            message: pickValue(payload, "message", "Message") || "",
            updatedAt: new Date().toISOString(),
        };
        notify("fieldDebug");
    }

    function applyDiagnosticPackageVerificationResult(payload) {
        if (!payload) return;
        state.diagnosticPackage = {
            ...(state.diagnosticPackage || {}),
            ...payload,
        };

        const verifiedPath = String(pickValue(payload, "path", "Path") || "");
        if (verifiedPath && Array.isArray(state.diagnosticPackageHistory)) {
            state.diagnosticPackageHistory = state.diagnosticPackageHistory.map((item) => {
                const itemPath = String(pickValue(item, "packagePath", "PackagePath", "path", "Path") || "");
                if (itemPath.toLowerCase() !== verifiedPath.toLowerCase()) return item;
                return {
                    ...item,
                    integrityStatus: pickValue(payload, "integrityStatus", "IntegrityStatus") || item.integrityStatus || item.IntegrityStatus,
                    verifiedEntryCount: pickValue(payload, "verifiedEntryCount", "VerifiedEntryCount"),
                    integrityEntryCount: pickValue(payload, "integrityEntryCount", "IntegrityEntryCount"),
                    integrityFindingCount: pickValue(payload, "integrityFindingCount", "IntegrityFindingCount"),
                    verifiedAt: pickValue(payload, "verifiedAt", "VerifiedAt"),
                    packageSha256: pickValue(payload, "packageSha256", "PackageSha256"),
                    indexSha256: pickValue(payload, "indexSha256", "IndexSha256"),
                };
            });
        }

        notify("fieldDebug");
    }

    function applyMaintenanceAdviceActionResult(payload) {
        if (!payload) return;
        const history = pickValue(payload, "history", "History");
        if (Array.isArray(history)) {
            state.maintenanceAdviceHistory = history;
        }

        state.maintenanceAdviceAction = {
            succeeded: pickValue(payload, "succeeded", "Succeeded"),
            cleared: pickValue(payload, "cleared", "Cleared"),
            adviceId: pickValue(payload, "adviceId", "AdviceId"),
            status: pickValue(payload, "status", "Status"),
            message: pickValue(payload, "message", "Message") || "",
            record: pickValue(payload, "record", "Record") || null,
            updatedAt: new Date().toISOString(),
        };
        notify("fieldDebug");
    }

    function applyShiftTaskActionResult(payload) {
        if (!payload) return;
        const history = pickValue(payload, "history", "History");
        if (Array.isArray(history)) {
            state.maintenanceAdviceHistory = history;
        }

        const tasks = pickValue(payload, "tasks", "Tasks");
        if (Array.isArray(tasks)) {
            state.health = {
                ...(state.health || {}),
                shiftTasks: tasks,
                ShiftTasks: tasks,
            };
        }

        state.shiftTaskAction = {
            succeeded: pickValue(payload, "succeeded", "Succeeded"),
            cleared: pickValue(payload, "cleared", "Cleared"),
            taskId: pickValue(payload, "taskId", "TaskId"),
            linkedAdviceId: pickValue(payload, "linkedAdviceId", "LinkedAdviceId"),
            status: pickValue(payload, "status", "Status"),
            message: pickValue(payload, "message", "Message") || "",
            record: pickValue(payload, "record", "Record") || null,
            updatedAt: new Date().toISOString(),
        };
        notify("fieldDebug");
    }

    function applyFieldHandoffReportResult(payload) {
        if (!payload) return;
        state.fieldHandoffReport = {
            ...(state.fieldHandoffReport || {}),
            ...payload,
            updatedAt: new Date().toISOString(),
        };

        const succeeded = pickValue(payload, "succeeded", "Succeeded") !== false;
        const path = pickValue(payload, "path", "Path", "reportPath", "ReportPath") || "";
        if (succeeded && path) {
            const report = {
                reportPath: path,
                fileName: pickValue(payload, "fileName", "FileName") || String(path).split(/[\\/]/).pop() || "",
                sizeBytes: pickValue(payload, "sizeBytes", "SizeBytes") || 0,
                generatedAt: pickValue(payload, "generatedAt", "GeneratedAt") || "",
                lastWriteTime: pickValue(payload, "generatedAt", "GeneratedAt") || "",
                overallStatus: pickValue(payload, "overallStatus", "OverallStatus") || "Pending",
                shiftTaskCount: pickValue(payload, "shiftTaskCount", "ShiftTaskCount") || 0,
            };
            const normalizedPath = String(path).toLowerCase();
            state.fieldHandoffReportHistory = [
                report,
                ...(state.fieldHandoffReportHistory || []).filter((item) => {
                    const itemPath = String(pickValue(item, "reportPath", "ReportPath", "path", "Path") || "").toLowerCase();
                    return itemPath !== normalizedPath;
                }),
            ].slice(0, 8);
        }
        notify("fieldDebug");
    }

    function applyFieldHandoffReportHistoryResult(payload) {
        if (!payload) return;
        const reports = pickValue(payload, "reports", "Reports");
        state.fieldHandoffReportHistory = Array.isArray(reports) ? reports : [];
        state.fieldHandoffReportHistoryStatus = {
            succeeded: pickValue(payload, "succeeded", "Succeeded"),
            message: pickValue(payload, "message", "Message") || "",
            updatedAt: new Date().toISOString(),
        };
        notify("fieldDebug");
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
        normalizeManualReviewMessage,
        normalizeReplayMessage,
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
        applyManualReviewUpdate,
        applyStatsUpdate,
        applyReplayUpdate,
        applyHealthSnapshot,
        applyFieldDebugResult,
        applyVisionDebugResult,
        applyDiagnosticPackageExportResult,
        applyDiagnosticPackageHistoryResult,
        applyDiagnosticPackageVerificationResult,
        applyMaintenanceAdviceActionResult,
        applyShiftTaskActionResult,
        applyFieldHandoffReportResult,
        applyFieldHandoffReportHistoryResult,
        applyBootstrapSnapshot,
    };
})();

// ==========================================
// ClearFrost image/overlay coordinate mapping
// ==========================================
(function () {
    "use strict";

    const root = typeof window !== "undefined" ? window : globalThis;

    function positiveNumber(value, fallback = 0) {
        const number = Number(value);
        return Number.isFinite(number) && number > 0 ? number : fallback;
    }

    function calculateContainedRect(outerWidth, outerHeight, innerWidth, innerHeight) {
        outerWidth = positiveNumber(outerWidth);
        outerHeight = positiveNumber(outerHeight);
        innerWidth = positiveNumber(innerWidth);
        innerHeight = positiveNumber(innerHeight);
        if (!outerWidth || !outerHeight || !innerWidth || !innerHeight) {
            return { x: 0, y: 0, width: 0, height: 0, scale: 0 };
        }

        const scale = Math.min(outerWidth / innerWidth, outerHeight / innerHeight);
        const width = innerWidth * scale;
        const height = innerHeight * scale;
        return {
            x: (outerWidth - width) / 2,
            y: (outerHeight - height) / 2,
            width,
            height,
            scale,
        };
    }

    function calculateImageContentMapping(input = {}) {
        const containerWidth = positiveNumber(input.containerWidth);
        const containerHeight = positiveNumber(input.containerHeight);
        const previewWidth = positiveNumber(input.previewWidth, positiveNumber(input.naturalWidth));
        const previewHeight = positiveNumber(input.previewHeight, positiveNumber(input.naturalHeight));
        const sourceWidth = positiveNumber(input.sourceWidth, previewWidth);
        const sourceHeight = positiveNumber(input.sourceHeight, previewHeight);

        if (!containerWidth || !containerHeight || !previewWidth || !previewHeight || !sourceWidth || !sourceHeight) {
            return {
                valid: false,
                sourceWidth,
                sourceHeight,
                previewRect: { x: 0, y: 0, width: 0, height: 0 },
                imageRect: { x: 0, y: 0, width: 0, height: 0 },
                scaleX: 0,
                scaleY: 0,
            };
        }

        const previewRect = calculateContainedRect(containerWidth, containerHeight, previewWidth, previewHeight);
        const sourceInPreviewRect = calculateContainedRect(previewWidth, previewHeight, sourceWidth, sourceHeight);
        const previewScaleX = previewRect.width / previewWidth;
        const previewScaleY = previewRect.height / previewHeight;
        const imageRect = {
            x: previewRect.x + sourceInPreviewRect.x * previewScaleX,
            y: previewRect.y + sourceInPreviewRect.y * previewScaleY,
            width: sourceInPreviewRect.width * previewScaleX,
            height: sourceInPreviewRect.height * previewScaleY,
        };

        return {
            valid: imageRect.width > 0 && imageRect.height > 0,
            sourceWidth,
            sourceHeight,
            previewWidth,
            previewHeight,
            previewRect,
            sourceInPreviewRect,
            imageRect,
            scaleX: imageRect.width / sourceWidth,
            scaleY: imageRect.height / sourceHeight,
        };
    }

    function mapImagePoint(mapping, point = {}) {
        const x = Number(point.x ?? point.X ?? 0);
        const y = Number(point.y ?? point.Y ?? 0);
        return {
            x: x * mapping.scaleX,
            y: y * mapping.scaleY,
        };
    }

    function mapImageRect(mapping, rect = {}) {
        const x = Number(rect.x ?? rect.X ?? 0);
        const y = Number(rect.y ?? rect.Y ?? 0);
        const width = Number(rect.width ?? rect.Width ?? rect.w ?? rect.W ?? 0);
        const height = Number(rect.height ?? rect.Height ?? rect.h ?? rect.H ?? 0);
        return {
            x: x * mapping.scaleX,
            y: y * mapping.scaleY,
            width: width * mapping.scaleX,
            height: height * mapping.scaleY,
        };
    }

    const CoordinateMappingTestCases = Object.freeze([
        {
            name: "16:9 source without bars",
            input: { containerWidth: 1200, containerHeight: 675, previewWidth: 960, previewHeight: 540, sourceWidth: 1280, sourceHeight: 720 },
            rect: { x: 0, y: 0, width: 1280, height: 720 },
            expectedRect: { x: 0, y: 0, width: 1200, height: 675 },
        },
        {
            name: "4:3 source in 16:9 backend preview",
            input: { containerWidth: 960, containerHeight: 540, previewWidth: 960, previewHeight: 540, sourceWidth: 1024, sourceHeight: 768 },
            rect: { x: 0, y: 0, width: 1024, height: 768 },
            expectedImageRect: { x: 120, y: 0, width: 720, height: 540 },
            expectedRect: { x: 0, y: 0, width: 720, height: 540 },
        },
        {
            name: "portrait source in scaled preview",
            input: { containerWidth: 480, containerHeight: 270, previewWidth: 960, previewHeight: 540, sourceWidth: 720, sourceHeight: 1280 },
            rect: { x: 0, y: 0, width: 720, height: 1280 },
            expectedImageRect: { x: 164.0625, y: 0, width: 151.875, height: 270 },
            expectedRect: { x: 0, y: 0, width: 151.875, height: 270 },
        },
        {
            name: "wide source with browser object-fit bars",
            input: { containerWidth: 1000, containerHeight: 800, previewWidth: 960, previewHeight: 540, sourceWidth: 2000, sourceHeight: 500 },
            rect: { x: 0, y: 0, width: 2000, height: 500 },
            expectedImageRect: { x: 0, y: 275, width: 1000, height: 250 },
            expectedRect: { x: 0, y: 0, width: 1000, height: 250 },
        },
    ]);

    function assertClose(actual, expected, label) {
        if (Math.abs(actual - expected) > 0.001) {
            throw new Error(`${label}: expected ${expected}, got ${actual}`);
        }
    }

    function runCoordinateMappingSelfTests() {
        CoordinateMappingTestCases.forEach((testCase) => {
            const mapping = calculateImageContentMapping(testCase.input);
            if (!mapping.valid) throw new Error(`${testCase.name}: mapping invalid`);
            if (testCase.expectedImageRect) {
                assertClose(mapping.imageRect.x, testCase.expectedImageRect.x, `${testCase.name} imageRect.x`);
                assertClose(mapping.imageRect.y, testCase.expectedImageRect.y, `${testCase.name} imageRect.y`);
                assertClose(mapping.imageRect.width, testCase.expectedImageRect.width, `${testCase.name} imageRect.width`);
                assertClose(mapping.imageRect.height, testCase.expectedImageRect.height, `${testCase.name} imageRect.height`);
            }

            const mappedRect = mapImageRect(mapping, testCase.rect);
            assertClose(mappedRect.x, testCase.expectedRect.x, `${testCase.name} rect.x`);
            assertClose(mappedRect.y, testCase.expectedRect.y, `${testCase.name} rect.y`);
            assertClose(mappedRect.width, testCase.expectedRect.width, `${testCase.name} rect.width`);
            assertClose(mappedRect.height, testCase.expectedRect.height, `${testCase.name} rect.height`);
        });
        return { ok: true, count: CoordinateMappingTestCases.length };
    }

    const api = {
        CoordinateMappingTestCases,
        calculateContainedRect,
        calculateImageContentMapping,
        mapImagePoint,
        mapImageRect,
        runCoordinateMappingSelfTests,
    };

    root.CF_COORDINATE_MAPPING = api;
    if (typeof module !== "undefined" && module.exports) {
        module.exports = api;
    }
})();

// ==========================================
// ClearFrost main screen rendering
// ==========================================
(function () {
    "use strict";

    const { escapeHtml, pickValue } = window.CF_UTILS;
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
    const KnownRenderReasons = new Set(["inspection", "stats", "health", "replay", "manualReview", "fieldDebug", "visionDebug", "bootstrap", "state"]);
    const InspectionStageLabels = Object.freeze({
        Unknown: "未知",
        Triggered: "已触发",
        Barcode: "读取条码",
        Capture: "取图",
        Inference: "推理中",
        RoiFilter: "规则判定",
        PlcWrite: "写入 PLC",
        RenderToUi: "刷新界面",
        SaveImage: "保存图像",
        SaveRecord: "保存记录",
        Completed: "完成",
        Failed: "失败",
        IDLE: "空闲",
    });
    const ReplayRunStatusLabels = Object.freeze({
        Pending: "待处理",
        Preparing: "准备中",
        BaselineRunning: "基准模型运行中",
        CandidateRunning: "候选模型运行中",
        Reporting: "生成报告中",
        Running: "运行中",
        CancelRequested: "正在取消",
        Completed: "已完成",
        Failed: "已失败",
        Canceled: "已取消",
        Interrupted: "已中断",
        Frozen: "已生成验证样本集",
        Invalid: "无效",
    });
    const ApprovalStatusLabels = Object.freeze({
        Approved: "已批准",
        Rejected: "已拒绝",
        Available: "可审批",
    });
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

    function setHtml(id, html, fallback = "") {
        const node = el(id);
        if (!node) return;
        const content = html === undefined || html === null || html === "" ? fallback : String(html);
        if (node.innerHTML !== content) node.innerHTML = content;
        const text = node.textContent || "";
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

    function formatInspectionStage(stage) {
        const value = String(stage || "").trim();
        return InspectionStageLabels[value] || value || "空闲";
    }

    function formatReplayRunStatus(status) {
        const value = String(status || "").trim();
        return ReplayRunStatusLabels[value] || value;
    }

    function formatApprovalStatus(status) {
        const value = String(status || "").trim();
        return ApprovalStatusLabels[value] || value;
    }

    function formatFallbackReason(reason) {
        const value = String(reason || "").trim();
        if (value === "FallbackDisabled") return "备用模型未启用";
        return value;
    }

    function renderInspectionContext(state) {
        const inspection = state.inspection || {};
        setText("camera-phase", formatInspectionStage(inspection.currentStage), "空闲");
        setText("feed-sn", getTraceIdentityLabel(inspection), "条码: -");
        const isStandaloneSource = !!inspection.sourceLabel && !inspection.inspectionId;
        setText(
            "feed-trigger-seq",
            isStandaloneSource
                ? "本地"
                : `触发${inspection.triggerSeq ?? "-"} / 结果${inspection.resultSeq ?? "-"}`,
            "触发- / 结果-",
        );
    }

    function getTraceIdentityLabel(item) {
        if (item?.productBarcode) return `条码: ${item.productBarcode}`;
        if (item?.sourceLabel) return item.sourceLabel;
        if (item?.barcodeEnabled === true) {
            return item?.barcodeReadSucceeded === false ? "条码未读取" : "等待条码";
        }
        if (item?.inspectionId) return `单号: ${item.inspectionId}`;
        return "条码: -";
    }

    function hasTerminalHandshakeFailure(item) {
        return item?.terminalHandshakeAttempted === true && item?.terminalHandshakeSucceeded === false;
    }

    function getProductJudgementText(item) {
        if (item?.isOk === true) return "OK";
        if (item?.isOk === false) return "NG";
        return "未知";
    }

    function getTerminalFailureMessage(item) {
        if (!hasTerminalHandshakeFailure(item)) return "";
        const code = item?.terminalHandshakeErrorCode || "-";
        const signal = item?.terminalHandshakeSignalName || "-";
        const address = item?.terminalHandshakeAddress || "-";
        const detail = item?.terminalHandshakeMessage ? `: ${item.terminalHandshakeMessage}` : "";
        return `产品判定 ${getProductJudgementText(item)}，PLC 终态失败 [${code}] ${signal}@${address}${detail}`;
    }

    function isFailedInspection(item) {
        return hasTerminalHandshakeFailure(item) || item?.isOk === false || item?.currentStage === "Failed";
    }

    function getInspectionAdvice(item, prefix = "处理建议", includeCode = false) {
        if (!isFailedInspection(item)) return "";
        return errorAdvice?.format?.(item, { prefix, includeCode }) || "";
    }

    function logCriticalInspectionAdvice(item) {
        const resolved = errorAdvice?.resolve?.(item);
        const hasErrorCode = Boolean(item?.errorCode || (hasTerminalHandshakeFailure(item) && item?.terminalHandshakeErrorCode));
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
        return `近${inspections.length}次备用模型 ${fallbackCount}次 (${ratio.toFixed(1)}%)`;
    }

    function getFallbackBadge(inspection, recentInspections) {
        const attempts = Math.max(0, Math.trunc(toFiniteNumber(inspection?.fallbackAttemptCount)));
        const inferenceMs = inspection?.inferenceMs ?? "-";
        const ratioText = getFallbackRatioText(recentInspections);
        const ratioSuffix = ratioText ? `; ${ratioText}` : "";

        if (inspection?.wasFallback === true) {
            return {
                text: attempts > 1 ? `备用模型 x${attempts}` : "备用模型",
                title: `备用模型命中，模型: ${inspection.usedModelName || "-"}，推理: ${inferenceMs}ms${ratioSuffix}`,
            };
        }

        const skippedReason = String(inspection?.fallbackSkippedReason || "").trim();
        if (skippedReason && skippedReason !== "FallbackDisabled") {
            return {
                text: "备用跳过",
                title: `备用模型未命中或跳过: ${formatFallbackReason(skippedReason)}${ratioSuffix}`,
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
            parts.push(`备用模型 ${attempts > 0 ? attempts : "?"}次`);
        } else if (attempts > 1) {
            parts.push(`模型尝试${attempts}次`);
        }

        const skippedReason = String(item?.fallbackSkippedReason || "").trim();
        if (skippedReason && skippedReason !== "FallbackDisabled" && item?.wasFallback !== true) {
            parts.push(`备用跳过:${formatFallbackReason(skippedReason)}`);
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
            `备用模型命中: ${item.usedModelName || "-"}，尝试${attempts > 0 ? attempts : "-"}次，推理${item.inferenceMs ?? "-"}ms${ratioSuffix}`,
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
        const terminalMessage = getTerminalFailureMessage(item);
        if (terminalMessage) return terminalMessage;

        const advice = getInspectionAdvice(item, "建议");
        if (advice) return advice;

        if (item?.isOk === false && item?.rulePrimaryReason) return item.rulePrimaryReason;

        const message = item?.message || item?.errorMessage || "";
        const objectPart = getObjectSummaryFromMessage(message);
        if (objectPart) return objectPart;
        if (item?.barcodeError) return item.barcodeError;
        if (item?.errorCode) return item.errorCode;
        if (item?.actualCount !== undefined && item?.actualCount !== null) return `检出 ${item.actualCount}`;
        return formatInspectionStage(item?.currentStage) || "-";
    }

    function renderCameraResult(state) {
        const inspection = state.inspection || {};
        const isOk = inspection.isOk;
        const terminalFailed = hasTerminalHandshakeFailure(inspection);
        const pill = el("camera-result-pill");
        if (pill) {
            const className = terminalFailed ? "result-cycle-failed" : isOk === true ? "result-ok" : isOk === false ? "result-ng" : "result-idle";
            const text = terminalFailed ? "周期失败" : isOk === true ? "OK" : isOk === false ? "NG" : "等待";
            if (pill.dataset.cfResult !== className) {
                pill.dataset.cfResult = className;
                pill.classList.remove("result-idle", "result-ok", "result-ng", "result-cycle-failed");
                pill.classList.add(className);
            }
            if (pill.textContent !== text) pill.textContent = text;
        }

        const terminalMessage = getTerminalFailureMessage(inspection);
        const adviceMessage = getInspectionAdvice(inspection);
        const ruleReason = isOk === false ? inspection.rulePrimaryReason : "";
        const message = adviceMessage || ruleReason || inspection.message || (isOk === true ? "检测通过" : isOk === false ? "检测未通过" : "等待检测结果");
        setText("camera-result-text", message, "等待检测结果");
        setText("camera-result-text", terminalMessage || message, "等待检测结果");
        setText("camera-total-ms", `${inspection.totalMs || 0}ms`, "0ms");
        setText("camera-target-count", inspection.actualCount ?? 0, "0");
        setText("camera-model", inspection.usedModelName, "-");
        setText("feed-model-name", inspection.usedModelName ? `模型 ${inspection.usedModelName}` : "模型 -", "模型 -");
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
            const terminalFailed = hasTerminalHandshakeFailure(item);
            const statusClass = terminalFailed ? "cycle-failed" : isOk ? "ok" : item.isOk === false ? "ng" : "run";
            const statusText = terminalFailed ? "周期失败" : isOk ? "OK" : item.isOk === false ? "NG" : "运行中";
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

    function formatBytesCompact(bytes) {
        const value = toFiniteNumber(bytes);
        if (value <= 0) return "-";
        if (value >= 1024 * 1024 * 1024) return `${(value / 1024 / 1024 / 1024).toFixed(2)}GB`;
        if (value >= 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)}MB`;
        if (value >= 1024) return `${(value / 1024).toFixed(1)}KB`;
        return `${Math.trunc(value)}B`;
    }

    function shortHash(hash) {
        const value = String(hash || "").trim();
        return value ? value.slice(0, 12) : "-";
    }

    function formatDiagnosticDateTime(value) {
        if (!value) return "-";
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return String(value);
        return date.toLocaleString();
    }

    function getDiagnosticPackagePath(pkg) {
        return getDiagnosticPackageValue(pkg, "path", "Path", "") ||
            getDiagnosticPackageValue(pkg, "packagePath", "PackagePath", "");
    }

    function getDiagnosticPackageFileName(pkg) {
        const explicitName = getDiagnosticPackageValue(pkg, "fileName", "FileName", "");
        if (explicitName) return explicitName;
        const path = getDiagnosticPackagePath(pkg);
        return String(path || "").split(/[\\/]/).pop() || "-";
    }

    function getDiagnosticPackageValue(pkg, camelName, pascalName, fallback = "") {
        return pkg?.[camelName] ?? pkg?.[pascalName] ?? fallback;
    }

    function buildDiagnosticPackageSummaryText(pkg) {
        const path = getDiagnosticPackagePath(pkg);
        const packageSha = getDiagnosticPackageValue(pkg, "packageSha256", "PackageSha256", "");
        const indexSha = getDiagnosticPackageValue(pkg, "indexSha256", "IndexSha256", "");
        const sizeBytes = getDiagnosticPackageValue(pkg, "sizeBytes", "SizeBytes", "");
        const integrityStatus = getDiagnosticPackageValue(pkg, "integrityStatus", "IntegrityStatus", "");
        const integrityEntries = getDiagnosticPackageValue(pkg, "integrityEntryCount", "IntegrityEntryCount", "");
        const verifiedEntries = getDiagnosticPackageValue(pkg, "verifiedEntryCount", "VerifiedEntryCount", "");
        const findingCount = getDiagnosticPackageValue(pkg, "integrityFindingCount", "IntegrityFindingCount", "");
        const exportedAt = getDiagnosticPackageValue(pkg, "exportedAt", "ExportedAt", "");

        return [
            "ClearFrost 诊断包核验摘要",
            `路径: ${path || "-"}`,
            `包 SHA-256: ${packageSha || "-"}`,
            `索引 SHA-256: ${indexSha || "-"}`,
            `大小: ${sizeBytes ? `${sizeBytes} bytes (${formatBytesCompact(sizeBytes)})` : "-"}`,
            `自检状态: ${integrityStatus || "-"}`,
            `索引条目: ${integrityEntries || "-"}`,
            `已验证条目: ${verifiedEntries || "-"}`,
            `异常数量: ${findingCount === "" ? "-" : findingCount}`,
            `导出时间: ${exportedAt || "-"}`,
        ].join("\n");
    }

    function getFieldHandoffReportValue(report, camelName, pascalName, fallback = "") {
        return report?.[camelName] ?? report?.[pascalName] ?? fallback;
    }

    function getFieldHandoffReportPath(report) {
        return getFieldHandoffReportValue(report, "path", "Path", "") ||
            getFieldHandoffReportValue(report, "reportPath", "ReportPath", "");
    }

    function getFieldHandoffReportFileName(report) {
        const explicitName = getFieldHandoffReportValue(report, "fileName", "FileName", "");
        if (explicitName) return explicitName;
        const path = getFieldHandoffReportPath(report);
        return String(path || "").split(/[\\/]/).pop() || "-";
    }

    function buildFieldHandoffReportSummaryText(report) {
        const path = getFieldHandoffReportPath(report);
        const status = getFieldHandoffReportValue(report, "overallStatus", "OverallStatus", "");
        const sizeBytes = getFieldHandoffReportValue(report, "sizeBytes", "SizeBytes", "");
        const generatedAt = getFieldHandoffReportValue(report, "generatedAt", "GeneratedAt", "");
        const activeAdviceCount = getFieldHandoffReportValue(report, "activeAdviceCount", "ActiveAdviceCount", "");
        const shiftTaskCount = getFieldHandoffReportValue(report, "shiftTaskCount", "ShiftTaskCount", "");
        const failedRecheckCount = getFieldHandoffReportValue(report, "failedRecheckCount", "FailedRecheckCount", "");
        const diagnosticPackageCount = getFieldHandoffReportValue(report, "diagnosticPackageCount", "DiagnosticPackageCount", "");
        const recentAuditCount = getFieldHandoffReportValue(report, "recentAuditCount", "RecentAuditCount", "");

        return [
            "ClearFrost 现场交接报告摘要",
            `路径: ${path || "-"}`,
            `交接结论: ${status || "-"}`,
            `大小: ${sizeBytes ? `${sizeBytes} bytes (${formatBytesCompact(sizeBytes)})` : "-"}`,
            `生成时间: ${generatedAt || "-"}`,
            `当前维护建议: ${activeAdviceCount === "" ? "-" : activeAdviceCount}`,
            `班次待办: ${shiftTaskCount === "" ? "-" : shiftTaskCount}`,
            `复检失败: ${failedRecheckCount === "" ? "-" : failedRecheckCount}`,
            `诊断包数量: ${diagnosticPackageCount === "" ? "-" : diagnosticPackageCount}`,
            `关键审计数量: ${recentAuditCount === "" ? "-" : recentAuditCount}`,
        ].join("\n");
    }

    function getCurrentStationName() {
        const settings = store.state.settings || {};
        return String(
            el("cfg-cam-name")?.value ||
            settings.StationName ||
            settings.stationName ||
            settings.CameraName ||
            settings.cameraName ||
            settings.name ||
            "").trim();
    }

    function getFaultAdviceItems(health) {
        const triggerSource = getCurrentTriggerSource(health);
        const items = getVisibleMaintenanceAdvice(health).map((item) => ({
            title: item.title || item.Title || "待处理问题",
            advice: item.advice || item.Advice || "请工程师查看现场诊断中心。",
            code: item.code || item.Code || "",
        }));

        if (triggerSource === "SerialPhotoelectric" && !isSerialTriggerConnected()) {
            items.push({
                title: "串口光电触发器未连接",
                advice: "请确认 COM 口、光电触发器供电和串口线后重新启动系统。",
                code: "SerialTriggerNotConnected",
            });
        }

        return items;
    }

    function buildFaultSummaryText(state = store.state) {
        const health = state?.health || {};
        const modelProbe = health.modelProbe || health.ModelProbe || {};
        const currentModel = getFieldValue(health, "currentModelName", "CurrentModelName") ||
            modelProbe.currentModelName || modelProbe.CurrentModelName ||
            getFieldValue(health, "modelStatus", "ModelStatus");
        const triggerSource = getCurrentTriggerSource(health);
        const triggerLabel = formatTriggerSourceLabel(triggerSource);
        const faultItems = getFaultAdviceItems(health);
        const stationName = getCurrentStationName() || "未设置";
        const cameraStatus = formatFieldCameraStatus(getFieldValue(health, "cameraStatus", "CameraStatus"));
        const plcStatus = formatFieldPlcStatus(getFieldValue(health, "plcStatus", "PlcStatus"), triggerSource);
        const modelStatus = isModelReady(modelProbe, currentModel) ? "已加载" : "未加载";
        const storageStatus = formatFieldStorageStatus(health);
        const conclusion = faultItems.length > 0 ? "需要处理" : "暂无待处理";

        const lines = [
            "【ClearFrost现场故障摘要】",
            `时间：${new Date().toLocaleString()}`,
            `工位：${stationName}`,
            `触发源：${triggerLabel}`,
            `当前结论：${conclusion}`,
            `相机：${cameraStatus}`,
            `PLC：${plcStatus}`,
            `模型：${modelStatus}`,
            `存储：${storageStatus}`,
            "待处理：",
        ];

        if (faultItems.length === 0) {
            lines.push("当前暂无待处理问题，设备状态可以继续生产。");
            lines.push("下一步：继续按当前工位配置生产；如需排查历史问题，可导出诊断包给工程师。");
            return lines.join("\n");
        }

        faultItems.slice(0, 6).forEach((item, index) => {
            const title = String(item.title || "待处理问题").trim();
            const advice = String(item.advice || "请工程师查看现场诊断中心。").trim();
            lines.push(`${index + 1}. ${title}：${advice}`);
        });
        lines.push("下一步：请工程师检查相机连接、模型选择和触发源通讯。");
        return lines.join("\n");
    }

    async function writeClipboardText(text) {
        if (navigator.clipboard?.writeText) {
            await navigator.clipboard.writeText(text);
            return;
        }

        const textarea = document.createElement("textarea");
        textarea.value = text;
        textarea.setAttribute("readonly", "readonly");
        textarea.style.position = "fixed";
        textarea.style.left = "-9999px";
        document.body.appendChild(textarea);
        textarea.select();
        const copied = document.execCommand("copy");
        document.body.removeChild(textarea);
        if (!copied) {
            throw new Error("ClipboardUnavailable");
        }
    }

    async function copyFaultSummary() {
        try {
            await writeClipboardText(buildFaultSummaryText(store.state));
            showToast("故障摘要已复制", "success", 1600);
            addLog("故障摘要已复制", "success");
        } catch {
            showToast("复制失败，请手动记录故障摘要", "error", 1800);
            addLog("故障摘要复制失败", "error");
        }
    }

    async function copyDiagnosticPackageSummary() {
        const pkg = store.state?.diagnosticPackage || {};
        const path = getDiagnosticPackagePath(pkg);
        if (!path) {
            showToast("暂无诊断包摘要", "warning", 1400);
            addLog("暂无可复制的诊断包核验摘要", "warning");
            return;
        }

        try {
            await writeClipboardText(buildDiagnosticPackageSummaryText(pkg));
            showToast("诊断包核验摘要已复制", "success", 1600);
            addLog("诊断包核验摘要已复制", "success");
        } catch {
            showToast("复制失败，请手动记录核验摘要", "error", 1800);
            addLog("诊断包核验摘要复制失败", "error");
        }
    }

    async function copyFieldHandoffReportSummary() {
        const report = store.state?.fieldHandoffReport || {};
        const path = getFieldHandoffReportPath(report);
        if (!path) {
            showToast("暂无交接报告摘要", "warning", 1400);
            addLog("暂无可复制的交接报告摘要", "warning");
            return;
        }

        try {
            await writeClipboardText(buildFieldHandoffReportSummaryText(report));
            showToast("交接报告摘要已复制", "success", 1600);
            addLog("交接报告摘要已复制", "success");
        } catch {
            showToast("复制失败，请手动记录交接报告摘要", "error", 1800);
            addLog("交接报告摘要复制失败", "error");
        }
    }

    function requestDiagnosticPackageHistory() {
        window.sendCommand("query_diagnostic_packages");
        addLog("正在刷新诊断包历史...", "info");
    }

    function requestFieldHandoffReportHistory() {
        window.sendCommand("query_field_handoff_reports");
        addLog("正在刷新交接报告历史...", "info");
    }

    function verifyDiagnosticPackage(path) {
        const packagePath = String(path || "").trim();
        if (!packagePath) {
            showToast("未选择诊断包", "warning", 1400);
            return;
        }

        window.sendCommand("verify_diagnostic_package", { path: packagePath });
        addLog("正在复核诊断包完整性...", "info");
        showToast("正在复核诊断包...", "info", 1200);
    }

    function exportFieldHandoffReport() {
        window.sendCommand("export_field_handoff_report");
        addLog("正在导出现场交接报告...", "info");
        showToast("正在导出交接报告...", "info", 1200);
    }

    function sendMaintenanceAdviceAction(adviceId, action) {
        const id = String(adviceId || "").trim();
        if (!id) {
            showToast("维护建议标识为空", "warning", 1400);
            return;
        }

        window.sendCommand("maintenance_advice_action", { adviceId: id, action });
        addLog(action === "recheck" ? "维护建议复检请求已发送" : "维护建议处理记录已提交", "info");
    }

    function acknowledgeMaintenanceAdvice(adviceId) {
        sendMaintenanceAdviceAction(adviceId, "acknowledge");
    }

    function recheckMaintenanceAdvice(adviceId) {
        sendMaintenanceAdviceAction(adviceId, "recheck");
    }

    function sendShiftTaskAction(payload, action) {
        const task = payload && typeof payload === "object" ? payload : { linkedAdviceId: payload };
        const taskId = String(task.taskId || task.TaskId || "").trim();
        const linkedAdviceId = String(task.linkedAdviceId || task.LinkedAdviceId || task.adviceId || task.AdviceId || "").trim();
        if (!taskId && !linkedAdviceId) {
            showToast("班次待办标识为空", "warning", 1400);
            return;
        }

        window.sendCommand("shift_task_action", { taskId, linkedAdviceId, action });
        addLog(action === "recheck" ? "班次待办复检请求已发送" : "班次待办处理记录已提交", "info");
    }

    function acknowledgeShiftTask(payload) {
        sendShiftTaskAction(payload, "acknowledge");
    }

    function recheckShiftTask(payload) {
        sendShiftTaskAction(payload, "recheck");
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
        updateTriggerSourceStatus(getCurrentTriggerSource(health));
        logQueuePressureAdvice(health);
        renderFieldDiagnostics(state);
    }

    function getNestedHealthSnapshot(health) {
        return health?.healthSnapshot || health?.HealthSnapshot || health || {};
    }

    function getFieldValue(health, camelName, pascalName, fallback = "") {
        const nested = getNestedHealthSnapshot(health);
        return health?.[camelName] ?? health?.[pascalName] ?? nested?.[camelName] ?? nested?.[pascalName] ?? fallback;
    }

    function getFieldArray(health, camelName, pascalName) {
        const nested = getNestedHealthSnapshot(health);
        const value = health?.[camelName] ?? health?.[pascalName] ?? nested?.[camelName] ?? nested?.[pascalName];
        return Array.isArray(value) ? value : [];
    }

    function formatQueueText(pending, capacity) {
        const p = Math.max(0, Math.trunc(toFiniteNumber(pending)));
        const c = Math.max(0, Math.trunc(toFiniteNumber(capacity)));
        return c > 0 ? `${p}/${c}` : `${p}/-`;
    }

    function getLastFieldError(health) {
        const errors = getFieldArray(health, "recentErrors", "RecentErrors");
        if (errors.length === 0) return "";
        const last = errors[errors.length - 1] || {};
        const source = last.source || last.Source || "系统";
        const message = last.message || last.Message || "";
        return `${source}: ${message}`;
    }

    function getLastTimingText(health) {
        const timings = getFieldArray(health, "recentInspectionTimings", "RecentInspectionTimings");
        const lastTiming = health.lastInspectionTiming || health.LastInspectionTiming ||
            getNestedHealthSnapshot(health).lastInspectionTiming || getNestedHealthSnapshot(health).LastInspectionTiming ||
            timings[timings.length - 1];
        if (!lastTiming) return "等待检测";

        const parts = [
            lastTiming.captureMs || lastTiming.CaptureMs ? `取图${lastTiming.captureMs ?? lastTiming.CaptureMs}ms` : "",
            lastTiming.inferenceMs || lastTiming.InferenceMs ? `推理${lastTiming.inferenceMs ?? lastTiming.InferenceMs}ms` : "",
            lastTiming.roiFilterMs || lastTiming.RoiFilterMs ? `规则${lastTiming.roiFilterMs ?? lastTiming.RoiFilterMs}ms` : "",
            lastTiming.plcWriteMs || lastTiming.PlcWriteMs ? `PLC${lastTiming.plcWriteMs ?? lastTiming.PlcWriteMs}ms` : "",
        ].filter(Boolean);
        return parts.length ? parts.join(" / ") : "暂无阶段耗时";
    }

    function getFieldObject(health, camelName, pascalName) {
        const nested = getNestedHealthSnapshot(health);
        const value = health?.[camelName] ?? health?.[pascalName] ?? nested?.[camelName] ?? nested?.[pascalName];
        return value && typeof value === "object" ? value : {};
    }

    function normalizeTriggerSource(value) {
        const raw = String(value || "").trim();
        const lower = raw.toLowerCase();
        if (!raw || lower === "plc") return "PLC";
        if (lower === "serialphotoelectric" || lower.includes("serial") || lower.includes("串口")) {
            return "SerialPhotoelectric";
        }
        if (lower === "manual" || lower.includes("手动")) return "Manual";
        return raw;
    }

    function getCurrentTriggerSource(health = null) {
        const settings = store.state.settings || {};
        const selectValue = el("cfg-trigger-source")?.value || "";
        const healthValue = health
            ? getFieldValue(health, "triggerSource", "TriggerSource", "")
            : "";
        return normalizeTriggerSource(
            healthValue ||
            settings.TriggerSource ||
            settings.triggerSource ||
            selectValue ||
            "PLC");
    }

    function formatTriggerSourceLabel(triggerSource) {
        const normalized = normalizeTriggerSource(triggerSource);
        if (normalized === "PLC") return "PLC触发";
        if (normalized === "SerialPhotoelectric") return "串口光电";
        if (normalized === "Manual") return "手动检测";
        return normalized || "PLC触发";
    }

    function isSerialTriggerConnected() {
        return Boolean(store.state.connections?.serialTrigger);
    }

    function updateTriggerSourceStatus(triggerSource = null) {
        const normalized = normalizeTriggerSource(triggerSource || getCurrentTriggerSource());
        const label = formatTriggerSourceLabel(normalized);
        setText("status-trigger-source-text", label, "PLC触发");
        setText("diag-trigger-source", label, "PLC触发");
        setDotState("status-trigger-source-dot", normalized === "Manual" ? "status-warning" : "status-on");

        const root = el("status-trigger-source");
        if (root) {
            root.setAttribute("aria-label", `当前触发源: ${label}`);
            root.title = `当前触发源: ${label}`;
        }
    }

    function isPlcNotConnectedAdvice(item) {
        const code = String(item.code || item.Code || "").trim();
        const source = String(item.source || item.Source || "").trim();
        const title = String(item.title || item.Title || "").trim();
        return code === "PlcNotConnected" ||
            (source.toLowerCase() === "plc" && title.includes("PLC") && title.includes("未连接"));
    }

    function getVisibleMaintenanceAdvice(health) {
        const triggerSource = getCurrentTriggerSource(health);
        const advice = getFieldArray(health, "maintenanceAdvice", "MaintenanceAdvice");
        if (triggerSource === "PLC") return advice;
        return advice.filter((item) => !isPlcNotConnectedAdvice(item));
    }

    function isStartupFailStatus(status) {
        const value = String(status ?? "").trim().toLowerCase();
        return value === "fail" || value === "2";
    }

    function getRoleLabel(role) {
        const value = String(role || "");
        if (value === "Primary") return "主模型";
        if (value === "Auxiliary1") return "子模型1";
        if (value === "Auxiliary2") return "子模型2";
        return value || "模型";
    }

    function getRegistryMatchLabel(strategy) {
        const value = String(strategy || "");
        if (value === "ModelPath") return "路径匹配";
        if (value === "UsedModelName") return "名称匹配";
        if (value === "ModelFileName") return "文件名匹配";
        return value || "未匹配";
    }

    function renderModelSlotChecklist(modelProbe) {
        const slots = Array.isArray(modelProbe.slots) ? modelProbe.slots :
            Array.isArray(modelProbe.Slots) ? modelProbe.Slots : [];
        const loadedSlots = slots.filter((slot) => (slot.isLoaded ?? slot.IsLoaded) === true);
        if (loadedSlots.length === 0) return "未加载";

        return loadedSlots.map((slot) => {
            const role = getRoleLabel(slot.role ?? slot.Role);
            const fileName = slot.modelFileName || slot.ModelFileName || "-";
            const modelId = slot.modelId || slot.ModelId || "";
            const version = slot.version || slot.Version || "";
            const hash = slot.modelHashPrefix || slot.ModelHashPrefix || "";
            const matched = (slot.registryMatched ?? slot.RegistryMatched) === true;
            const strategy = getRegistryMatchLabel(slot.registryMatchStrategy || slot.RegistryMatchStrategy);
            const identity = [modelId && version ? `${modelId}@${version}` : modelId || "", hash ? `#${hash}` : ""]
                .filter(Boolean)
                .join(" ");
            const badgeClass = matched ? "" : " warning";
            return `<span class="cf-diagnostic-line">${escapeHtml(role)}: ${escapeHtml(fileName)} ${identity ? `· ${escapeHtml(identity)}` : ""} <em class="cf-diagnostic-badge${badgeClass}">${escapeHtml(strategy)}</em></span>`;
        }).join("");
    }

    function renderRecipeChecklist(health) {
        const recipeId = getFieldValue(health, "recipeId", "RecipeId", "");
        const recipeVersion = getFieldValue(health, "recipeVersion", "RecipeVersion", "");
        if (!recipeId && !recipeVersion) return "未加载";

        const targetLabel = getFieldValue(health, "recipeTargetLabel", "RecipeTargetLabel", "");
        const targetCount = getFieldValue(health, "recipeTargetCount", "RecipeTargetCount", "");
        const target = targetLabel ? ` · ${targetLabel} x${targetCount || 0}` : "";
        return `${escapeHtml(recipeId || "default")} / ${escapeHtml(recipeVersion || "-")}${escapeHtml(target)}`;
    }

    function renderStartupBlockersChecklist(health) {
        const startup = getFieldObject(health, "startupDiagnostics", "StartupDiagnostics");
        const items = Array.isArray(startup.items) ? startup.items :
            Array.isArray(startup.Items) ? startup.Items : [];
        const blockers = items.filter((item) => {
            const isBlocking = (item.isBlocking ?? item.IsBlocking) === true;
            return isBlocking && isStartupFailStatus(item.status ?? item.Status);
        });
        if (blockers.length === 0) return `<em class="cf-diagnostic-badge">无阻断</em>`;

        const names = blockers
            .slice(0, 3)
            .map((item) => item.name || item.Name || item.message || item.Message || "阻断项")
            .join(" / ");
        const suffix = blockers.length > 3 ? ` +${blockers.length - 3}` : "";
        return `<em class="cf-diagnostic-badge error">${blockers.length}项</em> ${escapeHtml(names + suffix)}`;
    }

    function renderQueueChecklist(health) {
        const queues = getFieldObject(health, "queues", "Queues");
        const imagePending = queues.imagePending ?? queues.ImagePending ?? getFieldValue(health, "imageQueueLength", "ImageQueueLength", 0);
        const imageCapacity = queues.imageCapacity ?? queues.ImageCapacity ?? getFieldValue(health, "imageQueueCapacity", "ImageQueueCapacity", 0);
        const recordPending = queues.recordPending ?? queues.RecordPending ?? getFieldValue(health, "recordQueueLength", "RecordQueueLength", 0);
        const recordCapacity = queues.recordCapacity ?? queues.RecordCapacity ?? getFieldValue(health, "recordQueueCapacity", "RecordQueueCapacity", 0);
        const imageFailures = toFiniteNumber(queues.imageDroppedCount ?? queues.ImageDroppedCount) +
            toFiniteNumber(queues.imageFailedCount ?? queues.ImageFailedCount);
        const recordFailures = toFiniteNumber(queues.recordDroppedCount ?? queues.RecordDroppedCount) +
            toFiniteNumber(queues.recordFailedCount ?? queues.RecordFailedCount);
        const backlog = String(queues.backlogLevel || queues.BacklogLevel || "").toLowerCase();
        const warning = backlog === "warning" || imageFailures > 0 || recordFailures > 0;
        const badge = warning
            ? `<em class="cf-diagnostic-badge warning">需关注</em>`
            : `<em class="cf-diagnostic-badge">正常</em>`;
        const failures = imageFailures + recordFailures > 0 ? ` · 异常 ${imageFailures + recordFailures}` : "";
        return `${badge} 图像 ${formatQueueText(imagePending, imageCapacity)} · 记录 ${formatQueueText(recordPending, recordCapacity)}${failures}`;
    }

    function renderAuditChainChecklist(health) {
        const audit = getFieldObject(health, "auditChain", "AuditChain");
        const status = String(audit.status || audit.Status || "NotChecked");
        const checkedAt = audit.checkedAt || audit.CheckedAt || "";
        const totalRecords = audit.totalRecords ?? audit.TotalRecords ?? 0;
        const verifiedRecords = audit.verifiedRecords ?? audit.VerifiedRecords ?? 0;
        const findingCount = audit.findingCount ?? audit.FindingCount ?? 0;
        const lastHash = audit.lastRecordSha256 || audit.LastRecordSha256 || "";
        const statusLower = status.toLowerCase();
        if (!checkedAt || statusLower === "notchecked") {
            return `<em class="cf-diagnostic-badge pending">未校验</em>`;
        }

        const badgeClass = statusLower === "healthy"
            ? ""
            : statusLower === "warning"
                ? " warning"
                : " error";
        const hashText = lastHash ? ` · Last ${shortHash(lastHash)}` : "";
        return `<em class="cf-diagnostic-badge${badgeClass}">${escapeHtml(status)}</em> Verified ${escapeHtml(verifiedRecords)}/${escapeHtml(totalRecords)} · Findings ${escapeHtml(findingCount)}${escapeHtml(hashText)}`;
    }

    function renderFieldAcceptanceChecklist(health, modelProbe) {
        setHtml("diag-model-slot-list", renderModelSlotChecklist(modelProbe), "等待模型快照");
        setHtml("diag-recipe-version", renderRecipeChecklist(health), "未加载");
        setHtml("diag-startup-blockers", renderStartupBlockersChecklist(health), "无阻断项");
        setHtml("diag-queue-health", renderQueueChecklist(health), "正常");
        setHtml("diag-audit-chain", renderAuditChainChecklist(health), "未校验");
    }

    function getAdviceLevelClass(level) {
        const value = String(level || "").trim().toLowerCase();
        if (value === "critical" || value === "error") return "critical";
        if (value === "warning") return "warning";
        return "ok";
    }

    function renderMaintenanceAdviceList(health) {
        const advice = getVisibleMaintenanceAdvice(health);
        if (advice.length === 0) {
            setHtml(
                "diag-maintenance-advice",
                `<div class="cf-maintenance-advice-item ok"><strong>暂无建议</strong><span>当前没有需要处理的诊断建议</span></div>`,
            );
            return;
        }

        const html = advice.slice(0, 4).map((item) => {
            const levelClass = getAdviceLevelClass(item.level || item.Level);
            const title = item.title || item.Title || "维护建议";
            const action = item.advice || item.Advice || "";
            const code = item.code || item.Code || "";
            const adviceId = item.adviceId || item.AdviceId || "";
            const resolutionStatus = item.resolutionStatus || item.ResolutionStatus || "Open";
            const lastActionMessage = item.lastActionMessage || item.LastActionMessage || "";
            const statusBadge = resolutionStatus && resolutionStatus !== "Open"
                ? `<em class="cf-diagnostic-badge pending">${escapeHtml(resolutionStatus)}</em>`
                : "";
            const actionHtml = adviceId
                ? `<div class="cf-maintenance-actions">
                    <button type="button" data-action="acknowledgeMaintenanceAdvice" data-value="${escapeHtml(JSON.stringify(adviceId))}">
                        <span>已处理</span>
                    </button>
                    <button type="button" data-action="recheckMaintenanceAdvice" data-value="${escapeHtml(JSON.stringify(adviceId))}">
                        <span>复检</span>
                    </button>
                </div>`
                : "";
            return `<div class="cf-maintenance-advice-item ${levelClass}" data-code="${escapeHtml(code)}">
                <strong>${escapeHtml(title)} ${statusBadge}</strong>
                <em>下一步：${escapeHtml(action || "请打开工程师详情查看诊断信息。")}</em>
                ${lastActionMessage ? `<span>${escapeHtml(lastActionMessage)}</span>` : ""}
                ${actionHtml}
            </div>`;
        }).join("");
        setHtml("diag-maintenance-advice", html);
    }

    function renderMaintenanceHistoryRecheckButton(adviceId) {
        const id = String(adviceId || "").trim();
        if (!id) return "";

        return `<button type="button" data-action="recheckMaintenanceAdvice" data-value="${escapeHtml(JSON.stringify(id))}">
            <span>复检</span>
        </button>`;
    }

    function renderMaintenanceAdviceHistory(state, health) {
        const fromState = Array.isArray(state?.maintenanceAdviceHistory) ? state.maintenanceAdviceHistory : [];
        const fromHealth = getFieldArray(health, "maintenanceAdviceHistory", "MaintenanceAdviceHistory");
        const history = fromState.length > 0 ? fromState : fromHealth;
        if (history.length === 0) {
            setHtml(
                "diag-maintenance-history",
                `<div class="cf-maintenance-history-empty">
                    <strong>暂无处理记录</strong>
                    <span>处理或复检维护建议后会出现在这里</span>
                </div>`,
            );
            return;
        }

        const html = history.slice(0, 5).map((record) => {
            const adviceId = record.adviceId || record.AdviceId || "";
            const title = record.title || record.Title || "维护建议";
            const status = record.status || record.Status || "-";
            const message = record.message || record.Message || "";
            const actionAt = record.actionAt || record.ActionAt || "";
            const operatorId = record.operatorId || record.OperatorId || "";
            const recheckButton = renderMaintenanceHistoryRecheckButton(adviceId);
            return `<div class="cf-maintenance-history-item">
                <div>
                    <strong>${escapeHtml(title)}</strong>
                    <span>${escapeHtml(status)} · ${escapeHtml(operatorId || "-")} · ${escapeHtml(formatDiagnosticDateTime(actionAt))}</span>
                    ${message ? `<em>${escapeHtml(message)}</em>` : ""}
                </div>
                ${recheckButton}
            </div>`;
        }).join("");
        setHtml("diag-maintenance-history", html);
    }

    function renderShiftTaskBoard(health) {
        const tasks = getFieldArray(health, "shiftTasks", "ShiftTasks");
        if (tasks.length === 0) {
            setHtml(
                "diag-shift-task-list",
                `<div class="cf-shift-task-empty">
                    <strong>暂无班次待办</strong>
                    <span>当前无需要交接跟进的诊断任务</span>
                </div>`,
            );
            return;
        }

        const html = tasks.slice(0, 6).map((task) => {
            const levelClass = getAdviceLevelClass(task.level || task.Level);
            const status = task.status || task.Status || "Open";
            const source = task.source || task.Source || "Diagnostics";
            const title = task.title || task.Title || "班次待办";
            const evidence = task.evidence || task.Evidence || "";
            const action = task.action || task.Action || "";
            const owner = task.suggestedOwner || task.SuggestedOwner || "现场班组";
            const firstSeenAt = task.firstSeenAt || task.FirstSeenAt || "";
            const dueAt = task.dueAt || task.DueAt || "";
            const escalation = task.escalationLevel || task.EscalationLevel || "Normal";
            const isOverdue = (task.isOverdue ?? task.IsOverdue) === true;
            const adviceId = task.linkedAdviceId || task.LinkedAdviceId || "";
            const taskId = task.taskId || task.TaskId || "";
            const taskPayload = { taskId, linkedAdviceId: adviceId };
            const actionHtml = adviceId
                ? `<div class="cf-maintenance-actions">
                    <button type="button" data-action="acknowledgeShiftTask" data-value="${escapeHtml(JSON.stringify(taskPayload))}">
                        <span>已处理</span>
                    </button>
                    <button type="button" data-action="recheckShiftTask" data-value="${escapeHtml(JSON.stringify(taskPayload))}">
                        <span>复检</span>
                    </button>
                </div>`
                : "";
            const overdueBadge = isOverdue
                ? `<em class="cf-diagnostic-badge error">超时</em>`
                : `<em class="cf-diagnostic-badge ${escalation === "High" ? "warning" : ""}">${escapeHtml(escalation)}</em>`;
            return `<div class="cf-shift-task-item ${levelClass}${isOverdue ? " overdue" : ""}">
                <strong>${escapeHtml(title)}</strong>
                <span><em class="cf-diagnostic-badge ${levelClass === "critical" ? "error" : levelClass === "warning" ? "warning" : ""}">${escapeHtml(status)}</em>${overdueBadge}${escapeHtml(source)} · ${escapeHtml(owner)}</span>
                ${firstSeenAt ? `<span>首次 ${escapeHtml(formatDiagnosticDateTime(firstSeenAt))}</span>` : ""}
                <span>截止 ${escapeHtml(formatDiagnosticDateTime(dueAt))}</span>
                ${evidence ? `<span>${escapeHtml(evidence)}</span>` : ""}
                ${action ? `<em>${escapeHtml(action)}</em>` : ""}
                ${actionHtml}
            </div>`;
        }).join("");
        setHtml("diag-shift-task-list", html);
    }

    function renderDiagnosticPackageHistory(state) {
        const packages = Array.isArray(state?.diagnosticPackageHistory) ? state.diagnosticPackageHistory : [];
        if (packages.length === 0) {
            setHtml(
                "diag-package-history",
                `<div class="cf-diagnostics-package-empty">
                    <strong>暂无历史诊断包</strong>
                    <span>导出诊断包后会出现在这里</span>
                </div>`,
            );
            return;
        }

        const html = packages.slice(0, 8).map((pkg) => {
            const path = getDiagnosticPackagePath(pkg);
            const fileName = getDiagnosticPackageFileName(pkg);
            const sizeBytes = getDiagnosticPackageValue(pkg, "sizeBytes", "SizeBytes", 0);
            const lastWriteTime = getDiagnosticPackageValue(pkg, "lastWriteTime", "LastWriteTime", "");
            const status = getDiagnosticPackageValue(pkg, "integrityStatus", "IntegrityStatus", "Pending");
            const verifiedEntries = getDiagnosticPackageValue(pkg, "verifiedEntryCount", "VerifiedEntryCount", "");
            const integrityEntries = getDiagnosticPackageValue(pkg, "integrityEntryCount", "IntegrityEntryCount", "");
            const findingCount = getDiagnosticPackageValue(pkg, "integrityFindingCount", "IntegrityFindingCount", "");
            const verifiedAt = getDiagnosticPackageValue(pkg, "verifiedAt", "VerifiedAt", "");
            const statusLower = String(status || "").toLowerCase();
            const statusClass = statusLower === "healthy"
                ? "ok"
                : statusLower === "pending"
                    ? "pending"
                    : "warning";
            const entryText = integrityEntries
                ? `${verifiedEntries || 0}/${integrityEntries}`
                : "待复核";
            const findingText = findingCount === "" || findingCount === undefined
                ? ""
                : ` · 异常 ${findingCount}`;
            const verifiedText = verifiedAt ? ` · ${formatDiagnosticDateTime(verifiedAt)}` : "";
            return `<div class="cf-diagnostics-package-history-item">
                <div>
                    <strong title="${escapeHtml(path)}">${escapeHtml(fileName)}</strong>
                    <span>${escapeHtml(formatBytesCompact(sizeBytes))} · ${escapeHtml(formatDiagnosticDateTime(lastWriteTime))}</span>
                    <em class="cf-diagnostic-badge ${statusClass}">${escapeHtml(status || "Pending")} · ${escapeHtml(entryText)}${escapeHtml(findingText)}${escapeHtml(verifiedText)}</em>
                </div>
                <button type="button" data-action="verifyDiagnosticPackage" data-value="${escapeHtml(JSON.stringify(path))}">
                    <span>复核</span>
                </button>
            </div>`;
        }).join("");
        setHtml("diag-package-history", html);
    }

    function renderFieldHandoffReportHistory(state) {
        const reports = Array.isArray(state?.fieldHandoffReportHistory) ? state.fieldHandoffReportHistory : [];
        if (reports.length === 0) {
            setHtml(
                "diag-handoff-report-history",
                `<div class="cf-handoff-report-empty">
                    <strong>暂无历史交接报告</strong>
                    <span>导出交接报告后会出现在这里</span>
                </div>`,
            );
            return;
        }

        const html = reports.slice(0, 5).map((report) => {
            const path = getFieldHandoffReportPath(report);
            const fileName = getFieldHandoffReportFileName(report);
            const sizeBytes = getFieldHandoffReportValue(report, "sizeBytes", "SizeBytes", 0);
            const status = getFieldHandoffReportValue(report, "overallStatus", "OverallStatus", "Pending");
            const shiftTaskCount = getFieldHandoffReportValue(report, "shiftTaskCount", "ShiftTaskCount", "");
            const generatedAt = getFieldHandoffReportValue(report, "generatedAt", "GeneratedAt", "") ||
                getFieldHandoffReportValue(report, "lastWriteTime", "LastWriteTime", "");
            const statusLower = String(status || "").toLowerCase();
            const statusClass = statusLower === "ready"
                ? "ok"
                : statusLower === "blocked"
                    ? "error"
                    : "warning";
            return `<div class="cf-handoff-report-history-item">
                <div>
                    <strong title="${escapeHtml(path)}">${escapeHtml(fileName)}</strong>
                    <span>${escapeHtml(formatBytesCompact(sizeBytes))} · ${escapeHtml(formatDiagnosticDateTime(generatedAt))}${shiftTaskCount === "" ? "" : ` · 待办 ${escapeHtml(shiftTaskCount)}`}</span>
                    <em class="cf-diagnostic-badge ${statusClass}">${escapeHtml(status || "Pending")}</em>
                </div>
            </div>`;
        }).join("");
        setHtml("diag-handoff-report-history", html);
    }

    function getStartupBlockingItems(health) {
        const startup = getFieldObject(health, "startupDiagnostics", "StartupDiagnostics");
        const items = Array.isArray(startup.items) ? startup.items :
            Array.isArray(startup.Items) ? startup.Items : [];
        return items.filter((item) => {
            const isBlocking = (item.isBlocking ?? item.IsBlocking) === true;
            return isBlocking && isStartupFailStatus(item.status ?? item.Status);
        });
    }

    function isCameraReadyStatus(status) {
        const value = String(status || "").trim().toLowerCase();
        return value === "open" || value === "grabbing" || value === "connected";
    }

    function isPlcReadyStatus(status) {
        return String(status || "").trim().toLowerCase().startsWith("connected");
    }

    function formatFieldCameraStatus(status) {
        const value = String(status || "").trim();
        if (isCameraReadyStatus(value)) return "正常";
        if (!value || value === "Closed" || value === "Disconnected") return "未连接";
        return "异常";
    }

    function formatFieldPlcStatus(status, triggerSource = getCurrentTriggerSource()) {
        const value = String(status || "").trim();
        if (isPlcReadyStatus(value)) return "正常";
        if (normalizeTriggerSource(triggerSource) !== "PLC") return "未使用";
        if (!value || value === "Disconnected" || value === "NotConnected") return "未连接";
        return "异常";
    }

    function formatFieldStorageStatus(health) {
        const status = String(getFieldValue(health, "storageStatus", "StorageStatus", "") || "").trim();
        const freeDisk = Number(getFieldValue(health, "freeDiskGb", "FreeDiskGb", 0));
        if (Number.isFinite(freeDisk) && freeDisk > 0 && freeDisk < 2) return "空间不足";
        if (status.toLowerCase() === "writable" || status === "正常") return "正常";
        if (!status) return "正常";
        return "异常";
    }

    function isModelReady(modelProbe, currentModel) {
        const loaded = modelProbe.isModelLoaded ?? modelProbe.IsModelLoaded;
        if (loaded !== undefined) return Boolean(loaded);
        const name = String(currentModel || "").trim();
        return Boolean(name) && name !== "未加载";
    }

    function renderProductionReadiness(health, modelProbe, currentModel) {
        const triggerSource = getCurrentTriggerSource(health);
        const blockers = getStartupBlockingItems(health);
        const advice = getVisibleMaintenanceAdvice(health);
        const hasIssues = blockers.length > 0 || advice.some((item) => {
            const level = String(item.level || item.Level || "").toLowerCase();
            return level === "critical" || level === "warning";
        });
        const needsEngineer = advice.some((item) => {
            const text = `${item.title || item.Title || ""} ${item.advice || item.Advice || ""} ${item.code || item.Code || ""}`;
            return text.includes("严格模型验证") || text.includes("模型未完成上线验证") || text.includes("StartupBlocked");
        });
        const cameraReady = isCameraReadyStatus(getFieldValue(health, "cameraStatus", "CameraStatus"));
        const plcReady = isPlcReadyStatus(getFieldValue(health, "plcStatus", "PlcStatus"));
        const serialReady = isSerialTriggerConnected();
        const modelReady = isModelReady(modelProbe, currentModel);
        const storageReady = formatFieldStorageStatus(health) === "正常";
        const commonReady = cameraReady && modelReady && storageReady;

        if (needsEngineer) {
            setText("diag-production-readiness", "工程师检查");
            setText("diag-production-guidance", "模型上线验证或启动诊断需要工程师处理。");
            return;
        }

        if (triggerSource === "Manual") {
            setText("diag-production-readiness", commonReady && !hasIssues ? "可手动检测" : "需要处理");
            setText("diag-production-guidance", "可进行手动检测；自动生产触发未启用。");
            return;
        }

        if (triggerSource === "SerialPhotoelectric" && !serialReady) {
            setText("diag-production-readiness", "需要处理");
            setText("diag-production-guidance", "需要连接串口光电触发器后才能自动生产。");
            return;
        }

        if (triggerSource === "PLC" && !plcReady) {
            setText("diag-production-readiness", "需要处理");
            setText("diag-production-guidance", "需要连接 PLC 后才能自动生产。");
            return;
        }

        if (hasIssues || !commonReady) {
            setText("diag-production-readiness", "需要处理");
            setText("diag-production-guidance", "请先查看待处理问题，并按下一步建议处理。");
            return;
        }

        setText("diag-production-readiness", "可以生产");
        setText(
            "diag-production-guidance",
            triggerSource === "SerialPhotoelectric"
                ? "串口光电触发器已连接，设备、模型和存储状态正常。"
                : "设备、模型和存储状态正常。");
    }

    function renderFieldDiagnostics(state) {
        const health = state?.health || {};
        const modelProbe = health.modelProbe || health.ModelProbe || {};
        const currentModel = getFieldValue(health, "currentModelName", "CurrentModelName") ||
            modelProbe.currentModelName || modelProbe.CurrentModelName ||
            getFieldValue(health, "modelStatus", "ModelStatus");

        setText("diag-camera-status", formatFieldCameraStatus(getFieldValue(health, "cameraStatus", "CameraStatus")), "未连接");
        const triggerSource = getCurrentTriggerSource(health);
        updateTriggerSourceStatus(triggerSource);
        setText("diag-plc-status", formatFieldPlcStatus(getFieldValue(health, "plcStatus", "PlcStatus"), triggerSource), "未连接");
        setText("diag-current-model", isModelReady(modelProbe, currentModel) ? "已加载" : "未加载", "未加载");
        setText("diag-storage-status", formatFieldStorageStatus(health), "正常");
        renderProductionReadiness(health, modelProbe, currentModel);
        setText("diag-last-inspection-id", getFieldValue(health, "lastInspectionId", "LastInspectionId"), "-");
        setText("diag-p95", `${getFieldValue(health, "recentInspectionP95Ms", "RecentInspectionP95Ms", 0)}ms`, "0ms");
        setText("diag-p99", `${getFieldValue(health, "recentInspectionP99Ms", "RecentInspectionP99Ms", 0)}ms`, "0ms");
        setText(
            "diag-image-queue",
            formatQueueText(
                getFieldValue(health, "imageQueueLength", "ImageQueueLength", 0),
                getFieldValue(health, "imageQueueCapacity", "ImageQueueCapacity", 0),
            ),
            "0/-",
        );
        setText(
            "diag-record-queue",
            formatQueueText(
                getFieldValue(health, "recordQueueLength", "RecordQueueLength", 0),
                getFieldValue(health, "recordQueueCapacity", "RecordQueueCapacity", 0),
            ),
            "0/-",
        );
        setText("diag-free-disk", `${getFieldValue(health, "freeDiskGb", "FreeDiskGb", 0)}GB`, "0GB");
        setText("diag-memory", `${getFieldValue(health, "memoryMb", "MemoryMb", 0)}MB`, "0MB");
        setText("diag-last-error", getLastFieldError(health), "暂无错误");
        setText("diag-stage-timing", getLastTimingText(health), "等待检测");
        renderFieldAcceptanceChecklist(health, modelProbe);
        renderMaintenanceAdviceList(health);
        renderMaintenanceAdviceHistory(state, health);
        renderShiftTaskBoard(health);

        const debug = state?.fieldDebug || {};
        const debugMessage = debug.message || debug.Message || "等待调试命令";
        const debugCode = debug.errorCode || debug.ErrorCode || "";
        setText("diag-debug-status", debugCode ? `${debugMessage} [${debugCode}]` : debugMessage, "等待调试命令");

        const pkg = state?.diagnosticPackage || {};
        setText("diag-package-path", pkg.path || pkg.Path || pkg.message || pkg.Message || "", "尚未导出");
        const packageSha = getDiagnosticPackageValue(pkg, "packageSha256", "PackageSha256", "");
        const indexSha = getDiagnosticPackageValue(pkg, "indexSha256", "IndexSha256", "");
        const integrityStatus = getDiagnosticPackageValue(pkg, "integrityStatus", "IntegrityStatus", "");
        const integrityEntries = getDiagnosticPackageValue(pkg, "integrityEntryCount", "IntegrityEntryCount", "");
        const verifiedEntries = getDiagnosticPackageValue(pkg, "verifiedEntryCount", "VerifiedEntryCount", "");
        setText("diag-package-sha", shortHash(packageSha), "-");
        setText("diag-index-sha", shortHash(indexSha), "-");
        setText("diag-package-size", formatBytesCompact(pkg.sizeBytes ?? pkg.SizeBytes), "-");
        setText("diag-integrity-status", integrityStatus, "-");
        setText(
            "diag-index-entry-count",
            integrityEntries && verifiedEntries !== "" ? `${verifiedEntries}/${integrityEntries}` : integrityEntries,
            "-",
        );
        const packageShaEl = el("diag-package-sha");
        const indexShaEl = el("diag-index-sha");
        if (packageShaEl) packageShaEl.title = packageSha || "";
        if (indexShaEl) indexShaEl.title = indexSha || "";
        renderDiagnosticPackageHistory(state);

        const handoff = state?.fieldHandoffReport || {};
        const handoffPath = handoff.path || handoff.Path || handoff.reportPath || handoff.ReportPath || "";
        const handoffStatus = handoff.overallStatus || handoff.OverallStatus || "-";
        const handoffGeneratedAt = handoff.generatedAt || handoff.GeneratedAt || "";
        const handoffSize = handoff.sizeBytes ?? handoff.SizeBytes;
        setText("diag-handoff-report-path", handoffPath || handoff.message || handoff.Message || "", "尚未导出");
        setText("diag-handoff-status", handoffStatus, "-");
        setText("diag-handoff-size", formatBytesCompact(handoffSize), "-");
        setText("diag-handoff-generated-at", formatDiagnosticDateTime(handoffGeneratedAt), "-");
        renderFieldHandoffReportHistory(state);
    }

    function openFieldDiagnosticsPanel() {
        const modal = el("field-diagnostics-modal");
        if (!modal) return;
        modal.classList.remove("hidden");
        updateTriggerSourceStatus();
        window.sendCommand("request_health_snapshot");
        requestDiagnosticPackageHistory();
        requestFieldHandoffReportHistory();
    }

    function closeFieldDiagnosticsPanel() {
        el("field-diagnostics-modal")?.classList.add("hidden");
    }

    function getVisionDebugSettings() {
        return store.state.settings || {};
    }

    const VisionDebugPreprocessHints = Object.freeze({
        StandardLetterBox: {
            text: "StandardLetterBox：生产默认等比缩放 + 居中填充，适合标准 YOLO 导出模型。",
            risk: "",
        },
        IndustrialFast: {
            text: "IndustrialFast：历史工业快速模式，减少插值开销，适合低配 IPC 做临时调试。",
            risk: "风险：与标准 letterbox 坐标契约不同，窄图/竖图/小目标可能出现框位偏移或召回下降；仅建议对比验证，不改变生产默认。",
        },
    });

    function getVisionDebugRuleSetJson() {
        return String(el("vision-debug-rule-set")?.value || "").trim();
    }

    function setVisionDebugRuleSetJson(value) {
        const node = el("vision-debug-rule-set");
        if (node && node.value !== value) node.value = value || "";
        updateVisionDebugRuleSummary();
        validateVisionDebugRuleJson(false);
    }

    function getVisionDebugTemplateId() {
        return String(el("vision-debug-template-select")?.value || "screw_count");
    }

    function setVisionDebugImageEmptyVisible(visible) {
        const node = el("vision-debug-image-empty");
        if (node) node.classList.toggle("hidden", !visible);
    }

    function parseVisionDebugRuleSet() {
        const raw = getVisionDebugRuleSetJson();
        if (!raw) return null;
        return JSON.parse(raw);
    }

    function validateVisionDebugRuleJson(showToastOnError = true) {
        const status = el("vision-debug-rule-json-status");
        try {
            parseVisionDebugRuleSet();
            if (status) {
                status.textContent = "规则 JSON 格式正确。";
                status.classList.remove("error");
            }
            return true;
        } catch (ex) {
            const message = `规则 JSON 无效：${ex.message}`;
            if (status) {
                status.textContent = message;
                status.classList.add("error");
            }
            if (showToastOnError) showToast(message, "error", 1800);
            return false;
        }
    }

    function formatVisionDebugRuleSummary(ruleSet) {
        const settings = getVisionDebugSettings();
        const fallbackLabel = String(settings.TargetLabel ?? settings.targetLabel ?? "screw");
        const fallbackCount = Number(settings.TargetCount ?? settings.targetCount ?? 4);
        const rules = ruleSet?.Rules || ruleSet?.rules || [];
        if (!Array.isArray(rules) || rules.length === 0) {
            return [`目标：${fallbackLabel || "screw"}，数量：${Number.isFinite(fallbackCount) ? fallbackCount : 4}，最低置信度：0.0`];
        }

        return rules.slice(0, 4).map((rule) => {
            const type = String(rule.Type || rule.type || "");
            const label = rule.Label || rule.label || rule.TargetLabel || rule.targetLabel || fallbackLabel || "screw";
            const count = rule.Count ?? rule.count ?? rule.ExpectedCount ?? rule.expectedCount ?? fallbackCount;
            const minConfidence = rule.MinConfidence ?? rule.minConfidence ?? 0;
            const expectedLabels = rule.ExpectedLabels || rule.expectedLabels || [];
            if (type === "OrderedLabels" || expectedLabels.length > 0) {
                const labels = Array.isArray(expectedLabels)
                    ? expectedLabels.join(" → ")
                    : String(expectedLabels).split(",").map((item) => item.trim()).filter(Boolean).join(" → ");
                return `顺序：${labels || "未配置"}`;
            }

            if (type === "RelativePosition") {
                const primary = rule.PrimaryLabel || rule.primaryLabel || label;
                const reference = rule.ReferenceLabel || rule.referenceLabel || "参考目标";
                return `相对位置：${primary} 对 ${reference}`;
            }

            return `目标：${label}，数量：${count ?? 0}，最低置信度：${minConfidence}`;
        });
    }

    function updateVisionDebugRuleSummary() {
        const list = el("vision-debug-rule-summary");
        if (!list) return;
        let lines;
        try {
            lines = formatVisionDebugRuleSummary(parseVisionDebugRuleSet());
        } catch {
            lines = ["规则 JSON 无效，请展开高级区域修正后再保存。"];
        }
        list.innerHTML = lines.map((line) => `<div>${escapeHtml(line)}</div>`).join("");
    }

    function populateVisionDebugControls() {
        const settings = getVisionDebugSettings();
        const confidence = Number(settings.Confidence ?? settings.confidence ?? 0.5);
        const iou = Number(settings.IouThreshold ?? settings.iouThreshold ?? 0.3);
        const targetLabel = String(settings.TargetLabel ?? settings.targetLabel ?? "");
        const targetCount = Number(settings.TargetCount ?? settings.targetCount ?? 0);
        const ruleSetJson = settings.InspectionRuleSetJson || settings.inspectionRuleSetJson || "";
        const labels = Array.isArray(store.state.modelLabels) ? store.state.modelLabels : [];
        const confNode = el("vision-debug-confidence");
        const iouNode = el("vision-debug-iou");
        const targetCountNode = el("vision-debug-target-count");
        const targetSelect = el("vision-debug-target-label");
        const roiNode = el("vision-debug-roi-enabled");
        const preprocessNode = el("vision-debug-preprocess-mode");
        if (confNode) confNode.value = String(Math.max(0, Math.min(1, confidence)));
        if (iouNode) iouNode.value = String(Math.max(0, Math.min(1, iou)));
        if (targetCountNode) targetCountNode.value = String(Math.max(0, Math.trunc(targetCount || 0)));
        if (roiNode) roiNode.checked = true;
        if (preprocessNode) preprocessNode.value = "StandardLetterBox";
        if (targetSelect) {
            const options = [`<option value="">全部目标</option>`]
                .concat(labels.map((label) => `<option value="${escapeHtml(label)}">${escapeHtml(label)}</option>`));
            if (targetLabel && !labels.includes(targetLabel)) {
                options.push(`<option value="${escapeHtml(targetLabel)}">${escapeHtml(targetLabel)}</option>`);
            }
            targetSelect.innerHTML = options.join("");
            targetSelect.value = targetLabel;
        }
        if (!getVisionDebugRuleSetJson()) setVisionDebugRuleSetJson(ruleSetJson);
        updateVisionDebugRuleSummary();
        updateVisionDebugSliderLabels();
        updateVisionDebugPreprocessHint();
    }

    function updateVisionDebugSliderLabels() {
        const conf = Number(el("vision-debug-confidence")?.value ?? 0);
        const iou = Number(el("vision-debug-iou")?.value ?? 0);
        setText("vision-debug-confidence-value", conf.toFixed(2), "0.50");
        setText("vision-debug-iou-value", iou.toFixed(2), "0.30");
    }

    function updateVisionDebugPreprocessHint(snapshot = null) {
        const mode = String(el("vision-debug-preprocess-mode")?.value || snapshot?.preprocessingMode || snapshot?.PreprocessingMode || "StandardLetterBox");
        const hint = VisionDebugPreprocessHints[mode] || VisionDebugPreprocessHints.StandardLetterBox;
        const failed = snapshot && (snapshot.succeeded === false || snapshot.Succeeded === false);
        const errorText = failed
            ? `失败: ${snapshot.errorCode || snapshot.ErrorCode || ""} ${snapshot.message || snapshot.Message || snapshot.primaryFailureReason || snapshot.PrimaryFailureReason || ""}`.trim()
            : "";
        setText(
            "vision-debug-preprocess-help",
            [hint.text, hint.risk, errorText].filter(Boolean).join(" "),
            hint.text,
        );
    }

    function openVisionDebugPanel() {
        populateVisionDebugControls();
        const role = String(getVisionDebugSettings().CurrentOperatorRole || getVisionDebugSettings().currentOperatorRole || "Operator");
        const note = el("vision-debug-operator-note");
        if (note) note.classList.toggle("hidden", role !== "Operator");
        if (role === "Operator") {
            showToast("这是工程师调试工具，生产操作无需进入。", "info", 1800);
        }
        el("vision-debug-modal")?.classList.remove("hidden");
        requestVisionDebugRecentRecords();
        setVisionDebugImageEmptyVisible(true);
        redrawVisionDebugOverlay();
    }

    function closeVisionDebugPanel() {
        el("vision-debug-modal")?.classList.add("hidden");
    }

    function collectVisionDebugParams(extra = {}) {
        return {
            confidence: Number(el("vision-debug-confidence")?.value ?? 0.5),
            iouThreshold: Number(el("vision-debug-iou")?.value ?? 0.3),
            targetLabel: String(el("vision-debug-target-label")?.value || ""),
            targetCount: Number(el("vision-debug-target-count")?.value ?? 0),
            preprocessingMode: String(el("vision-debug-preprocess-mode")?.value || "StandardLetterBox"),
            roiEnabled: Boolean(el("vision-debug-roi-enabled")?.checked),
            ruleSetJson: getVisionDebugRuleSetJson(),
            ...extra,
        };
    }

    function requestVisionDebugRecentRecords() {
        window.sendCommand("vision_debug_query_recent", {});
    }

    function applySelectedVisionDebugTemplate() {
        const templateId = getVisionDebugTemplateId();
        if (templateId === "custom_rule") {
            showToast("自定义规则可在高级区域编辑 JSON。", "info", 1400);
            updateVisionDebugRuleSummary();
            return;
        }

        applyVisionDebugTemplate(templateId);
    }

    function showVisionDebugLocalImageNotice() {
        showToast("本地图片导入暂未开放，请先使用当前相机或历史样本。", "info", 1800);
    }

    function runVisionDebugCurrent() {
        if (!validateVisionDebugRuleJson()) return;
        setText("vision-debug-primary-reason", "正在重跑当前帧...");
        window.sendCommand("vision_debug_run_current", collectVisionDebugParams());
        window.handleCommandDispatched?.("vision_debug_run_current");
    }

    function runVisionDebugHistory() {
        const recordId = Number(el("vision-debug-history-select")?.value || 0);
        if (!recordId) {
            showToast("请选择历史样本", "warning", 1200);
            return;
        }
        if (!validateVisionDebugRuleJson()) return;
        setText("vision-debug-history-status", "正在用当前调试参数重跑历史样本...");
        window.sendCommand("vision_debug_run_history", collectVisionDebugParams({ recordId }));
        window.handleCommandDispatched?.("vision_debug_run_history");
    }

    function runVisionDebugBatch() {
        if (!validateVisionDebugRuleJson()) return;
        const rawLimit = Number(el("vision-debug-batch-limit")?.value || 20);
        const batchLimit = Math.max(1, Math.min(50, Math.trunc(rawLimit || 20)));
        const batchResult = String(el("vision-debug-batch-result")?.value || "All");
        setText("vision-debug-history-status", `正在批量回放最近 ${batchLimit} 条样本...`);
        window.sendCommand("vision_debug_run_batch", collectVisionDebugParams({ batchLimit, batchResult }));
        window.handleCommandDispatched?.("vision_debug_run_batch");
    }

    function saveVisionDebugParams() {
        if (!validateVisionDebugRuleJson()) return;
        window.sendCommand("vision_debug_save_params", collectVisionDebugParams());
        window.handleCommandDispatched?.("vision_debug_save_params");
    }

    function applyVisionDebugTemplate(templateId) {
        const templateSelect = el("vision-debug-template-select");
        if (templateSelect && Array.from(templateSelect.options || []).some((option) => option.value === templateId)) {
            templateSelect.value = templateId;
        }
        const projectDefaultTemplates = new Set([
            "w5_screw_count",
            "w6_screw_count",
            "n5_remote_missing_part",
            "n6_remote_missing_part",
            "electric_heating_screw_count",
        ]);
        const labels = projectDefaultTemplates.has(String(templateId || "").toLowerCase())
            ? []
            : Array.from(el("vision-debug-target-label")?.options || [])
                .map((option) => option.value)
                .filter(Boolean);
        window.sendCommand("vision_debug_apply_template", collectVisionDebugParams({ templateId, labels }));
        window.handleCommandDispatched?.("vision_debug_apply_template");
    }

    function renderVisionDebugRecentRecords(records) {
        const select = el("vision-debug-history-select");
        if (!select) return;
        if (!Array.isArray(records) || records.length === 0) {
            select.innerHTML = `<option value="">暂无历史记录</option>`;
            setText("vision-debug-history-status", "未查询到可回放样本");
            setVisionDebugImageEmptyVisible(true);
            return;
        }
        select.innerHTML = records.map((record) => {
            const id = pickValue(record, "id", "Id") || "";
            const time = pickValue(record, "timestamp", "Timestamp") || "";
            const inspectionId = pickValue(record, "inspectionId", "InspectionId") || "-";
            const result = pickValue(record, "result", "Result") || "";
            return `<option value="${escapeHtml(id)}">${escapeHtml(time)} · ${escapeHtml(result)} · ${escapeHtml(inspectionId)}</option>`;
        }).join("");
        setText("vision-debug-history-status", `${records.length} 条最近样本，使用当前调试参数重跑`);
        setVisionDebugImageEmptyVisible(false);
    }

    function renderVisionDebugResult(state) {
        const debug = state?.visionDebug || {};
        if (Array.isArray(debug.records)) renderVisionDebugRecentRecords(debug.records);
        if (debug.status === "templateApplied") {
            setVisionDebugRuleSetJson(debug.ruleSetJson || debug.RuleSetJson || "");
            setText("vision-debug-primary-reason", debug.message || "场景模板已生成");
            return;
        }
        if (debug.status === "paramsSaved") {
            setText("vision-debug-primary-reason", debug.message || "算法调试参数已保存");
            showToast(debug.message || "算法调试参数已保存", "success", 1500);
            return;
        }
        if (debug.status === "batchCompleted") {
            renderVisionDebugBatchReplay(debug.batchReplay || debug.BatchReplay);
            setText("vision-debug-history-status", debug.message || "批量回放完成");
            showToast(debug.message || "批量回放完成", "success", 1800);
            return;
        }
        if (debug.status === "failed") {
            const message = debug.message || debug.Message || "算法调试失败";
            const code = debug.errorCode || debug.ErrorCode || "";
            renderVisionDebugFailure(message, code);
            addLog(message, "error");
            return;
        }
        const snapshot = debug.snapshot || debug.Snapshot;
        if (!snapshot) return;
        setVisionDebugImageEmptyVisible(false);
        const finalOk = Boolean(snapshot.finalOk ?? snapshot.FinalOk);
        const pill = el("vision-debug-final-result");
        if (pill) {
            pill.textContent = finalOk ? "OK" : "NG";
            pill.classList.remove("ok", "ng", "error");
            pill.classList.add(finalOk ? "ok" : "ng");
        }
        setText("vision-debug-primary-reason", snapshot.primaryFailureReason || snapshot.PrimaryFailureReason || (finalOk ? "规则判定 OK" : "规则判定 NG"));
        setText("vision-debug-count-all", (snapshot.allDetections || snapshot.AllDetections || []).length, "0");
        setText("vision-debug-count-in", (snapshot.roiIncludedDetections || snapshot.RoiIncludedDetections || []).length, "0");
        setText("vision-debug-count-out", (snapshot.roiExcludedDetections || snapshot.RoiExcludedDetections || []).length, "0");
        setText("vision-debug-elapsed", `${snapshot.elapsedMs ?? snapshot.ElapsedMs ?? 0}ms`, "0ms");
        const imageWarning = snapshot.imageSourceWarning || snapshot.ImageSourceWarning || snapshot.comparison?.imageWarning || snapshot.Comparison?.ImageWarning || "";
        setText("vision-debug-image-warning", imageWarning, "");
        renderVisionDebugParameterComparison(snapshot);
        renderVisionDebugBoxes(snapshot);
        renderVisionDebugRules(snapshot);
        renderVisionDebugComparison(snapshot);
        updateVisionDebugPreprocessHint(snapshot);
        redrawVisionDebugOverlay();
    }

    function renderVisionDebugFailure(message, errorCode = "") {
        const emptyFrame = String(errorCode || "").toLowerCase() === "nocurrentframe";
        const displayMessage = emptyFrame
            ? "还没有可调试图片。请先启动相机并获取一帧，或从历史记录选择样本。"
            : message || "算法调试失败";
        const pill = el("vision-debug-final-result");
        if (pill) {
            pill.textContent = emptyFrame ? "等待" : "错误";
            pill.classList.remove("ok", "ng", "error");
            if (!emptyFrame) pill.classList.add("error");
        }
        setVisionDebugImageEmptyVisible(emptyFrame);
        setText("vision-debug-primary-reason", displayMessage);
        updateVisionDebugPreprocessHint({ succeeded: false, message: displayMessage });
    }

    function renderVisionDebugBoxes(snapshot) {
        const list = el("vision-debug-box-list");
        if (!list) return;
        const boxes = snapshot.allDetections || snapshot.AllDetections || [];
        if (!boxes.length) {
            list.innerHTML = `<div>未检测到目标</div>`;
            return;
        }
        list.innerHTML = boxes.map((box) => {
            const filtered = Boolean(box.filteredOutByRoi ?? box.FilteredOutByRoi);
            const label = box.label || box.Label || `Class_${box.classId ?? box.ClassId}`;
            const confidence = Number(box.confidence ?? box.Confidence ?? 0);
            const centerX = Number(box.centerX ?? box.CenterX ?? 0);
            const centerY = Number(box.centerY ?? box.CenterY ?? 0);
            const index = box.index ?? box.Index ?? "-";
            return `<div class="${filtered ? "cf-vision-debug-box-muted" : ""}">
                <strong>#${index} ${escapeHtml(label)}</strong>
                <span>${confidence.toFixed(2)} · 中心(${centerX.toFixed(0)}, ${centerY.toFixed(0)})${filtered ? " · ROI外过滤" : ""}</span>
            </div>`;
        }).join("");
    }

    function renderVisionDebugRules(snapshot) {
        const list = el("vision-debug-rule-details");
        if (!list) return;
        const rules = snapshot.ruleResults || snapshot.RuleResults || [];
        if (!rules.length) {
            list.innerHTML = `<div>未启用规则或规则判定失败</div>`;
            return;
        }
        list.innerHTML = rules.map((rule) => {
            const ok = Boolean(rule.isMatch ?? rule.IsMatch);
            const name = rule.ruleName || rule.RuleName || rule.ruleType || rule.RuleType || "规则";
            const expected = rule.expected || rule.Expected || "";
            const actual = rule.actual || rule.Actual || "";
            const reason = rule.reason || rule.Reason || rule.message || rule.Message || "";
            const associated = rule.associationSummary || rule.AssociationSummary || "";
            const indexes = rule.associatedBoxIndexes || rule.AssociatedBoxIndexes || [];
            const associationText = associated || (Array.isArray(indexes) && indexes.length ? `关联目标框: ${indexes.map((index) => `#${index}`).join(", ")}` : "关联目标框: 无");
            return `<div>
                <strong>${ok ? "OK" : "NG"} · ${escapeHtml(name)}</strong>
                <span>期望: ${escapeHtml(expected)} | 实际: ${escapeHtml(actual)}</span>
                <span>原因: ${escapeHtml(reason)}</span>
                <span>${escapeHtml(associationText)}</span>
            </div>`;
        }).join("");
    }

    function renderVisionDebugParameterComparison(snapshot) {
        const list = el("vision-debug-param-diff");
        if (!list) return;
        const comparison = snapshot.parameterComparison || snapshot.ParameterComparison;
        const items = comparison?.items || comparison?.Items || [];
        if (!Array.isArray(items) || !items.length) {
            list.innerHTML = `<div>等待试运行后生成参数对比</div>`;
            return;
        }

        list.innerHTML = items.map((item) => {
            const changed = Boolean(item.isDifferent ?? item.IsDifferent);
            const name = item.displayName || item.DisplayName || item.field || item.Field || "参数";
            const productionValue = item.productionValue || item.ProductionValue || "";
            const trialValue = item.trialValue || item.TrialValue || "";
            return `<div class="${changed ? "" : "cf-vision-debug-box-muted"}">
                <strong>${changed ? "变更" : "一致"} · ${escapeHtml(name)}</strong>
                <span>生产: ${escapeHtml(productionValue)}</span>
                <span>试运行: ${escapeHtml(trialValue)}</span>
            </div>`;
        }).join("");
    }

    function renderVisionDebugBatchReplay(summary) {
        const list = el("vision-debug-batch-summary");
        if (!list) return;
        if (!summary) {
            list.innerHTML = `<div>等待批量回放</div>`;
            return;
        }

        const stats = [
            `旧 OK ${summary.oldOkCount ?? summary.OldOkCount ?? 0}`,
            `旧 NG ${summary.oldNgCount ?? summary.OldNgCount ?? 0}`,
            `新 OK ${summary.newOkCount ?? summary.NewOkCount ?? 0}`,
            `新 NG ${summary.newNgCount ?? summary.NewNgCount ?? 0}`,
            `变化 ${summary.changedCount ?? summary.ChangedCount ?? 0}`,
            `NG→OK ${summary.ngToOkCount ?? summary.NgToOkCount ?? 0}`,
            `OK→NG ${summary.okToNgCount ?? summary.OkToNgCount ?? 0}`,
            `缺图 ${summary.missingImageCount ?? summary.MissingImageCount ?? 0}`,
            `渲染图 ${summary.renderedFallbackCount ?? summary.RenderedFallbackCount ?? 0}`,
        ];
        const reasonStats = summary.failureReasonStats || summary.FailureReasonStats || {};
        const reasonText = Object.keys(reasonStats).length
            ? Object.keys(reasonStats).map((key) => `${key} ${reasonStats[key]}`).join("；")
            : "无失败原因";
        const items = summary.items || summary.Items || [];
        const previewRows = Array.isArray(items)
            ? items.slice(0, 8).map((item) => {
                const oldResult = item.oldResult || item.OldResult || "";
                const newResult = item.newResult || item.NewResult || "";
                const warning = item.imageWarning || item.ImageWarning || item.failureReason || item.FailureReason || "";
                return `<span>${escapeHtml(item.inspectionId || item.InspectionId || item.recordId || item.RecordId)} · ${escapeHtml(oldResult || "-")}→${escapeHtml(newResult || "-")} ${warning ? `· ${escapeHtml(warning)}` : ""}</span>`;
            }).join("")
            : "";
        list.innerHTML = `<div>
            <strong>完成 ${summary.completedCount ?? summary.CompletedCount ?? 0}/${summary.totalRecords ?? summary.TotalRecords ?? 0} · 上限 ${summary.effectiveLimit ?? summary.EffectiveLimit ?? 50}</strong>
            <span>${stats.join(" | ")}</span>
            <span>失败统计: ${escapeHtml(reasonText)}</span>
            ${previewRows}
        </div>`;
    }

    function renderVisionDebugComparison(snapshot) {
        const comparison = snapshot.comparison || snapshot.Comparison;
        if (!comparison) return;
        const oldResult = comparison.oldResult || comparison.OldResult || "";
        const newResult = comparison.newResult || comparison.NewResult || "";
        setText("vision-debug-history-status", `旧判定 ${oldResult || "-"} / 新判定 ${newResult || "-"}`);
    }

    function getVisionDebugOverlayLayout(snapshot) {
        const image = el("camera-view");
        const container = el("camera-container");
        const canvas = el("vision-debug-overlay");
        if (!image || !container || !canvas) return null;
        const previewFrame = window.CF_STATE.previewFrame || {};
        const sourceWidth = Number(snapshot?.imageWidth ?? snapshot?.ImageWidth ?? previewFrame.sourceWidth ?? image.naturalWidth ?? image.width ?? 1280);
        const sourceHeight = Number(snapshot?.imageHeight ?? snapshot?.ImageHeight ?? previewFrame.sourceHeight ?? image.naturalHeight ?? image.height ?? 720);
        const previewWidth = Number(previewFrame.previewWidth || image.naturalWidth || image.width || sourceWidth || 1280);
        const previewHeight = Number(previewFrame.previewHeight || image.naturalHeight || image.height || sourceHeight || 720);
        const containerRect = container.getBoundingClientRect();
        const mapping = window.CF_COORDINATE_MAPPING?.calculateImageContentMapping({
            containerWidth: containerRect.width,
            containerHeight: containerRect.height,
            previewWidth,
            previewHeight,
            sourceWidth,
            sourceHeight,
        });
        if (!mapping?.valid) return null;
        const imageRect = mapping.imageRect;
        canvas.style.width = `${imageRect.width}px`;
        canvas.style.height = `${imageRect.height}px`;
        canvas.style.left = `${imageRect.x}px`;
        canvas.style.top = `${imageRect.y}px`;
        canvas.style.right = "auto";
        canvas.style.bottom = "auto";
        canvas.width = Math.max(1, Math.round(imageRect.width));
        canvas.height = Math.max(1, Math.round(imageRect.height));
        return {
            canvas,
            sourceWidth,
            sourceHeight,
            mapping: {
                ...mapping,
                scaleX: canvas.width / Math.max(1, sourceWidth),
                scaleY: canvas.height / Math.max(1, sourceHeight),
            },
        };
    }

    function clearVisionDebugOverlay() {
        const canvas = el("vision-debug-overlay");
        if (!canvas) return;
        const ctx = canvas.getContext("2d");
        ctx.clearRect(0, 0, canvas.width, canvas.height);
    }

    function redrawVisionDebugOverlay() {
        const snapshot = store.state.visionDebug?.snapshot || store.state.visionDebug?.Snapshot;
        const layout = getVisionDebugOverlayLayout(snapshot);
        if (!layout) return;
        const { canvas, mapping } = layout;
        const ctx = canvas.getContext("2d");
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        if (!snapshot) return;
        drawVisionDebugRoi(ctx, snapshot, mapping);
        const boxes = snapshot.allDetections || snapshot.AllDetections || [];
        boxes.forEach((box) => {
            const filtered = Boolean(box.filteredOutByRoi ?? box.FilteredOutByRoi);
            const mappedRect = window.CF_COORDINATE_MAPPING.mapImageRect(mapping, {
                x: box.x ?? box.X ?? 0,
                y: box.y ?? box.Y ?? 0,
                width: box.width ?? box.Width ?? 0,
                height: box.height ?? box.Height ?? 0,
            });
            const mappedCenter = window.CF_COORDINATE_MAPPING.mapImagePoint(mapping, {
                x: box.centerX ?? box.CenterX ?? 0,
                y: box.centerY ?? box.CenterY ?? 0,
            });
            const { x, y, width, height } = mappedRect;
            const index = box.index ?? box.Index ?? "";
            const label = box.label || box.Label || `Class_${box.classId ?? box.ClassId}`;
            const confidence = Number(box.confidence ?? box.Confidence ?? 0);
            ctx.save();
            ctx.globalAlpha = filtered ? 0.45 : 1;
            ctx.strokeStyle = filtered ? "rgba(100, 116, 139, 0.9)" : "rgba(22, 163, 74, 0.96)";
            ctx.fillStyle = filtered ? "rgba(100, 116, 139, 0.12)" : "rgba(22, 163, 74, 0.08)";
            ctx.lineWidth = filtered ? 1.5 : 2.5;
            ctx.setLineDash(filtered ? [6, 4] : []);
            ctx.strokeRect(x, y, width, height);
            ctx.fillRect(x, y, width, height);
            ctx.beginPath();
            ctx.arc(mappedCenter.x, mappedCenter.y, filtered ? 3 : 4, 0, Math.PI * 2);
            ctx.fillStyle = filtered ? "rgba(100, 116, 139, 0.95)" : "rgba(220, 38, 38, 0.95)";
            ctx.fill();
            ctx.strokeStyle = "#ffffff";
            ctx.lineWidth = 1.2;
            ctx.stroke();
            const caption = `#${index} ${label} ${confidence.toFixed(2)} (${Math.round(Number(box.centerX ?? box.CenterX ?? 0))},${Math.round(Number(box.centerY ?? box.CenterY ?? 0))})`;
            ctx.font = "12px Microsoft YaHei, sans-serif";
            const textWidth = ctx.measureText(caption).width + 10;
            const textY = Math.max(0, y - 20);
            ctx.fillStyle = filtered ? "rgba(51, 65, 85, 0.85)" : "rgba(21, 128, 61, 0.92)";
            ctx.fillRect(x, textY, textWidth, 18);
            ctx.fillStyle = "#ffffff";
            ctx.fillText(caption, x + 5, textY + 13);
            ctx.restore();
        });
    }

    function drawVisionDebugRoi(ctx, snapshot, mapping) {
        const roi = snapshot.roi || snapshot.Roi;
        if (!Array.isArray(roi) || roi.length !== 4) return;
        const sourceWidth = Number(snapshot.imageWidth ?? snapshot.ImageWidth ?? mapping.sourceWidth ?? 0);
        const sourceHeight = Number(snapshot.imageHeight ?? snapshot.ImageHeight ?? mapping.sourceHeight ?? 0);
        const x = Number(roi[0] || 0) * sourceWidth;
        const y = Number(roi[1] || 0) * sourceHeight;
        const width = Number(roi[2] || 0) * sourceWidth;
        const height = Number(roi[3] || 0) * sourceHeight;
        if (width <= 0 || height <= 0) return;
        const rect = window.CF_COORDINATE_MAPPING.mapImageRect(mapping, { x, y, width, height });
        ctx.save();
        ctx.strokeStyle = "rgba(164, 22, 26, 0.96)";
        ctx.fillStyle = "rgba(164, 22, 26, 0.08)";
        ctx.lineWidth = 2;
        ctx.setLineDash([8, 5]);
        ctx.strokeRect(rect.x, rect.y, rect.width, rect.height);
        ctx.fillRect(rect.x, rect.y, rect.width, rect.height);
        ctx.restore();
    }

    function renderReplayStatus(state) {
        const replay = state?.replay || {};
        const run = replay.currentRunId ? replay.runs?.[replay.currentRunId] : null;
        const dataset = replay.dataset || {};
        const approval = replay.approval || {};
        const approvalAvailable = approval.approvalAvailable ?? approval.available;
        const metrics = run?.metrics || dataset?.metrics || {};

        setText("replay-dataset-id", dataset.datasetId || run?.datasetId || "");
        setText("replay-dataset-hash", dataset.datasetHash || run?.datasetHash || "");
        setText("replay-dataset-count", Array.isArray(replay.datasets) ? replay.datasets.length : "");
        setText("replay-run-status", formatReplayRunStatus(run?.status || dataset.status || ""));
        setText("replay-run-progress", run ? `${run.completedSamples ?? 0}/${run.totalSamples ?? 0}` : "");
        setText("replay-changed-count", metrics.changedDecisionCount ?? "");
        setText("replay-new-missed-count", metrics.candidateNewMissedDetectionCount ?? "");
        setText("replay-fixed-missed-count", metrics.candidateFixedMissedDetectionCount ?? "");
        setText("replay-new-false-reject-count", metrics.candidateNewFalseRejectCount ?? "");
        setText("replay-fixed-false-reject-count", metrics.candidateFixedFalseRejectCount ?? "");
        setText("replay-run-count", replay.runs ? Object.keys(replay.runs).length : "");
        setText("replay-integrity-status", formatReplayRunStatus(replay.integrity?.status || ""));
        setText("replay-approval-status",
            approval.succeeded === true
                ? formatApprovalStatus("Approved")
                : approval.succeeded === false
                    ? (approval.errorCode || formatApprovalStatus("Rejected"))
                    : approvalAvailable === true
                        ? formatApprovalStatus("Available")
                        : approvalAvailable === false
                            ? formatApprovalStatus("Rejected")
                            : "");
        setText("replay-rejection-reasons", (approval.rejectionReasons || []).join("; ") || approval.message || approval.evidenceHash || "");
    }

    function renderManualReviewStatus(state) {
        const review = state?.manualReview || {};
        const response = review.lastResponse || {};
        setText("manual-review-count", Array.isArray(review.records) ? review.records.length : "");
        setText("manual-review-response", response.message || response.errorCode || "");
        setText("manual-review-revision", response.revision ?? "");
        setText("manual-review-ground-truth", response.groundTruth || "");
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
        if (hasRenderReason(reasons, "replay")) {
            renderReplayStatus(state);
        }
        if (hasRenderReason(reasons, "manualReview")) {
            renderManualReviewStatus(state);
        }
        if (hasRenderReason(reasons, "fieldDebug")) {
            renderFieldDiagnostics(state);
        }
        if (hasRenderReason(reasons, "visionDebug")) {
            renderVisionDebugResult(state);
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
        store.state.connections = store.state.connections || {};
        if (normalizedType === "serialtrigger" || normalizedType === "serialphotoelectric" || normalizedType === "serial") {
            store.state.connections.serialTrigger = Boolean(isConnected);
            updateTriggerSourceStatus();
            renderFieldDiagnostics(store.state);
            return;
        }

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
                ${systemBusy ? "正在启动..." : "启动系统"}`;
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

    function updatePreviewImage({ url, base64, frameId, sourceWidth, sourceHeight, previewWidth, previewHeight }) {
        const image = el("camera-view");
        if (!image) return;

        const normalizedFrameId = Number(frameId || Date.now());
        if (normalizedFrameId < lastPreviewFrameId) return;
        lastPreviewFrameId = normalizedFrameId;
        window.CF_STATE.previewFrameId = normalizedFrameId;
        window.CF_STATE.previewFrame = {
            sourceWidth: Number(sourceWidth || 0),
            sourceHeight: Number(sourceHeight || 0),
            previewWidth: Number(previewWidth || 0),
            previewHeight: Number(previewHeight || 0),
        };

        const src = url || (base64 ? (String(base64).startsWith("data:image") ? base64 : `data:image/jpeg;base64,${base64}`) : "");
        if (!src || image.src === src) return;

        image.onload = () => {
            image.onload = null;
            image.onerror = null;
            window.requestAnimationFrame(() => {
                if (typeof window.redrawROI === "function") window.redrawROI();
                redrawVisionDebugOverlay();
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
            console.error("状态更新失败:", error);
            addLog("状态解析失败", "error");
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

    function handleCommandError(data, envelope) {
        const cmd = data?.cmd || data?.Cmd || "";
        const errorCode = data?.errorCode || data?.ErrorCode || "CommandError";
        const message = data?.message || data?.Message || `前端命令处理失败: ${cmd || errorCode}`;
        const requestId = envelope?.requestId ? ` (${envelope.requestId})` : "";

        addLog(`${message}${requestId}`, "error");
        if (window.__CF_DEV_MODE) console.warn("Command error:", data, envelope);
    }

    function handleFieldDebugResult(data) {
        store.applyFieldDebugResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const message = data?.message || data?.Message || "";
        if (message) addLog(message, succeeded === false ? "error" : "info");
    }

    function handleDiagnosticPackageExportResult(data) {
        store.applyDiagnosticPackageExportResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const message = data?.message || data?.Message || "";
        if (message) addLog(message, succeeded === false ? "error" : "info");
    }

    function handleDiagnosticPackageHistoryResult(data) {
        store.applyDiagnosticPackageHistoryResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const message = data?.message || data?.Message || "";
        if (message && succeeded === false) addLog(message, "error");
    }

    function handleDiagnosticPackageVerificationResult(data) {
        store.applyDiagnosticPackageVerificationResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const message = data?.message || data?.Message || "";
        if (message) {
            addLog(message, succeeded === false ? "warning" : "success");
            showToast(message, succeeded === false ? "warning" : "success", 1600);
        }
    }

    function handleMaintenanceAdviceActionResult(data) {
        store.applyMaintenanceAdviceActionResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const cleared = data?.cleared ?? data?.Cleared;
        const message = data?.message || data?.Message || "";
        if (message) {
            addLog(message, succeeded === false ? "error" : cleared ? "success" : "warning");
            showToast(message, succeeded === false ? "error" : cleared ? "success" : "warning", 1600);
        }
    }

    function handleShiftTaskActionResult(data) {
        store.applyShiftTaskActionResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const cleared = data?.cleared ?? data?.Cleared;
        const message = data?.message || data?.Message || "";
        if (message) {
            addLog(message, succeeded === false ? "error" : cleared ? "success" : "warning");
            showToast(message, succeeded === false ? "error" : cleared ? "success" : "warning", 1600);
        }
    }

    function handleFieldHandoffReportResult(data) {
        store.applyFieldHandoffReportResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const message = data?.message || data?.Message || "";
        if (message) {
            addLog(message, succeeded === false ? "error" : "success");
            showToast(succeeded === false ? "交接报告导出失败" : "交接报告已导出", succeeded === false ? "error" : "success", 1600);
        }
    }

    function handleFieldHandoffReportHistoryResult(data) {
        store.applyFieldHandoffReportHistoryResult(data);
        const succeeded = data?.succeeded ?? data?.Succeeded;
        const message = data?.message || data?.Message || "";
        if (message && succeeded === false) addLog(message, "error");
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
            case "export_diagnostic_package":
                addLog("正在导出诊断包...", "info");
                showToast("正在导出诊断包...", "info", 1200);
                break;
            case "query_diagnostic_packages":
                addLog("诊断包历史刷新请求已发送", "info");
                break;
            case "verify_diagnostic_package":
                addLog("诊断包复核请求已发送", "info");
                break;
            case "maintenance_advice_action":
                addLog("维护建议处理/复检请求已发送", "info");
                break;
            case "shift_task_action":
                addLog("班次待办处理/复检请求已发送", "info");
                break;
            case "export_field_handoff_report":
                addLog("现场交接报告导出请求已发送", "info");
                break;
            case "query_field_handoff_reports":
                addLog("交接报告历史刷新请求已发送", "info");
                break;
            case "field_debug_step_capture":
            case "field_debug_step_infer":
            case "field_debug_plc_write_test":
            case "field_debug_barcode_read_test":
            case "field_debug_simulate_trigger":
                addLog("现场调试命令已发送", "info");
                break;
            case "vision_debug_run_current":
                addLog("算法调试当前帧重跑已发送", "info");
                break;
            case "vision_debug_run_history":
                addLog("历史样本算法调试已发送", "info");
                break;
            case "vision_debug_run_batch":
                addLog("批量历史样本回放已发送", "info");
                break;
            case "vision_debug_save_params":
                addLog("算法调试参数保存请求已发送", "info");
                break;
            case "vision_debug_apply_template":
                addLog("场景模板生成请求已发送", "info");
                break;
            default:
                break;
        }
    }

    function handleDetectionFrame(data) {
        if (!data) return;
        clearVisionDebugOverlay();
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
            terminalHandshakeAttempted: data.inspection?.terminalHandshakeAttempted ?? data.terminalHandshakeAttempted,
            terminalHandshakeSucceeded: data.inspection?.terminalHandshakeSucceeded ?? data.terminalHandshakeSucceeded,
            terminalHandshakeErrorCode: data.inspection?.terminalHandshakeErrorCode ?? data.terminalHandshakeErrorCode,
            terminalHandshakeSignalName: data.inspection?.terminalHandshakeSignalName ?? data.terminalHandshakeSignalName,
            terminalHandshakeAddress: data.inspection?.terminalHandshakeAddress ?? data.terminalHandshakeAddress,
            terminalHandshakeMessage: data.inspection?.terminalHandshakeMessage ?? data.terminalHandshakeMessage,
            cycleSucceeded: data.inspection?.cycleSucceeded ?? data.cycleSucceeded,
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
        acknowledgeMaintenanceAdvice,
        acknowledgeShiftTask,
        closeFieldDiagnosticsPanel,
        copyDiagnosticPackageSummary,
        copyFieldHandoffReportSummary,
        copyFaultSummary,
        escapeHtml,
        exportFieldHandoffReport,
        flashPlcTrigger,
        handleInspectionUpdate,
        openFieldDiagnosticsPanel,
        openVisionDebugPanel,
        renderHealthSnapshot: handleHealthSnapshot,
        renderFieldDiagnostics: () => renderFieldDiagnostics(window.CF_STATE),
        renderVisionDebugResult: () => renderVisionDebugResult(window.CF_STATE),
        renderInspectionContext: () => renderInspectionContext(window.CF_STATE),
        renderManualReviewStatus: () => renderManualReviewStatus(window.CF_STATE),
        renderReplayStatus: () => renderReplayStatus(window.CF_STATE),
        renderRecentInspections: () => renderRecentInspections(window.CF_STATE),
        requestDiagnosticPackageHistory,
        requestFieldHandoffReportHistory,
        requestExitApp,
        requestOpenCamera,
        requestVisionDebugRecentRecords,
        requestStartSystem,
        recheckMaintenanceAdvice,
        recheckShiftTask,
        runVisionDebugCurrent,
        runVisionDebugHistory,
        runVisionDebugBatch,
        verifyDiagnosticPackage,
        saveVisionDebugParams,
        applySelectedVisionDebugTemplate,
        applyVisionDebugTemplate,
        showVisionDebugLocalImageNotice,
        startSystem,
        handleCommandDispatched,
        setStartSystemButtonState,
        setDotState,
        setText,
        showToast,
        updateCameraName,
        updateConnection,
        updateTriggerSourceStatus,
        updateImage,
        updateImageUrl,
        updateInferenceMetrics,
        updateResult,
        updateStatus,
        closeVisionDebugPanel,
        redrawVisionDebugOverlay,
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
    bridge.registerMessageHandler("fieldDebugResult", handleFieldDebugResult);
    bridge.registerMessageHandler("visionDebugResult", (data) => store.applyVisionDebugResult(data));
    bridge.registerMessageHandler("diagnosticPackageExportResult", handleDiagnosticPackageExportResult);
    bridge.registerMessageHandler("diagnosticPackageHistoryResult", handleDiagnosticPackageHistoryResult);
    bridge.registerMessageHandler("diagnosticPackageVerificationResult", handleDiagnosticPackageVerificationResult);
    bridge.registerMessageHandler("maintenanceAdviceActionResult", handleMaintenanceAdviceActionResult);
    bridge.registerMessageHandler("shiftTaskActionResult", handleShiftTaskActionResult);
    bridge.registerMessageHandler("fieldHandoffReportResult", handleFieldHandoffReportResult);
    bridge.registerMessageHandler("fieldHandoffReportHistoryResult", handleFieldHandoffReportHistoryResult);
    bridge.registerMessageHandler("manualReviewRecords", (data) => store.applyManualReviewUpdate(data));
    bridge.registerMessageHandler("manualReviewResponse", (data) => store.applyManualReviewUpdate(data));
    bridge.registerMessageHandler("datasetCreateStatus", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("replayRunStatus", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("replayRunProgress", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("replayRunCompleted", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("replayRunFailed", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("replayRunCanceled", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("modelApprovalAvailability", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("replayApprovalResponse", (data) => store.applyReplayUpdate(data));
    bridge.registerMessageHandler("detectionFrame", handleDetectionFrame);
    bridge.registerMessageHandler("uiCommand", handleUiCommand);
    bridge.registerMessageHandler("commandError", handleCommandError);

    document.addEventListener("input", (event) => {
        const target = event.target;
        if (target?.id === "vision-debug-confidence" || target?.id === "vision-debug-iou") {
            updateVisionDebugSliderLabels();
        }
        if (target?.id === "vision-debug-preprocess-mode") {
            updateVisionDebugPreprocessHint();
        }
        if (target?.id === "vision-debug-rule-set") {
            updateVisionDebugRuleSummary();
            validateVisionDebugRuleJson(false);
        }
    });

    document.addEventListener("change", (event) => {
        const target = event.target;
        if (target?.id === "vision-debug-preprocess-mode") {
            updateVisionDebugPreprocessHint();
        }
    });
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
            name: "N5 遥控器漏装",
            StationName: "N5 遥控器漏装",
            DetectionType: "遥控器漏装",
            PlcIp: "10.182.82.19",
            PlcPort: 2700,
            PlcTriggerAddress: "D100",
            PlcResultAddress: "D102",
            CameraSerialNumber: "5G087BAGAK00018",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TriggerSource: "PLC",
            TargetLabel: "remote",
            TargetLabels: ["remote"],
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            GainRaw: 1.5,
            RecommendedExposureTime: 3500,
            RecommendedGainRaw: 1.5,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
        },
        N5_screw: {
            name: "N5 螺钉检测",
            StationName: "N5 螺钉检测",
            DetectionType: "螺钉检测",
            PlcIp: "10.182.82.19",
            PlcPort: 3000,
            PlcTriggerAddress: "D90",
            PlcResultAddress: "D92",
            CameraSerialNumber: "EF59601AAK00030",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TriggerSource: "PLC",
            TargetLabel: "screw",
            TargetLabels: ["screw"],
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            GainRaw: 1.5,
            RecommendedExposureTime: 3500,
            RecommendedGainRaw: 1.5,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
        },
        N6_remote: {
            name: "N6 遥控器漏装",
            StationName: "N6 遥控器漏装",
            DetectionType: "遥控器漏装",
            PlcIp: "192.168.100.122",
            PlcPort: 5777,
            PlcTriggerAddress: "D6607",
            PlcResultAddress: "D6608",
            CameraSerialNumber: "AM01040AAK00040",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TriggerSource: "PLC",
            TargetLabel: "remote",
            TargetLabels: ["remote"],
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            GainRaw: 1.5,
            RecommendedExposureTime: 3500,
            RecommendedGainRaw: 1.5,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
        },
        N6_screw: {
            name: "N6 螺钉检测",
            StationName: "N6 螺钉检测",
            DetectionType: "螺钉检测",
            PlcIp: "10.182.82.3",
            PlcPort: 4300,
            PlcTriggerAddress: "D100",
            PlcResultAddress: "D102",
            CameraSerialNumber: "",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_Binary",
            TriggerSource: "PLC",
            TargetLabel: "screw",
            TargetLabels: ["screw"],
            TargetCount: 1,
            ExposureTime: 3500,
            Gain: 1.5,
            GainRaw: 1.5,
            RecommendedExposureTime: 3500,
            RecommendedGainRaw: 1.5,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
        },
        W5_screw: {
            name: "W5 螺钉检测",
            StationName: "W5 螺钉检测",
            DetectionType: "螺钉检测",
            PlcIp: "192.168.22.44",
            PlcPort: 4999,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "EF59632AAK00291",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TriggerSource: "PLC",
            TargetLabel: "screw",
            TargetLabels: ["screw"],
            TargetCount: 4,
            ExposureTime: 50000,
            Gain: 1.1,
            GainRaw: 1.1,
            RecommendedExposureTime: 50000,
            RecommendedGainRaw: 1.1,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
        },
        W6_screw: {
            name: "W6 螺钉检测",
            StationName: "W6 螺钉检测",
            DetectionType: "螺钉检测",
            PlcIp: "192.168.250.1",
            PlcPort: 5999,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "EF59632AAK00291",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TriggerSource: "PLC",
            TargetLabel: "screw",
            TargetLabels: ["screw"],
            TargetCount: 4,
            ExposureTime: 3500,
            Gain: 1.5,
            GainRaw: 1.5,
            RecommendedExposureTime: 3500,
            RecommendedGainRaw: 1.5,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
        },
        electric_heating_screw: {
            name: "电加热螺钉检测",
            StationName: "电加热螺钉检测",
            DetectionType: "螺钉检测",
            PlcIp: "",
            PlcPort: 0,
            PlcTriggerAddress: "D555",
            PlcResultAddress: "D556",
            CameraSerialNumber: "",
            CameraManufacturer: "Huaray",
            CameraBrand: "Huaray",
            PlcProtocol: "Mitsubishi_MC_ASCII",
            TriggerSource: "PLC",
            TargetLabel: "screw",
            TargetLabels: ["screw"],
            TargetCount: 4,
            ExposureTime: 3500,
            Gain: 1.5,
            GainRaw: 1.5,
            RecommendedExposureTime: 3500,
            RecommendedGainRaw: 1.5,
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
            EnableMultiModelFallback: false,
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
            DefaultStoragePath: "C:\\GreeVisionData",
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
        store.state.settings = { ...(store.state.settings || {}), TriggerSource: triggerSource };
        window.updateTriggerSourceStatus?.(triggerSource);
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
        emptyOption.text = "-- 选择工位模板（可选）--";
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

    function setProjectPresetLoadHint(message) {
        const hint = byId("project-preset-load-hint");
        if (hint) {
            hint.textContent = message || "选择后只载入模板，不会自动保存；请确认相机序列号、PLC IP 和模型后再保存。";
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
            setProjectPresetLoadHint("");
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
        if (preset.EnableMultiModelFallback !== undefined) {
            applyMultiModelUiState(!!preset.EnableMultiModelFallback);
        }
        syncProjectPresetName();
        const templateName = getPresetDisplayName(presetId, preset);
        const message = `已载入 ${templateName}模板，请确认相机序列号、PLC IP 和模型后保存。`;
        setProjectPresetLoadHint(message);
        window.showToast?.(message, "success", 1800);
        window.addLog?.(message, "success");
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
    const AuditStatusLabels = Object.freeze({
        Requested: "已请求",
        Denied: "已拒绝",
        Succeeded: "已成功",
        Failed: "已失败",
    });
    const AuditOperationLabels = Object.freeze({
        ManualRelease: "强制放行",
        ManualReview: "人工复核",
        ReplayApproval: "回放审批",
        ReplayIntegrityScan: "回放完整性扫描",
        archive_replay_dataset: "归档回放数据集",
        create_replay_dataset: "创建回放数据集",
        preview_replay_dataset: "预览回放数据集",
        query_replay_datasets: "查询回放数据集",
        run_replay_comparison: "对比新旧模型",
        cancel_replay_run: "取消回放运行",
        query_replay_runs: "查询回放运行",
        query_replay_report: "查询回放报告",
        query_model_approval_evidence: "查询模型验证记录",
        run_replay_integrity_scan: "运行验证记录扫描",
        approve_replay_candidate: "审批候选模型",
        query_manual_review_records: "查询人工复核记录",
        save_manual_review: "保存人工复核",
    });
    const ProductionRoleLabels = Object.freeze({
        Operator: "操作员",
        ShiftLead: "班组长",
        Engineer: "工程师",
    });

    function byId(id) {
        return document.getElementById(id);
    }

    function formatAuditStatus(status) {
        const value = String(status || "").trim();
        return AuditStatusLabels[value] || value;
    }

    function formatProductionRole(role) {
        const value = String(role || "").trim();
        return ProductionRoleLabels[value] || value;
    }

    function formatAuditOperation(operation) {
        const value = String(operation || "").trim();
        return AuditOperationLabels[value] || value;
    }

    function formatAuditText(value) {
        let text = String(value || "").trim();
        if (!text) return "";

        Object.entries(AuditOperationLabels).forEach(([raw, label]) => {
            text = text.split(raw).join(label);
        });
        Object.entries(AuditStatusLabels).forEach(([raw, label]) => {
            text = text.split(raw).join(label);
        });
        Object.entries(ProductionRoleLabels).forEach(([raw, label]) => {
            text = text.split(`RequiredRole=${raw}`).join(`需要${label}权限`);
            text = text.split(raw).join(label);
        });
        return text;
    }

    function shortAuditHash(value) {
        const text = String(value || "").trim();
        if (!text) return "";
        return text.length <= 12 ? text : `${text.slice(0, 12)}...`;
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

    const TRACE_DATE_ITEM_CLASS = "p-2.5 hover:bg-celadon-50 hover:text-celadon-700 cursor-pointer rounded-xl text-[11px] text-ink-500 font-bold transition-[background-color,border-color,color,box-shadow] border border-transparent hover:border-celadon-100 mb-1";
    const TRACE_DATE_ITEM_ACTIVE_CLASS = "p-2.5 bg-celadon-50 text-celadon-700 cursor-pointer rounded-xl text-[11px] font-black transition-[background-color,border-color,color,box-shadow] shadow-sm border border-celadon-200 mb-1";
    const TRACE_HOUR_ITEM_CLASS = "px-4 py-2 bg-white/60 border border-slate-100 rounded-xl text-[11px] cursor-pointer hover:bg-white hover:text-celadon-600 hover:border-celadon-200 transition-[background-color,border-color,color,box-shadow] font-bold text-ink-500 shadow-sm flex items-center justify-between group";
    const TRACE_HOUR_ITEM_ACTIVE_CLASS = "px-4 py-2 bg-celadon-600 border-celadon-600 text-white rounded-xl text-[11px] cursor-pointer transition-[background-color,border-color,color,box-shadow] font-bold shadow-md flex items-center justify-between";

    function traceDateItemClass(isActive = false) {
        return isActive ? TRACE_DATE_ITEM_ACTIVE_CLASS : TRACE_DATE_ITEM_CLASS;
    }

    function traceHourItemClass(isActive = false) {
        return isActive ? TRACE_HOUR_ITEM_ACTIVE_CLASS : TRACE_HOUR_ITEM_CLASS;
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
        const hourSelect = byId("trace-hour-select");
        const hour = hourSelect ? hourSelect.value : (window.currentNGHour || "");
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
        if (title) title.textContent = "检测日志";
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

    function setTraceDateSelection(date) {
        const selectedDate = date || "";
        window.currentNGDate = selectedDate;
        const dateInput = byId("gallery-date-picker");
        if (dateInput && dateInput.value !== selectedDate) {
            dateInput.value = selectedDate;
        }

        const list = byId("ng-date-list");
        if (!list) return;
        Array.from(list.children).forEach((child) => {
            if (!child.dataset?.traceDate) return;
            child.className = traceDateItemClass(child.dataset.traceDate === selectedDate);
        });
    }

    function setTraceHourSelection(hour) {
        const selectedHour = hour ?? "";
        window.currentNGHour = selectedHour;
        const hourSelect = byId("trace-hour-select");
        if (hourSelect && hourSelect.value !== selectedHour) {
            hourSelect.value = selectedHour;
        }

        const list = byId("ng-hour-list");
        if (!list) return;
        Array.from(list.children).forEach((child) => {
            if (!child.dataset?.traceHour) return;
            child.className = traceHourItemClass(child.dataset.traceHour === selectedHour);
        });
    }

    function closeLogHistoryModal() {
        byId("log-history-modal")?.classList.add("hidden");
    }

    function openGalleryModal() {
        byId("gallery-modal")?.classList.remove("hidden");
        syncTraceControls();
        resetTracePagerState();
        const badge = byId("gallery-count");
        if (badge) badge.textContent = "0 条";
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
        verifyAuditChain();
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
            tbody.innerHTML = '<tr><td colspan="9" class="px-4 py-10 text-center text-slate-400 italic">正在加载审计记录...</td></tr>';
        }
        bridge.sendCommand("query_audit_records", buildAuditQuery());
    }

    function exportAuditRecords() {
        setAuditError("");
        bridge.sendCommand("export_audit_records", buildAuditQuery());
    }

    function verifyAuditChain() {
        setAuditError("");
        const badge = byId("audit-chain-badge");
        if (badge) {
            badge.textContent = "校验中";
            badge.className = "px-2 py-0.5 rounded-full bg-slate-100 text-slate-500 font-bold";
        }
        bridge.sendCommand("verify_audit_chain", {});
    }

    function updateAuditChainVerification(data) {
        const error = data?.error || data?.Error || "";
        const status = String(data?.status || data?.Status || (error ? "Unavailable" : "Unknown"));
        const totalRecords = data?.totalRecords ?? data?.TotalRecords ?? 0;
        const verifiedRecords = data?.verifiedRecords ?? data?.VerifiedRecords ?? 0;
        const findingCount = data?.findingCount ?? data?.FindingCount ?? 0;
        const lastHash = data?.lastRecordSha256 || data?.LastRecordSha256 || "";
        const findings = Array.isArray(data?.findings) ? data.findings : (data?.Findings || []);
        const badge = byId("audit-chain-badge");
        const countNode = byId("audit-chain-count");
        const findingNode = byId("audit-chain-findings");
        const hashNode = byId("audit-chain-last-hash");
        const messageNode = byId("audit-chain-message");

        const statusClass = status === "Healthy"
            ? "bg-bamboo-50 text-bamboo-700 border border-bamboo-100"
            : status === "Warning"
                ? "bg-amber-50 text-amber-700 border border-amber-100"
                : "bg-rouge-50 text-rouge-700 border border-rouge-100";

        if (badge) {
            badge.textContent = status;
            badge.className = `px-2 py-0.5 rounded-full font-bold ${statusClass}`;
        }
        if (countNode) countNode.textContent = `Verified ${verifiedRecords}/${totalRecords}`;
        if (findingNode) findingNode.textContent = `Findings ${findingCount}`;
        if (hashNode) {
            hashNode.textContent = `Last ${shortAuditHash(lastHash) || "-"}`;
            hashNode.title = lastHash || "";
        }
        if (messageNode) {
            const firstFinding = findings[0] || {};
            const summary = firstFinding.errorCode || firstFinding.ErrorCode || error || "";
            const line = firstFinding.lineNumber || firstFinding.LineNumber || "";
            messageNode.textContent = summary ? `${summary}${line ? ` @${line}` : ""}` : "";
        }
        if (error) {
            setAuditError(error);
        }
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

        if (badge) badge.textContent = `${records.length} 条`;
        if (!records.length) {
            tbody.innerHTML = '<tr><td colspan="9" class="px-4 py-10 text-center text-slate-400 italic">未匹配到审计记录</td></tr>';
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
                    <td class="px-3 py-3">${escapeHtml(formatAuditOperation(record.operation) || "-")}</td>
                    <td class="px-3 py-3"><span class="inline-block px-2 py-0.5 rounded-full text-[10px] font-bold border ${statusClass}">${escapeHtml(formatAuditStatus(status) || "-")}</span></td>
                    <td class="px-3 py-3">${escapeHtml(record.operatorId || "-")}</td>
                    <td class="px-3 py-3">${escapeHtml(formatProductionRole(record.role) || "-")}</td>
                    <td class="px-3 py-3">${escapeHtml(record.inspectionId || "-")}</td>
                    <td class="px-3 py-3 max-w-[130px] truncate" title="${escapeHtml(record.recordSha256 || "")}">${escapeHtml(shortAuditHash(record.recordSha256) || "-")}</td>
                    <td class="px-3 py-3 max-w-md whitespace-normal break-words">${escapeHtml(formatAuditText(record.details || record.reason) || "-")}</td>
                    <td class="px-3 py-3 max-w-xs whitespace-normal break-words">${escapeHtml(formatAuditText(record.failureBlocker) || "-")}</td>
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
        if (node) node.textContent = path ? `已导出: ${path}` : "";
        window.showToast?.("审计 CSV 已导出", "success", 1600);
    }

    function updateNGDates(data) {
        if (data === undefined) {
            const selectedDate = byId("gallery-date-picker")?.value || "";
            if (selectedDate) {
                setTraceDateSelection(selectedDate);
                setTraceHourSelection("");
                resetTracePagerState();
                if (byId("ng-hour-list")) byId("ng-hour-list").innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 opacity-50 font-serif">读取中...</div>';
                if (byId("ng-image-grid")) byId("ng-image-grid").innerHTML = "";
                bridge.sendCommand("get_ng_hours", selectedDate);
            } else {
                bridge.sendCommand("get_ng_dates");
            }
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
            div.className = traceDateItemClass();
            div.dataset.traceDate = date;
            div.tabIndex = 0;
            div.innerText = date;
            const selectDate = () => {
                setTraceDateSelection(date);
                setTraceHourSelection("");
                resetTracePagerState();
                if (byId("ng-hour-list")) byId("ng-hour-list").innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 opacity-50 font-serif">读取中...</div>';
                if (byId("ng-image-grid")) byId("ng-image-grid").innerHTML = "";
                bridge.sendCommand("get_ng_hours", date);
            };
            div.onclick = selectDate;
            div.onkeydown = (event) => {
                if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    selectDate();
                }
            };
            list.appendChild(div);
        });

        const selectedInputDate = byId("gallery-date-picker")?.value || "";
        const preferredDate = dates.includes(window.currentNGDate)
            ? window.currentNGDate
            : (dates.includes(selectedInputDate) ? selectedInputDate : dates[0]);
        Array.from(list.children)
            .find((child) => child.dataset?.traceDate === preferredDate)
            ?.click?.();
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
            div.className = traceHourItemClass();
            div.dataset.traceHour = hour;
            div.tabIndex = 0;
            div.innerHTML = `<span>${escapeHtml(hour)}:00 时段</span><span class="opacity-0 group-hover:opacity-100">›</span>`;
            const selectHour = () => {
                setTraceHourSelection(hour);
                resetTracePagerState();
                if (byId("ng-image-grid")) {
                    byId("ng-image-grid").innerHTML = '<div class="col-span-full h-full flex flex-col items-center justify-center py-20 text-ink-300 opacity-50"><div class="animate-spin rounded-full h-8 w-8 border-b-2 border-celadon-500 mb-4"></div><span class="text-xs font-serif italic">正在索引影像档案...</span></div>';
                }
                requestTracePage("initial");
            };
            div.onclick = selectHour;
            div.onkeydown = (event) => {
                if (event.key === "Enter" || event.key === " ") {
                    event.preventDefault();
                    selectHour();
                }
            };
            list.appendChild(div);
        });

        const preferredHour = hours.includes(window.currentNGHour) ? window.currentNGHour : hours[0];
        Array.from(list.children)
            .find((child) => child.dataset?.traceHour === preferredHour)
            ?.click?.();
    }

    function selectTraceHour(hour) {
        const selectedHour = hour ?? byId("trace-hour-select")?.value ?? "";
        setTraceHourSelection(selectedHour);
        resetTracePagerState();
        if (byId("ng-image-grid")) {
            byId("ng-image-grid").innerHTML = '<div class="col-span-full h-full flex flex-col items-center justify-center py-20 text-ink-300 opacity-50"><div class="animate-spin rounded-full h-8 w-8 border-b-2 border-celadon-500 mb-4"></div><span class="text-xs font-serif italic">正在索引影像档案...</span></div>';
        }
        requestTracePage("initial");
    }

    function searchTraceImages() {
        syncTraceControls();
        const date = byId("gallery-date-picker")?.value || window.currentNGDate;
        const hourSelect = byId("trace-hour-select");
        const hour = hourSelect ? hourSelect.value : (window.currentNGHour || "");
        if (date) setTraceDateSelection(date);
        setTraceHourSelection(hour);
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
            detectionRecordId: toNullableNumber(pickTraceValue(record, "detectionRecordId", "DetectionRecordId", "id", "Id")),
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

    function getReplayLimit() {
        const raw = Number(byId("replay-query-limit")?.value || 100);
        if (!Number.isFinite(raw)) return 100;
        return Math.max(1, Math.min(10000, Math.trunc(raw)));
    }

    function getReplayPanelPayload() {
        return {
            limit: getReplayLimit(),
            datasetId: String(byId("replay-dataset-input")?.value || "").trim(),
            runId: String(byId("replay-run-input")?.value || "").trim(),
            baselineModel: String(byId("replay-baseline-model")?.value || "").trim(),
            candidateModel: String(byId("replay-candidate-model")?.value || "").trim(),
            recipeVersion: activeTraceRecord?.recipeVersion || "",
        };
    }

    function setReplayPanelStatus(id, text) {
        const node = byId(id);
        if (node) node.textContent = text || "";
    }

    function queryManualReviewRecords() {
        const requestId = bridge.sendCommand("query_manual_review_records", {
            limit: getReplayLimit(),
            recipeVersion: activeTraceRecord?.recipeVersion || "",
        });
        setReplayPanelStatus("manual-review-response", `查询中 ${requestId}`);
    }

    function saveManualReview() {
        const inspectionId = activeTraceRecord?.inspectionId || "";
        if (!inspectionId) {
            window.showToast?.("请先选择一条追溯记录再保存真值", "warning", 1800);
            return;
        }

        const revisionRaw = String(byId("manual-review-expected-revision")?.value || "").trim();
        const requestId = bridge.sendCommand("save_manual_review", {
            detectionRecordId: activeTraceRecord?.detectionRecordId || 0,
            inspectionId,
            sampleId: inspectionId,
            groundTruth: byId("manual-review-ground-truth-input")?.value || "OK",
            disposition: byId("manual-review-disposition-input")?.value || "Confirmed",
            expectedRevision: revisionRaw ? Number(revisionRaw) : null,
            notes: String(byId("manual-review-notes")?.value || "").trim(),
        });
        setReplayPanelStatus("manual-review-response", `保存中 ${requestId}`);
    }

    function createReplayDataset() {
        const payload = getReplayPanelPayload();
        const requestId = bridge.sendCommand("create_replay_dataset", payload);
        setReplayPanelStatus("replay-run-status", `生成验证样本集 ${requestId}`);
    }

    function previewReplayDataset() {
        const payload = getReplayPanelPayload();
        const requestId = bridge.sendCommand("preview_replay_dataset", payload);
        setReplayPanelStatus("replay-run-status", `预览中 ${requestId}`);
    }

    function queryReplayDatasets() {
        const requestId = bridge.sendCommand("query_replay_datasets", getReplayPanelPayload());
        setReplayPanelStatus("replay-run-status", `查询数据集 ${requestId}`);
    }

    function archiveReplayDataset() {
        const requestId = bridge.sendCommand("archive_replay_dataset", getReplayPanelPayload());
        setReplayPanelStatus("replay-run-status", `归档中 ${requestId}`);
    }

    function runReplayComparison() {
        const payload = getReplayPanelPayload();
        const requestId = bridge.sendCommand("run_replay_comparison", payload);
        setReplayPanelStatus("replay-run-status", `对比新旧模型 ${requestId}`);
    }

    function cancelReplayRun() {
        const requestId = bridge.sendCommand("cancel_replay_run", getReplayPanelPayload());
        setReplayPanelStatus("replay-run-status", `正在取消 ${requestId}`);
    }

    function queryReplayRuns() {
        const requestId = bridge.sendCommand("query_replay_runs", getReplayPanelPayload());
        setReplayPanelStatus("replay-run-status", `查询运行记录 ${requestId}`);
    }

    function queryReplayReport() {
        const requestId = bridge.sendCommand("query_replay_report", getReplayPanelPayload());
        setReplayPanelStatus("replay-run-status", `生成报告 ${requestId}`);
    }

    function queryModelApprovalEvidence() {
        const requestId = bridge.sendCommand("query_model_approval_evidence", getReplayPanelPayload());
        setReplayPanelStatus("replay-approval-status", `查询验证记录 ${requestId}`);
    }

    function runReplayIntegrityScan() {
        const requestId = bridge.sendCommand("run_replay_integrity_scan", getReplayPanelPayload());
        setReplayPanelStatus("replay-approval-status", `扫描中 ${requestId}`);
    }

    function approveReplayCandidate() {
        const payload = getReplayPanelPayload();
        const requestId = bridge.sendCommand("approve_replay_candidate", payload);
        setReplayPanelStatus("replay-approval-status", `确认上线 ${requestId}`);
    }

    Object.assign(window, {
        closeGalleryModal,
        closeImageViewer,
        closeAuditModal,
        closeLogHistoryModal,
        closeStatisticsHistoryModal,
        exportAuditRecords,
        verifyAuditChain,
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
        queryManualReviewRecords,
        saveManualReview,
        createReplayDataset,
        previewReplayDataset,
        queryReplayDatasets,
        archiveReplayDataset,
        runReplayComparison,
        cancelReplayRun,
        queryReplayRuns,
        queryReplayReport,
        queryModelApprovalEvidence,
        runReplayIntegrityScan,
        approveReplayCandidate,
        searchTraceImages,
        selectTraceHour,
        updateDetectionLogTable,
        updateAuditExport,
        updateAuditChainVerification,
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
    bridge.registerMessageHandler("auditChainVerification", updateAuditChainVerification);
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
            const previewFrame = window.CF_STATE?.previewFrame || {};
            const containerRect = container.getBoundingClientRect();
            const mapping = window.CF_COORDINATE_MAPPING?.calculateImageContentMapping({
                containerWidth: containerRect.width,
                containerHeight: containerRect.height,
                previewWidth: Number(previewFrame.previewWidth || img.naturalWidth || img.width || 1280),
                previewHeight: Number(previewFrame.previewHeight || img.naturalHeight || img.height || 720),
                sourceWidth: Number(previewFrame.sourceWidth || img.naturalWidth || img.width || 1280),
                sourceHeight: Number(previewFrame.sourceHeight || img.naturalHeight || img.height || 720),
            });
            if (!mapping?.valid) return;

            const imageRect = mapping.imageRect;
            roiCanvas.style.width = `${imageRect.width}px`;
            roiCanvas.style.height = `${imageRect.height}px`;
            roiCanvas.style.left = `${imageRect.x}px`;
            roiCanvas.style.top = `${imageRect.y}px`;
            roiCanvas.style.right = "auto";
            roiCanvas.style.bottom = "auto";
            roiCanvas.width = Math.max(1, Math.round(imageRect.width));
            roiCanvas.height = Math.max(1, Math.round(imageRect.height));
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
            roiStartX = (event.clientX - rect.left) * (roiCanvas.width / Math.max(1, rect.width));
            roiStartY = (event.clientY - rect.top) * (roiCanvas.height / Math.max(1, rect.height));
        });

        roiCanvas.addEventListener("mousemove", (event) => {
            if (!isDrawingROI || !roiCanvas) return;
            const rect = roiCanvas.getBoundingClientRect();
            const currentX = (event.clientX - rect.left) * (roiCanvas.width / Math.max(1, rect.width));
            const currentY = (event.clientY - rect.top) * (roiCanvas.height / Math.max(1, rect.height));
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
            const currentX = (event.clientX - rect.left) * (roiCanvas.width / Math.max(1, rect.width));
            const currentY = (event.clientY - rect.top) * (roiCanvas.height / Math.max(1, rect.height));
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
