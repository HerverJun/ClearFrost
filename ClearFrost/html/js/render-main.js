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
    const KnownRenderReasons = new Set(["inspection", "stats", "health", "replay", "manualReview", "bootstrap", "state"]);
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

    function hasTerminalHandshakeFailure(item) {
        return item?.terminalHandshakeAttempted === true && item?.terminalHandshakeSucceeded === false;
    }

    function getProductJudgementText(item) {
        if (item?.isOk === true) return "OK";
        if (item?.isOk === false) return "NG";
        return "UNKNOWN";
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
        return item?.currentStage || "-";
    }

    function renderCameraResult(state) {
        const inspection = state.inspection || {};
        const isOk = inspection.isOk;
        const terminalFailed = hasTerminalHandshakeFailure(inspection);
        const pill = el("camera-result-pill");
        if (pill) {
            const className = terminalFailed ? "result-cycle-failed" : isOk === true ? "result-ok" : isOk === false ? "result-ng" : "result-idle";
            const text = terminalFailed ? "周期失败" : isOk === true ? "OK" : isOk === false ? "NG" : "WAIT";
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
            const terminalFailed = hasTerminalHandshakeFailure(item);
            const statusClass = terminalFailed ? "cycle-failed" : isOk ? "ok" : item.isOk === false ? "ng" : "run";
            const statusText = terminalFailed ? "周期失败" : isOk ? "OK" : item.isOk === false ? "NG" : "RUN";
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

    function renderReplayStatus(state) {
        const replay = state?.replay || {};
        const run = replay.currentRunId ? replay.runs?.[replay.currentRunId] : null;
        const dataset = replay.dataset || {};
        const approval = replay.approval || {};
        const approvalAvailable = approval.approvalAvailable ?? approval.available;
        const metrics = run?.metrics || dataset?.metrics || {};

        setText("replay-dataset-id", dataset.datasetId || run?.datasetId || "");
        setText("replay-dataset-hash", dataset.datasetHash || run?.datasetHash || "");
        setText("replay-run-status", run?.status || dataset.status || "");
        setText("replay-run-progress", run ? `${run.completedSamples ?? 0}/${run.totalSamples ?? 0}` : "");
        setText("replay-changed-count", metrics.changedDecisionCount ?? "");
        setText("replay-new-missed-count", metrics.candidateNewMissedDetectionCount ?? "");
        setText("replay-fixed-missed-count", metrics.candidateFixedMissedDetectionCount ?? "");
        setText("replay-new-false-reject-count", metrics.candidateNewFalseRejectCount ?? "");
        setText("replay-fixed-false-reject-count", metrics.candidateFixedFalseRejectCount ?? "");
        setText("replay-approval-status",
            approval.succeeded === true
                ? "Approved"
                : approval.succeeded === false
                    ? (approval.errorCode || "Rejected")
                    : approvalAvailable === true
                        ? "Available"
                        : approvalAvailable === false
                            ? "Rejected"
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
        escapeHtml,
        flashPlcTrigger,
        handleInspectionUpdate,
        renderHealthSnapshot: handleHealthSnapshot,
        renderInspectionContext: () => renderInspectionContext(window.CF_STATE),
        renderManualReviewStatus: () => renderManualReviewStatus(window.CF_STATE),
        renderReplayStatus: () => renderReplayStatus(window.CF_STATE),
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
})();
