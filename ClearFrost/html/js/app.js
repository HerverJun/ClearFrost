
// ==========================================
// ClearFrost Core Logic (app.js)
// ==========================================

// Global Stats & Charts (Shared)
window.statsChart = null;
window.inferenceChart = null;

// --- Communication ---

function sendCommand(cmd, value = null) {
    const payload = { cmd: cmd, value: value, timestamp: Date.now() };
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(payload);
        addLog(`CMD: ${cmd} ${value ? '(' + JSON.stringify(value) + ')' : ''}`, 'info');
    } else {
        console.log("[Dev] Mock Send:", payload);
        addLog(`[Mock] Sent: ${cmd}`, 'warning');
    }
}
window.sendCommand = sendCommand;

let _openCameraCooldownUntil = 0;
let _openCameraUnlockTimer = null;
let _openCameraPending = false;

function getToastContainer() {
    let container = document.getElementById('cf-toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'cf-toast-container';
        container.className = 'cf-toast-container';
        document.body.appendChild(container);
    }
    return container;
}

function showToast(message, type = 'info', durationMs = 1400) {
    if (!message) return;

    const container = getToastContainer();
    const toast = document.createElement('div');
    toast.className = `cf-toast cf-toast-${type}`;
    toast.textContent = message;
    container.appendChild(toast);

    requestAnimationFrame(() => {
        toast.classList.add('cf-toast-show');
    });

    window.setTimeout(() => {
        toast.classList.remove('cf-toast-show');
        window.setTimeout(() => toast.remove(), 220);
    }, durationMs);
}
window.showToast = showToast;

function setOpenCameraButtonBusy(isBusy) {
    const btn = document.getElementById('btn-open-camera');
    if (!btn) return;

    btn.disabled = isBusy;
    btn.classList.toggle('camera-open-pending', isBusy);
}

function requestOpenCamera() {
    const now = Date.now();
    if (now < _openCameraCooldownUntil) {
        showToast('相机正在打开中，请勿重复点击', 'warning', 1200);
        return;
    }

    _openCameraCooldownUntil = now + 1500;
    _openCameraPending = true;

    setOpenCameraButtonBusy(true);
    if (_openCameraUnlockTimer) window.clearTimeout(_openCameraUnlockTimer);
    _openCameraUnlockTimer = window.setTimeout(() => {
        setOpenCameraButtonBusy(false);
        _openCameraUnlockTimer = null;
    }, 1500);

    sendCommand('open_camera');
    showToast('打开相机指令已发送', 'info', 1200);
}
window.requestOpenCamera = requestOpenCamera;

// --- Logging ---

function addLog(msg, type = 'info') {
    const container = document.getElementById('log-container');
    if (!container) return;
    const div = document.createElement('div');
    const time = new Date().toLocaleTimeString();
    div.className = "p-1 font-mono text-[10px] border-l-2 " + (type === 'error' ? "border-vermilion text-vermilion bg-vermilion/5" : "border-celadon-300 text-ink-500 hover:bg-slate-50");
    div.innerText = `${time} ${msg}`;
    container.prepend(div);
    if (container.children.length > 50) container.lastChild.remove();
}
window.addLog = addLog;

function addDetectionLog(msg) {
    const container = document.getElementById('detection-log-container');
    if (!container) return;
    const div = document.createElement('div');
    const time = new Date().toLocaleTimeString();
    div.className = "pl-2 border-l border-slate-100 text-ink-600 py-1 hover:bg-slate-50 transition-colors font-mono text-[10px]";
    div.innerText = `[${time}] ${msg}`;
    container.prepend(div);
    if (container.children.length > 50) container.lastChild.remove();
}
window.addDetectionLog = addDetectionLog;

function clearLogs() {
    const el = document.getElementById('log-container');
    if (el) el.innerHTML = '';
}
window.clearLogs = clearLogs;

