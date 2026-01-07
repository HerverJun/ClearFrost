using MVSDK_Net;
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
using YoloDetection;
using YOLO.Vision;
using YOLO.Helpers;
using YOLO.Interfaces;
using YOLO.Services;

namespace YOLO
{
    public partial class 主窗口
    {
        #region 5. YOLO检测逻辑 (包含辅助逻辑)

        private void InitYolo()
        {
            // 同步调用异步方法
            SafeFireAndForget(InitYoloAsync(), "YOLO初始化");
        }

        private async Task InitYoloAsync()
        {
            await _uiController.LogToFrontend("正在加载 YOLO 模型...", "info");

            bool useGpu = _appConfig.EnableGpu;

            // 如果没指定模型名，尝试找一个默认的
            if (string.IsNullOrEmpty(模型名))
            {
                var files = Directory.GetFiles(模型路径, "*.onnx");
                if (files.Length > 0) 模型名 = Path.GetFileName(files[0]);
            }

            if (!string.IsNullOrEmpty(模型名))
            {
                try
                {
                    string fullPath = Path.Combine(模型路径, 模型名);
                    bool success = await _detectionService.LoadModelAsync(fullPath, useGpu);

                    if (success)
                    {
                        // 设置任务类型
                        _detectionService.SetTaskMode(_appConfig.TaskType);
                        await _uiController.LogToFrontend($"✓ YOLO模型已加载: {模型名} {(useGpu ? "[GPU]" : "[CPU]")}", "success");
                    }
                    else
                    {
                        await _uiController.LogToFrontend($"模型加载失败", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error");
                }
            }
            else
            {
                await _uiController.LogToFrontend("未找到模型文件，请在设置中下载或上传模型", "warning");
            }
        }

        private async void TestYolo_Handler(object? sender, EventArgs e)
        {
            try
            {
                await _uiController.LogToFrontend("开始YOLO测试...", "info");

                string? selectedFile = await ShowOpenFileDialogOnStaThread("选择测试图片", "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*");

                if (string.IsNullOrEmpty(selectedFile))
                {
                    await _uiController.LogToFrontend("已取消测试", "warning");
                    return;
                }

                await _uiController.LogToFrontend($"加载图片: {Path.GetFileName(selectedFile)}", "info");

                // 读取图片
                using (Bitmap originalBitmap = new Bitmap(selectedFile))
                {
                    // 检查模型是否初始化
                    if (!_detectionService.IsModelLoaded)
                    {
                        await _uiController.LogToFrontend("YOLO模型未初始化", "error");
                        return;
                    }

                    // 执行YOLO检测
                    Stopwatch sw = Stopwatch.StartNew();

                    float conf = _appConfig.Confidence;
                    float iou = _appConfig.IouThreshold;

                    // 使用检测服务进行推理
                    var detectionResult = await _detectionService.DetectAsync(originalBitmap, conf, iou);
                    List<YoloResult> allResults = detectionResult.Results ?? new List<YoloResult>();
                    string usedModelName = detectionResult.UsedModelName ?? "";
                    string[] usedModelLabels = detectionResult.UsedModelLabels ?? Array.Empty<string>();

                    // 记录使用的模型
                    if (detectionResult.WasFallback)
                    {
                        await _uiController.LogToFrontend($"⚡ 主模型未检测到, 已切换到: {usedModelName}", "warning");
                    }
                    else if (!string.IsNullOrEmpty(usedModelName))
                    {
                        await _uiController.LogToFrontend($"使用模型: {usedModelName}", "info");
                    }

                    // 发送性能指标
                    var metrics = _detectionService.GetLastMetrics();
                    if (metrics != null)
                    {
                        await _uiController.SendInferenceMetrics(metrics);
                    }

                    sw.Stop();

                    // 标准化需要imgW/H
                    List<YoloResult> pixelResults = StandardizeYoloResults(allResults, originalBitmap.Width, originalBitmap.Height);

                    // 应用UI绘制的ROI过滤
                    List<YoloResult> results = FilterByROI(pixelResults, originalBitmap.Width, originalBitmap.Height);

                    // 使用实际推理模型的标签列表（关键修复：确保辅助模型的标签正确显示）
                    string[] labels = usedModelLabels;

                    // 3. 判别该次是否合格
                    int targetCount = results.Count(r =>
                    {
                        int labelIndex = (int)r.BasicData[5];
                        if (labelIndex >= 0 && labelIndex < labels.Length)
                        {
                            return labels[labelIndex].Equals(_appConfig.TargetLabel, StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    });
                    bool isQualified = (targetCount == _appConfig.TargetCount);


                    string roiInfo = _currentROI != null ? $" (ROI过滤: {allResults.Count} → {results.Count})" : "";
                    string objDesc = GetDetectedObjectsDescription(results, usedModelLabels);
                    string modelInfo = detectionResult.WasFallback ? $" [模型: {usedModelName}]" : "";
                    await _uiController.LogDetectionToFrontend($"检测完成! 耗时: {sw.ElapsedMilliseconds}ms, 检测到 {objDesc}{roiInfo}{modelInfo}, 判定: {(isQualified ? "合格" : "不合格")}", isQualified ? "success" : "error");

                    // 4. 更新UI、PLC、保存结果 (复用生产逻辑)
                    UpdateUIAndPLC(isQualified, results, usedModelLabels);
                    ProcessAndSaveImages(originalBitmap, results, isQualified, usedModelLabels);
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"测试异常: {ex.Message}", "error");
            }
        }

        // 对应原 btnCapture_Click_1
        private async void btnCapture_Logic()
        {
            try
            {
                var result = await RunDetectionOnceAsync();
                if (result != null)
                {
                    await UpdateUIAndPLC(result.IsQualified, result.Results, result.UsedModelLabels);
                    ProcessAndSaveImages(result.OriginalBitmap, result.Results, result.IsQualified, result.UsedModelLabels);
                    result.OriginalBitmap?.Dispose();
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"手动检测异常: {ex.Message}", "error");
            }
        }

        private class DetectionResult
        {
            public bool IsQualified { get; set; }
            public List<YoloResult>? Results { get; set; }
            public Bitmap? OriginalBitmap { get; set; }
            public long ElapsedMs { get; set; }
            /// <summary>使用的模型标签列表（多模型切换时关键）</summary>
            public string[]? UsedModelLabels { get; set; }
        }

        private async Task<DetectionResult?> RunDetectionOnceAsync()
        {
            if (!cam.IMV_IsGrabbing())
            {
                _uiController.LogToFrontend("请先启动相机", "warning");
                return null;
            }

            IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
            Stopwatch sw = new Stopwatch();
            Bitmap? originalBitmap = null;

            try
            {
                cam.IMV_ExecuteCommandFeature("TriggerSoftware");
                int res = cam.IMV_GetFrame(ref frame, 3000);
                if (res != IMVDefine.IMV_OK)
                {
                    _uiController.LogToFrontend("获取图像帧失败", "error");
                    return null;
                }

                originalBitmap = ConvertFrameToBitmap(frame);
                sw.Start();

                // 保存最后一帧用于传统视觉预览
                lock (_frameLock)
                {
                    _lastCapturedFrame?.Dispose();
                    _lastCapturedFrame = BitmapConverter.ToMat(originalBitmap);
                }

                // 检查模型是否初始化
                if (!_detectionService.IsModelLoaded) throw new Exception("YOLO模型未初始化");

                float conf = _appConfig.Confidence;
                float iou = _appConfig.IouThreshold;

                // 使用检测服务进行推理
                var detectionResult = await _detectionService.DetectAsync(originalBitmap, conf, iou);
                List<YoloResult> allResults = detectionResult.Results ?? new List<YoloResult>();
                string usedModelName = detectionResult.UsedModelName ?? "";
                string[] usedModelLabels = detectionResult.UsedModelLabels ?? Array.Empty<string>();

                // 记录使用的模型
                if (detectionResult.WasFallback)
                {
                    await _uiController.LogToFrontend($"主模型未检测到, 切换到: {usedModelName}", "warning");
                }

                List<YoloResult> pixelResults = StandardizeYoloResults(allResults, originalBitmap.Width, originalBitmap.Height);
                // 使用内部参数进行ROI过滤
                List<YoloResult> finalResults = FilterResultsByROIWithThreshold(pixelResults, overlapThreshold);

                // 应用UI绘制的ROI过滤
                finalResults = FilterByROI(finalResults, originalBitmap.Width, originalBitmap.Height);

                // 使用实际推理模型的标签列表（关键修复：确保辅助模型的标签正确显示）
                string[] labels = usedModelLabels;
                int targetCount = finalResults.Count(r =>
                {
                    int labelIndex = (int)r.BasicData[5];
                    if (labelIndex >= 0 && labelIndex < labels.Length)
                    {
                        return labels[labelIndex].Equals(_appConfig.TargetLabel, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                });
                bool isQualified = (targetCount == _appConfig.TargetCount);

                sw.Stop();
                return new DetectionResult
                {
                    IsQualified = isQualified,
                    Results = finalResults,
                    OriginalBitmap = originalBitmap,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    UsedModelLabels = usedModelLabels
                };
            }
            catch (Exception ex)
            {
                originalBitmap?.Dispose(); // 发生异常时释放Bitmap
                _uiController.LogToFrontend($"检测异常: {ex.Message}", "error");
                return null;
            }
            finally
            {
                cam.IMV_ReleaseFrame(ref frame);
            }
        }

        private List<YoloResult> StandardizeYoloResults(List<YoloResult> results, int imgW, int imgH)
        {
            var outList = new List<YoloResult>();
            foreach (var r in results)
            {
                YoloResult newItem = new YoloResult();
                newItem.BasicData = (float[])r.BasicData.Clone();
                newItem.MaskData = r.MaskData?.Clone() ?? new Mat();
                if (r.KeyPoints != null)
                {
                    newItem.KeyPoints = new PosePoint[r.KeyPoints.Length];
                    for (int i = 0; i < r.KeyPoints.Length; i++)
                        newItem.KeyPoints[i] = new PosePoint { X = r.KeyPoints[i].X, Y = r.KeyPoints[i].Y, Score = r.KeyPoints[i].Score };
                }

                outList.Add(newItem);
            }
            return outList;
        }

        private async Task UpdateUIAndPLC(bool isQualified, List<YoloResult>? results, string[]? labels = null)
        {
            // 更新 WebUI (StatisticsUpdated 事件会自动更新 UI)
            _statisticsService.RecordDetection(isQualified);

            // 可以在html添加 showResultOverlay(bool) 接口，这里先不传
            // 若 WebUIController 支持，可调用 _uiController.ShowResult(isQualified);

            string[] actualLabels = labels ?? _detectionService.GetLabels();

            StringBuilder sb = new StringBuilder();
            if (actualLabels != null && results != null)
            {
                foreach (var r in results)
                {
                    int labelIdx = (int)r.BasicData[5];
                    string label = (labelIdx >= 0 && labelIdx < actualLabels.Length) ? actualLabels[labelIdx] : $"Unknown({labelIdx})";
                    float conf = r.BasicData[4];
                    sb.AppendLine($"发现物体: {label} ({conf:P0})");
                }
            }
            // 生产模式下的每次检测结果也输出到检测日志窗口
            string objDesc = GetDetectedObjectsDescription(results, actualLabels);
            await _uiController.LogDetectionToFrontend($"PLC触发: {(isQualified ? "OK" : "NG")} | {objDesc}", isQualified ? "success" : "error");

            _storageService?.WriteDetectionLog(sb.ToString(), isQualified);

            // 下发 PLC
            await WriteDetectionResult(isQualified);
        }

        private void ProcessAndSaveImages(Bitmap? original, List<YoloResult>? results, bool isQualified, string[]? labels = null)
        {
            if (original == null) return;

            using (Bitmap roiMarked = DrawROIBorder(original))
            {
                if (!_detectionService.IsModelLoaded || results == null) return;

                // 使用传入的标签列表，否则回退到主模型标签
                string[] actualLabels = labels ?? _detectionService.GetLabels();

                using (Image finalImg = _detectionService.GenerateResultImage(roiMarked, results, actualLabels))
                {
                    // 更新 WebUI 图片
                    using (Bitmap webImg = new Bitmap(finalImg))
                    {
                        SendImageToWeb(webImg);
                    }
                    // 保存
                    _storageService?.SaveDetectionImageAsync(roiMarked, isQualified);
                }
            }
        }

        /// <summary>
        /// 将图片发送到前端显示
        /// <para>注意：此方法不负责 Dispose 传入的 Bitmap，调用者需自行管理生命周期</para>
        /// </summary>
        /// <param name="bmp">要显示的图片对象</param>
        private void SendImageToWeb(Bitmap bmp)
        {
            if (bmp == null) return;
            using (MemoryStream ms = new MemoryStream())
            {
                //保存为 Jpeg 减少数据量
                bmp.Save(ms, ImageFormat.Jpeg);
                byte[] byteImage = ms.ToArray();
                string base64 = Convert.ToBase64String(byteImage);
                _uiController.UpdateImage(base64);
            }
        }

        // 仅在原图上画 ROI 虚线框 (供保存/显示)
        private Bitmap DrawROIBorder(Bitmap src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            Bitmap ret = new Bitmap(src);

            // 优先使用 WebUI 设置的 ROI (_currentROI)
            if (_currentROI != null && _currentROI.Length == 4)
            {
                // _currentROI is [x, y, w, h] normalized (0.0 ~ 1.0)
                int x = (int)(_currentROI[0] * src.Width);
                int y = (int)(_currentROI[1] * src.Height);
                int w = (int)(_currentROI[2] * src.Width);
                int h = (int)(_currentROI[3] * src.Height);

                // 避免无效绘制
                if (w > 0 && h > 0)
                {
                    using (Graphics g = Graphics.FromImage(ret))
                    {
                        using (Pen p = new Pen(Color.Red, 3) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 8, 4 } })
                        {
                            g.DrawRectangle(p, x, y, w, h);
                        }
                    }
                }
                return ret;
            }

            // 兼容旧版逻辑 (如果 WebUI 未设置 ROI，但后端启用了 useROI)
            if (!useROI) return ret;

            using (Graphics g = Graphics.FromImage(ret))
            {
                using (Pen p = new Pen(Color.Red, 3) { DashStyle = DashStyle.Dash, DashPattern = new float[] { 8, 4 } })
                    g.DrawRectangle(p, roiX, roiY, roiWidth, roiHeight);
            }
            return ret;
        }

        // 纯数值计算 ROI 过滤
        private List<YoloResult> FilterResultsByROIWithThreshold(List<YoloResult>? input, float threshold)
        {
            if (input == null) return new List<YoloResult>();
            if (!useROI || !isROISet) return input;
            var outList = new List<YoloResult>();
            RectangleF roiF = new RectangleF(roiX, roiY, roiWidth, roiHeight);

            foreach (var item in input)
            {
                float w = item.BasicData[2];
                float h = item.BasicData[3];
                float left = item.BasicData[0] - w / 2f;
                float top = item.BasicData[1] - h / 2f;
                RectangleF itemRect = new RectangleF(left, top, w, h);
                RectangleF inter = RectangleF.Intersect(roiF, itemRect);
                float interArea = Math.Max(0, inter.Width) * Math.Max(0, inter.Height);
                float boxArea = w * h;
                if (boxArea > 0 && (interArea / boxArea) >= threshold) outList.Add(item);
            }
            return outList;
        }

        /// <summary>
        /// 根据ROI区域过滤检测结果,只保留中心点在ROI内的检测
        /// </summary>
        private List<YoloResult> FilterByROI(List<YoloResult> results, int imageWidth, int imageHeight)
        {
            if (_currentROI == null || _currentROI.Length != 4)
                return results; // 没有ROI则不过滤

            // 如果ROI宽高几乎为0，也就是未设置有效ROI，则不过滤
            if (_currentROI[2] < 0.001f || _currentROI[3] < 0.001f)
                return results;

            // 将归一化坐标转换为像素坐标
            float roiX = _currentROI[0] * imageWidth;
            float roiY = _currentROI[1] * imageHeight;
            float roiW = _currentROI[2] * imageWidth;
            float roiH = _currentROI[3] * imageHeight;

            // 过滤:只保留中心点在ROI内的检测结果
            var filtered = results.Where(r =>
            {
                float centerX = r.BasicData[0];
                float centerY = r.BasicData[1];

                return centerX >= roiX && centerX <= (roiX + roiW) &&
                       centerY >= roiY && centerY <= (roiY + roiH);
            }).ToList();

            return filtered;
        }

        private string GetDetectedObjectsDescription(List<YoloResult>? results, string[]? labels = null)
        {
            if (results == null || results.Count == 0) return "未检测到物体";

            // 使用传入的标签列表，否则回退到默认
            string[]? actualLabels = labels ?? _detectionService.GetLabels();
            if (actualLabels == null || actualLabels.Length == 0) return $"{results.Count} 个物体";

            var descriptions = results
                .GroupBy(r => (int)r.BasicData[5])
                .Select(g =>
                {
                    int index = g.Key;
                    string name = (index >= 0 && index < actualLabels.Length) ? actualLabels[index] : $"未知({index})";

                    if (name.Equals("remote", StringComparison.OrdinalIgnoreCase)) name = "遥控器";
                    else if (name.Equals("screw", StringComparison.OrdinalIgnoreCase)) name = "螺钉";

                    return $"{g.Count()}个{name}";
                });

            return string.Join(", ", descriptions);
        }

        /// <summary>
        /// 处理最终检测结果
        /// </summary>
        private void ProcessFinalResult(DetectionResult result)
        {
            if (result == null || result.Results == null || result.OriginalBitmap == null) return;

            InvokeOnUIThread(() =>
            {
                SafeFireAndForget(UpdateUIAndPLC(result.IsQualified, result.Results, result.UsedModelLabels), "UI更新");
                ProcessAndSaveImages(result.OriginalBitmap, result.Results, result.IsQualified, result.UsedModelLabels);

                var lastMetrics = _detectionService.GetLastMetrics();
                if (lastMetrics != null)
                {
                    SafeFireAndForget(_uiController.SendInferenceMetrics(lastMetrics), "发送推理指标");
                }

                result.OriginalBitmap?.Dispose();
            });
        }

        /// <summary>
        /// 仅显示图像（中间结果，不保存）
        /// </summary>
        private void DisplayImageOnly(Bitmap? original, List<YoloResult>? results, string[]? labels = null)
        {
            if (!_detectionService.IsModelLoaded || original == null || results == null) return;

            using (Bitmap roiMarked = DrawROIBorder(original))
            {
                string[] actualLabels = labels ?? _detectionService.GetLabels();

                using (Image finalImg = _detectionService.GenerateResultImage(roiMarked, results, actualLabels))
                using (Bitmap webImg = new Bitmap(finalImg))
                {
                    SendImageToWeb(webImg);
                }
            }
        }

        /// <summary>
        /// 在独立的STA线程中运行OpenFileDialog，彻底解决WebView2线程冲突导致的闪退问题
        /// </summary>
        private Task<string?> ShowOpenFileDialogOnStaThread(string title, string filter)
        {
            var tcs = new TaskCompletionSource<string?>();

            Thread thread = new Thread(() =>
            {
                try
                {
                    using var ofd = new OpenFileDialog();
                    ofd.Title = title;
                    ofd.Filter = filter;
                    ofd.Multiselect = false;
                    ofd.AutoUpgradeEnabled = true; // 在独立线程中通常可以恢复新版界面

                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        tcs.SetResult(ofd.FileName);
                    }
                    else
                    {
                        tcs.SetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return tcs.Task;
        }

        private void btnSettings_Logic()
        {
            // 触发HTML密码模态框而非WinForms对话框
            _ = _uiController.ExecuteScriptAsync("openPasswordModal();");
        }

        private void ChangeModel_Logic(string modelFilename)
        {
            模型名 = modelFilename;
            SafeFireAndForget(InitYoloAndMultiModelAsync(), "模型初始化");
        }

        /// <summary>
        /// 初始化YOLO检测器和多模型管理器
        /// </summary>
        private async Task InitYoloAndMultiModelAsync()
        {
            if (string.IsNullOrEmpty(模型名)) return;

            string primaryModelPath = Path.Combine(模型路径, 模型名);
            if (!File.Exists(primaryModelPath))
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型文件不存在: {模型名}", "error"), "模型加载");
                return;
            }

            try
            {
                // 使用检测服务加载主模型
                bool success = await _detectionService.LoadModelAsync(primaryModelPath, _appConfig.EnableGpu);
                if (!success)
                {
                    SafeFireAndForget(_uiController.LogToFrontend($"主模型加载失败", "error"), "模型加载");
                    return;
                }

                // 设置参数
                _detectionService.SetEnableFallback(_appConfig.EnableMultiModelFallback);
                _detectionService.SetTaskMode(_appConfig.TaskType);

                SafeFireAndForget(_uiController.LogToFrontend($"✓ 主模型已加载: {模型名}"), "模型加载");

                // 加载辅助模型（如果配置了）
                if (!string.IsNullOrEmpty(_appConfig.Auxiliary1ModelPath))
                {
                    string aux1Path = Path.Combine(模型路径, _appConfig.Auxiliary1ModelPath);
                    if (File.Exists(aux1Path))
                    {
                        await _detectionService.LoadAuxiliary1ModelAsync(aux1Path);
                        SafeFireAndForget(_uiController.LogToFrontend($"✓ 辅助模型1已加载: {_appConfig.Auxiliary1ModelPath}"), "模型加载");
                    }
                }

                if (!string.IsNullOrEmpty(_appConfig.Auxiliary2ModelPath))
                {
                    string aux2Path = Path.Combine(模型路径, _appConfig.Auxiliary2ModelPath);
                    if (File.Exists(aux2Path))
                    {
                        await _detectionService.LoadAuxiliary2ModelAsync(aux2Path);
                        SafeFireAndForget(_uiController.LogToFrontend($"✓ 辅助模型2已加载: {_appConfig.Auxiliary2ModelPath}"), "模型加载");
                    }
                }
            }
            catch (Exception ex)
            {
                SafeFireAndForget(_uiController.LogToFrontend($"模型加载失败: {ex.Message}", "error"), "模型加载");
            }
        }

        #endregion
    }
}
