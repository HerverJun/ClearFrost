
// ==========================================
// ClearFrost UI Logic (ui.js)
// ==========================================

// Global state variables for UI
let roiCanvas = null;
let roiCtx = null;
let isDrawingROI = false;
let roiStartX = 0;
let roiStartY = 0;
let tmCropper = null;
let windowDragging = false;
let dragOffset = { x: 0, y: 0 };

// --- Window Dragging ---
function startDrag(e) {
    if (e.target.closest('button') || e.target.closest('input') || e.target.closest('.no-drag')) return;
    windowDragging = true;
    dragOffset.x = e.screenX - window.screenX;
    dragOffset.y = e.screenY - window.screenY;
    sendCommand('start_drag');
}
window.startDrag = startDrag;

document.addEventListener('mouseup', () => {
    if (windowDragging) {
        windowDragging = false;
        // Drag end is handled natively by C#, no message needed
    }
});

document.addEventListener('mousemove', (e) => {
    // Native drag handled by C# often, but here for completeness if needed
});

// --- Drawer & Dock ---

function toggleDrawer(panelId) {
    const panel = document.getElementById(panelId);
    if (!panel) return;

    const isLeft = panelId === 'left-panel';
    const isOpen = panel.classList.contains('drawer-open');

    if (isOpen) {
        panel.classList.remove('drawer-open');
        panel.classList.add(isLeft ? 'drawer-closed-left' : 'drawer-closed-right');
        // Show floating button
        const floatBtn = document.getElementById(isLeft ? 'float-btn-left' : 'float-btn-right');
        if (floatBtn) {
            floatBtn.classList.remove('pointer-events-none', 'opacity-0');
            floatBtn.classList.add('opacity-100');
        }
    } else {
        panel.classList.remove(isLeft ? 'drawer-closed-left' : 'drawer-closed-right');
        panel.classList.add('drawer-open');
        // Hide floating button
        const floatBtn = document.getElementById(isLeft ? 'float-btn-left' : 'float-btn-right');
        if (floatBtn) {
            floatBtn.classList.add('pointer-events-none', 'opacity-0');
            floatBtn.classList.remove('opacity-100');
        }
    }
}
window.toggleDrawer = toggleDrawer;

function toggleDock() {
    const dock = document.getElementById('bottom-dock');
    const arrow = document.getElementById('dock-arrow');
    const trigger = document.getElementById('dock-trigger-container');

    if (!dock) return;

    const isOpen = dock.classList.contains('dock-open');

    if (isOpen) {
        // 收起工具栏 → 显示呼出按钮
        dock.classList.remove('dock-open');
        dock.classList.add('dock-closed');
        if (arrow) arrow.classList.remove('rotate-180');
        if (trigger) {
            trigger.classList.remove('opacity-0', 'pointer-events-none');
            trigger.classList.add('opacity-100', 'pointer-events-auto');
        }
    } else {
        // 展开工具栏 → 隐藏呼出按钮
        dock.classList.remove('dock-closed');
        dock.classList.add('dock-open');
        if (arrow) arrow.classList.add('rotate-180');
        if (trigger) {
            trigger.classList.remove('opacity-100', 'pointer-events-auto');
            trigger.classList.add('opacity-0', 'pointer-events-none');
        }
    }
}
window.toggleDock = toggleDock;

// --- Vision Modes ---

function switchVisionMode(mode) {
    // mode: 0 = YOLO, 1 = Template (numeric from HTML buttons)
    const isYolo = (mode === 0 || mode === '0' || mode === 'yolo');

    // Update tab styles
    const yoloTab = document.getElementById('mode-tab-yolo');
    const templateTab = document.getElementById('mode-tab-template');

    if (isYolo) {
        if (yoloTab) {
            yoloTab.classList.remove('text-ink-400', 'hover:text-ink-600');
            yoloTab.classList.add('bg-porcelain-100', 'text-celadon-600', 'shadow-sm', 'ring-1', 'ring-celadon-200/50');
        }
        if (templateTab) {
            templateTab.classList.remove('bg-porcelain-100', 'text-celadon-600', 'shadow-sm', 'ring-1', 'ring-celadon-200/50');
            templateTab.classList.add('text-ink-400', 'hover:text-ink-600');
        }
    } else {
        if (templateTab) {
            templateTab.classList.remove('text-ink-400', 'hover:text-ink-600');
            templateTab.classList.add('bg-porcelain-100', 'text-celadon-600', 'shadow-sm', 'ring-1', 'ring-celadon-200/50');
        }
        if (yoloTab) {
            yoloTab.classList.remove('bg-porcelain-100', 'text-celadon-600', 'shadow-sm', 'ring-1', 'ring-celadon-200/50');
            yoloTab.classList.add('text-ink-400', 'hover:text-ink-600');
        }
    }

    // Toggle panels
    const yoloControls = document.getElementById('yolo-controls');
    const templateControls = document.getElementById('template-controls');

    if (isYolo) {
        if (yoloControls) yoloControls.classList.remove('hidden');
        if (templateControls) templateControls.classList.add('hidden');
    } else {
        if (yoloControls) yoloControls.classList.add('hidden');
        if (templateControls) templateControls.classList.remove('hidden');
        renderOperatorList();
    }

    // Send to backend (expects integer: 0=YOLO, 1=Template)
    sendCommand('set_vision_mode', isYolo ? 0 : 1);

    // Request full config including parameters when switching to template mode
    if (!isYolo) {
        setTimeout(() => sendCommand('get_vision_config'), 100);
    }
}
window.switchVisionMode = switchVisionMode;