function clearDetectionLogs() {
    const el = document.getElementById('detection-log-container');
    if (el) el.innerHTML = '';
}
window.clearDetectionLogs = clearDetectionLogs;

// --- State Updates (Receivers) ---

function updateStatus(json) {
    try {
        const data = typeof json === 'string' ? JSON.parse(json) : json;
        if (data.total !== undefined) document.getElementById('val-total').innerText = data.total;
        if (data.ok !== undefined) document.getElementById('val-ok').innerText = data.ok;
        if (data.ng !== undefined) document.getElementById('val-ng').innerText = data.ng;
        if (window.statsChart && (data.ok !== undefined || data.ng !== undefined)) {
            const currentOk = data.ok !== undefined ? data.ok : window.statsChart.data.datasets[0].data[0];
            const currentNg = data.ng !== undefined ? data.ng : window.statsChart.data.datasets[0].data[1];
            window.statsChart.data.datasets[0].data = [currentOk, currentNg];
            window.statsChart.update();
        }
    } catch (e) {
        console.error("Status Update Error:", e);
        addLog("Status Parse Error", 'error');
    }
}
window.updateStatus = updateStatus;

function updateInferenceMetrics(metrics) {
    const data = typeof metrics === 'string' ? JSON.parse(metrics) : metrics;
    if (!data) return;

    const perfDiv = document.getElementById('perf-metrics');
    if (perfDiv && perfDiv.classList.contains('hidden')) {
        perfDiv.classList.remove('hidden');
    }

    if (window.inferenceChart) {
        const fps = data.FPS || 0;
        const arr = window.inferenceChart.data.datasets[0].data;
        if (arr.length >= 60) arr.shift();
        arr.push(fps);
        window.inferenceChart.update('none');
    }

    const fpsElem = document.getElementById('perf-fps');
    if (fpsElem) fpsElem.innerText = (data.FPS || 0).toFixed(1);

    const totalElem = document.getElementById('perf-total');
    if (totalElem) totalElem.innerText = (data.TotalMs || 0).toFixed(1);
}
window.updateInferenceMetrics = updateInferenceMetrics;

function updateImage(base64) {
    const img = document.getElementById('camera-view');
    if (!img) return;
    const src = base64.startsWith('data:image') ? base64 : `data:image/jpeg;base64,${base64}`;
    img.src = src;
}
window.updateImage = updateImage;

function updateResult(isOk) {
    const el = document.getElementById('result-overlay');
    if (!el) return;
    el.classList.remove('hidden');
    // reset anim
    el.style.animation = 'none'; el.offsetHeight; el.style.animation = null;

    if (isOk) {
        el.innerText = "PASS";
        el.className = "absolute top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 px-12 py-5 rounded-2xl font-black text-6xl shadow-2xl backdrop-blur-md animate-fade-in border-4 border-jade-500 bg-jade-50/95 text-jade-700 tracking-wider z-30 transform -rotate-2 font-serif";
    } else {
        el.innerText = "FAIL";
        el.className = "absolute top-1/2 left-1/2 -translate-x-1/2 -translate-y-1/2 px-12 py-5 rounded-2xl font-black text-6xl shadow-2xl backdrop-blur-md animate-fade-in border-4 border-vermilion/50 bg-vermilion/90 text-white tracking-wider z-30 transform rotate-2 font-serif";
    }
}
window.updateResult = updateResult;

function updateConnection(type, isConnected) {
    const elId = type === 'cam' ? 'header-status-cam' : 'header-status-plc';
    const el = document.getElementById(elId);
    if (el) {
        if (isConnected) {
            el.classList.remove('bg-slate-300');
            el.classList.add('bg-emerald-500', 'shadow-[0_0_8px_rgba(16,185,129,0.6)]');
        } else {
            el.classList.remove('bg-emerald-500', 'shadow-[0_0_8px_rgba(16,185,129,0.6)]');
            el.classList.add('bg-slate-300');
        }
    }

    if (type === 'cam' && isConnected && _openCameraPending) {
        _openCameraPending = false;
        setOpenCameraButtonBusy(false);
        if (_openCameraUnlockTimer) {
            window.clearTimeout(_openCameraUnlockTimer);
            _openCameraUnlockTimer = null;
        }
        showToast('相机连接成功', 'success', 1300);
    }
}
window.updateConnection = updateConnection;

