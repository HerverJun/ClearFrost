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
    let lastAuditChainVerification = null;
    let lastAuditExportPath = "";
    let pendingAuditRecordsRequestId = "";
    let pendingAuditExportRequestId = "";
    let pendingAuditChainRequestId = "";
    const pendingReplayRequests = new Map();
    let auditErrorSource = "";
    const HistoryBridgeErrorMessage = "历史面板通信失败，请刷新页面后重试";
    const ReplayBridgeErrorMessage = "回放操作通信失败，请刷新页面后重试";
    const TraceBridgeErrorMessage = "追溯通信失败，请刷新页面后重试";
    const TraceCommandFailedRequestId = "__trace_command_failed__";
    const StatisticsHistoryDefaultDays = 30;
    const StatisticsHistoryMaxDays = 366;
    const ReplayDefaultQueryLimit = 100;
    const ReplayMaxQueryLimit = 1000;
    const AuditBridgeErrorMessage = "前端通信失败，请刷新页面后重试";
    const AuditExportEmptyPathMessage = "审计 CSV 导出未返回文件路径";
    const AuditStatusLabels = Object.freeze({
        Requested: "已请求",
        Denied: "已拒绝",
        Succeeded: "已成功",
        Failed: "已失败",
    });
    const AuditChainStatusLabels = Object.freeze({
        Healthy: "正常",
        Warning: "有警告",
        Blocking: "阻断",
        Unavailable: "不可用",
        Unknown: "未知",
        Pending: "待校验",
        NotChecked: "未校验",
    });
    const AuditOperationLabels = Object.freeze({
        ConfigSave: "保存配置",
        StoragePathRefresh: "刷新存储路径",
        ManualRelease: "强制放行",
        ManualReview: "人工复核",
        ReplayApproval: "回放审批",
        ReplayIntegrityScan: "回放完整性扫描",
        ReplayDatasetArchive: "归档回放数据集",
        DiagnosticPackageExport: "导出诊断包",
        DiagnosticPackageVerify: "复核诊断包",
        FieldEvidenceRetention: "现场证据保留清理",
        FieldHandoffReportExport: "导出交接报告",
        MaintenanceAdviceAction: "维护建议处理",
        ShiftTaskAction: "班次待办处理",
        FieldDebugPlcWriteTest: "PLC 写入测试",
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
    const AuditChainSeverityLabels = Object.freeze({
        Blocking: "阻断",
        Warning: "警告",
    });

    function byId(id) {
        return document.getElementById(id);
    }

    function escapeRegExp(value) {
        return String(value || "").replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    }

    function findLabel(labels, value) {
        const normalized = String(value || "").trim();
        if (!normalized) return "";

        const matchedKey = Object.keys(labels)
            .find((key) => key.toLowerCase() === normalized.toLowerCase());
        return matchedKey ? labels[matchedKey] : normalized;
    }

    function replaceLabelTokens(text, labels) {
        let result = String(text || "");
        Object.entries(labels).forEach(([raw, label]) => {
            result = result.replace(new RegExp(escapeRegExp(raw), "gi"), label);
        });
        return result;
    }

    function formatAuditStatus(status) {
        return findLabel(AuditStatusLabels, status);
    }

    function formatAuditChainStatus(status) {
        return findLabel(AuditChainStatusLabels, status);
    }

    function formatAuditChainStatusForSummary(status) {
        const raw = String(status || "").trim();
        const label = formatAuditChainStatus(raw);
        return label && label !== raw ? `${label} (${raw})` : raw;
    }

    function getAuditChainFindings(source) {
        if (Array.isArray(source?.findings)) return source.findings;
        if (Array.isArray(source?.Findings)) return source.Findings;
        return [];
    }

    function formatAuditChainSeverity(severity) {
        return findLabel(AuditChainSeverityLabels, severity);
    }

    function limitAuditSummaryText(value, maxLength = 120) {
        const text = String(value || "").replace(/\s+/g, " ").trim();
        if (text.length <= maxLength) return text;
        return `${text.slice(0, maxLength)}...`;
    }

    function formatAuditChainFindingHint(finding, fallback = "", preferFullPath = false) {
        const source = finding || {};
        const code = source.errorCode || source.ErrorCode || fallback || "";
        const line = source.lineNumber || source.LineNumber || "";
        const filePath = source.filePath || source.FilePath || "";
        const auditFileName = source.auditFileName || source.AuditFileName || "";
        const entry = source.entryName || source.EntryName || "";
        const location = preferFullPath
            ? (filePath || auditFileName || entry)
            : (auditFileName || filePath || entry);
        const place = location ? `${location}${line ? `:${line}` : ""}` : (line ? `行 ${line}` : "");
        const message = limitAuditSummaryText(source.message || source.Message || "", 80);
        return [
            code,
            place ? `@${place}` : "",
            message ? `- ${message}` : "",
        ].filter(Boolean).join(" ");
    }

    function getAuditChainStatusClass(status) {
        const value = String(status || "").trim().toLowerCase();
        if (["healthy", "success", "succeeded", "ok"].includes(value)) {
            return "bg-bamboo-50 text-bamboo-700 border border-bamboo-100";
        }

        if (["warning", "warn"].includes(value)) {
            return "bg-amber-50 text-amber-700 border border-amber-100";
        }

        if (["", "pending", "notchecked", "unknown"].includes(value)) {
            return "bg-slate-100 text-slate-500 border border-slate-200";
        }

        return "bg-rouge-50 text-rouge-700 border border-rouge-100";
    }

    function getAuditRecordStatusClass(status) {
        const value = String(status || "").trim().toLowerCase();
        if (["failed", "denied", "blocking", "error"].includes(value)) {
            return "bg-rouge-50 text-rouge-600 border-rouge-200";
        }

        if (["requested", "pending", "warning"].includes(value)) {
            return "bg-amber-50 text-amber-700 border-amber-200";
        }

        return "bg-bamboo-50 text-bamboo-600 border-bamboo-200";
    }

    function formatProductionRole(role) {
        return findLabel(ProductionRoleLabels, role);
    }

    function formatAuditOperation(operation) {
        return findLabel(AuditOperationLabels, operation);
    }

    function normalizeAuditOperationFilter(operation) {
        const value = String(operation || "").trim();
        if (!value) return "";

        const matched = Object.entries(AuditOperationLabels)
            .find(([raw, label]) =>
                raw.toLowerCase() === value.toLowerCase() ||
                String(label || "").trim().toLowerCase() === value.toLowerCase());
        return matched ? matched[0] : value;
    }

    function formatAuditText(value) {
        let text = String(value || "").trim();
        if (!text) return "";

        text = replaceLabelTokens(text, AuditOperationLabels);
        text = replaceLabelTokens(text, AuditStatusLabels);
        Object.entries(ProductionRoleLabels).forEach(([raw, label]) => {
            text = text.replace(new RegExp(`RequiredRole=${escapeRegExp(raw)}`, "gi"), `需要${label}权限`);
        });
        text = replaceLabelTokens(text, ProductionRoleLabels);
        return text;
    }

    function shortAuditHash(value) {
        const text = String(value || "").trim();
        if (!text) return "";
        return text.length <= 12 ? text : `${text.slice(0, 12)}...`;
    }

    function formatAuditHashComparison(label, expected, actual) {
        const expectedText = shortAuditHash(expected) || "-";
        const actualText = shortAuditHash(actual) || "-";
        if (expectedText === "-" && actualText === "-") return "";
        return `${label} 期望 ${expectedText} 实际 ${actualText}`;
    }

    async function writeClipboardText(text) {
        let clipboardError = null;
        if (navigator.clipboard?.writeText) {
            try {
                await navigator.clipboard.writeText(text);
                return;
            } catch (error) {
                clipboardError = error;
            }
        }

        const textarea = document.createElement("textarea");
        const target = document.body || document.documentElement;
        textarea.value = text;
        textarea.setAttribute("readonly", "readonly");
        textarea.style.position = "fixed";
        textarea.style.left = "-9999px";
        target.appendChild(textarea);
        try {
            textarea.select();
            const copied = document.execCommand?.("copy") === true;
            if (!copied) {
                throw clipboardError || new Error("ClipboardUnavailable");
            }
        } finally {
            textarea.remove();
        }
    }

    function buildAuditChainSummaryText(data) {
        const source = data || {};
        const error = source.error || source.Error || "";
        const status = String(source.status || source.Status || (error ? "Unavailable" : "Unknown"));
        const totalRecords = source.totalRecords ?? source.TotalRecords ?? 0;
        const verifiedRecords = source.verifiedRecords ?? source.VerifiedRecords ?? 0;
        const findingCount = source.findingCount ?? source.FindingCount ?? 0;
        const lastHash = source.lastRecordSha256 || source.LastRecordSha256 || "";
        const checkedAt = source.checkedAt || source.CheckedAt || new Date().toISOString();
        const findings = getAuditChainFindings(source);
        const statusText = formatAuditChainStatusForSummary(status);
        const lines = [
            "ClearFrost 审计链摘要",
            `状态: ${statusText || "-"}`,
            `已验证: ${verifiedRecords}/${totalRecords}`,
            `异常: ${findingCount}`,
            `最后记录哈希: ${lastHash || "-"}`,
            `校验时间: ${checkedAt}`,
        ];

        if (error) {
            lines.push(`错误: ${error}`);
        }

        const listedFindings = findings.slice(0, 3);
        listedFindings.forEach((finding, index) => {
            const code = finding.errorCode || finding.ErrorCode || "Finding";
            const line = finding.lineNumber || finding.LineNumber || "";
            const filePath = finding.filePath || finding.FilePath || "";
            const auditFileName = finding.auditFileName || finding.AuditFileName || "";
            const entry = finding.entryName || finding.EntryName || "";
            const location = filePath || auditFileName || entry;
            const severity = finding.severity || finding.Severity || "";
            const severityText = formatAuditChainSeverity(severity) || severity;
            const message = limitAuditSummaryText(finding.message || finding.Message || "");
            const expectedPreviousSha256 = finding.expectedPreviousSha256 || finding.ExpectedPreviousSha256 || "";
            const actualPreviousSha256 = finding.actualPreviousSha256 || finding.ActualPreviousSha256 || "";
            const expectedRecordSha256 = finding.expectedRecordSha256 || finding.ExpectedRecordSha256 || "";
            const actualRecordSha256 = finding.actualRecordSha256 || finding.ActualRecordSha256 || "";
            const details = [
                severityText ? `级别 ${severityText}` : "",
                message ? `消息 ${message}` : "",
                formatAuditHashComparison("上一哈希", expectedPreviousSha256, actualPreviousSha256),
                formatAuditHashComparison("记录哈希", expectedRecordSha256, actualRecordSha256),
            ].filter(Boolean).join("，");
            lines.push(`${index + 1}. ${code}${line ? ` @${line}` : ""}${location ? ` ${location}` : ""}${details ? ` - ${details}` : ""}`);
        });

        const remainingFindings = Math.max(0, findings.length - listedFindings.length);
        if (remainingFindings > 0) {
            lines.push(`其余异常: ${remainingFindings} 条未列出`);
        }

        return lines.join("\n");
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

    function toPositiveInteger(value) {
        const numberValue = Number(value);
        return Number.isInteger(numberValue) && numberValue > 0 ? numberValue : null;
    }

    function readManualReviewExpectedRevision() {
        const raw = String(byId("manual-review-expected-revision")?.value || "").trim();
        if (!raw) return { isValid: true, value: null };

        const revision = Number(raw);
        return Number.isInteger(revision) && revision >= 0
            ? { isValid: true, value: revision }
            : { isValid: false, value: null };
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

    function sendHistoryCommand(cmd, value = null, onFailure = null, failureMessage = HistoryBridgeErrorMessage) {
        try {
            const requestId = bridge?.sendCommand?.(cmd, value);
            if (!requestId) {
                throw new Error("WebViewBridgeUnavailable");
            }
            return requestId;
        } catch (error) {
            console.error(`History command failed: ${cmd}`, error);
            window.showToast?.(failureMessage, "error", 1800);
            if (typeof onFailure === "function") onFailure(error);
            return "";
        }
    }

    function setLogHistoryFailure(message = HistoryBridgeErrorMessage) {
        const tbody = byId("log-history-table");
        const badge = byId("log-count-badge");
        if (tbody) {
            tbody.innerHTML = `<tr><td colspan="3" class="px-4 py-10 text-center text-rouge-600 italic">${escapeHtml(message)}</td></tr>`;
        }
        if (badge) badge.textContent = "0 条";
    }

    function setStatisticsHistoryFailure(message = HistoryBridgeErrorMessage) {
        const table = byId("statistics-history-table");
        if (table) {
            table.innerHTML = `<tr><td colspan="5" class="px-4 py-10 text-center text-rouge-600 italic">${escapeHtml(message)}</td></tr>`;
        }
    }

    function setTraceArchiveFailure(message = TraceBridgeErrorMessage) {
        const dateList = byId("ng-date-list");
        const hourList = byId("ng-hour-list");
        const grid = byId("ng-image-grid");
        const badge = byId("gallery-count");
        const status = byId("trace-page-status");

        resetTracePagerState();
        if (dateList) dateList.innerHTML = `<div class="text-[10px] text-rouge-600 p-4 text-center italic font-serif">${escapeHtml(message)}</div>`;
        if (hourList) hourList.innerHTML = "";
        if (grid) grid.innerHTML = `<div class="cf-trace-empty text-rouge-600">${escapeHtml(message)}</div>`;
        if (badge) badge.textContent = "0 条";
        if (status) status.textContent = message;
    }

    function setTraceHourFailure(message = TraceBridgeErrorMessage) {
        const hourList = byId("ng-hour-list");
        const grid = byId("ng-image-grid");
        const status = byId("trace-page-status");

        resetTracePagerState();
        if (hourList) hourList.innerHTML = `<div class="text-[10px] text-rouge-600 italic px-4 py-2 font-serif">${escapeHtml(message)}</div>`;
        if (grid) grid.innerHTML = `<div class="cf-trace-empty text-rouge-600">${escapeHtml(message)}</div>`;
        if (status) status.textContent = message;
    }

    function showTraceLoadFailure(message) {
        const text = message || TraceBridgeErrorMessage;
        tracePagerState.pendingDirection = "";
        tracePagerState.pendingRequestId = TraceCommandFailedRequestId;

        const activePage = getActiveTracePage();
        if (activePage) {
            renderTracePage(activePage);
        } else {
            updateTracePaginationUi();
            const grid = byId("ng-image-grid");
            const badge = byId("gallery-count");
            if (grid) grid.innerHTML = `<div class="cf-trace-empty text-rouge-600">${escapeHtml(text)}</div>`;
            if (badge) badge.textContent = "0 条";
        }

        const status = byId("trace-page-status");
        if (status) status.textContent = text;
        window.showToast?.(text, "error", 1800);
    }

    function sendTraceCommand(cmd, value, onFailure) {
        try {
            const requestId = bridge?.sendCommand?.(cmd, value);
            if (!requestId) {
                throw new Error("WebViewBridgeUnavailable");
            }
            return requestId;
        } catch (error) {
            console.error(`Trace command failed: ${cmd}`, error);
            if (typeof onFailure === "function") onFailure(error);
            return TraceCommandFailedRequestId;
        }
    }

    function getTraceMessageRequestId(data, message) {
        return message?.requestId || message?.RequestId || data?.requestId || data?.RequestId || "";
    }

    function isStaleTraceResponse(requestId) {
        const pendingId = String(tracePagerState.pendingRequestId || "").trim();
        return !pendingId ||
            !requestId ||
            requestId !== pendingId ||
            requestId === tracePagerState.lastHandledRequestId;
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
            const deepLearningText = record.deepLearningTraceSummary || "";
            const adviceMarkup = adviceText
                ? `<p class="cf-trace-advice">${escapeHtml(adviceText)}</p>`
                : "";
            const deepLearningMarkup = deepLearningText
                ? `<p>深度学习: ${escapeHtml(deepLearningText)}</p>`
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
                        ${deepLearningMarkup}
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
        const requestId = sendTraceCommand("get_ng_images", payload, () => {
            showTraceLoadFailure(TraceBridgeErrorMessage);
        });
        tracePagerState.pendingRequestId = requestId;
        if (requestId !== TraceCommandFailedRequestId) {
            updateTracePaginationUi();
        }
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
        sendHistoryCommand("get_detection_logs", null, () => setLogHistoryFailure());
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
        const dateList = byId("ng-date-list");
        if (dateList) dateList.innerHTML = '<div class="text-[10px] text-ink-300 p-4 text-center italic font-serif opacity-50">读取中...</div>';
        sendHistoryCommand("get_ng_dates", null, () => setTraceArchiveFailure(), TraceBridgeErrorMessage);
    }

    function closeGalleryModal() {
        byId("gallery-modal")?.classList.add("hidden");
    }

    function openStatisticsHistoryModal() {
        byId("statistics-history-modal")?.classList.remove("hidden");
        requestStatisticsHistory(StatisticsHistoryDefaultDays);
    }

    function closeStatisticsHistoryModal() {
        byId("statistics-history-modal")?.classList.add("hidden");
    }

    function requestStatisticsHistory(days) {
        const requestedDays = normalizeStatisticsHistoryDays(days);
        const table = byId("statistics-history-table");
        if (table) table.innerHTML = '<tr><td colspan="5" class="text-center py-8">加载中...</td></tr>';
        sendHistoryCommand("get_statistics_history", requestedDays, () => setStatisticsHistoryFailure());
    }

    function normalizeStatisticsHistoryDays(days) {
        const value = Number.parseInt(days, 10);
        if (!Number.isFinite(value) || value <= 0) {
            return StatisticsHistoryDefaultDays;
        }

        return Math.min(value, StatisticsHistoryMaxDays);
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
            sendHistoryCommand("get_detection_logs", null, () => setLogHistoryFailure());
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
            operation: normalizeAuditOperationFilter(byId("audit-operation-filter")?.value),
            operatorId: byId("audit-operator-filter")?.value || "",
            role: byId("audit-role-filter")?.value || "",
            status: byId("audit-status-filter")?.value || "",
            failureReason: byId("audit-failure-filter")?.value || "",
            limit: 500,
        };
    }

    function parseAuditDateValue(value) {
        const text = String(value || "").trim();
        if (!text) return null;

        const timestamp = Date.parse(text);
        return Number.isFinite(timestamp) ? timestamp : null;
    }

    function validateAuditQuery(query) {
        const startText = query?.startTime || query?.StartTime || "";
        const endText = query?.endTime || query?.EndTime || "";
        const startTime = parseAuditDateValue(startText);
        const endTime = parseAuditDateValue(endText);
        if (String(startText).trim() && startTime === null) {
            return "开始时间格式无效";
        }

        if (String(endText).trim() && endTime === null) {
            return "结束时间格式无效";
        }

        if (startTime !== null && endTime !== null && startTime > endTime) {
            return "开始时间不能晚于结束时间";
        }

        return "";
    }

    function buildAuditFilterSummary(query) {
        const source = query || buildAuditQuery();
        const startTime = source.startTime || source.StartTime || "";
        const endTime = source.endTime || source.EndTime || "";
        const operation = normalizeAuditOperationFilter(source.operation || source.Operation || "");
        const operatorId = source.operatorId || source.OperatorId || "";
        const role = source.role || source.Role || "";
        const status = source.status || source.Status || "";
        const failureReason = source.failureReason || source.FailureReason || "";
        const filters = [];

        if (startTime || endTime) {
            filters.push(`时间 ${startTime || "-"} 至 ${endTime || "-"}`);
        }

        if (operation) {
            filters.push(`操作 ${formatAuditOperation(operation) || operation}`);
        }

        if (operatorId) {
            filters.push(`操作员 ${operatorId}`);
        }

        if (role) {
            filters.push(`角色 ${formatProductionRole(role) || role}`);
        }

        if (status) {
            filters.push(`状态 ${formatAuditStatus(status) || status}`);
        }

        if (failureReason) {
            filters.push(`失败原因 ${failureReason}`);
        }

        return filters.join("，");
    }

    function openAuditModal() {
        byId("audit-modal")?.classList.remove("hidden");
        resetAuditModalSessionState();
        queryAuditRecords();
        verifyAuditChain();
    }

    function closeAuditModal() {
        byId("audit-modal")?.classList.add("hidden");
        pendingAuditRecordsRequestId = "__audit_modal_closed_records__";
        pendingAuditExportRequestId = "__audit_modal_closed_export__";
        pendingAuditChainRequestId = "__audit_modal_closed_chain__";
    }

    function isAuditModalOpen() {
        const modal = byId("audit-modal");
        return Boolean(modal && !modal.classList.contains("hidden"));
    }

    function resetAuditModalSessionState() {
        auditErrorSource = "";
        pendingAuditRecordsRequestId = "__audit_modal_open_records__";
        pendingAuditExportRequestId = "__audit_modal_open_export__";
        pendingAuditChainRequestId = "__audit_modal_open_chain__";
        setAuditError("");
        setAuditExportPath("");
    }

    function setAuditError(message, source = "") {
        const node = byId("audit-error");
        if (!node) return;
        const text = String(message || "");
        if (!text && source && auditErrorSource && auditErrorSource !== source) return;
        auditErrorSource = text ? (source || "general") : "";
        node.textContent = text;
        node.classList.toggle("hidden", !text);
    }

    function setAuditCountBadge(text) {
        const badge = byId("audit-count-badge");
        if (badge) badge.textContent = text || "0 条";
    }

    function setAuditExportPath(text, title = "") {
        const node = byId("audit-export-path");
        if (node) {
            node.textContent = text || "";
            node.title = title || text || "";
        }
        if (!text) lastAuditExportPath = "";
    }

    function getMessageRequestId(message) {
        return message?.requestId || message?.RequestId || "";
    }

    function isLocalAuditResponse(message) {
        return message?.local === true || message?.Local === true;
    }

    function isStaleAuditResponse(message, pendingRequestId) {
        if (isLocalAuditResponse(message)) return false;
        const pendingId = String(pendingRequestId || "").trim();
        if (!pendingId) return true;
        const requestId = getMessageRequestId(message);
        if (!requestId) return true;
        return requestId !== pendingId;
    }

    function getAuditCommandErrorSource(cmd) {
        if (cmd === "query_audit_records") return "auditRecords";
        if (cmd === "export_audit_records") return "auditExport";
        if (cmd === "verify_audit_chain") return "auditChainVerification";
        return cmd || "general";
    }

    function sendAuditCommand(cmd, value, onFailure) {
        try {
            const requestId = bridge?.sendCommand?.(cmd, value);
            if (!requestId) {
                throw new Error("WebViewBridgeUnavailable");
            }
            return requestId;
        } catch (error) {
            console.error(`Audit command failed: ${cmd}`, error);
            setAuditError(AuditBridgeErrorMessage, getAuditCommandErrorSource(cmd));
            window.showToast?.(AuditBridgeErrorMessage, "error", 1800);
            if (typeof onFailure === "function") onFailure(error);
            return "__audit_command_failed__";
        }
    }

    function queryAuditRecords() {
        const query = buildAuditQuery();
        const validationError = validateAuditQuery(query);
        if (validationError) {
            pendingAuditRecordsRequestId = "__invalid_audit_records_request__";
            setAuditError(validationError, "auditRecords");
            setAuditCountBadge("0 条");
            setAuditExportPath("");
            const tbody = byId("audit-table");
            if (tbody) {
                tbody.innerHTML = `<tr><td colspan="9" class="px-4 py-10 text-center text-amber-600 italic">${escapeHtml(validationError)}<div class="mt-2 text-[10px] text-slate-400 not-italic">请调整时间范围后再查询</div></td></tr>`;
            }
            return;
        }

        setAuditError("", "auditRecords");
        const tbody = byId("audit-table");
        setAuditCountBadge("加载中");
        setAuditExportPath("");
        if (tbody) {
            tbody.innerHTML = '<tr><td colspan="9" class="px-4 py-10 text-center text-slate-400 italic">正在加载审计记录...</td></tr>';
        }
        pendingAuditRecordsRequestId = sendAuditCommand("query_audit_records", query, () => {
            setAuditCountBadge("0 条");
            setAuditExportPath("");
            if (tbody) {
                tbody.innerHTML = `<tr><td colspan="9" class="px-4 py-10 text-center text-rouge-600 italic">审计查询失败<div class="mt-2 text-[10px] text-slate-400 not-italic">${escapeHtml(AuditBridgeErrorMessage)}</div></td></tr>`;
            }
        });
    }

    function clearAuditFilters() {
        ["audit-start-time", "audit-end-time", "audit-operation-filter", "audit-operator-filter", "audit-role-filter", "audit-status-filter", "audit-failure-filter"]
            .forEach((id) => {
                const node = byId(id);
                if (node) node.value = "";
            });
        queryAuditRecords();
    }

    function exportAuditRecords() {
        const query = buildAuditQuery();
        const validationError = validateAuditQuery(query);
        if (validationError) {
            pendingAuditExportRequestId = "__invalid_audit_export_request__";
            setAuditError(validationError, "auditExport");
            setAuditExportPath("");
            return;
        }

        setAuditError("", "auditExport");
        lastAuditExportPath = "";
        setAuditExportPath("正在导出审计 CSV...");
        pendingAuditExportRequestId = sendAuditCommand("export_audit_records", query, () => {
            setAuditExportPath("");
        });
    }

    function resetAuditChainVerificationState() {
        lastAuditChainVerification = null;
        const badge = byId("audit-chain-badge");
        const countNode = byId("audit-chain-count");
        const findingNode = byId("audit-chain-findings");
        const hashNode = byId("audit-chain-last-hash");
        const messageNode = byId("audit-chain-message");

        if (badge) {
            badge.textContent = "校验中";
            badge.className = "px-2 py-0.5 rounded-full bg-slate-100 text-slate-500 font-bold";
        }
        if (countNode) countNode.textContent = "已验证 -/-";
        if (findingNode) findingNode.textContent = "异常 -";
        if (hashNode) {
            hashNode.textContent = "最后 -";
            hashNode.title = "";
        }
        if (messageNode) {
            messageNode.textContent = "";
            messageNode.title = "";
        }
    }

    function verifyAuditChain() {
        setAuditError("", "auditChainVerification");
        resetAuditChainVerificationState();
        pendingAuditChainRequestId = sendAuditCommand("verify_audit_chain", {}, () => {
            updateAuditChainVerification({
                status: "Unavailable",
                checkedAt: new Date().toISOString(),
                totalRecords: 0,
                verifiedRecords: 0,
                findingCount: 1,
                lastRecordSha256: "",
                findings: [{
                    severity: "Blocking",
                    errorCode: "AuditBridgeUnavailable",
                    message: AuditBridgeErrorMessage,
                }],
                error: AuditBridgeErrorMessage,
            }, { local: true });
        });
    }

    async function copyAuditChainSummary() {
        if (!lastAuditChainVerification) {
            window.showToast?.("请先校验审计链", "warning", 1400);
            return;
        }

        try {
            await writeClipboardText(buildAuditChainSummaryText(lastAuditChainVerification));
            window.showToast?.("审计链摘要已复制", "success", 1600);
        } catch {
            window.showToast?.("复制失败，请手动记录审计链摘要", "error", 1800);
        }
    }

    function updateAuditChainVerification(data, message) {
        if (!isAuditModalOpen()) {
            return;
        }

        if (isStaleAuditResponse(message, pendingAuditChainRequestId)) {
            return;
        }

        lastAuditChainVerification = data || {};
        const error = data?.error || data?.Error || "";
        const status = String(data?.status || data?.Status || (error ? "Unavailable" : "Unknown"));
        const totalRecords = data?.totalRecords ?? data?.TotalRecords ?? 0;
        const verifiedRecords = data?.verifiedRecords ?? data?.VerifiedRecords ?? 0;
        const findingCount = data?.findingCount ?? data?.FindingCount ?? 0;
        const lastHash = data?.lastRecordSha256 || data?.LastRecordSha256 || "";
        const findings = getAuditChainFindings(data);
        const badge = byId("audit-chain-badge");
        const countNode = byId("audit-chain-count");
        const findingNode = byId("audit-chain-findings");
        const hashNode = byId("audit-chain-last-hash");
        const messageNode = byId("audit-chain-message");

        const statusClass = getAuditChainStatusClass(status);

        if (badge) {
            badge.textContent = formatAuditChainStatus(status) || status;
            badge.className = `px-2 py-0.5 rounded-full font-bold ${statusClass}`;
        }
        if (countNode) countNode.textContent = `已验证 ${verifiedRecords}/${totalRecords}`;
        if (findingNode) findingNode.textContent = `异常 ${findingCount}`;
        if (hashNode) {
            hashNode.textContent = `最后 ${shortAuditHash(lastHash) || "-"}`;
            hashNode.title = lastHash || "";
        }
        if (messageNode) {
            const firstFinding = findings[0] || {};
            const hint = formatAuditChainFindingHint(firstFinding, error);
            messageNode.textContent = hint;
            messageNode.title = formatAuditChainFindingHint(firstFinding, error, true);
        }
        if (error) {
            setAuditError(error, "auditChainVerification");
        } else {
            setAuditError("", "auditChainVerification");
        }
    }

    function updateAuditRecords(data, message) {
        if (!isAuditModalOpen()) {
            return;
        }

        if (isStaleAuditResponse(message, pendingAuditRecordsRequestId)) {
            return;
        }

        const records = Array.isArray(data) ? data : (data?.records || data?.Records || []);
        const error = data?.error || data?.Error || "";
        const tbody = byId("audit-table");
        if (!tbody) return;

        if (error) {
            setAuditError(error, "auditRecords");
            setAuditCountBadge("0 条");
            setAuditExportPath("");
            tbody.innerHTML = `<tr><td colspan="9" class="px-4 py-10 text-center text-rouge-600 italic">审计查询失败<div class="mt-2 text-[10px] text-slate-400 not-italic">${escapeHtml(error)}</div></td></tr>`;
            return;
        }

        setAuditError("", "auditRecords");
        setAuditCountBadge(`${records.length} 条`);
        if (!records.length) {
            const filterSummary = buildAuditFilterSummary(data?.query || data?.Query);
            const title = filterSummary ? "未匹配到审计记录" : "暂无审计记录";
            const detail = filterSummary
                ? `<div class="mt-2 text-[10px] text-slate-400 not-italic">当前筛选：${escapeHtml(filterSummary)}</div>`
                : "";
            tbody.innerHTML = `<tr><td colspan="9" class="px-4 py-10 text-center text-slate-400 italic">${escapeHtml(title)}${detail}</td></tr>`;
            return;
        }

        tbody.innerHTML = records.map((record) => {
            const status = String(record.status || "");
            const statusClass = getAuditRecordStatusClass(status);
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

    function updateAuditExport(data, message) {
        if (!isAuditModalOpen()) {
            return;
        }

        if (isStaleAuditResponse(message, pendingAuditExportRequestId)) {
            return;
        }

        const error = data?.error || data?.Error || "";
        const path = String(data?.path || data?.Path || "").trim();
        if (error) {
            setAuditError(error, "auditExport");
            setAuditExportPath("");
            return;
        }

        if (!path) {
            setAuditError(AuditExportEmptyPathMessage, "auditExport");
            setAuditExportPath("");
            return;
        }

        setAuditError("", "auditExport");
        lastAuditExportPath = path;
        setAuditExportPath(`已导出: ${path}`, path);
        window.showToast?.("审计 CSV 已导出", "success", 1600);
    }

    async function copyAuditExportPath() {
        const path = String(lastAuditExportPath || "").trim();
        if (!path) {
            window.showToast?.("请先导出审计 CSV", "warning", 1400);
            return;
        }

        try {
            await writeClipboardText(path);
            window.showToast?.("审计导出路径已复制", "success", 1600);
        } catch {
            window.showToast?.("复制失败，请手动记录导出路径", "error", 1800);
        }
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
                sendHistoryCommand("get_ng_hours", selectedDate, () => setTraceHourFailure(), TraceBridgeErrorMessage);
            } else {
                sendHistoryCommand("get_ng_dates", null, () => setTraceArchiveFailure(), TraceBridgeErrorMessage);
            }
            return;
        }
        const dates = Array.isArray(data) ? data : (data?.dates || data?.Dates || []);
        const error = Array.isArray(data) ? "" : (data?.error || data?.Error || "");
        const list = byId("ng-date-list");
        if (!list) return;
        list.innerHTML = "";

        if (error) {
            setTraceArchiveFailure(error);
            return;
        }

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
                sendHistoryCommand("get_ng_hours", date, () => setTraceHourFailure(), TraceBridgeErrorMessage);
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
        const error = Array.isArray(data) ? "" : (data?.error || data?.Error || "");
        const list = byId("ng-hour-list");
        if (!list) return;
        list.innerHTML = "";
        const hourSelect = byId("trace-hour-select");
        if (hourSelect) {
            hourSelect.innerHTML = "";
            delete hourSelect.dataset.synced;
        }

        if (error) {
            if (hourSelect) hourSelect.innerHTML = '<option value="">全部时段</option>';
            setTraceHourFailure(error);
            return;
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

    function parseJsonObject(value) {
        if (!value) return null;
        if (typeof value === "object") return value;
        if (typeof value !== "string") return null;
        try {
            const parsed = JSON.parse(value);
            return parsed && typeof parsed === "object" ? parsed : null;
        } catch {
            return null;
        }
    }

    function getSummarySection(source, pascalName, camelName) {
        if (!source || typeof source !== "object") return null;
        return source[pascalName] || source[camelName] || null;
    }

    function extractDeepLearningSummary(resultJson) {
        const parsed = parseJsonObject(resultJson);
        if (!parsed) return null;
        return parsed.DeepLearningSummary || parsed.deepLearningSummary || null;
    }

    function formatNumber(value, digits = 2) {
        const numberValue = Number(value);
        return Number.isFinite(numberValue) ? numberValue.toFixed(digits) : "";
    }

    function firstArrayItem(source, pascalName, camelName) {
        const items = source?.[pascalName] || source?.[camelName] || [];
        return Array.isArray(items) && items.length > 0 ? items[0] : null;
    }

    function formatTraceDeepLearningSummary(record) {
        const summary = record?.deepLearningSummary;
        if (!summary) return "";

        const classification = getSummarySection(summary, "Classification", "classification");
        const top1Label = classification?.Top1Label || classification?.top1Label || "";
        const top1Confidence = classification?.Top1Confidence ?? classification?.top1Confidence;
        if (top1Label) {
            const confidence = formatNumber(top1Confidence);
            return confidence ? `分类 Top1=${top1Label} ${confidence}` : `分类 Top1=${top1Label}`;
        }

        const segmentation = getSummarySection(summary, "Segmentation", "segmentation");
        const segmentationCount = Number(segmentation?.InstanceCount ?? segmentation?.instanceCount ?? 0);
        if (segmentationCount > 0) {
            const instance = firstArrayItem(segmentation, "Instances", "instances");
            const area = formatNumber(instance?.MaskArea ?? instance?.maskArea, 0);
            const coverageValue = Number(instance?.MaskCoverage ?? instance?.maskCoverage);
            const coverage = Number.isFinite(coverageValue) ? (coverageValue * 100).toFixed(1) : "";
            const detail = [area ? `面积 ${area}` : "", coverage ? `覆盖率 ${coverage}%` : ""].filter(Boolean).join("，");
            return detail ? `分割 ${segmentationCount} 个，${detail}` : `分割 ${segmentationCount} 个`;
        }

        const obb = getSummarySection(summary, "Obb", "obb");
        const obbCount = Number(obb?.InstanceCount ?? obb?.instanceCount ?? 0);
        if (obbCount > 0) {
            const instance = firstArrayItem(obb, "Instances", "instances");
            const label = instance?.Label || instance?.label || "";
            const angle = formatNumber(instance?.Angle ?? instance?.angle, 1);
            const angleText = angle ? `角度 ${angle}°` : "角度未提供";
            return `OBB ${label ? `${label} ` : ""}${angleText}`;
        }

        const pose = getSummarySection(summary, "Pose", "pose");
        const poseCount = Number(pose?.InstanceCount ?? pose?.instanceCount ?? 0);
        const keyPointCount = Number(pose?.TotalKeyPointCount ?? pose?.totalKeyPointCount ?? 0);
        if (poseCount > 0 || keyPointCount > 0) {
            return `姿态 ${poseCount} 个，关键点 ${keyPointCount} 个`;
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
                ruleSummary: "",
                resultJson: "",
                deepLearningSummary: null,
                deepLearningTraceSummary: "",
            };
        }

        const record = item || {};
        const imageUrl = pickTraceValue(record, "imageUrl", "ImageUrl");
        const renderedImageUrl = pickTraceValue(record, "renderedImageUrl", "RenderedImageUrl");
        const missingRenderedImageValue = pickTraceValue(record, "missingRenderedImage", "MissingRenderedImage");
        const hasRenderedImageValue = pickTraceValue(record, "hasRenderedImage", "HasRenderedImage");
        const resultJson = pickTraceValue(record, "resultJson", "ResultJson") || "";
        const deepLearningSummary = extractDeepLearningSummary(resultJson);
        const hasRenderedImage = hasRenderedImageValue !== ""
            ? toBoolean(hasRenderedImageValue)
            : Boolean(renderedImageUrl) && !toBoolean(missingRenderedImageValue);
        const normalized = {
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
            ruleSummary: pickTraceValue(record, "ruleSummary", "RuleSummary") || "",
            resultJson,
            deepLearningSummary,
            imageUrl,
            renderedImageUrl,
            thumbnailUrl: pickTraceValue(record, "thumbnailUrl", "ThumbnailUrl") || renderedImageUrl || imageUrl,
            displayImageUrl: pickTraceValue(record, "displayImageUrl", "DisplayImageUrl") || renderedImageUrl || imageUrl,
            hasRenderedImage,
            missingRenderedImage: hasRenderedImageValue !== "" ? !toBoolean(hasRenderedImageValue) : !renderedImageUrl || toBoolean(missingRenderedImageValue),
        };
        normalized.deepLearningTraceSummary = formatTraceDeepLearningSummary(normalized);
        return normalized;
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

        sendHistoryCommand("run_history_rule_preview", {
            inspectionId: normalized.inspectionId || "",
            timestamp: normalized.timestamp || "",
            imagePath,
            renderedImagePath,
            ruleSetJson: getCurrentRuleSetJson(),
        }, () => {
            setHistoryRulePreviewStatus({ status: "failed", message: HistoryBridgeErrorMessage });
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
            const summaryLine = [
                normalized.deepLearningTraceSummary ? `深度学习: ${normalized.deepLearningTraceSummary}` : "",
                normalized.ruleSummary ? `规则: ${normalized.ruleSummary}` : "",
            ].filter(Boolean).join(" · ");
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
                <div class="cf-trace-preview-status ${summaryLine ? "" : "hidden"}">${escapeHtml(summaryLine)}</div>
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
        const requestId = getTraceMessageRequestId(data, message);
        if (isStaleTraceResponse(requestId)) {
            return;
        }

        const error = data?.error || data?.Error || "";
        if (error) {
            showTraceLoadFailure(error);
            tracePagerState.lastHandledRequestId = requestId || tracePagerState.lastHandledRequestId;
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
        const raw = Number(byId("replay-query-limit")?.value || ReplayDefaultQueryLimit);
        if (!Number.isFinite(raw)) return ReplayDefaultQueryLimit;
        return Math.max(1, Math.min(ReplayMaxQueryLimit, Math.trunc(raw)));
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

    function sendReplayCommand(cmd, value, statusId, pendingText, failureText) {
        const requestId = sendHistoryCommand(cmd, value, () => {
            setReplayPanelStatus(statusId, `${failureText}：${ReplayBridgeErrorMessage}`);
        }, ReplayBridgeErrorMessage);

        if (requestId) {
            pendingReplayRequests.set(requestId, { statusId, failureText });
            setReplayPanelStatus(statusId, `${pendingText} ${requestId}`);
        }

        return requestId;
    }

    function getCommandErrorDetail(event) {
        const detail = event?.detail || {};
        const data = detail.data || {};
        const envelope = detail.envelope || {};
        return {
            cmd: detail.cmd || data.cmd || data.Cmd || "",
            message: detail.message || data.message || data.Message || HistoryBridgeErrorMessage,
            requestId: String(detail.requestId || envelope.requestId || data.requestId || data.RequestId || "").trim(),
        };
    }

    function updateAuditChainCommandError(message, requestId) {
        updateAuditChainVerification({
            status: "Unavailable",
            totalRecords: 0,
            verifiedRecords: 0,
            findingCount: 1,
            lastRecordSha256: "",
            findings: [{
                severity: "Blocking",
                errorCode: "CommandError",
                message,
            }],
            error: message,
        }, { requestId });
    }

    function handleHistoryCommandError(event) {
        const { cmd, message, requestId } = getCommandErrorDetail(event);
        if (!cmd) return;

        if (cmd === "get_detection_logs") {
            setLogHistoryFailure(message);
            return;
        }

        if (cmd === "get_statistics_history") {
            setStatisticsHistoryFailure(message);
            return;
        }

        if (cmd === "get_ng_dates") {
            setTraceArchiveFailure(message);
            return;
        }

        if (cmd === "get_ng_hours") {
            setTraceHourFailure(message);
            return;
        }

        if (cmd === "get_ng_images" && requestId && requestId === tracePagerState.pendingRequestId) {
            showTraceLoadFailure(message);
            return;
        }

        if (cmd === "query_audit_records") {
            updateAuditRecords({ records: [], error: message }, { requestId });
            return;
        }

        if (cmd === "export_audit_records") {
            updateAuditExport({ path: "", error: message }, { requestId });
            return;
        }

        if (cmd === "verify_audit_chain") {
            updateAuditChainCommandError(message, requestId);
            return;
        }

        if (cmd === "run_history_rule_preview") {
            setHistoryRulePreviewStatus({ status: "failed", message });
            return;
        }

        const replayRequest = requestId ? pendingReplayRequests.get(requestId) : null;
        if (replayRequest) {
            setReplayPanelStatus(replayRequest.statusId, `${replayRequest.failureText}：${message}`);
            pendingReplayRequests.delete(requestId);
        }
    }

    function queryManualReviewRecords() {
        sendReplayCommand("query_manual_review_records", {
            limit: getReplayLimit(),
            recipeVersion: activeTraceRecord?.recipeVersion || "",
        }, "manual-review-response", "查询中", "查询失败");
    }

    function saveManualReview() {
        const inspectionId = activeTraceRecord?.inspectionId || "";
        if (!inspectionId) {
            window.showToast?.("请先选择一条追溯记录再保存真值", "warning", 1800);
            return;
        }

        const detectionRecordId = toPositiveInteger(activeTraceRecord?.detectionRecordId);
        if (detectionRecordId === null) {
            window.showToast?.("当前追溯记录缺少数据库记录编号，无法保存真值", "warning", 1800);
            return;
        }

        const expectedRevision = readManualReviewExpectedRevision();
        if (!expectedRevision.isValid) {
            window.showToast?.("复核版本号必须是非负整数", "warning", 1800);
            return;
        }

        sendReplayCommand("save_manual_review", {
            detectionRecordId,
            inspectionId,
            sampleId: inspectionId,
            groundTruth: byId("manual-review-ground-truth-input")?.value || "OK",
            disposition: byId("manual-review-disposition-input")?.value || "Confirmed",
            expectedRevision: expectedRevision.value,
            notes: String(byId("manual-review-notes")?.value || "").trim(),
        }, "manual-review-response", "保存中", "保存失败");
    }

    function createReplayDataset() {
        const payload = getReplayPanelPayload();
        sendReplayCommand("create_replay_dataset", payload, "replay-run-status", "生成验证样本集", "生成失败");
    }

    function previewReplayDataset() {
        const payload = getReplayPanelPayload();
        sendReplayCommand("preview_replay_dataset", payload, "replay-run-status", "预览中", "预览失败");
    }

    function queryReplayDatasets() {
        sendReplayCommand("query_replay_datasets", getReplayPanelPayload(), "replay-run-status", "查询数据集", "查询失败");
    }

    function archiveReplayDataset() {
        sendReplayCommand("archive_replay_dataset", getReplayPanelPayload(), "replay-run-status", "归档中", "归档失败");
    }

    function runReplayComparison() {
        const payload = getReplayPanelPayload();
        sendReplayCommand("run_replay_comparison", payload, "replay-run-status", "对比新旧模型", "对比失败");
    }

    function cancelReplayRun() {
        sendReplayCommand("cancel_replay_run", getReplayPanelPayload(), "replay-run-status", "正在取消", "取消失败");
    }

    function queryReplayRuns() {
        sendReplayCommand("query_replay_runs", getReplayPanelPayload(), "replay-run-status", "查询运行记录", "查询失败");
    }

    function queryReplayReport() {
        sendReplayCommand("query_replay_report", getReplayPanelPayload(), "replay-run-status", "生成报告", "生成失败");
    }

    function queryModelApprovalEvidence() {
        sendReplayCommand("query_model_approval_evidence", getReplayPanelPayload(), "replay-approval-status", "查询验证记录", "查询失败");
    }

    function runReplayIntegrityScan() {
        sendReplayCommand("run_replay_integrity_scan", getReplayPanelPayload(), "replay-approval-status", "扫描中", "扫描失败");
    }

    function approveReplayCandidate() {
        const payload = getReplayPanelPayload();
        sendReplayCommand("approve_replay_candidate", payload, "replay-approval-status", "确认上线", "确认上线失败");
    }

    Object.assign(window, {
        closeGalleryModal,
        closeImageViewer,
        closeAuditModal,
        clearAuditFilters,
        copyAuditChainSummary,
        copyAuditExportPath,
        closeLogHistoryModal,
        closeStatisticsHistoryModal,
        exportAuditRecords,
        verifyAuditChain,
        openGalleryModal,
        openTraceViewer,
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
    window.addEventListener("cf-command-error", handleHistoryCommandError);
})();