function toggleMultiModel(enabled) {
    const checkbox = document.getElementById('enable-multi-model');
    const statusText = document.getElementById('multi-model-status');
    const configSection = document.getElementById('multi-model-config');

    // Update checkbox state
    if (checkbox) checkbox.checked = enabled;

    // Update status text
    if (statusText) {
        statusText.innerText = enabled ? "已启用" : "自动切换";
        if (enabled) {
            statusText.classList.add('text-celadon-600', 'font-bold');
            statusText.classList.remove('text-ink-500');
        } else {
            statusText.classList.remove('text-celadon-600', 'font-bold');
            statusText.classList.add('text-ink-500');
        }
    }

    // Enable/disable auxiliary model configuration section
    if (configSection) {
        if (enabled) {
            configSection.classList.remove('opacity-50', 'pointer-events-none');
        } else {
            configSection.classList.add('opacity-50', 'pointer-events-none');
        }
    }

    sendCommand('toggle_multi_model', enabled);
    addLog(enabled ? '✓ 多模型自动切换已启用' : '多模型自动切换已禁用', enabled ? 'success' : 'info');
}
window.toggleMultiModel = toggleMultiModel;

// --- Camera Management ---

function onCameraSelected(cameraId) {
    // cameraId can be passed directly from onchange="onCameraSelected(this.value)"
    // or we fallback to reading from the select element
    const select = document.getElementById('cfg-cam-select');
    const id = cameraId || (select ? select.value : '');
    window.activeCameraId = id;

    if (!window.cameraList) window.cameraList = [];
    const cam = window.cameraList.find(c => c.id === id);
    if (cam) {
        const nameEl = document.getElementById('cfg-cam-name');
        const serialEl = document.getElementById('cfg-cam-serial');
        const expEl = document.getElementById('cfg-cam-exposure');
        const gainEl = document.getElementById('cfg-cam-gain');
        if (nameEl) nameEl.value = cam.displayName || '';
        if (serialEl) serialEl.value = cam.serialNumber || '';
        if (expEl) expEl.value = cam.exposureTime || '';
        if (gainEl) gainEl.value = cam.gain || '';
    }

    // Notify backend of camera switch
    if (id) sendCommand('switch_camera', id);
}
window.onCameraSelected = onCameraSelected;

function addNewCamera() {
    // 收集表单中的相机配置信息
    const displayName = document.getElementById('cfg-cam-name')?.value || `相机 ${(window.cameraList?.length || 0) + 1}`;
    const manufacturer = document.getElementById('cfg-cam-manufacturer')?.value || 'MindVision';
    const serialNumber = document.getElementById('cfg-cam-serial')?.value || '';
    const exposureTime = parseFloat(document.getElementById('cfg-cam-exposure')?.value) || 50000;
    const gain = parseFloat(document.getElementById('cfg-cam-gain')?.value) || 1.0;

    if (!serialNumber) {
        alert('请输入相机序列号');
        return;
    }

    const camData = {
        displayName: displayName,
        manufacturer: manufacturer,
        serialNumber: serialNumber,
        exposureTime: exposureTime,
        gain: gain
    };

    sendCommand('add_camera', camData);
    addLog(`正在添加/更新相机: ${displayName}...`, 'info');
}
window.addNewCamera = addNewCamera;

function deleteCurrentCamera() {
    const select = document.getElementById('cfg-cam-select');
    if (!select || !select.value) return;
    window.chrome.webview.postMessage(JSON.stringify({
        cmd: 'delete_camera',
        value: select.value
    }));
}
window.deleteCurrentCamera = deleteCurrentCamera;

// --- Super Search Camera ---
function superSearchCameras() {
    const modal = document.getElementById('super-search-modal');
    const loading = document.getElementById('super-search-loading');
    const results = document.getElementById('super-search-results');
    const empty = document.getElementById('super-search-empty');

    if (!modal) return;

    // 显示弹窗和加载状态
    modal.classList.remove('hidden');
    loading.classList.remove('hidden');
    results.classList.add('hidden');
    empty.classList.add('hidden');
    results.innerHTML = '';

    // 发送搜索命令
    window.chrome.webview.postMessage(JSON.stringify({
        cmd: 'super_search_cameras'
    }));
}
window.superSearchCameras = superSearchCameras;

function closeSuperSearchModal() {
    const modal = document.getElementById('super-search-modal');
    if (modal) modal.classList.add('hidden');
}
window.closeSuperSearchModal = closeSuperSearchModal;

// 接收超级搜索结果
function receiveSuperSearchResult(data) {
    const loading = document.getElementById('super-search-loading');
    const results = document.getElementById('super-search-results');
    const empty = document.getElementById('super-search-empty');

    loading.classList.add('hidden');

    if (!data || !data.cameras || data.cameras.length === 0) {
        empty.classList.remove('hidden');
        return;
    }

    results.classList.remove('hidden');
    results.innerHTML = data.cameras.map(cam => `
        <div class="bg-gradient-to-r from-slate-50 to-slate-100 rounded-xl p-4 border border-slate-200 hover:shadow-md transition-all">
            <div class="flex items-center justify-between">
                <div class="flex-1">
                    <div class="flex items-center gap-2 mb-1">
                        <span class="text-sm font-bold text-slate-700">${cam.userDefinedName || cam.model || '未命名相机'}</span>
                        <span class="px-2 py-0.5 text-[10px] font-semibold rounded-full bg-indigo-100 text-indigo-600">${cam.manufacturer}</span>
                    </div>
                    <div class="text-xs text-slate-500 space-y-0.5">
                        <div><span class="font-medium">序列号:</span> ${cam.serialNumber}</div>
                        <div><span class="font-medium">型号:</span> ${cam.model || '-'}</div>
                        <div><span class="font-medium">接口:</span> ${cam.interfaceType || '-'}</div>
                    </div>
                </div>
                <button onclick="directConnectCamera('${cam.serialNumber}', '${cam.manufacturer}', '${cam.model || ''}', '${cam.userDefinedName || ''}')"
                    class="px-4 py-2 bg-gradient-to-r from-green-500 to-emerald-500 text-white text-sm font-semibold rounded-lg hover:shadow-lg hover:scale-105 transition-all flex items-center gap-1">
                    <svg xmlns="http://www.w3.org/2000/svg" class="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M13 10V3L4 14h7v7l9-11h-7z" />
                    </svg>
                    连接
                </button>
            </div>
        </div>
    `).join('');
}
window.receiveSuperSearchResult = receiveSuperSearchResult;

