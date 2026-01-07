// ============================================================================
// 文件名: BackgroundDiffOp.cs
// 描述:   背景差分有无检测算子 - 基于背景差分的物体有无检测
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace YOLO.Vision
{
    /// <summary>
    /// 背景差分有无检测算子
    /// </summary>
    public class BackgroundDiffOp : IImageOperator, ITemplateTrainable, IDisposable
    {
        public string Name => "有无检测 (背景差分)";
        public string TypeId => "background_diff";
        public bool IsTrained
        {
            get
            {
                lock (_opLock) return _backgroundGray != null && !_backgroundGray.Empty();
            }
        }

        public Dictionary<string, object> Parameters
        {
            get
            {
                lock (_opLock)
                {
                    return new Dictionary<string, object>
                    {
                        { "minArea", _minArea },
                        { "maxArea", _maxArea },
                        { "minAspectRatio", _minAspectRatio },
                        { "maxAspectRatio", _maxAspectRatio },
                        { "diffThreshold", _diffThreshold },
                        { "useEdgeDiff", _useEdgeDiff },
                        { "roiX", _roiX },
                        { "roiY", _roiY },
                        { "roiW", _roiW },
                        { "roiH", _roiH },
                        { "isTrained", IsTrained }
                    };
                }
            }
        }

        private readonly object _opLock = new();
        private bool _disposed;

        // State
        private Mat? _backgroundGray;
        private Mat? _backgroundEdge;
        private string _backgroundPath = string.Empty;

        // Parameters
        private int _minArea = 5000;
        private int _maxArea = 200000;
        private double _minAspectRatio = 1.5;
        private double _maxAspectRatio = 6.0;
        private int _diffThreshold = 30;
        private bool _useEdgeDiff = true;

        // ROI
        private int _roiX = 0;
        private int _roiY = 0;
        private int _roiW = 0;
        private int _roiH = 0;

        public PresenceDetectionResult? LastResult { get; private set; }

        public void Dispose()
        {
            if (_disposed) return;
            lock (_opLock)
            {
                _backgroundGray?.Dispose();
                _backgroundGray = null;
                _backgroundEdge?.Dispose();
                _backgroundEdge = null;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        public Mat Execute(Mat input)
        {
            if (input == null || input.Empty())
                throw new ArgumentNullException(nameof(input));

            lock (_opLock)
            {
                if (_backgroundGray == null)
                {
                    var err = input.Clone();
                    Cv2.PutText(err, "No Background Trained", new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 1, Scalar.Red, 2);
                    return err;
                }

                // 1. ROI Crop
                Rect roi = GetSafeRoi(input.Width, input.Height);
                using var processedInput = new Mat(input, roi);

                // 2. Preprocess
                using var inputGray = new Mat();
                if (processedInput.Channels() == 3)
                    Cv2.CvtColor(processedInput, inputGray, ColorConversionCodes.BGR2GRAY);
                else
                    processedInput.CopyTo(inputGray);

                if (inputGray.Size() != _backgroundGray.Size())
                {
                    var err = input.Clone();
                    Cv2.PutText(err, "Background Size Mismatch", new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 1, Scalar.Red, 2);
                    return err;
                }

                using var diffMap = new Mat();

                // 3. Difference Calculation
                if (_useEdgeDiff)
                {
                    using var currentEdge = new Mat();
                    Cv2.Canny(inputGray, currentEdge, 50, 150);

                    if (_backgroundEdge == null)
                    {
                        _backgroundEdge = new Mat();
                        Cv2.Canny(_backgroundGray, _backgroundEdge, 50, 150);
                    }

                    Cv2.Absdiff(currentEdge, _backgroundEdge, diffMap);
                }
                else
                {
                    using var diffGray = new Mat();
                    Cv2.Absdiff(inputGray, _backgroundGray, diffGray);
                    Cv2.Threshold(diffGray, diffMap, _diffThreshold, 255, ThresholdTypes.Binary);
                }

                // 4. Morphology
                using var kernelDilate = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
                Cv2.Dilate(diffMap, diffMap, kernelDilate);

                using var kernelClose = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 9));
                Cv2.MorphologyEx(diffMap, diffMap, MorphTypes.Close, kernelClose);

                using var kernelOpen = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
                Cv2.MorphologyEx(diffMap, diffMap, MorphTypes.Open, kernelOpen);

                // 5. Analysis
                Cv2.FindContours(diffMap, out var contours, out var hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                bool found = false;
                Rect bestRect = new Rect();
                double bestArea = 0;
                double maxScore = 0;

                foreach (var contour in contours)
                {
                    double area = Cv2.ContourArea(contour);
                    if (area < _minArea || area > _maxArea) continue;

                    Rect box = Cv2.BoundingRect(contour);
                    double w = box.Width;
                    double h = box.Height;
                    double aspectRatio = Math.Max(w, h) / Math.Min(w, h);

                    if (aspectRatio < _minAspectRatio || aspectRatio > _maxAspectRatio) continue;

                    double boxArea = w * h;
                    double rectangularity = area / boxArea;
                    if (rectangularity < 0.3) continue;

                    if (area > maxScore)
                    {
                        maxScore = area;
                        bestRect = box;
                        bestArea = area;
                        found = true;
                    }
                }

                // 6. Result Construction
                var resultMat = input.Clone();
                Rect globalBestRect = new Rect(bestRect.X + roi.X, bestRect.Y + roi.Y, bestRect.Width, bestRect.Height);
                Rect globalRoi = new Rect(roi.X, roi.Y, roi.Width, roi.Height);

                Cv2.Rectangle(resultMat, globalRoi, Scalar.Yellow, 1);

                LastResult = new PresenceDetectionResult
                {
                    IsPresent = found,
                    Confidence = found ? Math.Min(100, (bestArea / _minArea) * 50) : 0,
                    BoundingBox = globalBestRect,
                    ChangeArea = (int)bestArea,
                    Message = found ? $"Detected Area: {bestArea:F0}" : "No Object"
                };

                if (found)
                {
                    Cv2.Rectangle(resultMat, globalBestRect, Scalar.Lime, 2);
                    Cv2.PutText(resultMat, $"PRESENT ({bestArea:F0})", new OpenCvSharp.Point(globalBestRect.X, globalBestRect.Y - 10), HersheyFonts.HersheySimplex, 0.8, Scalar.Lime, 2);
                }
                else
                {
                    Cv2.PutText(resultMat, "EMPTY", new OpenCvSharp.Point(roi.X + 10, roi.Y + 30), HersheyFonts.HersheySimplex, 0.8, Scalar.Gray, 2);
                }

                return resultMat;
            }
        }

        public void SetParameter(string name, object value)
        {
            lock (_opLock)
            {
                switch (name.ToLower())
                {
                    case "minarea": _minArea = Convert.ToInt32(value); break;
                    case "maxarea": _maxArea = Convert.ToInt32(value); break;
                    case "minaspectratio": _minAspectRatio = Convert.ToDouble(value); break;
                    case "maxaspectratio": _maxAspectRatio = Convert.ToDouble(value); break;
                    case "diffthreshold": _diffThreshold = Convert.ToInt32(value); break;
                    case "useedgediff": _useEdgeDiff = Convert.ToBoolean(value); break;
                    case "roix": _roiX = Convert.ToInt32(value); break;
                    case "roiy": _roiY = Convert.ToInt32(value); break;
                    case "roiw": _roiW = Convert.ToInt32(value); break;
                    case "roih": _roiH = Convert.ToInt32(value); break;
                }
            }
        }

        public List<OperatorParameterInfo> GetParameterInfo()
        {
            return new List<OperatorParameterInfo>
            {
                new() { Name = "minArea", DisplayName = "最小面积", Type = "number", DefaultValue = 5000, CurrentValue = _minArea },
                new() { Name = "maxArea", DisplayName = "最大面积", Type = "number", DefaultValue = 200000, CurrentValue = _maxArea },
                new() { Name = "minAspectRatio", DisplayName = "最小长宽比", Type = "number", DefaultValue = 1.5, CurrentValue = _minAspectRatio },
                new() { Name = "maxAspectRatio", DisplayName = "最大长宽比", Type = "number", DefaultValue = 6.0, CurrentValue = _maxAspectRatio },
                new() { Name = "diffThreshold", DisplayName = "灰度差分阈值", Type = "number", DefaultValue = 30, CurrentValue = _diffThreshold },
                new() { Name = "useEdgeDiff", DisplayName = "使用边缘差分", Type = "boolean", DefaultValue = true, CurrentValue = _useEdgeDiff },
                new() { Name = "roiX", DisplayName = "ROI X", Type = "number", DefaultValue = 0, CurrentValue = _roiX },
                new() { Name = "roiY", DisplayName = "ROI Y", Type = "number", DefaultValue = 0, CurrentValue = _roiY },
                new() { Name = "roiW", DisplayName = "ROI W", Type = "number", DefaultValue = 0, CurrentValue = _roiW },
                new() { Name = "roiH", DisplayName = "ROI H", Type = "number", DefaultValue = 0, CurrentValue = _roiH },
            };
        }

        public void SetTemplateFromMat(Mat template)
        {
            if (template == null || template.Empty()) return;

            lock (_opLock)
            {
                _backgroundGray?.Dispose();
                _backgroundEdge?.Dispose();

                Rect roi = GetSafeRoi(template.Width, template.Height);
                using var cropped = new Mat(template, roi);

                _backgroundGray = new Mat();
                if (cropped.Channels() == 3)
                    Cv2.CvtColor(cropped, _backgroundGray, ColorConversionCodes.BGR2GRAY);
                else
                    cropped.CopyTo(_backgroundGray);

                if (_useEdgeDiff)
                {
                    _backgroundEdge = new Mat();
                    Cv2.Canny(_backgroundGray, _backgroundEdge, 50, 150);
                }
            }
        }

        private Rect GetSafeRoi(int width, int height)
        {
            if (_roiW <= 0 || _roiH <= 0) return new Rect(0, 0, width, height);

            int x = Math.Max(0, _roiX);
            int y = Math.Max(0, _roiY);
            int w = Math.Min(width - x, _roiW);
            int h = Math.Min(height - y, _roiH);
            return new Rect(x, y, w, h);
        }
    }
}