// 接收检测结果
window.receiveDetectionResult = function (result) {
    if (!result) return;
    updateResult(result.IsPass);
    if (result.ResultImageBase64) {
        updateImage(result.ResultImageBase64);
    }
    addDetectionLog(`${result.IsPass ? '✓ 通过' : '✗ 未通过'} - ${result.Message} (${result.ProcessingTimeMs.toFixed(1)}ms)`);
};

// 更新预览图像
window.updatePreviewImage = function (preview) {
    if (preview && preview.ImageBase64) {
        updateImage(preview.ImageBase64);
        if (preview.ProcessingTimeMs) {
            addLog(`预览处理耗时: ${preview.ProcessingTimeMs.toFixed(1)}ms`);
        }
    }
};

// 更新模板预览
window.updateTemplatePreview = function (base64) {
    const container = document.getElementById('template-preview');
    if (container) {
        const src = base64.startsWith('data:image') ? base64 : `data:image/jpeg;base64,${base64}`;
        container.innerHTML = `<img src="${src}" class="w-full h-full object-cover rounded-lg">`;
    }
};

// 初始化模型列表 - 被C#后端 SendModelList() 调用
function initModelList(files) {
    const select = document.getElementById('model-select');
    if (!select) return;

    // 清空下拉框
    select.innerHTML = '';

    // 容错处理
    if (!files || files.length === 0) {
        const opt = document.createElement('option');
        opt.text = "未找到可用模型";
        opt.value = "";
        select.add(opt);
        return;
    }

    // 遍历数组动态创建选项
    files.forEach(fileName => {
        const opt = document.createElement('option');
        opt.value = fileName;
        opt.text = fileName;
        select.add(opt);
    });

    // 同步填充辅助模型下拉框
    const aux1Select = document.getElementById('auxiliary1-select');
    const aux2Select = document.getElementById('auxiliary2-select');
    if (aux1Select) {
        aux1Select.innerHTML = '<option value="">不使用</option>';
        files.forEach(fileName => {
            const opt = document.createElement('option');
            opt.value = fileName;
            opt.text = fileName;
            aux1Select.add(opt);
        });
    }
    if (aux2Select) {
        aux2Select.innerHTML = '<option value="">不使用</option>';
        files.forEach(fileName => {
            const opt = document.createElement('option');
            opt.value = fileName;
            opt.text = fileName;
            aux2Select.add(opt);
        });
    }

    // 默认选中第一个并触发一次 change_model 指令告知后端
    select.selectedIndex = 0;
    sendCommand('change_model', select.value);
    addLog(`✓ 成功加载 ${files.length} 个模型`, 'info');
}
window.initModelList = initModelList;

// 更新相机名称显示 - 被C#后端 UpdateCameraName() 调用
function updateCameraName(name) {
    const el = document.getElementById('camera-name-display');
    if (el && name) {
        el.innerText = name;
    }
}
window.updateCameraName = updateCameraName;

// 更新存储路径显示 - 被C#后端 UpdateStoragePathInUI() 调用
function updateStoragePath(path) {
    const el = document.getElementById('cfg-storage-path');
    if (el) {
        el.value = path || '';
    }
}
window.updateStoragePath = updateStoragePath;