// 直接连接相机（无序列号过滤）
function directConnectCamera(serialNumber, manufacturer, model, userDefinedName) {
    window.chrome.webview.postMessage(JSON.stringify({
        cmd: 'direct_connect_camera',
        value: {
            serialNumber: serialNumber,
            manufacturer: manufacturer,
            model: model,
            userDefinedName: userDefinedName
        }
    }));
    closeSuperSearchModal();
}
window.directConnectCamera = directConnectCamera;


function requestPreview() {
    sendCommand('get_preview');
    addLog('正在获取预览...', 'info');
}
window.requestPreview = requestPreview;

// --- Template Operators ---

function getOperatorName(typeId) {
    const map = {
        // Match backend snake_case TypeIds from OperatorFactory.cs
        'template_match': '模板匹配 (Template)',
        'feature_match': '特征匹配 (AKAZE)',
        'orb_match': '特征匹配 (ORB)',
        'pyramid_shape_match': '形状匹配 (金字塔)',
        'background_diff': '有无检测 (背景差分)',
        // Legacy PascalCase fallbacks
        'ShapeMatch': '形状匹配 (Shape)',
        'GrayscaleMatch': '灰度匹配 (Gray)',
        'FeatureMatch': '特征匹配 (Feature)',
        'LineFinder': '直线查找 (Line)',
        'CircleFinder': '圆查找 (Circle)'
    };
    return map[typeId] || typeId;
}

