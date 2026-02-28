using MVSDK_Net;
using ClearFrost.Config;
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
        #region 5. YOLO检测逻辑 (检测与视觉逻辑)

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
                    string modelPath = Path.Combine(模型路径, 模型名);
                    bool success = await _detectionService.LoadModelAsync(modelPath, useGpu);
                    if (success)
                    {
                        await _uiController.LogToFrontend($"模型加载成功: {模型名}", "success");
                        await RestoreMultiModelConfigAsync();
                    }
                    else
                    {
                        await _uiController.LogToFrontend("模型加载失败", "error");
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

        private async Task RestoreMultiModelConfigAsync()
        {
            _detectionService.SetEnableFallback(_appConfig.EnableMultiModelFallback);

            if (!string.IsNullOrWhiteSpace(_appConfig.Auxiliary1ModelPath))
            {
                string aux1Path = Path.Combine(模型路径, _appConfig.Auxiliary1ModelPath);
                if (File.Exists(aux1Path))
                {
                    bool ok = await _detectionService.LoadAuxiliary1ModelAsync(aux1Path);
                    if (ok)
                    {
                        await _uiController.LogToFrontend($"已恢复辅助模型1: {_appConfig.Auxiliary1ModelPath}");
                    }
                }
                else
                {
                    await _uiController.LogToFrontend($"辅助模型1文件不存在，跳过恢复: {_appConfig.Auxiliary1ModelPath}", "warning");
                }
            }

            if (!string.IsNullOrWhiteSpace(_appConfig.Auxiliary2ModelPath))
            {
                string aux2Path = Path.Combine(模型路径, _appConfig.Auxiliary2ModelPath);
                if (File.Exists(aux2Path))
                {
                    bool ok = await _detectionService.LoadAuxiliary2ModelAsync(aux2Path);
                    if (ok)
                    {
                        await _uiController.LogToFrontend($"已恢复辅助模型2: {_appConfig.Auxiliary2ModelPath}");
                    }
                }
                else
                {
                    await _uiController.LogToFrontend($"辅助模型2文件不存在，跳过恢复: {_appConfig.Auxiliary2ModelPath}", "warning");
                }
            }
        }

        private async Task TestYolo_HandlerAsync()
        {
            try
            {
                await _uiController.LogToFrontend("开始YOLO测试...", "info");

                string? selectedFile = await ShowOpenFileDialogOnStaThread("选择测试图片", "图像文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*");

                if (string.IsNullOrEmpty(selectedFile))
                {
                    await _uiController.LogToFrontend("已取消测试", "warning");
                    return;
                }

                await _uiController.LogToFrontend($"测试图片: {Path.GetFileName(selectedFile)}", "info");

                // 读取图片
                using (Bitmap originalBitmap = new Bitmap(selectedFile))
                {
                    // 检查模型是否初始化
                    if (!_detectionService.IsModelLoaded)
                    {
                        await _uiController.LogToFrontend("YOLO模型未初始化", "error");
                        return;
                    }

                    var sw = Stopwatch.StartNew();

                    // 执行检测
                    var result = await _detectionService.DetectAsync(originalBitmap, _appConfig.Confidence, overlapThreshold, _appConfig.TargetLabel, _appConfig.TargetCount);

                    sw.Stop();

                    // 获取检测结果
                    var results = result.Results ?? new List<YoloResult>();
                    bool isQualified = result.IsQualified;

                    // 应用 ROI 过滤
                    results = FilterResultsByROI(results, originalBitmap.Width, originalBitmap.Height);

                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using (var sourceMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(originalBitmap))
                    using (var renderedMat = TryRenderDetectionMat(sourceMat, results, labels))
                    {
                        await _uiController.UpdateImage(renderedMat ?? sourceMat);

                        // 保存检测图像到追溯库（不合格时复用渲染结果）
                        await SaveDetectionImage(sourceMat, results, isQualified, result.UsedModelLabels, renderedMat);
                    }

                    // 更新UI (发送到检测流水，包含模型切换信息)
                    string objDesc = GetDetailedDetectionLog(results, labels);
                    string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                    await _uiController.LogDetectionToFrontend($"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | {sw.ElapsedMilliseconds}ms{modelInfo}", isQualified ? "success" : "error");

                    // 更新统计
                    _statisticsService.RecordDetection(isQualified);
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"测试失败: {ex.Message}", "error");
            }
        }

        private async Task<string?> ShowOpenFileDialogOnStaThread(string title, string filter)
        {
            string? result = null;
            await Task.Run(() =>
            {
                Thread thread = new Thread(() =>
                {
                    using var ofd = new OpenFileDialog();
                    ofd.Title = title;
                    ofd.Filter = filter;
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        result = ofd.FileName;
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join();
            });
            return result;
        }

        private void ChangeModel_Logic(string modelName)
        {
            if (string.IsNullOrEmpty(modelName)) return;

            模型名 = modelName;
            SafeFireAndForget(ChangeModelAsync(modelName), "切换模型");
        }

        private async Task ChangeModelAsync(string modelName)
        {
            try
            {
                await _uiController.LogToFrontend($"正在切换模型: {modelName}", "info");

                string modelPath = Path.Combine(模型路径, modelName);
                if (!File.Exists(modelPath))
                {
                    await _uiController.LogToFrontend($"模型文件不存在: {modelName}", "error");
                    return;
                }

                bool success = await _detectionService.LoadModelAsync(modelPath, _appConfig.EnableGpu);
                if (success)
                {
                    _appConfig.Save();
                    await _uiController.LogToFrontend($"模型切换成功: {modelName}", "success");
                }
                else
                {
                    await _uiController.LogToFrontend("模型切换失败", "error");
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"模型切换异常: {ex.Message}", "error");
            }
        }

        /// <summary>
        /// 手动检测逻辑 (PLC触发或手动按钮)
        /// </summary>
        private string GetDetailedDetectionLog(List<YoloResult> results, string[]? labels)
        {
            if (results == null || results.Count == 0) return "未检测到目标";

            // 格式: screw 0.98, body 0.99
            var details = results.Select(r =>
            {
                string label = (labels != null && r.ClassId >= 0 && r.ClassId < labels.Length)
                    ? labels[r.ClassId]
                    : $"Class_{r.ClassId}";
                return $"{label} {r.Confidence:F2}";
            });

            return $"Found {results.Count}: {string.Join(", ", details)}";
        }

        private async Task btnCapture_LogicAsync()
        {
            // 使用信号量防止并发检测
            if (!await _detectionSemaphore.WaitAsync(0))
            {
                await _uiController.LogToFrontend("检测正在进行中，请稍候...", "warning");
                return;
            }

            var totalSw = Stopwatch.StartNew();
            long captureMs = 0;
            long inferenceMs = 0;
            long roiFilterMs = 0;
            long plcWriteMs = 0;
            long renderToUiMs = 0;
            long saveQueueMs = 0;
            long dbWriteMs = 0;
            bool finalQualified = false;
            int finalResultCount = 0;

            try
            {
                await _uiController.LogToFrontend("开始检测...", "info");

                Mat? frameToProcess = null;

                var captureSw = Stopwatch.StartNew();
                // 首先尝试触发相机拍照并获取实时图像
                try
                {
                    // 触发软件拍照
                    int res = cam.IMV_ExecuteCommandFeature("TriggerSoftware");
                    if (res == IMVDefine.IMV_OK)
                    {
                        // 获取帧
                        IMVDefine.IMV_Frame frame = new IMVDefine.IMV_Frame();
                        bool shouldReleaseFrame = false;
                        try
                        {
                            res = cam.IMV_GetFrame(ref frame, 2000); // 2秒超时
                            shouldReleaseFrame = res == IMVDefine.IMV_OK;
                            if (shouldReleaseFrame && frame.frameInfo.size > 0)
                            {
                                // 转换为 Mat
                                frameToProcess = ConvertFrameToMat(frame);
                            }
                        }
                        finally
                        {
                            if (shouldReleaseFrame)
                            {
                                cam.IMV_ReleaseFrame(ref frame);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[手动检测] 触发拍照失败: {ex.Message}");
                }

                // 如果相机拍照失败，尝试使用缓存的最后一帧
                if (frameToProcess == null)
                {
                    lock (_frameLock)
                    {
                        if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                        {
                            frameToProcess = _lastCapturedFrame.Clone();
                        }
                    }
                }
                captureSw.Stop();
                captureMs = captureSw.ElapsedMilliseconds;

                if (frameToProcess == null)
                {
                    await _uiController.LogToFrontend("无可用图像进行检测，请先打开相机", "error");
                    return;
                }

                using (var mat = frameToProcess)
                {
                    var inferSw = Stopwatch.StartNew();
                    // 执行检测
                    var result = await _detectionService.DetectAsync(mat, _appConfig.Confidence, overlapThreshold, _appConfig.TargetLabel, _appConfig.TargetCount);
                    inferSw.Stop();
                    inferenceMs = inferSw.ElapsedMilliseconds;

                    bool isQualified = result.IsQualified;
                    finalQualified = isQualified;
                    var results = result.Results ?? new List<YoloResult>();

                    // 应用 ROI 过滤
                    var roiSw = Stopwatch.StartNew();
                    results = FilterResultsByROI(results, mat.Width, mat.Height);
                    roiSw.Stop();
                    roiFilterMs = roiSw.ElapsedMilliseconds;
                    finalResultCount = results.Count;

                    // 将检测结果写入PLC
                    var plcSw = Stopwatch.StartNew();
                    await WriteDetectionResultToPlc(isQualified);
                    plcSw.Stop();
                    plcWriteMs = plcSw.ElapsedMilliseconds;

                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using (var renderedMat = TryRenderDetectionMat(mat, results, labels))
                    {
                        // 发送结果到前端
                        var renderSw = Stopwatch.StartNew();
                        await _uiController.UpdateImage(renderedMat ?? mat);
                        renderSw.Stop();
                        renderToUiMs = renderSw.ElapsedMilliseconds;

                        // 保存图像（不合格时复用同一份渲染结果）
                        var saveSw = Stopwatch.StartNew();
                        _ = await SaveDetectionImage(mat, results, isQualified, result.UsedModelLabels, renderedMat);
                        saveSw.Stop();
                        saveQueueMs = saveSw.ElapsedMilliseconds;
                    }

                    // 日志 (发送到检测流水，包含模型切换信息)
                    string objDesc = GetDetailedDetectionLog(results, labels);
                    string modelInfo = result.WasFallback ? $" [切换至: {result.UsedModelName}]" : "";
                    await _uiController.LogDetectionToFrontend($"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | {inferenceMs}ms{modelInfo}", isQualified ? "success" : "error");

                    // 更新统计
                    _statisticsService.RecordDetection(isQualified);

                    // 写入数据库
                    var dbSw = Stopwatch.StartNew();
                    await _databaseService.SaveDetectionRecordAsync(new DetectionRecord
                    {
                        Timestamp = DateTime.Now,
                        IsQualified = isQualified,
                        ModelName = _detectionService.CurrentModelName,
                        InferenceMs = (int)inferenceMs
                    });
                    dbSw.Stop();
                    dbWriteMs = dbSw.ElapsedMilliseconds;
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"检测异常: {ex.Message}", "error");
            }
            finally
            {
                totalSw.Stop();
                if (captureMs > 0 || inferenceMs > 0 || roiFilterMs > 0 || plcWriteMs > 0 || renderToUiMs > 0 || saveQueueMs > 0 || dbWriteMs > 0)
                {
                    WritePerformanceProfileLog(
                        "手动",
                        finalQualified,
                        totalSw.ElapsedMilliseconds,
                        captureMs,
                        inferenceMs,
                        roiFilterMs,
                        renderToUiMs,
                        saveQueueMs,
                        plcWriteMs,
                        dbWriteMs,
                        1,
                        finalResultCount);
                }

                _detectionSemaphore.Release();
            }
        }

        private Mat? TryRenderDetectionMat(Mat sourceImage, List<YoloResult> results, string[] labels)
        {
            if (results == null || results.Count == 0)
            {
                return null;
            }

            using var bitmap = sourceImage.ToBitmap();
            using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
            return OpenCvSharp.Extensions.BitmapConverter.ToMat(resultImage);
        }

        private Task<string> SaveDetectionImage(Mat image, List<YoloResult> results, bool isQualified, string[]? usedLabels = null, Mat? renderedImage = null)
        {
            try
            {
                DateTime now = DateTime.Now;
                string subFolder = isQualified ? "Qualified" : "Unqualified";
                string dateFolder = now.ToString("yyyy年MM月dd日");
                string hourFolder = now.ToString("HH");
                string directory = Path.Combine(Path_Images, subFolder, dateFolder, hourFolder);

                Directory.CreateDirectory(directory);

                string fileName = $"{(isQualified ? "PASS" : "FAIL")}_{now:HHmmssfff}.jpg";
                string filePath = Path.Combine(directory, fileName);

                // 不合格图像优先复用调用方已渲染结果，避免二次 ToBitmap + 渲染。
                if (!isQualified && results.Count > 0)
                {
                    if (renderedImage != null && !renderedImage.Empty())
                    {
                        _imageSaveQueue.Enqueue(renderedImage, filePath);
                    }
                    else
                    {
                        string[] labels = usedLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                        using var bitmap = image.ToBitmap();
                        using var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels);
                        using var renderedMat = OpenCvSharp.Extensions.BitmapConverter.ToMat(resultImage);
                        _imageSaveQueue.Enqueue(renderedMat, filePath);
                    }
                }
                else
                {
                    _imageSaveQueue.Enqueue(image, filePath);
                }

                return Task.FromResult(filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存检测图像失败: {ex.Message}");
                return Task.FromResult(string.Empty);
            }
        }

        private void WritePerformanceProfileLog(
            string mode,
            bool isQualified,
            long totalMs,
            long captureMs,
            long inferenceMs,
            long roiFilterMs,
            long renderToUiMs,
            long saveQueueMs,
            long plcWriteMs,
            long dbWriteMs,
            int attempts,
            int resultCount)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                sb.AppendLine($"模式: {mode}");
                sb.AppendLine($"总耗时: {totalMs}ms");
                sb.AppendLine($"尝试次数: {Math.Max(1, attempts)} (重试{Math.Max(0, attempts - 1)}次)");
                sb.AppendLine($"目标数量: {resultCount}");
                sb.AppendLine("阶段耗时:");
                sb.AppendLine($"- 取图: {captureMs}ms");
                sb.AppendLine($"- 推理: {inferenceMs}ms");
                sb.AppendLine($"- ROI过滤: {roiFilterMs}ms");
                sb.AppendLine($"- 前端渲染: {renderToUiMs}ms");
                sb.AppendLine($"- 图像入队: {saveQueueMs}ms");
                sb.AppendLine($"- PLC写入: {plcWriteMs}ms");
                sb.AppendLine($"- 数据库写入: {dbWriteMs}ms");

                _storageService.WriteDetectionLog(sb.ToString(), isQualified);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[主窗口-性能日志] 写入失败: {ex.Message}");
            }
        }

        private async Task WriteDetectionResultToPlc(bool isQualified)
        {
            if (_plcService.IsConnected)
            {
                try
                {
                    short resultAddress = (short)_appConfig.PlcResultAddress;
                    bool success = await _plcService.WriteResultAsync(resultAddress, isQualified);
                    if (!success)
                    {
                        await _uiController.LogToFrontend("PLC写入失败: 结果未成功落地", "error");
                    }
                }
                catch (Exception ex)
                {
                    await _uiController.LogToFrontend($"PLC写入失败: {ex.Message}", "error");
                }
            }
        }


        private void btnSettings_Logic()
        {
            // 打开设置对话框 (通过前端密码验证)
            SafeFireAndForget(_uiController.ExecuteScriptAsync("showPasswordModal()"), "显示密码框");
        }

        #endregion

        #region ROI 过滤辅助方法

        /// <summary>
        /// 根据 ROI 区域过滤检测结果（仅保留中心点在 ROI 内的检测框）
        /// </summary>
        private List<YoloResult> FilterResultsByROI(List<YoloResult> results, int imageWidth, int imageHeight)
        {
            if (_currentROI == null || _currentROI.Length != 4 || _currentROI[2] <= 0.001f || _currentROI[3] <= 0.001f)
                return results; // 无 ROI 设置或 ROI 为空（宽度或高度约为0），返回全部结果

            // 将归一化 ROI 转换为像素坐标
            float roiX = _currentROI[0] * imageWidth;
            float roiY = _currentROI[1] * imageHeight;
            float roiW = _currentROI[2] * imageWidth;
            float roiH = _currentROI[3] * imageHeight;

            Debug.WriteLine($"[ROI过滤] ROI区域: X={roiX:F0}, Y={roiY:F0}, W={roiW:F0}, H={roiH:F0}");

            // 过滤：仅保留检测框中心点在 ROI 内的结果
            // 注意：YoloResult 直接有 CenterX, CenterY 属性
            var filtered = results.Where(r =>
            {
                float centerX = r.CenterX;
                float centerY = r.CenterY;
                bool inROI = centerX >= roiX && centerX <= roiX + roiW &&
                             centerY >= roiY && centerY <= roiY + roiH;
                if (!inROI)
                    Debug.WriteLine($"[ROI过滤] 过滤掉: 中心点({centerX:F0},{centerY:F0}) 不在ROI内");
                return inROI;
            }).ToList();

            Debug.WriteLine($"[ROI过滤] 过滤前: {results.Count} 个, 过滤后: {filtered.Count} 个");
            return filtered;
        }

        #endregion
    }
}
