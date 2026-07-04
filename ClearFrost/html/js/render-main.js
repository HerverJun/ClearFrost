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
        Frozen: "已固化",
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

    function renderFieldDiagnostics(state) {
        const health = state?.health || {};
        const modelProbe = health.modelProbe || health.ModelProbe || {};
        const currentModel = getFieldValue(health, "currentModelName", "CurrentModelName") ||
            modelProbe.currentModelName || modelProbe.CurrentModelName ||
            getFieldValue(health, "modelStatus", "ModelStatus");

        setText("diag-camera-status", getFieldValue(health, "cameraStatus", "CameraStatus"), "未连接");
        setText("diag-plc-status", getFieldValue(health, "plcStatus", "PlcStatus"), "未连接");
        setText("diag-current-model", currentModel, "未加载");
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

        const debug = state?.fieldDebug || {};
        const debugMessage = debug.message || debug.Message || "等待调试命令";
        const debugCode = debug.errorCode || debug.ErrorCode || "";
        setText("diag-debug-status", debugCode ? `${debugMessage} [${debugCode}]` : debugMessage, "等待调试命令");

        const pkg = state?.diagnosticPackage || {};
        setText("diag-package-path", pkg.path || pkg.Path || pkg.message || pkg.Message || "", "尚未导出");
    }

    function openFieldDiagnosticsPanel() {
        const modal = el("field-diagnostics-modal");
        if (!modal) return;
        modal.classList.remove("hidden");
        window.sendCommand("request_health_snapshot");
    }

    function closeFieldDiagnosticsPanel() {
        el("field-diagnostics-modal")?.classList.add("hidden");
    }

    function getVisionDebugSettings() {
        return store.state.settings || {};
    }

    function getVisionDebugRuleSetJson() {
        return String(el("vision-debug-rule-set")?.value || "").trim();
    }

    function setVisionDebugRuleSetJson(value) {
        const node = el("vision-debug-rule-set");
        if (node && node.value !== value) node.value = value || "";
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
        updateVisionDebugSliderLabels();
    }

    function updateVisionDebugSliderLabels() {
        const conf = Number(el("vision-debug-confidence")?.value ?? 0);
        const iou = Number(el("vision-debug-iou")?.value ?? 0);
        setText("vision-debug-confidence-value", conf.toFixed(2), "0.50");
        setText("vision-debug-iou-value", iou.toFixed(2), "0.30");
    }

    function openVisionDebugPanel() {
        populateVisionDebugControls();
        el("vision-debug-modal")?.classList.remove("hidden");
        requestVisionDebugRecentRecords();
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

    function runVisionDebugCurrent() {
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
        setText("vision-debug-history-status", "正在用当前调试参数重跑历史样本...");
        window.sendCommand("vision_debug_run_history", collectVisionDebugParams({ recordId }));
        window.handleCommandDispatched?.("vision_debug_run_history");
    }

    function saveVisionDebugParams() {
        window.sendCommand("vision_debug_save_params", collectVisionDebugParams());
        window.handleCommandDispatched?.("vision_debug_save_params");
    }

    function applyVisionDebugTemplate(templateId) {
        const labels = Array.from(el("vision-debug-target-label")?.options || [])
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
        if (debug.status === "failed") {
            const message = debug.message || debug.Message || "算法调试失败";
            renderVisionDebugFailure(message);
            addLog(message, "error");
            return;
        }
        const snapshot = debug.snapshot || debug.Snapshot;
        if (!snapshot) return;
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
        renderVisionDebugBoxes(snapshot);
        renderVisionDebugRules(snapshot);
        renderVisionDebugComparison(snapshot);
        redrawVisionDebugOverlay();
    }

    function renderVisionDebugFailure(message) {
        const pill = el("vision-debug-final-result");
        if (pill) {
            pill.textContent = "错误";
            pill.classList.remove("ok", "ng", "error");
            pill.classList.add("error");
        }
        setText("vision-debug-primary-reason", message || "算法调试失败");
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
            return `<div>
                <strong>${ok ? "OK" : "NG"} · ${escapeHtml(name)}</strong>
                <span>期望: ${escapeHtml(expected)} | 实际: ${escapeHtml(actual)}</span>
                <span>${escapeHtml(reason)}</span>
            </div>`;
        }).join("");
    }

    function renderVisionDebugComparison(snapshot) {
        const comparison = snapshot.comparison || snapshot.Comparison;
        if (!comparison) return;
        const oldResult = comparison.oldResult || comparison.OldResult || "";
        const newResult = comparison.newResult || comparison.NewResult || "";
        setText("vision-debug-history-status", `旧判定 ${oldResult || "-"} / 新判定 ${newResult || "-"}`);
    }

    function getVisionDebugOverlayLayout() {
        const image = el("camera-view");
        const container = el("camera-container");
        const canvas = el("vision-debug-overlay");
        if (!image || !container || !canvas) return null;
        const imageWidth = image.naturalWidth || image.width || 1280;
        const imageHeight = image.naturalHeight || image.height || 720;
        const containerRect = container.getBoundingClientRect();
        const containerRatio = containerRect.width / Math.max(1, containerRect.height);
        const imageRatio = imageWidth / Math.max(1, imageHeight);
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
        canvas.style.width = `${renderedWidth}px`;
        canvas.style.height = `${renderedHeight}px`;
        canvas.style.left = `${offsetX}px`;
        canvas.style.top = `${offsetY}px`;
        canvas.width = Math.max(1, Math.round(renderedWidth));
        canvas.height = Math.max(1, Math.round(renderedHeight));
        return { canvas, imageWidth, imageHeight, scaleX: canvas.width / imageWidth, scaleY: canvas.height / imageHeight };
    }

    function clearVisionDebugOverlay() {
        const canvas = el("vision-debug-overlay");
        if (!canvas) return;
        const ctx = canvas.getContext("2d");
        ctx.clearRect(0, 0, canvas.width, canvas.height);
    }

    function redrawVisionDebugOverlay() {
        const snapshot = store.state.visionDebug?.snapshot || store.state.visionDebug?.Snapshot;
        const layout = getVisionDebugOverlayLayout();
        if (!layout) return;
        const { canvas, scaleX, scaleY } = layout;
        const ctx = canvas.getContext("2d");
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        if (!snapshot) return;
        const boxes = snapshot.allDetections || snapshot.AllDetections || [];
        boxes.forEach((box) => {
            const filtered = Boolean(box.filteredOutByRoi ?? box.FilteredOutByRoi);
            const x = Number(box.x ?? box.X ?? 0) * scaleX;
            const y = Number(box.y ?? box.Y ?? 0) * scaleY;
            const width = Number(box.width ?? box.Width ?? 0) * scaleX;
            const height = Number(box.height ?? box.Height ?? 0) * scaleY;
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
        closeFieldDiagnosticsPanel,
        escapeHtml,
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
        requestExitApp,
        requestOpenCamera,
        requestVisionDebugRecentRecords,
        requestStartSystem,
        runVisionDebugCurrent,
        runVisionDebugHistory,
        saveVisionDebugParams,
        applyVisionDebugTemplate,
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
    });
})();