function renderOperatorList() {
    const container = document.getElementById('operator-list');
    if (!container) return;

    if (!window.operatorList || window.operatorList.length === 0) {
        container.innerHTML = '<div class="text-[10px] text-ink-300 text-center py-8 italic font-serif">点击 "添加步骤" 构建检测流程...</div>';
        return;
    }

    container.innerHTML = window.operatorList.map((op, index) => {
        const typeId = op.TypeId || op.typeId || '';
        const name = getOperatorName(typeId);
        const instanceId = op.InstanceId || op.instanceId || index.toString();
        const params = op.Parameters || {};

        // Get parameter info from backend if available
        const paramInfoList = (window.operatorParameters && window.operatorParameters[instanceId]) || [];

        // Build parameter HTML dynamically
        let paramHtml = '';
        if (paramInfoList.length > 0) {
            paramHtml = '<div class="mt-2 space-y-2">';
            paramInfoList.forEach(p => {
                const val = params[p.Name] ?? params[p.Name?.toLowerCase()] ?? p.CurrentValue ?? p.DefaultValue;
                if (p.Type === 'slider') {
                    const step = p.Step || 1;
                    const min = p.Min ?? 0;
                    const max = p.Max ?? 100;
                    paramHtml += `
                    <div class="grid grid-cols-3 gap-1 items-center">
                        <label class="text-[9px] text-ink-400 col-span-1">${p.DisplayName || p.Name}</label>
                        <input type="range" min="${min}" max="${max}" step="${step}" value="${val}"
                               onchange="updateOperatorParam('${instanceId}', '${p.Name}', parseFloat(this.value)); this.nextElementSibling.innerText=this.value;"
                               class="col-span-1 h-1 accent-celadon-500">
                        <span class="text-[9px] font-mono text-ink-500 text-right">${val}</span>
                    </div>`;
                } else if (p.Type === 'checkbox' || p.Type === 'boolean') {
                    // 支持 'checkbox' 和 'boolean' 类型
                    paramHtml += `
                    <label class="flex items-center gap-2 cursor-pointer">
                        <input type="checkbox" ${val ? 'checked' : ''}
                               onchange="updateOperatorParam('${instanceId}', '${p.Name}', this.checked)"
                               class="w-3 h-3 accent-celadon-500">
                        <span class="text-[9px] text-ink-400">${p.DisplayName || p.Name}</span>
                    </label>`;
                } else if (p.Type === 'number') {
                    // 支持 'number' 类型作为数字输入框
                    paramHtml += `
                    <div class="grid grid-cols-3 gap-1 items-center">
                        <label class="text-[9px] text-ink-400 col-span-1">${p.DisplayName || p.Name}</label>
                        <input type="number" value="${val}" step="any"
                               onchange="updateOperatorParam('${instanceId}', '${p.Name}', parseFloat(this.value))"
                               class="col-span-2 px-2 py-1 text-[10px] font-mono bg-white border border-slate-200 rounded focus:border-celadon-400 focus:outline-none">
                    </div>`;
                } else if (p.Type === 'file') {
                    // 显示文件路径（只读，通过模板管理器设置）
                    const fileName = val ? val.split(/[/\\]/).pop() : '未设置';
                    paramHtml += `
                    <div class="grid grid-cols-3 gap-1 items-center">
                        <label class="text-[9px] text-ink-400 col-span-1">${p.DisplayName || p.Name}</label>
                        <span class="col-span-2 px-2 py-1 text-[9px] font-mono text-ink-500 bg-slate-50 border border-slate-100 rounded truncate" title="${val || ''}">${fileName}</span>
                    </div>`;
                }
            });
            paramHtml += '</div>';
        }

        // Add template management button for trainable operators
        const trainableTypes = ['template_match', 'feature_match', 'orb_match', 'pyramid_shape_match', 'background_diff'];
        if (trainableTypes.includes(typeId)) {
            // 获取当前算子的训练状态和模板缩略图
            const isTrained = params.isTrained || params.IsTrained || false;
            const thumbnail = params.templateThumbnail || params.TemplateThumbnail || '';

            // 添加模板预览区域
            if (thumbnail && isTrained) {
                paramHtml += `
                <div class="mt-2 pt-2 border-t border-slate-100">
                    <div class="flex items-center gap-2">
                        <div class="w-12 h-12 rounded-lg overflow-hidden border border-celadon-200 bg-white shadow-sm">
                            <img src="data:image/jpeg;base64,${thumbnail}" class="w-full h-full object-cover" alt="模板">
                        </div>
                        <div class="flex-1">
                            <div class="text-[9px] text-celadon-600 font-bold flex items-center gap-1">
                                <svg class="w-3 h-3" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.707-9.293a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clip-rule="evenodd"></path></svg>
                                已训练
                            </div>
                            <p class="text-[8px] text-ink-400 mt-0.5">模板已就绪</p>
                        </div>
                    </div>
                </div>`;
            } else {
                paramHtml += `
                <div class="mt-2 pt-2 border-t border-slate-100">
                    <div class="flex items-center gap-2">
                        <div class="w-12 h-12 rounded-lg overflow-hidden border border-dashed border-slate-200 bg-slate-50 flex items-center justify-center">
                            <svg class="w-5 h-5 text-slate-300" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1.5" d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"></path></svg>
                        </div>
                        <div class="flex-1">
                            <div class="text-[9px] text-gamboge-500 font-bold flex items-center gap-1">
                                <svg class="w-3 h-3" fill="currentColor" viewBox="0 0 20 20"><path fill-rule="evenodd" d="M8.257 3.099c.765-1.36 2.722-1.36 3.486 0l5.58 9.92c.75 1.334-.213 2.98-1.742 2.98H4.42c-1.53 0-2.493-1.646-1.743-2.98l5.58-9.92zM11 13a1 1 0 11-2 0 1 1 0 012 0zm-1-8a1 1 0 00-1 1v3a1 1 0 002 0V6a1 1 0 00-1-1z" clip-rule="evenodd"></path></svg>
                                未训练
                            </div>
                            <p class="text-[8px] text-ink-400 mt-0.5">请点击下方按钮设置模板</p>
                        </div>
                    </div>
                </div>`;
            }

            paramHtml += `
            <div class="mt-2">
                <button onclick="openTemplateManager('${instanceId}', '${name}')" class="w-full py-1 bg-celadon-50 text-celadon-600 text-[9px] rounded hover:bg-celadon-100 transition-colors flex items-center justify-center gap-1">
                    <svg class="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z"></path></svg>
                    管理模板
                </button>
            </div>`;
        }

        return `
        <div class="bg-white border border-slate-200 rounded-xl p-3 shadow-sm hover:shadow-md transition-shadow relative group">
            <div class="flex items-center justify-between mb-1">
                <div class="flex items-center space-x-2">
                    <span class="flex items-center justify-center w-5 h-5 rounded-full bg-celadon-50 text-celadon-600 text-[10px] font-bold font-mono">${index + 1}</span>
                    <span class="text-sm font-bold text-ink-700">${name}</span>
                </div>
                <button onclick="removeOperator('${instanceId}')" class="text-slate-300 hover:text-rouge-500 transition-colors">
                    <svg class="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="2" d="M6 18L18 6M6 6l12 12"></path></svg>
                </button>
            </div>
            ${paramHtml}
        </div>
        `;
    }).join('');
}
window.renderOperatorList = renderOperatorList;

function openAddOperatorMenu() { document.getElementById('add-operator-modal').classList.remove('hidden'); }
window.openAddOperatorMenu = openAddOperatorMenu;

function closeAddOperatorModal() { document.getElementById('add-operator-modal').classList.add('hidden'); }
window.closeAddOperatorModal = closeAddOperatorModal;

function addOperator(typeId) {
    if (!typeId) return;
    sendCommand('pipeline_update', { action: 'add', typeId: typeId });
    closeAddOperatorModal();
}
window.addOperator = addOperator;

function removeOperator(index) {
    if (confirm('确定要移除此步骤吗？')) sendCommand('pipeline_update', { action: 'remove', instanceId: index });
}
window.removeOperator = removeOperator;

function updateOperatorParam(index, key, value) {
    sendCommand('pipeline_update', { action: 'update', instanceId: index, paramName: key, paramValue: value });
}
window.updateOperatorParam = updateOperatorParam;

function updateTemplateThreshold(val) {
    const value = parseFloat(val);
    const display = document.getElementById('template-threshold-value');
    if (display) display.innerText = value.toFixed(2);
    sendCommand('set_template_threshold', value);
}
window.updateTemplateThreshold = updateTemplateThreshold;

// --- YOLO Parameter Controls ---

function updateConfidence(val) {
    const value = parseFloat(val) / 100;
    const display = document.getElementById('conf-value');
    if (display) display.innerText = value.toFixed(2);
    sendCommand('set_confidence', value);
}
window.updateConfidence = updateConfidence;

function updateIou(val) {
    const value = parseFloat(val) / 100;
    const display = document.getElementById('iou-value');
    if (display) display.innerText = value.toFixed(2);
    sendCommand('set_iou', value);
}
window.updateIou = updateIou;