// PLC触发信号闪烁 - 被C#后端 FlashPlcTrigger() 调用
function flashPlcTrigger() {
    const el = document.getElementById('header-status-trigger');
    if (el) {
        // 移除之前的动画类以便重新触发
        el.classList.remove('status-trigger-flash');
        // 强制重排以重置动画
        void el.offsetWidth;
        // 添加动画类
        el.classList.add('status-trigger-flash');

        // 动画结束后复位 class，确保后续每次触发都能稳定重播
        el.addEventListener('animationend', () => {
            el.classList.remove('status-trigger-flash');
        }, { once: true });
    }
}
window.flashPlcTrigger = flashPlcTrigger;

// 接收相机列表 (由后端调用)
function receiveCameraList(data) {
    try {
        window.cameraList = data.cameras || [];
        window.activeCameraId = data.activeId || '';

        const select = document.getElementById('cfg-cam-select');
        if (select) {
            select.innerHTML = '';
            if (window.cameraList.length === 0) {
                select.innerHTML = '<option value="">无可用相机</option>';
            } else {
                window.cameraList.forEach(cam => {
                    const opt = document.createElement('option');
                    opt.value = cam.id;
                    opt.textContent = cam.displayName || cam.id;
                    if (cam.id === window.activeCameraId) opt.selected = true;
                    select.appendChild(opt);
                });
            }
        }

        // 更新配置表单
        const activeCam = window.cameraList.find(c => c.id === window.activeCameraId);
        if (activeCam) {
            const nameEl = document.getElementById('cfg-cam-name');
            const serialEl = document.getElementById('cfg-cam-serial');
            const expEl = document.getElementById('cfg-cam-exposure');
            const gainEl = document.getElementById('cfg-cam-gain');
            if (nameEl) nameEl.value = activeCam.displayName || '';
            if (serialEl) serialEl.value = activeCam.serialNumber || '';
            if (expEl) expEl.value = activeCam.exposureTime || '';
            if (gainEl) gainEl.value = activeCam.gain || '';
        }

        addLog('已更新相机列表 (' + window.cameraList.length + ' 台)', 'info');
    } catch (e) {
        console.error('receiveCameraList error:', e);
    }
}
window.receiveCameraList = receiveCameraList;

// 接收视觉配置
window.receiveVisionConfig = function (config) {
    if (!config || !config.Config) return;
    window.operatorList = config.Config.Operators || [];
    window.operatorParameters = config.OperatorParameters || {}; // Store param info per operator
    window.availableOperators = config.AvailableOperators || [];
    if (window.renderOperatorList) window.renderOperatorList();

    // 更新阈值滑块
    if (config.Config.TemplateThreshold) {
        const slider = document.getElementById('template-threshold-slider');
        const value = document.getElementById('template-threshold-value');
        if (slider && value) {
            slider.value = Math.round(config.Config.TemplateThreshold * 100);
            value.innerText = config.Config.TemplateThreshold.toFixed(2);
        }
    }
};

// 接收流程更新确认
window.receivePipelineUpdate = function (config) {
    console.log('receivePipelineUpdate:', config);
    window.operatorList = config.Operators || [];
    if (window.renderOperatorList) window.renderOperatorList();
    addLog(`流程已更新，共 ${window.operatorList.length} 个步骤`);

    // Request full config to get updated OperatorParameters
    setTimeout(() => sendCommand('get_vision_config'), 50);
};

// 接收可用算子列表 (保留接口)
window.receiveAvailableOperators = function (operators) {
    // 可用于动态生成算子选项
};

