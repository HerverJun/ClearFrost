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