function updateTaskType(val) {
    const taskType = parseInt(val, 10);
    sendCommand('set_task_type', taskType);
    const taskNames = {
        0: '分类 (Classify)',
        1: '目标检测 (Detect)',
        3: '实例分割 (Segment)',
        5: '姿态估计 (Pose)',
        6: '旋转框检测 (OBB)'
    };
    addLog(`任务类型已设置为: ${taskNames[taskType] || taskType}`);
}
window.updateTaskType = updateTaskType;

function uploadTemplateImage() {
    // 使用 tm-file-input 作为模板上传的文件选择器 (模板管理器中的元素)
    const fileInput = document.getElementById('tm-file-input');
    if (fileInput) fileInput.click();
}
window.uploadTemplateImage = uploadTemplateImage;

// --- Modals ---

function openSettingsModal(config) {
    document.getElementById('settings-modal').classList.remove('hidden');
    // 如果后端传入了配置数据（密码验证通过后），直接填充设置
    // 否则，发送 open_settings 命令触发密码验证流程
    if (config) {
        populateSettings(config);
    } else {
        sendCommand('open_settings');
    }
}
window.openSettingsModal = openSettingsModal;

function closeSettingsModal() { document.getElementById('settings-modal').classList.add('hidden'); }
window.closeSettingsModal = closeSettingsModal;

// Populate settings from backend config object
function populateSettings(data) {
    // 映射后端属性名到前端input id
    const mapping = {
        'StoragePath': 'cfg-storage-path',
        'PlcProtocol': 'cfg-plc-protocol',
        'PlcIp': 'cfg-plc-ip',
        'PlcPort': 'cfg-plc-port',
        'PlcTriggerAddress': 'cfg-plc-trigger',
        'PlcResultAddress': 'cfg-plc-result',
        'CameraName': 'cfg-cam-name',
        'CameraSerialNumber': 'cfg-cam-serial',
        'ExposureTime': 'cfg-cam-exposure',
        'GainRaw': 'cfg-cam-gain',
        'TargetLabel': 'cfg-logic-target-label',
        'TargetCount': 'cfg-logic-target-count',
        'MaxRetryCount': 'cfg-logic-retry-count',
        'RetryIntervalMs': 'cfg-logic-retry-interval',
        'EnableGpu': 'cfg-yolo-gpu'
    };
    for (const key in data) {
        const inputId = mapping[key];
        if (!inputId) continue;
        const el = document.getElementById(inputId);
        if (el) {
            if (el.type === 'checkbox') el.checked = !!data[key];
            else el.value = data[key] ?? '';
        }
    }
    if (data.TaskType !== undefined) {
        const taskTypeSelect = document.getElementById('task-type-select');
        if (taskTypeSelect) taskTypeSelect.value = data.TaskType.toString();
    }
}
window.populateSettings = populateSettings;

function initSettings(config) {
    const data = typeof config === 'string' ? JSON.parse(config) : config;
    populateSettings(data);
    addLog("系统配置已加载", "success");
}
window.initSettings = initSettings;

function saveSettings() {
    // 显式映射: 前端 input ID -> AppConfig 属性名
    const fieldMapping = {
        'cfg-storage-path': 'StoragePath',
        'cfg-plc-protocol': 'PlcProtocol',
        'cfg-plc-ip': 'PlcIp',
        'cfg-plc-port': 'PlcPort',
        'cfg-plc-trigger': 'PlcTriggerAddress',
        'cfg-plc-result': 'PlcResultAddress',
        'cfg-cam-name': 'CameraName',
        'cfg-cam-serial': 'CameraSerialNumber',
        'cfg-cam-exposure': 'ExposureTime',
        'cfg-cam-gain': 'GainRaw',
        'cfg-logic-target-label': 'TargetLabel',
        'cfg-logic-target-count': 'TargetCount',
        'cfg-logic-retry-count': 'MaxRetryCount',
        'cfg-logic-retry-interval': 'RetryIntervalMs',
        'cfg-yolo-gpu': 'EnableGpu'
    };

    const data = {};
    const numericFields = ['PlcPort', 'PlcTriggerAddress', 'PlcResultAddress', 'ExposureTime', 'GainRaw', 'TargetCount', 'MaxRetryCount', 'RetryIntervalMs'];

    for (const [inputId, propName] of Object.entries(fieldMapping)) {
        const el = document.getElementById(inputId);
        if (!el) continue;

        if (el.type === 'checkbox') {
            data[propName] = el.checked;
        } else if (numericFields.includes(propName) || el.type === 'number') {
            const numVal = parseFloat(el.value);
            data[propName] = isNaN(numVal) ? 0 : numVal;
        } else {
            data[propName] = el.value || '';
        }
    }

    // Task Type
    const tt = document.getElementById('task-type-select');
    if (tt) data['TaskType'] = parseInt(tt.value);

    sendCommand('save_settings', data);
    closeSettingsModal();
}
window.saveSettings = saveSettings;

function openPasswordModal() {
    document.getElementById('password-modal').classList.remove('hidden');
    const el = document.getElementById('admin-password');
    if (el) { el.value = ''; el.focus(); }
}
window.openPasswordModal = openPasswordModal;
window.showPasswordModal = openPasswordModal; // Alias for HTML onclick

function checkPassword() { // Renamed from verifyPassword to match some usages, or ensure consistency
    const pwd = document.getElementById('admin-password').value;
    sendCommand('verify_password', pwd);
    document.getElementById('admin-password').value = '';
}
window.verifyPassword = checkPassword; // Alias

function closePasswordModal() { document.getElementById('password-modal').classList.add('hidden'); }
window.closePasswordModal = closePasswordModal;

function openLogHistoryModal() {
    document.getElementById('log-history-modal').classList.remove('hidden');
    sendCommand('get_detection_logs');
}
window.openLogHistoryModal = openLogHistoryModal;
window.closeLogHistoryModal = () => document.getElementById('log-history-modal').classList.add('hidden');

