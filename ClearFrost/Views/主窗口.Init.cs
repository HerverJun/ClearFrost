using MVSDK_Net;
using ClearFrost.Config;
using ClearFrost.Models;
using ClearFrost.Hardware;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System.IO;
using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClearFrost.Yolo;
using ClearFrost.Vision;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    public partial class 主窗口
    {
        #region 2. 初始化与生命周期 (Initialization)

        private void RegisterEvents()
        {
            // PLC 服务事件
            _plcService.ConnectionChanged += (connected) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.UpdateConnection("plc", connected), "更新PLC状态");
                    SafeFireAndForget(_uiController.LogToFrontend(
                        connected ? $"PLC: 已连接 ({_plcService.ProtocolName})" : "PLC: 已断开",
                        connected ? "success" : "error"), "PLC状态日志");
                });
            };
            _plcService.TriggerReceived += () =>
            {
                Debug.WriteLine($"[主窗口] 📥 收到PLC触发事件 - {DateTime.Now:HH:mm:ss.fff}");
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.FlashPlcTrigger(), "PLC触发指示灯");
                });

                if (!_plcTriggerQueue.Writer.TryWrite(DateTime.Now))
                {
                    SafeFireAndForget(_uiController.LogToFrontend("PLC触发队列已满，已丢弃最旧触发", "warning"), "PLC触发队列告警");
                }
            };
            _plcService.ErrorOccurred += (error) =>
            {
                SafeFireAndForget(_uiController.LogToFrontend($"PLC错误: {error}", "error"), "PLC错误日志");
            };

            StartPlcTriggerConsumer();

            // Detection 服务事件
            _detectionService.DetectionCompleted += (result) =>
            {
                // 检测完成后的 UI 更新
                SafeFireAndForget(_uiController.LogToFrontend(
                    $"检测完成: {(result.IsQualified ? "合格" : "不合格")} ({result.ElapsedMs}ms)",
                    result.IsQualified ? "success" : "error"), "检测结果日志");
            };
            _detectionService.ModelLoaded += (modelName) =>
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型已加载: {modelName}", "success"), "模型加载日志");
            };
            _detectionService.ErrorOccurred += (error) =>
            {
                SafeFireAndForget(_uiController.LogToFrontend($"检测错误: {error}", "error"), "检测错误日志");
            };

            // Statistics 服务事件
            _statisticsService.StatisticsUpdated += (snapshot) =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.UpdateUI(snapshot.TotalCount, snapshot.QualifiedCount, snapshot.UnqualifiedCount), "统计更新");
                });
            };
            _statisticsService.DayReset += () =>
            {
                InvokeOnUIThread(() =>
                {
                    SafeFireAndForget(_uiController.LogToFrontend("检测到跨日，统计已自动重置", "info"), "跨日重置日志");
                });
            };

            // 订阅退出事件
            _uiController.OnExitApp += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    // 停止所有后台任务
                    this.停止 = true;
                    // 保存配置
                    _appConfig?.Save();
                    // 强制退出
                    Application.Exit();
                });
            };

            // 订阅最小化事件
            _uiController.OnMinimizeApp += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    this.WindowState = FormWindowState.Minimized;
                });
            };

            // 订阅最大化/还原事
            _uiController.OnToggleMaximize += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    if (this.WindowState == FormWindowState.Maximized)
                        this.WindowState = FormWindowState.Normal;
                    else
                        this.WindowState = FormWindowState.Maximized;
                });
            };

            // 订阅拖动窗口事件
            _uiController.OnStartDrag += (s, e) =>
            {
                this.Invoke((MethodInvoker)delegate
                {
                    WindowHelpers.StartWindowDrag(this);
                });
            };

            // 绑定 WebUI 事件
            _uiController.OnOpenCamera += (s, e) => SafeFireAndForget(btnOpenCamera_LogicAsync(), "打开相机");
            _uiController.OnManualDetect += (s, e) => InvokeOnUIThread(() => SafeFireAndForget(btnCapture_LogicAsync(), "手动检测"));
            _uiController.OnManualRelease += (s, e) => SafeFireAndForget(fx_btn_LogicAsync(), "手动放行"); // Async void handler
            _uiController.OnOpenSettings += (s, e) => InvokeOnUIThread(() => btnSettings_Logic());
            _uiController.OnChangeModel += (s, modelName) => InvokeOnUIThread(() => ChangeModel_Logic(modelName));
            _uiController.OnConnectPlc += (s, e) => SafeFireAndForget(ConnectPlcViaServiceAsync(), "PLC手动连接");
            _uiController.OnThresholdChanged += (s, val) =>
            {
                overlapThreshold = val / 100f;
            };
            _uiController.OnGetStatisticsHistory += async (s, e) =>
            {
                // 使用 StatisticsService 获取底层数据
                var stats = ((StatisticsService)_statisticsService).GetDetectionStats();
                var history = ((StatisticsService)_statisticsService).GetStatisticsHistory();
                await _uiController.SendStatisticsHistory(history, stats);
            };
            _uiController.OnClearStatisticsHistory += async (s, e) =>
            {
                _statisticsService.ClearHistory();
                // 刷新历史记录及图表
                var stats = ((StatisticsService)_statisticsService).GetDetectionStats();
                var history = ((StatisticsService)_statisticsService).GetStatisticsHistory();
                await _uiController.SendStatisticsHistory(history, stats);
                await _uiController.LogToFrontend("✅ 历史统计数据已清空", "success");
            };
            _uiController.OnResetStatistics += async (s, e) =>
            {
                _statisticsService.ResetToday();
                await _uiController.UpdateUI(0, 0, 0);
                await _uiController.LogToFrontend("✅ 今日统计已清除", "success");
            };

            // ================== 模板管理器事件 ==================
            _uiController.OnGetFrameForTemplate += async (s, e) =>
            {
                Mat? frameClone = null;
                lock (_frameLock)
                {
                    if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                    {
                        frameClone = _lastCapturedFrame.Clone();
                    }
                }

                if (frameClone != null)
                {
                    try
                    {
                        using var clone = frameClone;
                        // 缩放以加快传输
                        int maxDim = 1200;
                        if (clone.Width > maxDim || clone.Height > maxDim)
                        {
                            double scale = Math.Min((double)maxDim / clone.Width, (double)maxDim / clone.Height);
                            Cv2.Resize(clone, clone, new OpenCvSharp.Size(0, 0), scale, scale);
                        }

                        using var bitmap = clone.ToBitmap();
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, ImageFormat.Jpeg);
                        string base64 = Convert.ToBase64String(ms.ToArray());

                        await _uiController.ReceiveTemplateFrame(base64);
                    }
                    catch (Exception ex)
                    {
                        await _uiController.LogToFrontend($"获取模板帧失败: {ex.Message}", "error");
                    }
                }
                else
                {
                    await _uiController.LogToFrontend("请先打开相机并确保有画面", "warning");
                }
            };

            _uiController.OnTrainOperator += async (s, request) =>
            {
                if (_pipelineProcessor == null) return;

                try
                {
                    var opNode = _pipelineProcessor.GetOperator(request.InstanceId);
                    if (opNode == null)
                    {
                        await _uiController.LogToFrontend($"找不到算子 InstanceId={request.InstanceId}", "error");
                        return;
                    }

                    byte[] imageBytes = Convert.FromBase64String(request.ImageBase64);
                    using var mat = Cv2.ImDecode(imageBytes, ImreadModes.Color);
                    if (mat.Empty()) throw new Exception("解码图像为空");

                    if (opNode.Operator is ITemplateTrainable trainable)
                    {
                        // 统一保存模板图像到本地作为备份
                        string templateDir = Path.Combine(BaseStoragePath, "Templates");
                        if (!Directory.Exists(templateDir)) Directory.CreateDirectory(templateDir);
                        string templatePath = Path.Combine(templateDir, $"template_{request.InstanceId}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                        Cv2.ImWrite(templatePath, mat);

                        // 训练/设置模板
                        // 1. 先更新 templatePath (避免 FeatureMatchOp 等算子因为设置路径而清空内存中的模板)
                        if (opNode.Operator is IImageOperator op && op.Parameters.ContainsKey("templatePath"))
                        {
                            op.SetParameter("templatePath", templatePath);
                        }

                        // 2. 训练/设置模板 (确保这是最后一步，保证 _templateImage 会被正确赋值且 IsTrained 为 true)
                        trainable.SetTemplateFromMat(mat);

                        await _uiController.LogToFrontend($"✅ 算子 [{opNode.Operator.Name}] 模板已更新并训练");
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"ℹ️ 算子 [{opNode.Operator.Name}] 不支持模板训练", "warning");
                    }

                    // 刷新UI参数（通知前端更新 isTrained 状态）
                    // 更新配置并刷新UI
                    var config = _pipelineProcessor.ExportConfig();
                    _appConfig.VisionPipelineJson = JsonSerializer.Serialize(config);
                    _appConfig.Save();

                    await _uiController.SendPipelineUpdated(config);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"训练失败: {ex.Message}", "error");
                }
            };

            // ================== 传统视觉事件订阅 ==================
            _uiController.OnVisionModeChanged += async (s, mode) =>
            {
                _appConfig.VisionMode = mode;
                _appConfig.Save();
                await _uiController.LogToFrontend($"视觉模式切换为: {(mode == 0 ? "YOLO" : "传统视觉")}");

                // 初始化传统视觉流程处理器
                if (mode == 1 && _pipelineProcessor == null)
                {
                    _pipelineProcessor = new PipelineProcessor();
                    // 尝试从配置加载
                    if (!string.IsNullOrEmpty(_appConfig.VisionPipelineJson) && _appConfig.VisionPipelineJson != "[]")
                    {
                        try
                        {
                            var config = JsonSerializer.Deserialize<VisionConfig>(_appConfig.VisionPipelineJson);
                            if (config != null)
                            {
                                try
                                {
                                    _pipelineProcessor.ImportConfig(config);
                                }
                                catch (Exception ex)
                                {
                                    await _uiController.LogToFrontend($"流程加载失败: {ex.Message}", "error");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[主窗口] Pipeline init error: {ex.Message}");
                        }
                    }
                }
            };

            _uiController.OnGetVisionConfig += async (s, e) =>
            {
                if (_pipelineProcessor == null) _pipelineProcessor = new PipelineProcessor();

                var config = _pipelineProcessor.ExportConfig();
                config.TemplatePath = _appConfig.TemplateImagePath;
                config.TemplateThreshold = _appConfig.TemplateThreshold;

                // Build OperatorParameters dictionary from each operator's GetParameterInfo()
                var operatorParams = new Dictionary<string, List<OperatorParameterInfo>>();
                foreach (var opNode in _pipelineProcessor.Operators)
                {
                    try
                    {
                        var paramInfo = opNode.Operator.GetParameterInfo();
                        operatorParams[opNode.InstanceId] = paramInfo;
                    }
                    catch (Exception ex) { Debug.WriteLine($"[主窗口] GetParameterInfo failed for {opNode.InstanceId}: {ex.Message}"); }
                }

                var response = new VisionConfigResponse
                {
                    Config = config,
                    AvailableOperators = OperatorFactory.GetAvailableOperators(),
                    OperatorParameters = operatorParams
                };
                await _uiController.SendVisionConfig(response);
            };

            _uiController.OnPipelineUpdate += async (s, request) =>
            {
                if (_pipelineProcessor == null) _pipelineProcessor = new PipelineProcessor();

                try
                {
                    switch (request.Action?.ToLower())
                    {
                        case "add":
                            await _uiController.LogToFrontend($"[DEBUG] 准备添加算子, TypeId={request.TypeId}");
                            await _uiController.LogToFrontend($"[DEBUG] 添加前算子数: {_pipelineProcessor.Operators.Count}");
                            var newOp = OperatorFactory.Create(request.TypeId ?? "");
                            if (newOp != null)
                            {
                                // 如果是匹配算子，自动设置当前的模板路径
                                if (!string.IsNullOrEmpty(_appConfig.TemplateImagePath))
                                {
                                    if (newOp is TemplateMatchOp tmOp)
                                    {
                                        tmOp.SetParameter("templatePath", _appConfig.TemplateImagePath);
                                    }
                                    else if (newOp is FeatureMatchOp fmOp)
                                    {
                                        fmOp.SetParameter("templatePath", _appConfig.TemplateImagePath);
                                    }
                                }
                                var instanceId = _pipelineProcessor.AddOperator(newOp);
                                await _uiController.LogToFrontend($"[DEBUG] 添加后算子数: {_pipelineProcessor.Operators.Count}, InstanceId={instanceId}");
                                await _uiController.LogToFrontend($"✅ 已添加算子: {newOp.Name}");
                            }
                            else
                            {
                                await _uiController.LogToFrontend($"[DEBUG] OperatorFactory.Create 返回 null, TypeId={request.TypeId}", "error");
                            }
                            break;
                        case "remove":
                            if (_pipelineProcessor.RemoveOperator(request.InstanceId ?? ""))
                            {
                                await _uiController.LogToFrontend($"✅ 已移除算子");
                            }
                            break;
                        case "update":
                            if (!string.IsNullOrEmpty(request.InstanceId) && !string.IsNullOrEmpty(request.ParamName))
                            {
                                // 处理 JsonElement 类型的参数值
                                object actualValue = request.ParamValue ?? 0;
                                if (actualValue is JsonElement jsonElement)
                                {
                                    actualValue = jsonElement.ValueKind switch
                                    {
                                        JsonValueKind.Number => jsonElement.TryGetDouble(out var d) ? d : 0,
                                        JsonValueKind.String => jsonElement.GetString() ?? "",
                                        JsonValueKind.True => true,
                                        JsonValueKind.False => false,
                                        _ => 0
                                    };
                                }
                                _pipelineProcessor.UpdateOperatorParameter(request.InstanceId, request.ParamName, actualValue);
                            }
                            break;
                    }

                    // 保存配置
                    var config = _pipelineProcessor.ExportConfig();
                    _appConfig.VisionPipelineJson = JsonSerializer.Serialize(config);
                    _appConfig.Save();

                    // 调试日志
                    await _uiController.LogToFrontend($"[DEBUG] 处理器中有 {_pipelineProcessor.Operators.Count} 个算子");
                    await _uiController.LogToFrontend($"[DEBUG] 导出配置有 {config.Operators.Count} 个算子");

                    // 发送更新后的配置
                    await _uiController.SendPipelineUpdated(config);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"流程更新失败: {ex.Message}", "error");
                }
            };

            _uiController.OnGetPreview += async (s, e) =>
            {
                Mat? frameClone = null;
                lock (_frameLock)
                {
                    if (_pipelineProcessor != null && _lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                    {
                        frameClone = _lastCapturedFrame.Clone();
                    }
                }

                if (_pipelineProcessor == null || frameClone == null)
                {
                    await _uiController.LogToFrontend("无可用图像进行预览", "warning");
                    return;
                }

                try
                {
                    using var inputFrame = frameClone;
                    var sw = Stopwatch.StartNew();
                    using var preview = await _pipelineProcessor.GetPreviewAsync(inputFrame);
                    sw.Stop();

                    // 转换为 Base64
                    using var bitmap = preview.ToBitmap();
                    using var ms = new MemoryStream();
                    bitmap.Save(ms, ImageFormat.Jpeg);
                    string base64 = Convert.ToBase64String(ms.ToArray());

                    var response = new PreviewResponse
                    {
                        ImageBase64 = base64,
                        ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                    };
                    await _uiController.SendPreviewImage(response);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"预览失败: {ex.Message}", "error");
                }
            };

            _uiController.OnUploadTemplate += async (s, action) =>
            {
                if (action == "select")
                {
                    // 文件选择对话框
                    InvokeOnUIThread(async () =>
                    {
                        using var ofd = new OpenFileDialog();
                        ofd.Filter = "图像文件|*.jpg;*.jpeg;*.png;*.bmp";
                        ofd.Title = "选择模板图像";

                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            _appConfig.TemplateImagePath = ofd.FileName;
                            _appConfig.Save();

                            // 更新所有 TemplateMatchOp 和 FeatureMatchOp 算子
                            if (_pipelineProcessor != null)
                            {
                                foreach (var op in _pipelineProcessor.Operators)
                                {
                                    if (op.Operator is TemplateMatchOp tmOp)
                                    {
                                        tmOp.SetParameter("templatePath", ofd.FileName);
                                    }
                                    else if (op.Operator is FeatureMatchOp fmOp)
                                    {
                                        fmOp.SetParameter("templatePath", ofd.FileName);
                                    }
                                    else if (op.Operator is OrbMatchOp omOp)
                                    {
                                        omOp.SetParameter("templatePath", ofd.FileName);
                                    }
                                }
                                var config = _pipelineProcessor.ExportConfig();
                                _appConfig.VisionPipelineJson = JsonSerializer.Serialize(config);
                                _appConfig.Save();
                            }

                            await _uiController.LogToFrontend($"✅ 模板已加载: {Path.GetFileName(ofd.FileName)}");

                            // 发送模板预览到前端
                            try
                            {
                                using var templateMat = Cv2.ImRead(ofd.FileName, ImreadModes.Color);
                                if (!templateMat.Empty())
                                {
                                    // 缩放到合适大小
                                    using var resized = new Mat();
                                    double scale = Math.Min(128.0 / templateMat.Width, 128.0 / templateMat.Height);
                                    Cv2.Resize(templateMat, resized, new OpenCvSharp.Size(0, 0), scale, scale);

                                    using var bitmap = resized.ToBitmap();
                                    using var ms = new MemoryStream();
                                    bitmap.Save(ms, ImageFormat.Jpeg);
                                    string base64 = Convert.ToBase64String(ms.ToArray());
                                    await _uiController.ExecuteScriptAsync($"updateTemplatePreview('{base64}')");
                                }
                            }
                            catch (Exception ex) { Debug.WriteLine($"[主窗口] Template preview update failed: {ex.Message}"); }
                        }
                    });
                }
                else if (action == "capture")
                {
                    // 从当前帧截取 -> 打开前端裁剪弹窗
                    Mat? frameClone = null;
                    lock (_frameLock)
                    {
                        if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                        {
                            frameClone = _lastCapturedFrame.Clone();
                        }
                    }

                    if (frameClone != null)
                    {
                        try
                        {
                            using var clone = frameClone;
                            // 缩小及计算比例
                            int targetWidth = 1200;
                            double scale = 1.0;
                            Mat displayMat = clone;
                            using Mat? resizedMat = clone.Width > targetWidth ? new Mat() : null;
                            if (resizedMat != null)
                            {
                                scale = (double)targetWidth / clone.Width;
                                Cv2.Resize(clone, resizedMat, new OpenCvSharp.Size(0, 0), scale, scale);
                                _currentCropScale = scale;
                                displayMat = resizedMat;
                            }
                            else
                            {
                                _currentCropScale = 1.0;
                            }

                            using var bitmap = displayMat.ToBitmap();
                            using var ms = new MemoryStream();
                            bitmap.Save(ms, ImageFormat.Jpeg);
                            string base64 = Convert.ToBase64String(ms.ToArray());

                            // 调用前端 openCropper
                            await _uiController.ExecuteScriptAsync($"openCropper('{base64}')");
                            await _uiController.LogToFrontend("请在弹窗中裁剪模板区域", "info");
                        }
                        catch (Exception ex)
                        {
                            await _uiController.LogToFrontend($"打开裁剪失败: {ex.Message}", "error");
                        }
                    }
                    else
                    {
                        await _uiController.LogToFrontend("请先打开相机并确保有画面", "warning");
                    }
                }
            };

            // 处理裁剪后的模板保存
            _uiController.OnSaveCroppedTemplate += async (s, json) =>
            {
                try
                {
                    // 解析 JSON: {x, y, width, height, rotate, scaleX, scaleY}
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var r = doc.RootElement;
                    double x = r.GetProperty("x").GetDouble();
                    double y = r.GetProperty("y").GetDouble();
                    double w = r.GetProperty("width").GetDouble();
                    double h = r.GetProperty("height").GetDouble();
                    double rotate = 0;
                    if (r.TryGetProperty("rotate", out var rotProp)) rotate = rotProp.GetDouble();

                    Mat? frameClone = null;
                    lock (_frameLock)
                    {
                        if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                        {
                            frameClone = _lastCapturedFrame.Clone();
                        }
                    }

                    if (frameClone != null)
                    {
                        using var clone = frameClone;
                        Mat sourceToCrop = clone;

                        // 1. 处理旋转 (仅支持 90度 整数倍)
                        if (Math.Abs(rotate) > 0.1)
                        {
                            int rot = (int)rotate;
                            RotateFlags? flag = null;
                            if (rot == 90 || rot == -270) flag = RotateFlags.Rotate90Clockwise;
                            else if (rot == -90 || rot == 270) flag = RotateFlags.Rotate90Counterclockwise;
                            else if (rot == 180 || rot == -180) flag = RotateFlags.Rotate180;

                            if (flag.HasValue)
                            {
                                var rotated = new Mat();
                                Cv2.Rotate(sourceToCrop, rotated, flag.Value);
                                // Move ownership
                                if (sourceToCrop != clone) sourceToCrop.Dispose();
                                sourceToCrop = rotated;
                            }
                        }

                        // 2. 映射坐标
                        if (_currentCropScale <= 0) _currentCropScale = 1.0;
                        double realX = x / _currentCropScale;
                        double realY = y / _currentCropScale;
                        double realW = w / _currentCropScale;
                        double realH = h / _currentCropScale;

                        // 3. 安全裁剪
                        int ix = Math.Max(0, (int)realX);
                        int iy = Math.Max(0, (int)realY);
                        int iw = (int)realW;
                        int ih = (int)realH;

                        // Boundary checks
                        if (ix + iw > sourceToCrop.Width) iw = sourceToCrop.Width - ix;
                        if (iy + ih > sourceToCrop.Height) ih = sourceToCrop.Height - iy;

                        if (iw > 0 && ih > 0)
                        {
                            var roi = new Rect(ix, iy, iw, ih);
                            using var cropMat = new Mat(sourceToCrop, roi);

                            if (!cropMat.Empty())
                            {
                                string templateDir = Path.Combine(BaseStoragePath, "Templates");
                                if (!Directory.Exists(templateDir)) Directory.CreateDirectory(templateDir);
                                string templatePath = Path.Combine(templateDir, $"template_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                                Cv2.ImWrite(templatePath, cropMat);

                                _appConfig.TemplateImagePath = templatePath;
                                _appConfig.Save();

                                // Update Ops
                                if (_pipelineProcessor != null)
                                {
                                    foreach (var op in _pipelineProcessor.Operators)
                                    {
                                        if (op.Operator is TemplateMatchOp tmOp)
                                        {
                                            tmOp.SetTemplateFromMat(cropMat);
                                            tmOp.SetParameter("templatePath", templatePath);
                                        }
                                        else if (op.Operator is FeatureMatchOp fmOp)
                                        {
                                            fmOp.SetParameter("templatePath", templatePath);
                                        }
                                    }
                                }

                                await _uiController.LogToFrontend("✅ 高分辨率模板已应用");

                                using var preview = new Mat();
                                double pScale = 128.0 / Math.Max(iw, ih);
                                Cv2.Resize(cropMat, preview, new OpenCvSharp.Size(0, 0), pScale, pScale);
                                using var bmp = preview.ToBitmap();
                                using var msp = new MemoryStream();
                                bmp.Save(msp, ImageFormat.Jpeg);
                                string b64 = Convert.ToBase64String(msp.ToArray());
                                await _uiController.ExecuteScriptAsync($"updateTemplatePreview('{b64}')");
                            }
                        }

                        if (sourceToCrop != clone) sourceToCrop.Dispose();
                    }

                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"保存模板失败: {ex.Message}", "error");
                }
            };

            // ================== 多相机事件 ==================
            _uiController.OnGetCameraList += async (s, e) =>
            {
                var cameras = _appConfig.Cameras.Select(c => new
                {
                    id = c.Id,
                    displayName = c.DisplayName,
                    serialNumber = c.SerialNumber,
                    manufacturer = c.Manufacturer,
                    exposureTime = c.ExposureTime,
                    gain = c.Gain
                }).ToList();

                await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
            };

            _uiController.OnSwitchCamera += async (s, cameraId) =>
            {
                try
                {
                    var prevCam = _cameraManager.ActiveCamera;
                    if (prevCam != null && prevCam.IsOpen)
                    {
                        prevCam.Close();
                    }

                    _cameraManager.ActiveCameraId = cameraId;
                    var newCam = _cameraManager.ActiveCamera;

                    if (newCam != null)
                    {
                        cam = newCam.Camera;
                        _cameraManager.SaveToConfig(_appConfig);
                        _appConfig.Save();

                        await _uiController.LogToFrontend($"✅ 已切换到相机: {newCam.Config.DisplayName}");
                    }
                    else
                    {
                        // 尝试在配置中查找（支持离线切换）
                        var cfgCam = _appConfig.Cameras.FirstOrDefault(c => c.Id == cameraId);
                        if (cfgCam != null)
                        {
                            _appConfig.ActiveCameraId = cameraId;
                            _appConfig.Save();
                            // 虽然离线，但更新了配置，后续点击"连接相机"时会尝试连接此相机
                            await _uiController.LogToFrontend($"ℹ️ 已切换到相机 (未连接): {cfgCam.DisplayName}", "warning");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"切换相机失败: 未找到 {cameraId}", "error");
                        }
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"切换相机错误: {ex.Message}", "error");
                }
            };

            _uiController.OnAddCamera += async (s, json) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var r = doc.RootElement;

                    string displayName = r.TryGetProperty("displayName", out var dn) ? dn.GetString()?.Trim() ?? "" : "";
                    string serialNumber = r.TryGetProperty("serialNumber", out var sn) ? sn.GetString()?.Trim() ?? "" : "";
                    string manufacturer = r.TryGetProperty("manufacturer", out var mf) ? mf.GetString() ?? "Huaray" : "Huaray";
                    double exposure = r.TryGetProperty("exposureTime", out var exp) ? exp.GetDouble() : 50000;
                    double gain = r.TryGetProperty("gain", out var g) ? g.GetDouble() : 1.0;

                    if (string.IsNullOrEmpty(serialNumber))
                    {
                        await _uiController.LogToFrontend("序列号不能为空", "error");
                        return;
                    }

                    // 检查是否已存在（更新）或新增
                    var existing = _appConfig.Cameras.FirstOrDefault(c => c.SerialNumber == serialNumber);
                    if (existing != null)
                    {
                        existing.DisplayName = displayName;
                        existing.Manufacturer = manufacturer;
                        existing.ExposureTime = exposure;
                        existing.Gain = gain;
                        await _uiController.LogToFrontend($"✅ 已更新相机配置: {displayName} ({manufacturer})");
                    }
                    else
                    {
                        var newConfig = new CameraConfig
                        {
                            Id = $"cam_{DateTime.Now:yyyyMMddHHmmss}",
                            SerialNumber = serialNumber,
                            DisplayName = displayName,
                            Manufacturer = manufacturer,
                            ExposureTime = exposure,
                            Gain = gain,
                            IsEnabled = true
                        };
                        _appConfig.Cameras.Add(newConfig);

                        // 尝试添加到相机管理器（可能失败如果相机未连接）
                        bool added = _cameraManager.AddCamera(newConfig);
                        if (added)
                        {
                            await _uiController.LogToFrontend($"✅ 已添加新相机: {displayName} ({manufacturer})");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"ℹ️ 相机配置已保存，但设备未连接或SDK加载失败: {displayName}", "warning");
                        }
                    }

                    _appConfig.Save();

                    // 刷新前端列表
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"添加相机失败: {ex.Message}", "error");
                }
            };

            _uiController.OnDeleteCamera += async (s, cameraId) =>
            {
                try
                {
                    var camToRemove = _appConfig.Cameras.FirstOrDefault(c => c.Id == cameraId);
                    if (camToRemove == null)
                    {
                        await _uiController.LogToFrontend($"未找到相机: {cameraId}", "error");
                        return;
                    }

                    _cameraManager.RemoveCamera(cameraId);
                    _appConfig.Cameras.Remove(camToRemove);
                    _appConfig.Save();

                    await _uiController.LogToFrontend($"? 已删除相机: {camToRemove.DisplayName}");

                    // 刷新前端列表
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"删除相机失败: {ex.Message}", "error");
                }
            };

            // 相机超级搜索 - 发现局域网中所有相机（复用 CameraManager.AddCamera 的枚举逻辑）
            _uiController.OnSuperSearchCameras += async (s, e) =>
            {
                var cameraList = new List<Dictionary<string, string>>();

                try
                {
                    Debug.WriteLine("[超级搜索] 事件触发开始");
                    await _uiController.LogToFrontend("正在搜索局域网中的所有相机...");

                    // 直接调用 SDK（与 CameraManager.AddCamera 完全一致的调用方式）
                    var deviceList = new IMVDefine.IMV_DeviceList();
                    int res = MyCamera.IMV_EnumDevices(ref deviceList, (uint)IMVDefine.IMV_EInterfaceType.interfaceTypeAll);

                    Debug.WriteLine($"[超级搜索] IMV_EnumDevices 返回码: {res}, 设备数: {deviceList.nDevNum}");

                    if (res == IMVDefine.IMV_OK && deviceList.nDevNum > 0)
                    {
                        int structSize = Marshal.SizeOf(typeof(IMVDefine.IMV_DeviceInfo));
                        for (int i = 0; i < (int)deviceList.nDevNum; i++)
                        {
                            try
                            {
                                var info = (IMVDefine.IMV_DeviceInfo)Marshal.PtrToStructure(
                                    deviceList.pDevInfo + structSize * i,
                                    typeof(IMVDefine.IMV_DeviceInfo))!;

                                string sn = info.serialNumber?.Trim() ?? "";
                                Debug.WriteLine($"[超级搜索] 发现设备[{i}]: SN='{sn}'");

                                if (!string.IsNullOrEmpty(sn))
                                {
                                    cameraList.Add(new Dictionary<string, string>
                                    {
                                        ["serialNumber"] = sn,
                                        ["manufacturer"] = "Huaray",
                                        ["model"] = "Huaray Camera",
                                        ["userDefinedName"] = sn,
                                        ["interfaceType"] = "GigE/USB"
                                    });
                                }
                            }
                            catch (Exception innerEx)
                            {
                                Debug.WriteLine($"[超级搜索] 解析设备[{i}]失败: {innerEx.Message}");
                            }
                        }
                    }
                    else if (res != IMVDefine.IMV_OK)
                    {
                        Debug.WriteLine($"[超级搜索] SDK 枚举失败，错误码: {res}");
                    }
                    else
                    {
                        Debug.WriteLine("[超级搜索] 未发现任何设备");
                    }

                    await _uiController.LogToFrontend($"发现 {cameraList.Count} 台相机", cameraList.Count > 0 ? "success" : "warning");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[超级搜索] 异常: {ex}");
                    await _uiController.LogToFrontend($"相机搜索失败: {ex.Message}", "error");
                }

                // 无论成功失败，必须通知前端结束加载状态
                Debug.WriteLine($"[超级搜索] 准备发送 {cameraList.Count} 个结果到前端");
                await _uiController.SendDiscoveredCameras(cameraList);
                Debug.WriteLine("[超级搜索] 完成");
            };

            // 相机超级搜索 (海康) - 使用海康SDK发现所有相机
            _uiController.OnSuperSearchCamerasHik += async (s, e) =>
            {
                try
                {
                    await _uiController.LogToFrontend("正在通过海康SDK搜索局域网相机...");
                    var allCameras = _cameraManager.DiscoverHikvisionCameras();
                    var cameraList = allCameras.Select(c => new
                    {
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        model = c.Model,
                        userDefinedName = c.UserDefinedName,
                        interfaceType = c.InterfaceType
                    }).ToList();
                    await _uiController.SendDiscoveredCameras(cameraList);
                    await _uiController.LogToFrontend($"海康SDK发现 {cameraList.Count} 台相机", cameraList.Count > 0 ? "success" : "warning");
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"海康搜索失败: {ex.Message}", "error");
                }
            };

            // 直接连接相机（无序列号过滤）
            _uiController.OnDirectConnectCamera += async (s, json) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string sn = root.TryGetProperty("serialNumber", out var snEl) ? snEl.GetString()?.Trim() ?? "" : "";
                    string manufacturer = root.TryGetProperty("manufacturer", out var mfEl) ? mfEl.GetString() ?? "" : "";
                    string model = root.TryGetProperty("model", out var mdEl) ? mdEl.GetString() ?? "" : "";
                    string displayName = root.TryGetProperty("userDefinedName", out var dnEl) ? dnEl.GetString() ?? "" : "";

                    if (string.IsNullOrEmpty(sn))
                    {
                        await _uiController.LogToFrontend("相机序列号为空，无法连接", "error");
                        return;
                    }

                    // 创建新相机配置
                    var newConfig = new CameraConfig
                    {
                        Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                        SerialNumber = sn,
                        Manufacturer = manufacturer,
                        DisplayName = string.IsNullOrEmpty(displayName) ? model : displayName,
                        ExposureTime = 10000,
                        Gain = 1.0
                    };

                    bool added = _cameraManager.AddCamera(newConfig);
                    if (added)
                    {
                        _appConfig.Cameras.Add(newConfig);
                        _appConfig.ActiveCameraId = newConfig.Id;
                        _appConfig.Save();

                        // 刷新前端相机列表
                        var cameras = _appConfig.Cameras.Select(c => new
                        {
                            id = c.Id,
                            displayName = c.DisplayName,
                            serialNumber = c.SerialNumber,
                            manufacturer = c.Manufacturer,
                            exposureTime = c.ExposureTime,
                            gain = c.Gain
                        }).ToList();
                        await _uiController.SendCameraList(cameras, _appConfig.ActiveCameraId ?? "");
                        await _uiController.LogToFrontend($"相机 [{newConfig.DisplayName}] 已连接", "success");
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"相机连接失败: {sn}", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"直接连接相机失败: {ex.Message}", "error");
                }
            };

            // 手动测试模板匹配
            _uiController.OnTestTemplateMatch += async (s, e) =>
            {
                if (_pipelineProcessor == null)
                {
                    await _uiController.LogToFrontend("请先构建处理流程", "warning");
                    return;
                }

                string? fileName = await ShowOpenFileDialogOnStaThread("选择测试图片", "图像文件|*.jpg;*.jpeg;*.png;*.bmp");

                if (!string.IsNullOrEmpty(fileName))
                {

                    var ofd = new { FileName = fileName }; // Mocking ofd object for minimal code change or just use fileName directly

                    try
                    {
                        await _uiController.LogToFrontend($"正在测试: {Path.GetFileName(ofd.FileName)}");

                        using var testImage = Cv2.ImRead(ofd.FileName, ImreadModes.Color);
                        if (testImage.Empty())
                        {
                            await _uiController.LogToFrontend("无法读取图像文件", "error");
                            return;
                        }

                        var sw = Stopwatch.StartNew();
                        var result = await _pipelineProcessor.ProcessAsync(testImage);
                        sw.Stop();

                        // 获取最后一个算子的输出（带锚框）
                        Mat? lastOutput = _pipelineProcessor.GetLastOutput();
                        if (lastOutput == null || lastOutput.Empty())
                        {
                            await _uiController.LogToFrontend("处理后无输出图像", "warning");
                            return;
                        }

                        // 确保是彩色图像
                        using Mat outputForDisplay = new Mat();
                        if (lastOutput.Channels() == 1)
                        {
                            Cv2.CvtColor(lastOutput, outputForDisplay, ColorConversionCodes.GRAY2BGR);
                        }
                        else
                        {
                            lastOutput.CopyTo(outputForDisplay);
                        }

                        // 转换为 Base64 并发送
                        using var bitmap = outputForDisplay.ToBitmap();
                        using var ms = new MemoryStream();
                        bitmap.Save(ms, ImageFormat.Jpeg);
                        string base64 = Convert.ToBase64String(ms.ToArray());

                        var response = new PreviewResponse
                        {
                            ImageBase64 = base64,
                            ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                        };
                        await _uiController.SendPreviewImage(response);

                        // 显示结果
                        string resultMsg;
                        // 如果 Pipeline 返回了详细消息（不是默认的"检测通过/未通过"），则直接显示
                        if (!string.IsNullOrEmpty(result.Message) && result.Message != "检测通过" && result.Message != "检测未通过")
                        {
                            resultMsg = result.IsPass
                                ? $"? {result.Message}"
                                : $"? 匹配失败: {result.Message}";
                        }
                        else
                        {
                            // 默认显示 (兼容旧逻辑)
                            resultMsg = result.IsPass
                                ? $"? 匹配成功! 得分: {result.Score:F3}"
                                : $"? 匹配失败 (得分: {result.Score:F3} < 阈值)";
                        }

                        await _uiController.LogToFrontend(resultMsg, result.IsPass ? "success" : "error");
                        await _uiController.LogToFrontend($"处理耗时: {sw.Elapsed.TotalMilliseconds:F1}ms");

                        // 更新统计数据 (StatisticsUpdated 事件会自动更新 UI)
                        _statisticsService.RecordDetection(result.IsPass);
                    }
                    catch (Exception ex)
                    {
                        await _uiController.LogToFrontend($"测试失败: {ex.Message}", "error");
                    }
                }

            };

            // 注册窗体关闭事件
            this.FormClosing += OnFormClosingHandler;
        }

        private async void 主窗口_Load(object? sender, EventArgs e)
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                // UI Controller might not be ready if error happens too early, but we try
                if (_uiController != null)
                {
                    await _uiController.LogToFrontend($"系统初始化异常: {ex.Message}", "error");
                }
                else
                {
                    MessageBox.Show($"初始化严重错误: {ex.Message}");
                }
            }
        }

        private async Task InitializeAsync()
        {
            // 阻止系统休眠
            WindowHelpers.PreventSleep();

            // 确保无边框全屏
            this.FormBorderStyle = FormBorderStyle.None;
            this.WindowState = FormWindowState.Maximized;

            // 订阅 WebUI 就绪事件
            _uiController.OnAppReady += async (s, ev) =>
            {
                try
                {
                    await _uiController.LogToFrontend("? WebUI已就绪");
                    await _uiController.LogToFrontend("系统初始化完成");
                    await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");

                    // 发送相机列表以消除前端“正在加载相机列表”的提示
                    var cameras = _appConfig.Cameras.Select(c => new
                    {
                        id = c.Id,
                        displayName = c.DisplayName,
                        serialNumber = c.SerialNumber,
                        manufacturer = c.Manufacturer,
                        exposureTime = c.ExposureTime,
                        gain = c.Gain
                    }).ToList();
                    await _uiController.SendCameraList(cameras, _cameraManager.ActiveCameraId ?? _appConfig.ActiveCameraId);

                    // 初始化前端设置 (Sidebar Controls)
                    await _uiController.InitSettings(_appConfig);

                    // 发送已加载的统计数据到前端（修复重启后饼状图不更新的问题）
                    var currentStats = _statisticsService.Current;
                    await _uiController.UpdateUI(currentStats.TotalCount, currentStats.QualifiedCount, currentStats.UnqualifiedCount);
                    if (currentStats.TotalCount > 0)
                    {
                        await _uiController.LogToFrontend($"已加载今日统计: 总计{currentStats.TotalCount}, 合格{currentStats.QualifiedCount}, 不合格{currentStats.UnqualifiedCount}");
                    }

                    await InitModelList();
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"WebUI初始化流程异常: {ex.Message}", "error");
                }
            };

            // 订阅测试YOLO事件
            _uiController.OnTestYolo += (s, e) => SafeFireAndForget(TestYolo_HandlerAsync(), "YOLO测试");

            // 订阅ROI更新事件
            _uiController.OnUpdateROI += (sender, normalizedRect) =>
            {
                _currentROI = normalizedRect;
            };

            // 订阅YOLO参数修改事件
            _uiController.OnSetConfidence += (sender, conf) =>
            {
                _appConfig.Confidence = conf;
                _appConfig.Save();
            };

            _uiController.OnSetIou += (sender, iou) =>
            {
                _appConfig.IouThreshold = iou;
                _appConfig.Save();
            };

            // 订阅任务类型修改事件
            _uiController.OnSetTaskType += (sender, taskType) =>
            {
                _appConfig.TaskType = taskType;
                _appConfig.Save();
                // 使用检测服务更新任务类型
                _detectionService.SetTaskMode(taskType);
            };

            _uiController.OnSetAuxiliary1Model += async (sender, modelName) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _detectionService.UnloadAuxiliary1Model();
                        _appConfig.Auxiliary1ModelPath = "";
                        await _uiController.LogToFrontend("辅助模型1已卸载");
                    }
                    else
                    {
                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            await _detectionService.LoadAuxiliary1ModelAsync(modelPath);
                            _appConfig.Auxiliary1ModelPath = modelName;
                            await _uiController.LogToFrontend($"? 辅助模型1已加载: {modelName}");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"辅助模型1文件不存在: {modelName}", "error");
                        }
                    }
                    _appConfig.Save();
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载辅助模型1失败: {ex.Message}", "error");
                }
            };

            _uiController.OnSetAuxiliary2Model += async (sender, modelName) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(modelName))
                    {
                        _detectionService.UnloadAuxiliary2Model();
                        _appConfig.Auxiliary2ModelPath = "";
                        await _uiController.LogToFrontend("辅助模型2已卸载");
                    }
                    else
                    {
                        string modelPath = Path.Combine(模型路径, modelName);
                        if (File.Exists(modelPath))
                        {
                            await _detectionService.LoadAuxiliary2ModelAsync(modelPath);
                            _appConfig.Auxiliary2ModelPath = modelName;
                            await _uiController.LogToFrontend($"? 辅助模型2已加载: {modelName}");
                        }
                        else
                        {
                            await _uiController.LogToFrontend($"辅助模型2文件不存在: {modelName}", "error");
                        }
                    }
                    _appConfig.Save();
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"加载辅助模型2失败: {ex.Message}", "error");
                }
            };

            _uiController.OnToggleMultiModelFallback += async (sender, enabled) =>
            {
                _appConfig.EnableMultiModelFallback = enabled;
                _detectionService.SetEnableFallback(enabled);
                _appConfig.Save();
                await _uiController.LogToFrontend(enabled ? "? 多模型自动切换已启用" : "多模型自动切换已禁用");
            };

            // 订阅密码验证事件
            _uiController.OnVerifyPassword += async (sender, password) =>
            {
                if (password == _appConfig.AdminPassword)
                {
                    // 密码正确,关闭密码框并发送配置到前端打开设置界面
                    await _uiController.ExecuteScriptAsync("closePasswordModal();");
                    await _uiController.SendCurrentConfig(_appConfig);
                }
                else
                {
                    // 密码错误
                    await _uiController.ExecuteScriptAsync("alert('密码错误'); closePasswordModal();");
                }
            };

            // 订阅配置保存事件
            _uiController.OnSaveSettings += async (sender, configJson) =>
            {
                try
                {
                    // 使用 JsonDocument 解析，允许部分更新
                    using (JsonDocument doc = JsonDocument.Parse(configJson))
                    {
                        var root = doc.RootElement;

                        // 逐个读取并更新配置属性
                        if (root.TryGetProperty("StoragePath", out var sp)) _appConfig.StoragePath = sp.GetString() ?? _appConfig.StoragePath;
                        if (root.TryGetProperty("PlcProtocol", out var ppr)) _appConfig.PlcProtocol = ppr.GetString() ?? _appConfig.PlcProtocol;
                        if (root.TryGetProperty("PlcIp", out var pi)) _appConfig.PlcIp = pi.GetString() ?? _appConfig.PlcIp;
                        if (root.TryGetProperty("PlcPort", out var pp)) _appConfig.PlcPort = pp.TryGetInt32(out int ppVal) ? ppVal : _appConfig.PlcPort;
                        if (root.TryGetProperty("PlcTriggerAddress", out var pt)) _appConfig.PlcTriggerAddress = ParsePlcAddress(pt, _appConfig.PlcTriggerAddress);
                        if (root.TryGetProperty("PlcResultAddress", out var pr)) _appConfig.PlcResultAddress = ParsePlcAddress(pr, _appConfig.PlcResultAddress);
                        if (root.TryGetProperty("PlcTriggerDelayMs", out var ptd)) _appConfig.PlcTriggerDelayMs = ptd.TryGetInt32(out int ptdVal) ? Math.Max(0, ptdVal) : _appConfig.PlcTriggerDelayMs;
                        if (root.TryGetProperty("PlcPollingIntervalMs", out var ppi)) _appConfig.PlcPollingIntervalMs = ppi.TryGetInt32(out int ppiVal) ? Math.Max(50, ppiVal) : _appConfig.PlcPollingIntervalMs;
#pragma warning disable CS0618
                        var activeCam = _appConfig.ActiveCamera;
                        if (root.TryGetProperty("CameraName", out var cn))
                        {
                            _appConfig.CameraName = cn.GetString()?.Trim() ?? _appConfig.CameraName;
                            if (activeCam != null) activeCam.DisplayName = _appConfig.CameraName;
                        }
                        if (root.TryGetProperty("CameraSerialNumber", out var cs))
                        {
                            _appConfig.CameraSerialNumber = cs.GetString()?.Trim() ?? _appConfig.CameraSerialNumber;
                            if (activeCam != null) activeCam.SerialNumber = _appConfig.CameraSerialNumber;
                        }
                        if (root.TryGetProperty("CameraManufacturer", out var cm))
                        {
                            _appConfig.CameraManufacturer = cm.GetString()?.Trim() ?? _appConfig.CameraManufacturer;
                            if (activeCam != null) activeCam.Manufacturer = _appConfig.CameraManufacturer;
                        }
                        if (root.TryGetProperty("ExposureTime", out var et))
                        {
                            _appConfig.ExposureTime = et.TryGetDouble(out double etVal) ? etVal : _appConfig.ExposureTime;
                            if (activeCam != null) activeCam.ExposureTime = _appConfig.ExposureTime;
                        }
                        if (root.TryGetProperty("GainRaw", out var gr))
                        {
                            _appConfig.GainRaw = gr.TryGetDouble(out double grVal) ? grVal : _appConfig.GainRaw;
                            if (activeCam != null) activeCam.Gain = _appConfig.GainRaw;
                        }
#pragma warning restore CS0618
                        if (root.TryGetProperty("TargetLabel", out var tl)) _appConfig.TargetLabel = tl.GetString() ?? _appConfig.TargetLabel;
                        if (root.TryGetProperty("TargetCount", out var tc)) _appConfig.TargetCount = tc.TryGetInt32(out int tcVal) ? tcVal : _appConfig.TargetCount;
                        if (root.TryGetProperty("MaxRetryCount", out var mrc)) _appConfig.MaxRetryCount = mrc.TryGetInt32(out int mrcVal) ? mrcVal : _appConfig.MaxRetryCount;
                        if (root.TryGetProperty("RetryIntervalMs", out var rim)) _appConfig.RetryIntervalMs = rim.TryGetInt32(out int rimVal) ? rimVal : _appConfig.RetryIntervalMs;
                        if (root.TryGetProperty("TaskType", out var taskType)) _appConfig.TaskType = taskType.TryGetInt32(out int taskTypeVal) ? taskTypeVal : _appConfig.TaskType;
                        if (root.TryGetProperty("EnableGpu", out var eg)) _appConfig.EnableGpu = eg.ValueKind == JsonValueKind.True;
                        if (root.TryGetProperty("IndustrialRenderMode", out var irm)) _appConfig.IndustrialRenderMode = irm.ValueKind == JsonValueKind.True;
                        YoloDetector.IndustrialRenderMode = _appConfig.IndustrialRenderMode;
                        _detectionService.SetTaskMode(_appConfig.TaskType);

                        // 保存并重新加载
                        _appConfig.Save();

                        // 更新相关路径
                        _uiController.ImageBasePath = Path_Images;
                        _uiController.LogBasePath = Path_Logs;
                        InitDirectories();
                        _uiController.SetImageMapping(Path_Images);

                        // 重新初始化YOLO(如果GPU设置改变)
                        InitYolo();

                        // 尝试重新连接PLC (应用新IP/端口)
                        _ = ConnectPlcViaServiceAsync();

                        await _uiController.ExecuteScriptAsync("closeSettingsModal();");
                        await _uiController.UpdateCameraName(_appConfig.ActiveCamera?.DisplayName ?? "未配置");
                        await _uiController.LogToFrontend("? 系统设置已更新", "success");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.ExecuteScriptAsync($"alert('保存失败: {ex.Message.Replace("'", "\\'")}');");
                }
            };

            // 订阅选择文件夹事件
            _uiController.OnSelectStorageFolder += (sender, e) =>
            {
                InvokeOnUIThread(async () =>
                {
                    using (var fbd = new FolderBrowserDialog())
                    {
                        fbd.Description = "选择数据存储根目录";
                        fbd.UseDescriptionForTitle = true;
                        // fbd.ShowNewFolderButton = true; // Default is true
                        if (Directory.Exists(_appConfig.StoragePath))
                            fbd.SelectedPath = _appConfig.StoragePath;

                        if (fbd.ShowDialog() == DialogResult.OK)
                        {
                            string path = fbd.SelectedPath;
                            await _uiController.UpdateStoragePathInUI(path);
                        }
                    }
                });
            };

            // 模型加载与 WebView2 初始化并行，减少冷启动等待时间
            Task initYoloTask = InitYoloAsync();

            // 初始化 WebUI
            if (webView21 != null)
            {
                await _uiController.InitializeAsync(webView21);
                // 配置 NG 图片查看路径
                _uiController.ImageBasePath = Path_Images;
                _uiController.SetImageMapping(Path_Images);
                // 配置检测日志路径
                _uiController.LogBasePath = Path_Logs;
            }

            await initYoloTask;

            // 统计数据已由 _statisticsService 在构造时加载
            // 检测跨日，如果需要则保存历史并重置今日数据
            bool isNewDay = _statisticsService.CheckAndResetForNewDay();
            if (isNewDay)
            {
                SafeFireAndForget(_uiController.LogToFrontend("检测到新的一天，统计数据已重置", "info"), "日志记录");
            }

            InitDirectories();

            // 启动后台清理
            StartCleanupTask();
        }

        private async Task InitModelList()
        {
            await _uiController.LogToFrontend("开始加载模型列表...");

            if (!Directory.Exists(模型路径))
            {
                Directory.CreateDirectory(模型路径);
                await _uiController.LogToFrontend($"创建模型目录: {模型路径}");
            }

            var files = Directory.GetFiles(模型路径, "*.onnx");
            await _uiController.LogToFrontend($"找到 {files.Length} 个ONNX模型文件");

            var names = files.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).ToArray();

            // Push to Frontend (Requirement from Step 177/147)
            await _uiController.SendModelList(names!);
            await _uiController.LogToFrontend($"? 已通过 SendModelList 推送 {names.Length} 个模型");
        }

        private void InitDirectories()
        {
            if (!Directory.Exists(Path_Logs)) Directory.CreateDirectory(Path_Logs);
            if (!Directory.Exists(Path_Images)) Directory.CreateDirectory(Path_Images);
            if (!Directory.Exists(Path_System)) Directory.CreateDirectory(Path_System);
        }

        private void StartCleanupTask()
        {
            Task.Run(async () =>
            {
                while (!停止)
                {
                    _storageService?.CleanOldData(30);
                    await Task.Delay(TimeSpan.FromHours(24));
                }
            });
        }

        protected void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
        {
            // 防止重复调用
            if (e.CloseReason == CloseReason.ApplicationExitCall) return;

            try
            {
                _storageService?.WriteStartupLog("软件关闭", null);

                // 恢复系统休眠策略
                WindowHelpers.RestoreSleep();

                // 保存统计数据
                _statisticsService?.SaveAll();

                // 停止后台任务
                this.停止 = true;
                _plcService?.StopMonitoring();
                _plcTriggerQueue.Writer.TryComplete();
                _plcTriggerQueueCts.Cancel();
                try
                {
                    _plcTriggerConsumerTask?.Wait(300);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"PLC Trigger Consumer Stop Error: {ex.Message}");
                }
                _plcTriggerQueueCts.Dispose();

                // 使用线程等待模式进行资源释放，防止界面卡死
                // 给予500ms的尝试断开时间，超时强制退出
                var cleanupTask = Task.Run(() =>
                {
                    try
                    {
                        if (plcConnected)
                        {
                            _plcService?.Disconnect();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"PLC Disconnect Error: {ex.Message}");
                    }

                    try
                    {
                        ReleaseCameraResources();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Camera Release Error: {ex.Message}");
                    }

                    try
                    {
                        // DetectionService 资源由其内部管理，此处无需手动释放
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"YOLO Dispose Error: {ex.Message}");
                    }

                    try
                    {
                        _pipelineProcessor?.Dispose();
                        _pipelineProcessor = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Pipeline Dispose Error: {ex.Message}");
                    }

                    try
                    {
                        _imageSaveQueue?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"ImageSaveQueue Dispose Error: {ex.Message}");
                    }
                });

                // 等待清理完成或超时 (800ms)
                if (!cleanupTask.Wait(800))
                {
                    // 超时，强制不再等待
                }
            }
            catch (Exception)
            {
                // 确保任何错误都不阻止关闭
            }
        }

        private static short ParsePlcAddress(JsonElement value, short fallback)
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                if (value.TryGetInt16(out short shortValue))
                {
                    return shortValue;
                }

                if (value.TryGetInt32(out int intValue) && intValue >= short.MinValue && intValue <= short.MaxValue)
                {
                    return (short)intValue;
                }

                return fallback;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                string raw = value.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(raw))
                {
                    return fallback;
                }

                // 兼容现场输入: D100 / d100 / DB1.100 / 100
                if (raw.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                {
                    int dotIndex = raw.LastIndexOf('.');
                    raw = dotIndex >= 0 && dotIndex < raw.Length - 1
                        ? raw.Substring(dotIndex + 1)
                        : raw.Substring(2);
                }
                else if (char.IsLetter(raw[0]))
                {
                    raw = raw.Substring(1);
                }

                if (short.TryParse(raw, out short parsed))
                {
                    return parsed;
                }
            }

            return fallback;
        }

        private void InvokeOnUIThread(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        #endregion
    }
}


