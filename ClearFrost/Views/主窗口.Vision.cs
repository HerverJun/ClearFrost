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

                    // 生成带标注的结果图像
                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using (var resultImage = _detectionService.GenerateResultImage(originalBitmap, results, labels))
                    {
                        // 发送结果图像到前端
                        using (var ms = new MemoryStream())
                        {
                            resultImage.Save(ms, ImageFormat.Jpeg);
                            string base64 = Convert.ToBase64String(ms.ToArray());
                            await _uiController.UpdateImage(base64);
                        }
                    }

                    // 更新UI
                    string objDesc = results.Count > 0 ? $"检测到 {results.Count} 个目标" : "未检测到目标";
                    await _uiController.LogToFrontend($"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | 耗时: {sw.ElapsedMilliseconds}ms", isQualified ? "success" : "error");

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
        private async Task btnCapture_LogicAsync()
        {
            // 使用信号量防止并发检测
            if (!await _detectionSemaphore.WaitAsync(0))
            {
                await _uiController.LogToFrontend("检测正在进行中，请稍候...", "warning");
                return;
            }

            try
            {
                await _uiController.LogToFrontend("开始检测...", "info");

                Mat? frameToProcess = null;
                lock (_frameLock)
                {
                    if (_lastCapturedFrame != null && !_lastCapturedFrame.Empty())
                    {
                        frameToProcess = _lastCapturedFrame.Clone();
                    }
                }

                if (frameToProcess == null)
                {
                    await _uiController.LogToFrontend("无可用图像进行检测，请先打开相机", "error");
                    return;
                }

                using (var mat = frameToProcess)
                {
                    var sw = Stopwatch.StartNew();

                    // 执行检测
                    var result = await _detectionService.DetectAsync(mat, _appConfig.Confidence, overlapThreshold, _appConfig.TargetLabel, _appConfig.TargetCount);

                    sw.Stop();

                    bool isQualified = result.IsQualified;
                    var results = result.Results ?? new List<YoloResult>();

                    // 应用 ROI 过滤
                    results = FilterResultsByROI(results, mat.Width, mat.Height);

                    // 将检测结果写入PLC
                    await WriteDetectionResultToPlc(isQualified);

                    // 保存图像
                    string imagePath = await SaveDetectionImage(mat, results, isQualified, result.UsedModelLabels);

                    // 发送结果到前端
                    string[] labels = result.UsedModelLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using (var bitmap = mat.ToBitmap())
                    using (var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels))
                    using (var ms = new MemoryStream())
                    {
                        resultImage.Save(ms, ImageFormat.Jpeg);
                        string base64 = Convert.ToBase64String(ms.ToArray());
                        await _uiController.UpdateImage(base64);
                    }

                    // 日志
                    string objDesc = results.Count > 0 ? $"检测到 {results.Count} 个目标" : "未检测到目标";
                    await _uiController.LogToFrontend($"检测完成: {(isQualified ? "合格" : "不合格")} | {objDesc} | 耗时: {sw.ElapsedMilliseconds}ms", isQualified ? "success" : "error");

                    // 更新统计
                    _statisticsService.RecordDetection(isQualified);

                    // 写入数据库
                    await _databaseService.SaveDetectionRecordAsync(new DetectionRecord
                    {
                        Timestamp = DateTime.Now,
                        IsQualified = isQualified,
                        ModelName = _detectionService.CurrentModelName,
                        InferenceMs = (int)sw.ElapsedMilliseconds
                    });
                }
            }
            catch (Exception ex)
            {
                await _uiController.LogToFrontend($"检测异常: {ex.Message}", "error");
            }
            finally
            {
                _detectionSemaphore.Release();
            }
        }

        private async Task<string> SaveDetectionImage(Mat image, List<YoloResult> results, bool isQualified, string[]? usedLabels = null)
        {
            try
            {
                string subFolder = isQualified ? "OK" : "NG";
                string dateFolder = DateTime.Now.ToString("yyyy-MM-dd");
                string directory = Path.Combine(Path_Images, subFolder, dateFolder);

                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                string fileName = $"{DateTime.Now:HHmmss_fff}.jpg";
                string filePath = Path.Combine(directory, fileName);

                // 如果有检测结果，先绘制边框
                if (results.Count > 0)
                {
                    string[] labels = usedLabels ?? _detectionService.GetLabels() ?? Array.Empty<string>();
                    using (var bitmap = image.ToBitmap())
                    using (var resultImage = _detectionService.GenerateResultImage(bitmap, results, labels))
                    {
                        resultImage.Save(filePath, ImageFormat.Jpeg);
                    }
                }
                else
                {
                    Cv2.ImWrite(filePath, image);
                }

                return filePath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存检测图像失败: {ex.Message}");
                return "";
            }
        }

        private async Task WriteDetectionResultToPlc(bool isQualified)
        {
            if (_plcService.IsConnected)
            {
                try
                {
                    short resultAddress = (short)_appConfig.PlcResultAddress;
                    await _plcService.WriteResultAsync(resultAddress, isQualified);
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
            if (_currentROI == null || _currentROI.Length != 4)
                return results; // 无 ROI 设置，返回全部结果

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