function openGalleryModal() {
    document.getElementById('gallery-modal').classList.remove('hidden');
    sendCommand('get_ng_dates');
}
window.openGalleryModal = openGalleryModal;
window.closeGalleryModal = () => document.getElementById('gallery-modal').classList.add('hidden');

function openStatisticsHistoryModal() {
    document.getElementById('statistics-history-modal').classList.remove('hidden');
    requestStatisticsHistory(30);
}
window.openStatisticsHistoryModal = openStatisticsHistoryModal;
window.closeStatisticsHistoryModal = () => document.getElementById('statistics-history-modal').classList.add('hidden');

function requestStatisticsHistory(days) {
    if (days) {
        document.querySelectorAll('.stat-tab').forEach(b => {
            b.classList.remove('bg-celadon-100', 'text-celadon-700');
            b.classList.add('text-slate-500', 'hover:bg-slate-50');
        });
        // Find button logic omitted for brevity
    }
    document.getElementById('statistics-history-table').innerHTML = '<tr><td colspan="5" class="text-center py-8">加载中...</td></tr>';
    sendCommand('get_statistics_history', days);
}
window.requestStatisticsHistory = requestStatisticsHistory;

window.closeImageViewer = () => document.getElementById('image-viewer').classList.add('hidden');

// --- Template Manager & Cropper ---

let currentOperatorIndex = -1;

function openTemplateManager(opIndex, opName) {
    currentOperatorIndex = opIndex;
    const el = document.getElementById('tm-op-name');
    if (el) el.innerText = opName || (opIndex !== undefined ? `算子 #${opIndex + 1}` : '模板编辑');
    document.getElementById('template-manager-modal').classList.remove('hidden');
    tmResetUI();
    sendCommand('get_operator_template', opIndex);
}
window.openTemplateManager = openTemplateManager;

function closeTemplateManager() {
    document.getElementById('template-manager-modal').classList.add('hidden');
    if (tmCropper) { tmCropper.destroy(); tmCropper = null; }
}
window.closeTemplateManager = closeTemplateManager;

function tmResetUI() {
    console.log('[tmResetUI] Resetting template UI');

    // 销毁 Cropper 实例
    if (tmCropper) {
        tmCropper.destroy();
        tmCropper = null;
    }

    // 重置界面元素
    const container = document.getElementById('tm-editor-container');
    const placeholder = document.getElementById('tm-placeholder');
    const img = document.getElementById('tm-image');

    if (container) container.classList.add('hidden');
    if (placeholder) placeholder.classList.remove('hidden');
    if (img) img.src = '';

    const statusEl = document.getElementById('tm-status');
    if (statusEl) statusEl.innerText = '就绪';

    console.log('[tmResetUI] Reset complete');
}
window.tmResetUI = tmResetUI;

function captureTemplateFrame() { sendCommand('capture_for_template', currentOperatorIndex); }
window.captureTemplateFrame = captureTemplateFrame;

function initTmCropper(imageSrc) {
    console.log('[initTmCropper] Called with imageSrc length:', imageSrc?.length);

    const container = document.getElementById('tm-editor-container');
    const img = document.getElementById('tm-image');
    const placeholder = document.getElementById('tm-placeholder');

    if (!container || !img) {
        console.error('[initTmCropper] Required elements not found! container:', container, 'img:', img);
        addLog('模板编辑器元素未找到', 'error');
        return;
    }

    console.log('[initTmCropper] Elements found, setting up image');

    // 隐藏占位符，显示容器
    if (placeholder) placeholder.classList.add('hidden');
    container.classList.remove('hidden');

    // 设置图片源
    img.src = imageSrc;

    // 销毁旧的 Cropper 实例
    if (tmCropper) {
        console.log('[initTmCropper] Destroying old cropper');
        tmCropper.destroy();
        tmCropper = null;
    }

    // 初始化新的 Cropper
    if (window.Cropper) {
        console.log('[initTmCropper] Initializing new Cropper instance');
        tmCropper = new Cropper(img, {
            viewMode: 1,
            dragMode: 'move',
            autoCropArea: 0.5,
            restore: false,
            guides: true,
            center: true,
            highlight: false,
            cropBoxMovable: true,
            cropBoxResizable: true,
            toggleDragModeOnDblclick: false,
        });
        const statusEl = document.getElementById('tm-status');
        if (statusEl) statusEl.innerText = '正在编辑模板区域...';
        console.log('[initTmCropper] Cropper initialized successfully');
    } else {
        console.error('[initTmCropper] Cropper.js not loaded!');
        alert("Cropper.js 未加载");
    }
}
window.initTmCropper = initTmCropper;