// 接收历史统计数据
window.receiveStatisticsHistory = function (data) {
    const tbody = document.getElementById('statistics-history-table');
    if (!tbody) return;

    if (!data || data.length === 0) {
        tbody.innerHTML = '<tr><td colspan="5" class="px-4 py-10 text-center text-slate-400 italic">暂无历史数据</td></tr>';
        return;
    }

    tbody.innerHTML = data.map((item, index) => {
        const isToday = index === 0;
        const rowClass = isToday ? 'bg-celadon-50/50' : 'hover:bg-slate-50';
        const dateLabel = isToday ? `${item.date} <span class="text-[9px] text-celadon-600 font-bold">(今日)</span>` : item.date;
        const rateColor = item.rate >= 95 ? 'text-bamboo-600' : item.rate >= 80 ? 'text-gamboge-500' : 'text-rouge-600';

        return `
            <tr class="${rowClass} transition-colors">
                <td class="px-4 py-3 font-medium text-slate-700">${dateLabel}</td>
                <td class="px-4 py-3 text-center font-mono font-bold text-slate-600">${item.total}</td>
                <td class="px-4 py-3 text-center font-mono text-bamboo-600">${item.ok}</td>
                <td class="px-4 py-3 text-center font-mono text-rouge-600">${item.ng}</td>
                <td class="px-4 py-3 text-center font-mono font-bold ${rateColor}">${item.rate.toFixed(1)}%</td>
            </tr>
        `;
    }).join('');
};

// 后端调用 updateDetectionLogTable
window.updateDetectionLogTable = function (logs) {
    const tbody = document.getElementById('log-history-table');
    const badge = document.getElementById('log-count-badge');
    if (!tbody) return;

    if (!logs || logs.length === 0) {
        tbody.innerHTML = '<tr><td colspan="3" class="px-4 py-10 text-center text-slate-400 italic">暂无检测日志</td></tr>';
        if (badge) badge.textContent = '0 条';
        return;
    }

    if (badge) badge.textContent = `${logs.length} 条`;

    tbody.innerHTML = logs.map(log => {
        const isNG = log.result.includes('不合格') || log.result.includes('NG');
        const resultClass = isNG
            ? 'bg-rouge-50 text-rouge-600 border-rouge-200'
            : 'bg-bamboo-50 text-bamboo-600 border-bamboo-200';
        return `
            <tr class="hover:bg-slate-50 transition-colors">
                <td class="px-4 py-3 font-mono text-slate-600 whitespace-nowrap">${log.time || '-'}</td>
                <td class="px-4 py-3 text-center">
                    <span class="inline-block px-2 py-0.5 rounded-full text-[10px] font-bold border ${resultClass}">
                        ${log.result || '-'}
                    </span>
                </td>
                <td class="px-4 py-3 text-slate-500 max-w-md truncate" title="${(log.details || '').replace(/"/g, '&quot;')}">
                    ${log.details || '-'}
                </td>
            </tr>
        `;
    }).join('');
};

// Gallery Logic Receivers
window.updateNGDates = function (dates) {
    const list = document.getElementById('ng-date-list');
    if (!list) return;
    list.innerHTML = '';

    if (!dates || dates.length === 0) {
        list.innerHTML = '<div class="text-[10px] text-ink-300 p-4 text-center italic font-serif opacity-50">暂无历史存根</div>';
        return;
    }

    dates.forEach(d => {
        const div = document.createElement('div');
        div.className = "p-2.5 hover:bg-celadon-50 hover:text-celadon-700 cursor-pointer rounded-xl text-[11px] text-ink-500 font-bold transition-all border border-transparent hover:border-celadon-100 mb-1";
        div.innerText = d;
        div.onclick = () => {
            Array.from(list.children).forEach(c => c.className = "p-2.5 hover:bg-celadon-50 hover:text-celadon-700 cursor-pointer rounded-xl text-[11px] text-ink-500 font-bold transition-all border border-transparent hover:border-celadon-100 mb-1");
            div.className = "p-2.5 bg-celadon-50 text-celadon-700 cursor-pointer rounded-xl text-[11px] font-black transition-all shadow-sm border border-celadon-200 mb-1";
            window.currentNGDate = d;
            document.getElementById('ng-hour-list').innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 opacity-50 font-serif">读取中...</div>';
            document.getElementById('ng-image-grid').innerHTML = '';
            sendCommand('get_ng_hours', d);
        };
        list.appendChild(div);
    });
};

