// ==========================================
// ClearFrost history, logs and gallery
// ==========================================
(function () {
    "use strict";

    const bridge = window.CF_BRIDGE;
    const { escapeHtml } = window.CF_UTILS;

    function byId(id) {
        return document.getElementById(id);
    }

    function syncLogHistoryChrome() {
        if (!document.body.classList.contains("cf-stitch-page")) return;
        const title = document.querySelector("#log-history-modal .cf-ornate-header h3");
        if (title) title.textContent = "检测日志 (Detection Logs)";
        const exportButton = document.querySelector('#log-history-modal [data-cmd="export_log_csv"]');
        if (exportButton) exportButton.classList.add("hidden");
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
                    <td class="px-4 py-3 text-slate-500 max-w-md truncate" title="${escapeHtml(details)}">
                        ${escapeHtml(details || "-")}
                    </td>
                </tr>
            `;
        }).join("");
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
            if (hourSelect) hourSelect.innerHTML = '<option value="">08:00 - 09:00</option>';
            list.innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 font-serif opacity-50">无时段数据</div>';
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
                if (byId("ng-image-grid")) {
                    byId("ng-image-grid").innerHTML = '<div class="col-span-full h-full flex flex-col items-center justify-center py-20 text-ink-300 opacity-50"><div class="animate-spin rounded-full h-8 w-8 border-b-2 border-celadon-500 mb-4"></div><span class="text-xs font-serif italic">正在索引影像档案...</span></div>';
                }
                bridge.sendCommand("get_ng_images", { date: window.currentNGDate, hour });
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
        const hour = byId("trace-hour-select")?.value || window.currentNGHour || "08";
        if (date) window.currentNGDate = date;
        window.currentNGHour = hour;
        bridge.sendCommand("get_ng_images", { date: window.currentNGDate, hour: window.currentNGHour });
    }

    function updateNGImages(data) {
        const images = Array.isArray(data) ? data : (data?.images || data?.Images || []);
        const grid = byId("ng-image-grid");
        const badge = byId("gallery-count");
        if (badge) badge.textContent = `${images.length} 张`;
        if (!grid) return;
        grid.innerHTML = "";

        if (!images.length) {
            grid.innerHTML = '<div class="cf-trace-empty">此时间段未发现异常图片记录</div>';
            return;
        }

        const baseUrl = `http://ng-images.local/Unqualified/${window.currentNGDate}/${window.currentNGHour}/`;
        images.slice(0, 300).forEach((filename) => {
            const url = baseUrl + encodeURIComponent(filename);
            const card = document.createElement("div");
            const traceName = String(filename).replace(/\.[^.]+$/, "") || "-";
            const date = window.currentNGDate || "-";
            const hour = window.currentNGHour ? `${window.currentNGHour}:00` : "--:--";
            card.className = "cf-trace-card";
            card.innerHTML = `<div class="cf-trace-thumb">
                    <img src="${url}" loading="lazy" alt="${escapeHtml(filename)}">
                    <span>NG</span>
                </div>
                <div class="cf-trace-card-body">
                    <div>
                        <p>文件: ${escapeHtml(traceName)}</p>
                        <p>${escapeHtml(date)} ${escapeHtml(hour)}</p>
                    </div>
                    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" aria-hidden="true">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.8"
                            d="M12 3 2.7 20h18.6L12 3Zm0 6v5m0 3h.01" />
                    </svg>
                </div>
                <button type="button">View Details -></button>`;
            card.onclick = () => {
                if (byId("viewer-img")) byId("viewer-img").src = url;
                if (byId("viewer-info")) byId("viewer-info").innerText = filename;
                byId("image-viewer")?.classList.remove("hidden");
            };
            grid.appendChild(card);
        });
    }

    Object.assign(window, {
        closeGalleryModal,
        closeImageViewer,
        closeLogHistoryModal,
        closeStatisticsHistoryModal,
        openGalleryModal,
        openLogHistoryModal,
        openStatisticsHistoryModal,
        receiveStatisticsHistory,
        requestStatisticsHistory,
        searchTraceImages,
        selectTraceHour,
        updateDetectionLogTable,
        updateNGDates,
        updateNGHours,
        updateNGImages,
    });

    bridge.registerMessageHandler("statisticsHistory", receiveStatisticsHistory);
    bridge.registerMessageHandler("detectionLogTable", updateDetectionLogTable);
    bridge.registerMessageHandler("historyDates", updateNGDates);
    bridge.registerMessageHandler("historyHours", updateNGHours);
    bridge.registerMessageHandler("historyImages", updateNGImages);
})();