function loadTemplateFile(fileInput) {
    console.log('[loadTemplateFile] Called, fileInput:', fileInput);

    // If called without argument (from button click), just trigger the file picker
    if (!fileInput || !fileInput.files) {
        console.log('[loadTemplateFile] No fileInput, triggering file picker');
        document.getElementById('tm-file-input').click();
        return;
    }

    // Handle file selection from onchange event
    const file = fileInput.files[0];
    console.log('[loadTemplateFile] File selected:', file);
    console.log('[loadTemplateFile] File details - name:', file.name, 'size:', file.size, 'type:', file.type);

    if (!file) {
        console.log('[loadTemplateFile] No file in files array');
        return;
    }

    addLog(`正在读取文件: ${file.name}...`);
    console.log('[loadTemplateFile] Creating FileReader...');

    const reader = new FileReader();
    console.log('[loadTemplateFile] FileReader created:', reader);

    // 添加所有可能的事件监听
    reader.onloadstart = function (e) {
        console.log('[loadTemplateFile] FileReader onloadstart');
    };

    reader.onprogress = function (e) {
        console.log('[loadTemplateFile] FileReader onprogress:', e.loaded, '/', e.total);
    };

    reader.onload = function (e) {
        console.log('[loadTemplateFile] FileReader onload triggered!');
        console.log('[loadTemplateFile] Result length:', e.target.result?.length);
        const base64 = e.target.result;

        if (!base64) {
            console.error('[loadTemplateFile] base64 is null or empty!');
            addLog('读取结果为空', 'error');
            return;
        }

        // 直接初始化裁剪器，initTmCropper 会负责创建 DOM 结构
        console.log('[loadTemplateFile] Calling initTmCropper with base64 length:', base64.length);
        try {
            initTmCropper(base64);
            console.log('[loadTemplateFile] initTmCropper completed');
        } catch (err) {
            console.error('[loadTemplateFile] initTmCropper error:', err);
            addLog('初始化裁剪器失败: ' + err.message, 'error');
            return;
        }

        // 更新状态提示
        const statusEl = document.getElementById('tm-status');
        if (statusEl) statusEl.innerText = '图片已加载，请调整裁剪区域';

        // 隐藏占位符（如果存在）
        const placeholder = document.getElementById('tm-placeholder');
        if (placeholder) placeholder.classList.add('hidden');

        addLog('✓ 本地模板图片已加载');

        // Reset file input so same file can be selected again
        fileInput.value = '';
    };

    reader.onerror = function (err) {
        console.error('[loadTemplateFile] FileReader onerror:', err);
        console.error('[loadTemplateFile] Error details:', reader.error);
        addLog('读取图片文件失败: ' + (reader.error?.message || '未知错误'), 'error');
        fileInput.value = '';
    };

    reader.onloadend = function (e) {
        console.log('[loadTemplateFile] FileReader onloadend, readyState:', reader.readyState);
    };

    reader.onabort = function (e) {
        console.log('[loadTemplateFile] FileReader onabort!');
        addLog('文件读取被中止', 'error');
    };

    console.log('[loadTemplateFile] Starting readAsDataURL...');
    try {
        reader.readAsDataURL(file);
        console.log('[loadTemplateFile] readAsDataURL called, readyState:', reader.readyState);
    } catch (err) {
        console.error('[loadTemplateFile] readAsDataURL exception:', err);
        addLog('启动文件读取失败: ' + err.message, 'error');
    }
}
window.loadTemplateFile = loadTemplateFile;

function tmRotate(deg) { if (tmCropper) tmCropper.rotate(deg); }
window.tmRotate = tmRotate;
function tmReset() { if (tmCropper) tmCropper.reset(); }
window.tmReset = tmReset;

function saveTemplate() {
    console.log('[saveTemplate] Called, tmCropper:', tmCropper);
    console.log('[saveTemplate] currentOperatorIndex:', currentOperatorIndex);

    if (!tmCropper) {
        console.error('[saveTemplate] tmCropper is null!');
        addLog('裁剪器未初始化', 'error');
        return;
    }

    try {
        const canvas = tmCropper.getCroppedCanvas();
        console.log('[saveTemplate] Got canvas:', canvas);

        if (!canvas) {
            console.error('[saveTemplate] getCroppedCanvas returned null');
            addLog('获取裁剪画布失败', 'error');
            return;
        }

        const base64 = canvas.toDataURL('image/jpeg');
        console.log('[saveTemplate] base64 length:', base64.length);

        // 发送训练命令到后端
        const imageData = base64.split(',')[1];
        console.log('[saveTemplate] Sending train_operator command, instanceId:', currentOperatorIndex, 'imageData length:', imageData?.length);

        sendCommand('train_operator', {
            instanceId: currentOperatorIndex,
            imageBase64: imageData
        });

        const statusEl = document.getElementById('tm-status');
        if (statusEl) statusEl.innerText = '模板已保存，正在训练...';

        addLog('✓ 模板已提交训练');

        // 关闭弹窗
        setTimeout(() => {
            closeTemplateManager();
        }, 500);

    } catch (err) {
        console.error('[saveTemplate] Error:', err);
        addLog('保存模板失败: ' + err.message, 'error');
    }
}
window.saveTemplate = saveTemplate;


// --- ROI Canvas Logic ---