window.updateNGHours = function (hours) {
    const list = document.getElementById('ng-hour-list');
    if (!list) return;
    list.innerHTML = '';

    if (!hours || hours.length === 0) {
        list.innerHTML = '<div class="text-[10px] text-ink-300 italic px-4 py-2 font-serif opacity-50">无时段数据</div>';
        return;
    }

    hours.forEach(h => {
        const div = document.createElement('div');
        div.className = "px-4 py-2 bg-white/60 border border-slate-100 rounded-xl text-[11px] cursor-pointer hover:bg-white hover:text-celadon-600 hover:border-celadon-200 transition-all font-bold text-ink-500 shadow-sm flex items-center justify-between group";
        div.innerHTML = `<span>${h}:00 时段</span> <svg class="w-3 h-3 opacity-0 group-hover:opacity-100 transition-opacity" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M9 5l7 7-7 7"></path></svg>`;
        div.onclick = () => {
            Array.from(list.children).forEach(c => c.className = "px-4 py-2 bg-white/60 border border-slate-100 rounded-xl text-[11px] cursor-pointer hover:bg-white hover:text-celadon-600 hover:border-celadon-200 transition-all font-bold text-ink-500 shadow-sm flex items-center justify-between group");
            div.className = "px-4 py-2 bg-celadon-600 border-celadon-600 text-white rounded-xl text-[11px] cursor-pointer transition-all font-bold shadow-md flex items-center justify-between";
            window.currentNGHour = h;
            document.getElementById('ng-image-grid').innerHTML = '<div class="col-span-full h-full flex flex-col items-center justify-center py-20 text-ink-300 opacity-50"><div class="animate-spin rounded-full h-8 w-8 border-b-2 border-celadon-500 mb-4"></div><span class="text-xs font-serif italic">正在索引影像档案...</span></div>';
            sendCommand('get_ng_images', { date: window.currentNGDate, hour: h });
        };
        list.appendChild(div);
    });
};

window.updateNGImages = function (images) {
    const grid = document.getElementById('ng-image-grid');
    if (!grid) return;
    grid.innerHTML = '';

    if (!images || images.length === 0) {
        grid.innerHTML = '<div class="col-span-full h-full flex flex-col items-center justify-center py-20 text-ink-300 opacity-40"><svg class="w-12 h-12 mb-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"></path></svg><span class="text-xs font-serif italic">此时间段未发现异常影像存根</span></div>';
        return;
    }

    // 构建图片URL: http://ng-images.local/Unqualified/{date}/{hour}/{filename}
    // 注意：此处假设C#后端有拦截器处理这个伪协议域名
    const baseUrl = `http://ng-images.local/Unqualified/${window.currentNGDate}/${window.currentNGHour}/`;

    images.forEach(filename => {
        const url = baseUrl + filename;
        const div = document.createElement('div');
        div.className = "relative group aspect-square bg-white rounded-2xl border border-slate-100 cursor-zoom-in overflow-hidden shadow-sm hover:shadow-xl hover:border-celadon-200 transition-all duration-300";
        div.innerHTML = `<img src="${url}" class="w-full h-full object-cover transition-transform duration-700 group-hover:scale-110" loading="lazy">
                            <div class="absolute inset-x-0 bottom-0 bg-gradient-to-t from-ink-950/80 to-transparent p-3 pt-8 opacity-0 group-hover:opacity-100 transition-opacity">
                                <div class="text-[9px] font-mono text-white/90 truncate">${filename}</div>
                            </div>`;
        div.onclick = () => {
            document.getElementById('viewer-img').src = url;
            document.getElementById('viewer-info').innerText = filename;
            document.getElementById('image-viewer').classList.remove('hidden');
        };
        grid.appendChild(div);
    });
};

// 接收裁剪帧回调
window.receiveTemplateFrame = function (base64) {
    const src = base64.startsWith('data:image') ? base64 : `data:image/jpeg;base64,${base64}`;
    if (window.initTmCropper) window.initTmCropper(src);
}
