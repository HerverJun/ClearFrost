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
