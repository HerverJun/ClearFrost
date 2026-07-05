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
        const advice = getFieldArray(health, "maintenanceAdvice", "MaintenanceAdvice");
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

    function formatFieldPlcStatus(status) {
        const value = String(status || "").trim();
        if (isPlcReadyStatus(value)) return "正常";
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
        const blockers = getStartupBlockingItems(health);
        const advice = getFieldArray(health, "maintenanceAdvice", "MaintenanceAdvice");
        const hasIssues = blockers.length > 0 || advice.some((item) => {
            const level = String(item.level || item.Level || "").toLowerCase();
            return level === "critical" || level === "warning";
        });
        const needsEngineer = advice.some((item) => {
            const text = `${item.title || item.Title || ""} ${item.advice || item.Advice || ""} ${item.code || item.Code || ""}`;
            return text.includes("严格模型验证") || text.includes("模型未完成上线验证") || text.includes("StartupBlocked");
        });
        const deviceReady =
            isCameraReadyStatus(getFieldValue(health, "cameraStatus", "CameraStatus")) &&
            isPlcReadyStatus(getFieldValue(health, "plcStatus", "PlcStatus")) &&
            isModelReady(modelProbe, currentModel) &&
            formatFieldStorageStatus(health) === "正常";

        if (needsEngineer) {
            setText("diag-production-readiness", "工程师检查");
            setText("diag-production-guidance", "模型上线验证或启动诊断需要工程师处理。");
            return;
        }

        if (hasIssues || !deviceReady) {
            setText("diag-production-readiness", "需要处理");
            setText("diag-production-guidance", "请先查看待处理问题，并按下一步建议处理。");
            return;
        }

        setText("diag-production-readiness", "可以生产");
        setText("diag-production-guidance", "设备、模型和存储状态正常。");
    }

    function renderFieldDiagnostics(state) {
        const health = state?.health || {};
        const modelProbe = health.modelProbe || health.ModelProbe || {};
        const currentModel = getFieldValue(health, "currentModelName", "CurrentModelName") ||
            modelProbe.currentModelName || modelProbe.CurrentModelName ||
            getFieldValue(health, "modelStatus", "ModelStatus");

        setText("diag-camera-status", formatFieldCameraStatus(getFieldValue(health, "cameraStatus", "CameraStatus")), "未连接");
        setText("diag-plc-status", formatFieldPlcStatus(getFieldValue(health, "plcStatus", "PlcStatus")), "未连接");
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
        showToast("本地图片验证入口已保留，请优先从历史记录选择样本。", "info", 1800);
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