function initRoiInteractions() {
    roiCanvas = document.getElementById('roi-canvas');
    if (!roiCanvas) return;

    const img = document.getElementById('camera-view');
    const container = document.getElementById('camera-container');

    function updateCanvasLayout() {
        if (!img) return;
        const iW = img.naturalWidth || img.width || 1280;
        const iH = img.naturalHeight || img.height || 720;
        if (iW === 0) return;

        const containerRect = container.getBoundingClientRect();
        const containerW = containerRect.width;
        const containerH = containerRect.height;
        const containerRatio = containerW / containerH;
        const imgRatio = iW / iH;

        let renderedW, renderedH, offsetX, offsetY;

        if (containerRatio > imgRatio) {
            renderedH = containerH;
            renderedW = containerH * imgRatio;
            offsetX = (containerW - renderedW) / 2;
            offsetY = 0;
        } else {
            renderedW = containerW;
            renderedH = containerW / imgRatio;
            offsetX = 0;
            offsetY = (containerH - renderedH) / 2;
        }

        roiCanvas.style.width = `${renderedW}px`;
        roiCanvas.style.height = `${renderedH}px`;
        roiCanvas.style.left = `${offsetX}px`;
        roiCanvas.style.top = `${offsetY}px`;
        roiCanvas.width = renderedW;
        roiCanvas.height = renderedH;
    }

    const resizeObserver = new ResizeObserver(() => requestAnimationFrame(updateCanvasLayout));
    if (container) resizeObserver.observe(container);
    if (img) img.addEventListener('load', updateCanvasLayout);
    window.addEventListener('resize', updateCanvasLayout);
    setTimeout(updateCanvasLayout, 100);

    roiCanvas.addEventListener('mousedown', (e) => {
        isDrawingROI = true;
        const rect = roiCanvas.getBoundingClientRect();
        roiStartX = e.clientX - rect.left;
        roiStartY = e.clientY - rect.top;
    });

    roiCanvas.addEventListener('mousemove', (e) => {
        if (!isDrawingROI) return;
        const rect = roiCanvas.getBoundingClientRect();
        const currentX = e.clientX - rect.left;
        const currentY = e.clientY - rect.top;
        const ctx = roiCanvas.getContext('2d');
        ctx.clearRect(0, 0, roiCanvas.width, roiCanvas.height);

        const width = currentX - roiStartX;
        const height = currentY - roiStartY;
        ctx.strokeStyle = '#a4161a';
        ctx.lineWidth = 2;
        ctx.setLineDash([8, 4]);
        ctx.strokeRect(roiStartX, roiStartY, width, height);
        ctx.fillStyle = 'rgba(164, 22, 26, 0.05)';
        ctx.fillRect(roiStartX, roiStartY, width, height);
    });

    roiCanvas.addEventListener('mouseup', (e) => {
        if (!isDrawingROI) return;
        isDrawingROI = false;
        const rect = roiCanvas.getBoundingClientRect();
        const currentX = e.clientX - rect.left;
        const currentY = e.clientY - rect.top;
        let x = Math.min(roiStartX, currentX);
        let y = Math.min(roiStartY, currentY);
        let w = Math.abs(currentX - roiStartX);
        let h = Math.abs(currentY - roiStartY);

        if (w < 10 || h < 10) return;
        const normX = x / roiCanvas.width;
        const normY = y / roiCanvas.height;
        const normW = w / roiCanvas.width;
        const normH = h / roiCanvas.height;

        sendCommand('update_roi', { rect: [normX, normY, normW, normH] });
        addLog(`ROI Set: [${normX.toFixed(2)}, ${normY.toFixed(2)}, ${normW.toFixed(2)}, ${normH.toFixed(2)}]`);
    });

    roiCanvas.addEventListener('mouseleave', () => { isDrawingROI = false; });
}

function clearRoi() {
    const canvas = document.getElementById('roi-canvas');
    if (canvas) {
        const ctx = canvas.getContext('2d');
        ctx.clearRect(0, 0, canvas.width, canvas.height);
    }
    sendCommand('update_roi', { rect: [0, 0, 0, 0] });
    addLog("ROI Cleared");
}
window.clearRoi = clearRoi;


// --- Initalization ---

document.addEventListener('DOMContentLoaded', () => {
    // initialize ROI
    initRoiInteractions();

    // initialize File Input


    // Initialize Charts
    const ctx = document.getElementById('inferenceChart');
    if (ctx && window.Chart) {
        window.inferenceChart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: Array(60).fill(''),
                datasets: [{
                    label: 'FPS', data: Array(60).fill(0),
                    borderColor: '#06b6d4', borderWidth: 1, tension: 0.4, pointRadius: 0
                }]
            },
            options: {
                responsive: true, maintainAspectRatio: false, animation: false,
                plugins: { legend: { display: false } },
                scales: { x: { display: false }, y: { display: true, beginAtZero: true, suggestedMax: 30 } }
            }
        });
    }

    const statsCtx = document.getElementById('statsChart');
    if (statsCtx && window.Chart) {
        window.statsChart = new Chart(statsCtx, {
            type: 'doughnut',
            data: {
                labels: ['OK', 'NG'],
                datasets: [{
                    data: [0, 0], backgroundColor: ['#22c55e', '#ef4444'], borderWidth: 0
                }]
            },
            options: {
                responsive: true, maintainAspectRatio: false, cutout: '70%',
                plugins: { legend: { display: false } }
            }
        });
    }

    // Signal Ready
    setTimeout(() => sendCommand('app_ready'), 500);
});

// --- Cropper Modal Logic (Legacy) ---
let cropper = null;

function openCropper(imageBase64) {
    const modal = document.getElementById('cropModal');
    const image = document.getElementById('cropImage');
    if (!modal || !image) return;

    image.src = `data:image/jpeg;base64,${imageBase64}`;
    modal.classList.remove('hidden');

    if (cropper) cropper.destroy();

    if (window.Cropper) {
        cropper = new Cropper(image, {
            aspectRatio: 1,
            viewMode: 1,
            dragMode: 'move',
            autoCropArea: 0.8,
            restore: false,
            guides: true,
            center: true,
            highlight: false,
            cropBoxMovable: true,
            cropBoxResizable: true,
            toggleDragModeOnDblclick: false,
            preview: document.getElementById('cropPreview'),
            zoomable: false,
        });
    }
}
window.openCropper = openCropper;

function closeCropper() {
    const modal = document.getElementById('cropModal');
    if (modal) modal.classList.add('hidden');
    if (cropper) {
        cropper.destroy();
        cropper = null;
    }
}
window.closeCropper = closeCropper;

function confirmCrop() {
    if (!cropper) return;
    const data = cropper.getData();
    sendCommand('save_cropped_template_data', JSON.stringify(data));
    closeCropper();
    addLog('正在后台裁剪高分辨率模板...');
}
window.confirmCrop = confirmCrop;

function captureTemplate() {
    sendCommand('get_frame_for_template');
    addLog('从相机截取模板...');
}
window.captureTemplate = captureTemplate;
