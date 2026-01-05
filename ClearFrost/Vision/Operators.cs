// ============================================================================
// 文件名: Operators.cs
// 描述:   图像处理算子集合 - 传统视觉检测的核心算法实现
//
// 算子类型:
//   [基础处理]
//     - GrayscaleOp:     灰度转换
//     - BlurOp:          高斯模糊
//     - ThresholdOp:     二值化阈值
//     - CannyOp:         Canny边缘检测
//     - MorphologyOp:    形态学操作 (腐蚀/膨胀/开闭运算)
//
//   [模板匹配]
//     - TemplateMatchOp:      经典模板匹配 (OpenCV matchTemplate)
//     - FeatureMatchOp:       AKAZE特征匹配 (支持双向验证+RANSAC)
//     - OrbMatchOp:           ORB特征匹配 (快速二进制特征)
//     - PyramidShapeMatchOp:  金字塔形状匹配 (基于梯度的工业级匹配)
//
// 接口说明:
//   - IImageOperator:       所有算子的基础接口
//   - ITemplateTrainable:   支持模板训练的算子接口
//   - OperatorFactory:      算子工厂，用于动态创建算子实例
//
// 作者: ClearFrost Team
// 创建日期: 2024
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ShapeMatcher = ClearFrost.Vision;

namespace YOLO.Vision
{
    /// <summary>
    /// 模板匹配算子
    /// 使用模板匹配查找目标
    /// </summary>
    /// <summary>
    /// 模板匹配算子
    /// 使用模板匹配查找目标
    /// </summary>
    public class TemplateMatchOp : IImageOperator, ITemplateTrainable
    {
        public string Name => "模板匹配";
        public string TypeId => "template_match";

        public bool IsTrained => _templateImage != null && !_templateImage.Empty();

        private Mat? _templateImage;
        private string _templatePath = string.Empty;
        private double _scoreThreshold = 0.8;
        private TemplateMatchModes _matchMethod = TemplateMatchModes.CCoeffNormed;
        private string _thumbnailBase64 = string.Empty;

        private readonly object _opLock = new object();

        public Dictionary<string, object> Parameters
        {
            get
            {
                lock (_opLock)
                {
                    return new Dictionary<string, object>
                    {
                        { "isTrained", IsTrained },
                        { "templatePath", _templatePath },
                        { "scoreThreshold", _scoreThreshold },
                        { "templateThumbnail", _thumbnailBase64 },
                        { "matchMethod", (int)_matchMethod }
                    };
                }
            }
        }

        /// <summary>
        /// 模板图像
        /// </summary>
        public Mat? TemplateImage
        {
            get { lock (_opLock) return _templateImage; }
            set
            {
                lock (_opLock)
                {
                    _templateImage?.Dispose();
                    _templateImage = value;
                }
            }
        }

        /// <summary>
        /// 匹配分数阈值
        /// </summary>
        public double ScoreThreshold
        {
            get { lock (_opLock) return _scoreThreshold; }
            set { lock (_opLock) _scoreThreshold = Math.Clamp(value, 0.0, 1.0); }
        }

        /// <summary>
        /// 匹配方法
        /// </summary>
        public TemplateMatchModes MatchMethod
        {
            get { lock (_opLock) return _matchMethod; }
            set { lock (_opLock) _matchMethod = value; }
        }

        /// <summary>
        /// 最后一次匹配的结果
        /// </summary>
        public TemplateMatchResult? LastMatchResult { get; private set; }

        public Mat Execute(Mat input)
        {
            lock (_opLock)
            {
                if (_templateImage == null || _templateImage.Empty())
                {
                    LastMatchResult = null;
                    return input.Clone();
                }

                // 1. 准备灰度图 (若是多通道则转换并自动释放，若是单通道则直接引用)
                // 使用 using 声明 null 对象是合法的，Dispose 不会报错
                using Mat? inputGrayOwned = input.Channels() > 1 ? new Mat() : null;
                Mat inputGray = inputGrayOwned ?? input;
                if (inputGrayOwned != null) Cv2.CvtColor(input, inputGray, ColorConversionCodes.BGR2GRAY);

                using Mat? templateGrayOwned = _templateImage.Channels() > 1 ? new Mat() : null;
                Mat templateGray = templateGrayOwned ?? _templateImage;
                if (templateGrayOwned != null) Cv2.CvtColor(_templateImage, templateGray, ColorConversionCodes.BGR2GRAY);

                // 2. 执行模板匹配
                using Mat matchResult = new Mat();
                Cv2.MatchTemplate(inputGray, templateGray, matchResult, _matchMethod);

                // 3. 查找最佳匹配位置
                Cv2.MinMaxLoc(matchResult, out double minVal, out double maxVal,
                             out OpenCvSharp.Point minLoc, out OpenCvSharp.Point maxLoc);

                // 根据匹配方法确定最佳位置和分数
                OpenCvSharp.Point bestLoc;
                double score;
                if (_matchMethod == TemplateMatchModes.SqDiff ||
                    _matchMethod == TemplateMatchModes.SqDiffNormed)
                {
                    bestLoc = minLoc;
                    score = 1.0 - minVal;
                }
                else
                {
                    bestLoc = maxLoc;
                    score = maxVal;
                }

                // 保存匹配结果
                LastMatchResult = new TemplateMatchResult
                {
                    Location = bestLoc,
                    Score = score,
                    Width = _templateImage.Width,
                    Height = _templateImage.Height,
                    IsMatch = score >= _scoreThreshold
                };

                // 4. 生成输出图像
                Mat output = new Mat();
                if (input.Channels() == 1)
                    Cv2.CvtColor(input, output, ColorConversionCodes.GRAY2BGR);
                else
                    input.CopyTo(output);

                // 5. 绘制结果
                var boxColor = LastMatchResult.IsMatch ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                Cv2.Rectangle(output,
                    new Rect(bestLoc.X, bestLoc.Y, _templateImage.Width, _templateImage.Height),
                    boxColor, 2);

                string statusText = LastMatchResult.IsMatch ? "OK" : "NG";
                string scoreText = $"{statusText}: {score:F3}";
                Cv2.PutText(output, scoreText, new OpenCvSharp.Point(bestLoc.X, bestLoc.Y - 10),
                    HersheyFonts.HersheySimplex, 0.6, boxColor, 2);

                return output;
            }
        }

        public void SetParameter(string paramName, object value)
        {
            lock (_opLock)
            {
                switch (paramName.ToLower())
                {
                    case "templatepath":
                        _templatePath = value?.ToString() ?? string.Empty;
                        LoadTemplate(_templatePath);
                        break;
                    case "scorethreshold":
                        _scoreThreshold = Convert.ToDouble(value);
                        break;
                    case "matchmethod":
                        _matchMethod = (TemplateMatchModes)Convert.ToInt32(value);
                        break;
                    case "templatethumbnail":
                        _thumbnailBase64 = value?.ToString() ?? string.Empty;
                        break;
                }
            }
        }

        public List<OperatorParameterInfo> GetParameterInfo()
        {
            lock (_opLock)
            {
                return new List<OperatorParameterInfo>
                {
                    new OperatorParameterInfo
                    {
                        Name = "scoreThreshold",
                        DisplayName = "匹配阈值",
                        Type = "slider",
                        Min = 0.1,
                        Max = 1.0,
                        Step = 0.05,
                        DefaultValue = 0.8,
                        CurrentValue = _scoreThreshold
                    },
                    new OperatorParameterInfo
                    {
                        Name = "templatePath",
                        DisplayName = "模板图像",
                        Type = "file",
                        DefaultValue = "",
                        CurrentValue = _templatePath
                    }
                };
            }
        }

        /// <summary>
        /// 加载模板图像
        /// </summary>
        private void LoadTemplate(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                _templateImage?.Dispose();
                _templateImage = Cv2.ImRead(path, ImreadModes.Color);
                _thumbnailBase64 = TemplateHelper.GenerateThumbnail(_templateImage);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TemplateMatchOp] 加载模板失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 Mat 设置模板
        /// </summary>
        public void SetTemplateFromMat(Mat template)
        {
            lock (_opLock)
            {
                _templateImage?.Dispose();
                _templateImage = template.Clone();
                _thumbnailBase64 = TemplateHelper.GenerateThumbnail(_templateImage);
            }
        }
    }

    /// <summary>
    /// 特征匹配算子基类
    /// 封装了模板加载、特征提取、匹配、RANSAC验证和绘图的公共逻辑
    /// </summary>
    /// <typeparam name="TDetector">特征检测器类型</typeparam>
    public abstract class FeatureMatchOpBase<TDetector> : IImageOperator, IDisposable, ITemplateTrainable
        where TDetector : class
    {
        public abstract string Name { get; }
        public abstract string TypeId { get; }

        public bool IsTrained => _templateImage != null && !_templateImage.Empty();

        // Common Parameters
        protected int _featureCount = 500;
        protected double _scoreThreshold = 10; // Min Inliers
        protected string _templatePath = string.Empty;
        protected bool _useRansac = true;

        // State
        protected Mat? _templateImage;
        protected KeyPoint[]? _templateKeyPoints;
        protected Mat? _templateDescriptors;
        protected bool _templateProcessed = false;
        protected string _thumbnailBase64 = string.Empty;

        protected TDetector? _detector;
        protected readonly object _opLock = new();
        protected bool _disposed = false;

        public virtual Dictionary<string, object> Parameters
        {
            get
            {
                lock (_opLock)
                {
                    return new Dictionary<string, object>
                    {
                        { "isTrained", IsTrained },
                        { "featureCount", _featureCount },
                        { "scoreThreshold", _scoreThreshold },
                        { "templatePath", _templatePath },
                        { "useRansac", _useRansac },
                        { "templateThumbnail", _thumbnailBase64 }
                    };
                }
            }
        }

        public TemplateMatchResult? LastMatchResult { get; protected set; }

        public FeatureMatchOpBase() { }

        // Abstract / Virtual Methods
        protected abstract void EnsureDetector();
        protected abstract (KeyPoint[], Mat) DetectAndCompute(TDetector detector, Mat image, int maxFeatures);

        /// <summary>
        /// Default matching strategy: KNN=2 with Ratio Test (0.75)
        /// Override to implement Symmetry Test or other strategies.
        /// </summary>
        protected virtual List<DMatch> MatchDescriptors(Mat templateDesc, Mat sceneDesc)
        {
            using var matcher = new BFMatcher(GetMatcherNormType(), crossCheck: false);
            var matches = matcher.KnnMatch(templateDesc, sceneDesc, k: 2);

            var goodMatches = new List<DMatch>();
            foreach (var m in matches)
            {
                if (m.Length >= 2 && m[0].Distance < 0.75 * m[1].Distance)
                {
                    goodMatches.Add(m[0]);
                }
            }
            return goodMatches;
        }

        protected virtual NormTypes GetMatcherNormType() => NormTypes.Hamming;

        public Mat Execute(Mat input)
        {
            lock (_opLock)
            {
                if (_disposed) return input.Clone();

                // 1. Template Loading
                EnsureTemplateLoaded();

                if (_templateImage == null || _templateImage.Empty())
                {
                    LastMatchResult = null;
                    return input.Clone();
                }

                // 2. Process Template Features
                if (!_templateProcessed)
                {
                    ProcessTemplateFeatures();
                }

                if (_templateDescriptors == null || _templateDescriptors.Empty() ||
                    _templateKeyPoints == null || _templateKeyPoints.Length == 0)
                {
                    LastMatchResult = null;
                    return input.Clone();
                }

                // 3. Process Scene Features
                using Mat? inputGrayOwned = input.Channels() > 1 ? new Mat() : null;
                Mat inputGray = inputGrayOwned ?? input;
                if (inputGrayOwned != null) Cv2.CvtColor(input, inputGray, ColorConversionCodes.BGR2GRAY);

                EnsureDetector();

                // Call abstract method for detection
                var (sceneKeyPoints, sceneDescriptors) = DetectAndCompute(_detector!, inputGray, _featureCount);

                // sceneDescriptors ownership is handled here (must be disposed)
                using (sceneDescriptors)
                {
                    if (sceneDescriptors.Empty() || sceneKeyPoints.Length < 4)
                    {
                        LastMatchResult = CreateFailedResult($"场景特征不足 (点数: {sceneKeyPoints.Length} < 4)");
                        return CreateOutputImage(input);
                    }

                    // 4. Feature Matching
                    var goodMatches = MatchDescriptors(_templateDescriptors, sceneDescriptors);

                    // 5. Build Result & RANSAC
                    return ProcessMatches(input, goodMatches, sceneKeyPoints);
                }
            }
        }

        protected void EnsureTemplateLoaded()
        {
            if (_templateImage == null && !string.IsNullOrEmpty(_templatePath) && File.Exists(_templatePath))
            {
                try
                {
                    using var temp = Cv2.ImRead(_templatePath, ImreadModes.Color);
                    SetTemplateFromMatInternal(temp);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[{GetType().Name}] Template load error: {ex.Message}");
                }
            }
        }

        protected void ProcessTemplateFeatures()
        {
            _templateDescriptors?.Dispose();
            _templateDescriptors = null;
            _templateKeyPoints = null;

            using Mat templateGray = new Mat();
            if (_templateImage!.Channels() > 1) Cv2.CvtColor(_templateImage, templateGray, ColorConversionCodes.BGR2GRAY);
            else _templateImage.CopyTo(templateGray);

            EnsureDetector();
            var (kpts, descs) = DetectAndCompute(_detector!, templateGray, _featureCount);

            _templateKeyPoints = kpts;
            _templateDescriptors = descs;
            _templateProcessed = true;
        }

        protected Mat ProcessMatches(Mat input, List<DMatch> goodMatches, KeyPoint[] sceneKeyPoints)
        {
            int inliers = 0;
            bool isMatch = false;
            string failureReason = "";

            // Base score logic
            int score = goodMatches.Count;

            using Mat mask = new Mat();
            Mat? validHomography = null;

            if (_useRansac && goodMatches.Count >= 4)
            {
                var srcPts = goodMatches.Select(m => _templateKeyPoints![m.QueryIdx].Pt).ToArray();
                var dstPts = goodMatches.Select(m => sceneKeyPoints[m.TrainIdx].Pt).ToArray();

                Mat h = Cv2.FindHomography(
                    InputArray.Create(srcPts),
                    InputArray.Create(dstPts),
                    HomographyMethods.Ransac,
                    5.0,
                    mask);

                validHomography = h; // Transfer ownership

                if (!mask.Empty()) inliers = Cv2.CountNonZero(mask);

                // Inlier ratio check
                double inlierRatio = goodMatches.Count > 0 ? (double)inliers / goodMatches.Count : 0;
                bool isInliersPass = inliers >= _scoreThreshold;
                bool isRatioPass = inlierRatio >= 0.25; // Hardcoded safety ratio

                isMatch = isInliersPass && isRatioPass;
                score = inliers;

                if (!isMatch)
                {
                    if (!isInliersPass) failureReason = $"内点不足({inliers} < {_scoreThreshold})";
                    else if (!isRatioPass) failureReason = $"内点率过低({inlierRatio:F2})";
                }
            }
            else
            {
                // Non-RANSAC fallback
                isMatch = score >= _scoreThreshold;
                if (!isMatch) failureReason = ($"匹配点不足({score} < {_scoreThreshold})");
            }

            using (validHomography)
            {
                LastMatchResult = new TemplateMatchResult
                {
                    Score = score,
                    IsMatch = isMatch,
                    Width = _templateImage!.Width,
                    Height = _templateImage.Height,
                    Location = new OpenCvSharp.Point(0, 0),
                    Message = failureReason
                };

                Mat output = CreateOutputImage(input);
                var boxColor = isMatch ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255);

                if (isMatch && validHomography != null && !validHomography.Empty())
                {
                    try { DrawPerspectiveBox(output, validHomography, boxColor); }
                    catch { DrawFallbackBox(output, goodMatches, sceneKeyPoints, boxColor); }
                }
                else if (goodMatches.Count > 0)
                {
                    DrawFallbackBox(output, goodMatches, sceneKeyPoints, boxColor);
                }

                DrawOverlayInfo(output, isMatch, score, goodMatches.Count, boxColor, failureReason);
                return output;
            }
        }

        // --- Helpers ---
        protected Mat CreateOutputImage(Mat input)
        {
            Mat output = new Mat();
            if (input.Channels() == 1) Cv2.CvtColor(input, output, ColorConversionCodes.GRAY2BGR);
            else input.CopyTo(output);
            return output;
        }

        protected TemplateMatchResult CreateFailedResult(string reason)
        {
            return new TemplateMatchResult
            {
                Score = 0,
                IsMatch = false,
                Width = _templateImage?.Width ?? 0,
                Height = _templateImage?.Height ?? 0,
                Message = reason
            };
        }

        protected void DrawPerspectiveBox(Mat output, Mat homography, Scalar color)
        {
            int w = _templateImage!.Width;
            int h = _templateImage.Height;
            var corners = new[] { new Point2f(0, 0), new Point2f(w, 0), new Point2f(w, h), new Point2f(0, h) };
            var projected = Cv2.PerspectiveTransform(corners, homography);
            var points = projected.Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y)).ToArray();

            double areaRatio = Math.Abs(Cv2.ContourArea(points)) / (double)(w * h);
            if (Cv2.IsContourConvex(points) && areaRatio > 0.1 && areaRatio < 4.0)
            {
                for (int i = 0; i < 4; i++) Cv2.Line(output, points[i], points[(i + 1) % 4], color, 3);
                var center = new OpenCvSharp.Point((int)points.Average(p => p.X), (int)points.Average(p => p.Y));
                LastMatchResult!.Location = center;
            }
            else throw new Exception("Invalid shape");
        }

        protected void DrawFallbackBox(Mat output, List<DMatch> matches, KeyPoint[] kpts, Scalar color)
        {
            if (matches.Count == 0) return;
            var pt = kpts[matches[0].TrainIdx].Pt;
            LastMatchResult!.Location = new OpenCvSharp.Point((int)pt.X, (int)pt.Y);
            var rect = new Rect((int)pt.X - _templateImage!.Width / 2, (int)pt.Y - _templateImage.Height / 2, _templateImage.Width, _templateImage.Height);
            Cv2.Rectangle(output, rect, color, 2);
        }

        protected void DrawOverlayInfo(Mat output, bool isMatch, int score, int totalMatches, Scalar color, string extraMsg)
        {
            if (LastMatchResult?.Location.X == 0 && LastMatchResult?.Location.Y == 0 && isMatch) return;

            // Draw failure message if applicable
            if (!isMatch && !string.IsNullOrEmpty(extraMsg))
            {
                Cv2.PutText(output, extraMsg, new OpenCvSharp.Point(10, 30), HersheyFonts.HersheySimplex, 0.6, color, 2);
            }

            if (LastMatchResult != null)
            {
                Cv2.DrawMarker(output, LastMatchResult.Location, color, MarkerTypes.Cross, 20, 2);
                string info = $"{(isMatch ? "OK" : "NG")}: {(_useRansac ? "Inliers" : "Matches")}={score}/{totalMatches}";
                var loc = LastMatchResult.Location;
                Cv2.PutText(output, info, new OpenCvSharp.Point(loc.X == 0 ? 10 : loc.X, loc.Y == 0 ? 60 : loc.Y - 10), HersheyFonts.HersheySimplex, 0.6, color, 2);
            }
        }

        public abstract List<OperatorParameterInfo> GetParameterInfo();

        public void SetParameter(string paramName, object value)
        {
            lock (_opLock)
            {
                if (SetParameterInternal(paramName.ToLower(), value))
                {
                    // If parameter changed that affects template cache, base logic handles it here?
                    // Implementing classes should report if cache invalidation needed OR handle it themselves.
                    // For simplicity, let implementing method handle specific params, but we handle common ones.
                }
            }
        }

        /// <summary>
        /// Handle common parameters. Returns true if parameter was handled.
        /// </summary>
        protected virtual bool SetParameterInternal(string name, object value)
        {
            switch (name)
            {
                case "featurecount":
                    int fc = Convert.ToInt32(value);
                    if (_featureCount != fc) { _featureCount = fc; InvalidateTemplateCache(); }
                    return true;
                case "scorethreshold": _scoreThreshold = Convert.ToDouble(value); return true;
                case "useransac": _useRansac = Convert.ToBoolean(value); return true;
                case "templatethumbnail": _thumbnailBase64 = value?.ToString() ?? ""; return true;
                case "templatepath":
                    string np = value?.ToString() ?? "";
                    if (_templatePath != np)
                    {
                        _templatePath = np;
                        _templateImage?.Dispose(); _templateImage = null;
                        InvalidateTemplateCache();
                    }
                    return true;
            }
            return false;
        }

        protected void InvalidateTemplateCache()
        {
            _templateProcessed = false;
            _templateKeyPoints = null;
            _templateDescriptors?.Dispose();
            _templateDescriptors = null;
        }

        public void SetTemplateFromMat(Mat template)
        {
            lock (_opLock) SetTemplateFromMatInternal(template);
        }

        protected void SetTemplateFromMatInternal(Mat template)
        {
            _templateImage?.Dispose();
            _templateImage = template.Clone();
            _thumbnailBase64 = TemplateHelper.GenerateThumbnail(_templateImage);
            InvalidateTemplateCache();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                lock (_opLock)
                {
                    _templateImage?.Dispose(); _templateImage = null;
                    _templateDescriptors?.Dispose(); _templateDescriptors = null;
                    if (_detector is IDisposable d) d.Dispose();
                    _detector = default;
                    _templateKeyPoints = null;
                }
            }
            _disposed = true;
        }

        ~FeatureMatchOpBase()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// AKAZE 特征匹配算子 (工业增强版)
    /// </summary>
    public class FeatureMatchOp : FeatureMatchOpBase<AKAZE>
    {
        public override string Name => "Feature Match (AKAZE)";
        public override string TypeId => "feature_match";

        private float _akazeThreshold = 0.001f;
        private bool _useSymmetryTest = true;

        public FeatureMatchOp() { }

        public override Dictionary<string, object> Parameters
        {
            get
            {
                var dict = base.Parameters;
                dict["akazeThreshold"] = _akazeThreshold;
                dict["useSymmetryTest"] = _useSymmetryTest;
                return dict;
            }
        }

        protected override void EnsureDetector()
        {
            if (_detector == null)
            {
                _detector = AKAZE.Create(threshold: _akazeThreshold);
            }
        }

        protected override bool SetParameterInternal(string name, object value)
        {
            if (base.SetParameterInternal(name, value)) return true;

            switch (name)
            {
                case "akazethreshold":
                    float newThres = Convert.ToSingle(value);
                    if (Math.Abs(_akazeThreshold - newThres) > 0.00001f)
                    {
                        _akazeThreshold = Math.Clamp(newThres, 0.0001f, 0.1f);
                        _detector?.Dispose();
                        _detector = null;
                        InvalidateTemplateCache();
                    }
                    return true;
                case "usesymmetrytest": _useSymmetryTest = Convert.ToBoolean(value); return true;
            }
            return false;
        }

        // Implementation of Detection
        protected override (KeyPoint[], Mat) DetectAndCompute(AKAZE detector, Mat image, int maxFeatures)
        {
            KeyPoint[] allKeyPoints;
            using Mat allDescriptors = new Mat();

            detector.DetectAndCompute(image, null, out allKeyPoints, allDescriptors);

            // Filter to maxFeatures
            if (allKeyPoints.Length > maxFeatures)
            {
                FilterKeyPointsAndDescriptors(allKeyPoints, allDescriptors, maxFeatures, out var finalKpts, out var finalDescs);
                return (finalKpts, finalDescs);
            }
            else
            {
                return (allKeyPoints, allDescriptors.Clone());
            }
        }

        // Override Matching to support Symmetry Test
        protected override List<DMatch> MatchDescriptors(Mat templateDesc, Mat sceneDesc)
        {
            if (_useSymmetryTest)
            {
                using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
                var forwardMatches = matcher.KnnMatch(templateDesc, sceneDesc, k: 2);
                var backwardMatches = matcher.KnnMatch(sceneDesc, templateDesc, k: 2);

                var backwardBest = new Dictionary<int, int>();
                foreach (var m in backwardMatches)
                {
                    if (m.Length >= 2)
                    {
                        if (m[0].Distance < 0.75 * m[1].Distance) backwardBest[m[0].QueryIdx] = m[0].TrainIdx;
                    }
                    else if (m.Length == 1)
                    {
                        backwardBest[m[0].QueryIdx] = m[0].TrainIdx;
                    }
                }

                var goodMatches = new List<DMatch>();
                foreach (var m in forwardMatches)
                {
                    if (m.Length < 2) continue;
                    if (m[0].Distance >= 0.75 * m[1].Distance) continue;

                    if (backwardBest.TryGetValue(m[0].TrainIdx, out int reverseTemplateIdx) && reverseTemplateIdx == m[0].QueryIdx)
                    {
                        goodMatches.Add(m[0]);
                    }
                }
                return goodMatches;
            }
            else
            {
                return base.MatchDescriptors(templateDesc, sceneDesc);
            }
        }

        private void FilterKeyPointsAndDescriptors(KeyPoint[] kpts, Mat descs, int count, out KeyPoint[] outKpts, out Mat outDescs)
        {
            int[] indices = new int[kpts.Length];
            for (int i = 0; i < kpts.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) => kpts[b].Response.CompareTo(kpts[a].Response));

            int safeCount = Math.Min(count, kpts.Length);
            outKpts = new KeyPoint[safeCount];
            outDescs = new Mat(safeCount, descs.Cols, descs.Type());

            for (int i = 0; i < safeCount; i++)
            {
                int originalIdx = indices[i];
                outKpts[i] = kpts[originalIdx];
                using var srcRow = descs.Row(originalIdx);
                using var dstRow = outDescs.Row(i);
                srcRow.CopyTo(dstRow);
            }
        }

        public override List<OperatorParameterInfo> GetParameterInfo() => new()
        {
            new OperatorParameterInfo { Name = "featureCount", DisplayName = "特征点上限", Type = "slider", Min = 100, Max = 2000, Step = 100, DefaultValue = 500, CurrentValue = _featureCount },
            new OperatorParameterInfo { Name = "scoreThreshold", DisplayName = "评分阈值", Type = "slider", Min = 4, Max = 20, Step = 1, DefaultValue = 10, CurrentValue = _scoreThreshold },
            new OperatorParameterInfo { Name = "akazeThreshold", DisplayName = "检测灵敏度", Type = "slider", Min = 0.0001, Max = 0.01, Step = 0.0001, DefaultValue = 0.001, CurrentValue = _akazeThreshold },
            new OperatorParameterInfo { Name = "useSymmetryTest", DisplayName = "启用双向验证", Type = "checkbox", DefaultValue = true, CurrentValue = _useSymmetryTest },
            new OperatorParameterInfo { Name = "useRansac", DisplayName = "启用RANSAC", Type = "checkbox", DefaultValue = true, CurrentValue = _useRansac }
        };
    }

    /// <summary>
    /// ORB特征匹配算子 (同步增强版)
    /// </summary>
    public class OrbMatchOp : FeatureMatchOpBase<ClearFrost.Vision.RobustOrbExtractor>
    {
        public override string Name => "Feature Match (ORB)";
        public override string TypeId => "orb_match";

        // Robust ORB parameters
        private int _nLevels = 8;
        private float _scaleFactor = 1.2f;
        private int _iniThFast = 20;
        private int _minThFast = 7;

        public OrbMatchOp() { }

        protected override void EnsureDetector()
        {
            if (_detector == null)
            {
                _detector = new ClearFrost.Vision.RobustOrbExtractor(_nLevels, _scaleFactor, _iniThFast, _minThFast);
            }
        }

        public override Dictionary<string, object> Parameters
        {
            get
            {
                var dict = base.Parameters;
                dict["nLevels"] = _nLevels;
                dict["scaleFactor"] = _scaleFactor;
                dict["iniThFast"] = _iniThFast;
                dict["minThFast"] = _minThFast;
                return dict;
            }
        }

        protected override bool SetParameterInternal(string name, object value)
        {
            if (base.SetParameterInternal(name, value)) return true;

            bool needReset = false;
            switch (name)
            {
                case "nlevels": _nLevels = Convert.ToInt32(value); needReset = true; break;
                case "scalefactor": _scaleFactor = (float)Convert.ToDouble(value); needReset = true; break;
                case "inithfast": _iniThFast = Convert.ToInt32(value); needReset = true; break;
                case "minthfast": _minThFast = Convert.ToInt32(value); needReset = true; break;
            }

            if (needReset)
            {
                // RobustOrbExtractor does not implement IDisposable, just null it out.
                _detector = null;
                InvalidateTemplateCache();
                return true;
            }
            return false;
        }

        protected override (KeyPoint[], Mat) DetectAndCompute(ClearFrost.Vision.RobustOrbExtractor detector, Mat image, int maxFeatures)
        {
            // RobustOrbExtractor handles filtering internally if implemented so.
            // Based on previous code, it returns keypoints and descriptors.
            // The previous implementation utilized _featureCount inside Execute, passing it to RobustOrbExtractor.DetectAndCompute.
            // We ensure we pass maxFeatures here.
            return detector.DetectAndCompute(image, maxFeatures);
        }

        public override List<OperatorParameterInfo> GetParameterInfo() => new()
        {
            new OperatorParameterInfo { Name = "featureCount", DisplayName = "特征点上限", Type = "slider", Min = 100, Max = 2000, Step = 100, DefaultValue = 500, CurrentValue = _featureCount },
            new OperatorParameterInfo { Name = "scoreThreshold", DisplayName = "最小内点数", Type = "slider", Min = 4, Max = 100, Step = 1, DefaultValue = 10, CurrentValue = _scoreThreshold },
            new OperatorParameterInfo { Name = "nLevels", DisplayName = "金字塔层数", Type = "slider", Min = 1, Max = 12, Step = 1, DefaultValue = 8, CurrentValue = _nLevels },
            new OperatorParameterInfo { Name = "scaleFactor", DisplayName = "尺度因子", Type = "slider", Min = 1.0, Max = 2.0, Step = 0.1, DefaultValue = 1.2, CurrentValue = _scaleFactor },
            new OperatorParameterInfo { Name = "iniThFast", DisplayName = "FAST主阈值", Type = "slider", Min = 5, Max = 50, Step = 1, DefaultValue = 20, CurrentValue = _iniThFast },
            new OperatorParameterInfo { Name = "minThFast", DisplayName = "FAST最小阈值", Type = "slider", Min = 2, Max = 20, Step = 1, DefaultValue = 7, CurrentValue = _minThFast }
        };
    }

    /// <summary>
    /// 金字塔形状匹配算子 (工业级, 抗光照)
    /// 基于 VisionPro/Halcon 风格的梯度方向匹配
    /// </summary>
    public class PyramidShapeMatchOp : IImageOperator, IDisposable, ITemplateTrainable
    {
        public string Name => "形状匹配 (金字塔)";
        public string TypeId => "pyramid_shape_match";

        public bool IsTrained => _isTrained;

        // 核心匹配器实例
        private ShapeMatcher.PyramidShapeMatcher? _matcher;
        private bool _isTrained = false;
        private ShapeMatcher.MatchResult _lastResult = ShapeMatcher.MatchResult.Empty;

        // 可调参数
        private int _pyramidLevels = 3;
        private int _magnitudeThreshold = 30;
        private int _angleRange = 180;
        private int _angleStep = 1;
        private double _minScore = 80;

        // 模板尺寸 (训练时记录)
        private int _templateWidth = 100;
        private int _templateHeight = 100;
        private string _thumbnailBase64 = string.Empty;

        // 线程安全锁
        private readonly object _opLock = new();
        private bool _disposed = false;

        public Dictionary<string, object> Parameters => new()
        {
            { "pyramidLevels", _pyramidLevels },
            { "magnitudeThreshold", _magnitudeThreshold },
            { "angleRange", _angleRange },
            { "angleStep", _angleStep },
            { "minScore", _minScore },

            { "isTrained", IsTrained },
            { "templateThumbnail", _thumbnailBase64 }
        };

        public TemplateMatchResult? LastMatchResult { get; private set; }

        /// <summary>
        /// 训练模板 (从 ROI 区域)
        /// </summary>
        public void Train(Mat templateImage, int angleRange = 180)
        {
            lock (_opLock)
            {
                _matcher?.Dispose();
                _matcher = new ShapeMatcher.PyramidShapeMatcher(_pyramidLevels, _magnitudeThreshold, _angleStep);
                _matcher.Train(templateImage, angleRange);
                _templateWidth = templateImage.Width;
                _templateHeight = templateImage.Height;
                _thumbnailBase64 = TemplateHelper.GenerateThumbnail(templateImage);
                _isTrained = true;
                _angleRange = angleRange;
            }
        }

        public Mat Execute(Mat input)
        {
            lock (_opLock)
            {
                if (_disposed || !_isTrained || _matcher == null)
                {
                    LastMatchResult = new TemplateMatchResult
                    {
                        Score = 0,
                        IsMatch = false,
                        Message = "未训练模板"
                    };
                    return input.Clone();
                }

                try
                {
                    _lastResult = _matcher.Match(input, _minScore);
                }
                catch (Exception ex)
                {
                    LastMatchResult = new TemplateMatchResult
                    {
                        Score = 0,
                        IsMatch = false,
                        Message = $"匹配异常: {ex.Message}"
                    };
                    return input.Clone();
                }

                bool isMatch = _lastResult.IsValid && _lastResult.Score >= _minScore;
                LastMatchResult = new TemplateMatchResult
                {
                    Location = _lastResult.Position,
                    Score = _lastResult.Score,
                    Width = _templateWidth,
                    Height = _templateHeight,
                    IsMatch = isMatch,
                    Message = isMatch ? "OK" : $"分数不足 ({_lastResult.Score:F1} < {_minScore})"
                };

                // 绘制结果
                Mat output;
                if (input.Channels() == 1)
                {
                    output = new Mat();
                    Cv2.CvtColor(input, output, ColorConversionCodes.GRAY2BGR);
                }
                else
                {
                    output = input.Clone();
                }

                DrawResult(output);
                return output;
            }
        }

        /// <summary>
        /// 绘制匹配结果（旋转矩形）
        /// </summary>
        private void DrawResult(Mat display)
        {
            if (!_lastResult.IsValid) return;

            var color = LastMatchResult?.IsMatch == true
                ? new Scalar(0, 255, 0)   // 亮绿色
                : new Scalar(0, 0, 255);  // 红色

            // 计算旋转矩形四个角点
            double angleRad = _lastResult.Angle * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);

            int halfW = _templateWidth / 2;
            int halfH = _templateHeight / 2;

            var corners = new Point2f[4];
            corners[0] = RotatePoint(-halfW, -halfH, cos, sin, _lastResult.Position);
            corners[1] = RotatePoint(halfW, -halfH, cos, sin, _lastResult.Position);
            corners[2] = RotatePoint(halfW, halfH, cos, sin, _lastResult.Position);
            corners[3] = RotatePoint(-halfW, halfH, cos, sin, _lastResult.Position);

            var pts = corners.Select(p => new OpenCvSharp.Point((int)p.X, (int)p.Y)).ToArray();

            // 绘制旋转矩形 (使用 Polylines)
            Cv2.Polylines(display, new[] { pts }, true, color, 2);

            // 绘制中心十字
            Cv2.DrawMarker(display, _lastResult.Position, color, MarkerTypes.Cross, 20, 2);

            // 绘制分数和角度
            string info = $"Score: {_lastResult.Score:F1}% | Angle: {_lastResult.Angle:F1}";
            Cv2.PutText(display, info,
                new OpenCvSharp.Point(_lastResult.Position.X - 80, _lastResult.Position.Y - 30),
                HersheyFonts.HersheySimplex, 0.5, color, 2);
        }

        private static Point2f RotatePoint(float x, float y, double cos, double sin, OpenCvSharp.Point center)
        {
            return new Point2f(
                (float)(center.X + x * cos - y * sin),
                (float)(center.Y + x * sin + y * cos));
        }

        public void SetParameter(string paramName, object value)
        {
            lock (_opLock)
            {
                switch (paramName.ToLower())
                {
                    case "pyramidlevels":
                        int newLevels = Convert.ToInt32(value);
                        if (_pyramidLevels != newLevels) { _pyramidLevels = Math.Clamp(newLevels, 1, 5); _isTrained = false; }
                        break;
                    case "magnitudethreshold":
                        int newMag = Convert.ToInt32(value);
                        if (_magnitudeThreshold != newMag) { _magnitudeThreshold = Math.Clamp(newMag, 10, 100); _isTrained = false; }
                        break;
                    case "anglerange":
                        _angleRange = Math.Clamp(Convert.ToInt32(value), 0, 180);
                        break;
                    case "anglestep":
                        int newStep = Convert.ToInt32(value);
                        if (_angleStep != newStep) { _angleStep = Math.Clamp(newStep, 1, 10); _isTrained = false; }
                        break;
                    case "minscore":
                        _minScore = Math.Clamp(Convert.ToDouble(value), 50, 99);
                        break;
                    case "templatethumbnail":
                        _thumbnailBase64 = value?.ToString() ?? string.Empty;
                        break;
                }
            }
        }

        public List<OperatorParameterInfo> GetParameterInfo() => new()
        {
            new() { Name = "pyramidLevels", DisplayName = "金字塔层数", Type = "slider", Min = 1, Max = 5, Step = 1, DefaultValue = 3, CurrentValue = _pyramidLevels },
            new() { Name = "magnitudeThreshold", DisplayName = "梯度阈值", Type = "slider", Min = 10, Max = 100, Step = 5, DefaultValue = 30, CurrentValue = _magnitudeThreshold },
            new() { Name = "angleRange", DisplayName = "搜索角度范围 (±度)", Type = "slider", Min = 0, Max = 180, Step = 15, DefaultValue = 180, CurrentValue = _angleRange },
            new() { Name = "angleStep", DisplayName = "角度步进", Type = "slider", Min = 1, Max = 10, Step = 1, DefaultValue = 1, CurrentValue = _angleStep },
            new() { Name = "minScore", DisplayName = "最小匹配分数 (%)", Type = "slider", Min = 50, Max = 99, Step = 1, DefaultValue = 80, CurrentValue = _minScore }
        };

        public void SetTemplateFromMat(Mat template) => Train(template, _angleRange);

        public void Dispose()
        {
            if (_disposed) return;
            lock (_opLock)
            {
                _matcher?.Dispose();
                _matcher = null;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }

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
        private int _diffThreshold = 30; // For gray diff
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
                    // No background trained
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

                // Check sizes match (important if ROI changed or background set differently)
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
                    // Edge Difference
                    using var currentEdge = new Mat();
                    Cv2.Canny(inputGray, currentEdge, 50, 150);

                    if (_backgroundEdge == null) // Should have been created in Train, but safety check
                    {
                        _backgroundEdge = new Mat();
                        Cv2.Canny(_backgroundGray, _backgroundEdge, 50, 150);
                    }

                    Cv2.Absdiff(currentEdge, _backgroundEdge, diffMap);
                }
                else
                {
                    // Grayscale Difference
                    using var diffGray = new Mat();
                    Cv2.Absdiff(inputGray, _backgroundGray, diffGray);
                    Cv2.Threshold(diffGray, diffMap, _diffThreshold, 255, ThresholdTypes.Binary);
                }

                // 4. Morphology
                // Connect edges/regions
                using var kernelDilate = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
                Cv2.Dilate(diffMap, diffMap, kernelDilate);

                // Fill holes
                using var kernelClose = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 9));
                Cv2.MorphologyEx(diffMap, diffMap, MorphTypes.Close, kernelClose);

                // Remove noise (Open)
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

                    // Rectangularity check: Area / BoxArea
                    double boxArea = w * h;
                    double rectangularity = area / boxArea;
                    if (rectangularity < 0.3) continue; // Skip very irregular shapes

                    // Logic to pick "best" (largest change)
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
                // Map local ROI coordinates back to global
                Rect globalBestRect = new Rect(bestRect.X + roi.X, bestRect.Y + roi.Y, bestRect.Width, bestRect.Height);
                Rect globalRoi = new Rect(roi.X, roi.Y, roi.Width, roi.Height);

                // Draw ROI
                Cv2.Rectangle(resultMat, globalRoi, Scalar.Yellow, 1);

                LastResult = new PresenceDetectionResult
                {
                    IsPresent = found,
                    Confidence = found ? Math.Min(100, (bestArea / _minArea) * 50) : 0, // Rough confidence
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
                
                // ROI params usually handled by UI via specialized controls, but exposed here for completeness
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
                // clean old
                _backgroundGray?.Dispose();
                _backgroundEdge?.Dispose();

                // 1. Calculate safe ROI to crop for template
                // The template image might be full frame. We should crop it to ROI if ROI is set.
                // However, usually we set ROI *based* on the view. 
                // Let's assume the user sends the Full Image as "Background".
                // We crop it using the current ROI settings to store only the relevant part?
                // OR we store the full background and crop at runtime.
                // Storing full background allows changing ROI later without re-training. 
                // BUT, if we change ROI, the background patch changes.
                // Let's store the ROI-cropped version if ROI is valid at training time?
                // Requirement: "ROI设置生效，只检测指定区域"
                // Decision: Store the FULL background, crop dynamically in Execute.
                // UNLESS the ROI crop is part of the template definition.
                // To keep it simple: Train takes the full image. We store the cropped version if ROI is non-zero,
                // OR we store the full image and crop in Execute.
                // Storing the FULL image is safer for robustness if ROI changes.
                // BUT, the problem is _backgroundGray must match inputGray size in Execute.
                // So: In Execute `if (inputGray.Size() != _backgroundGray.Size())`
                // So we MUST store the background corresponding to the ROI.
                // Actually, let's allow `SetTemplateFromMat` to just take the "ROI" part if the UI sends it cropped?
                // Typically ITemplateTrainable sends what's in the box.
                // Let's assume UpdateTemplate logic in UI sends the cropped image OR full image.
                // If it sends full image, we crop it here.

                // For simplicity and consistency with Execute:
                // We assume the user might set ROI parameters *before* or *after* training.
                // If we want to support changing ROI *after* training, we need the full background.
                // BUT `_roiX` etc are parameters.
                // Let's store the full background patch corresponding to the ROI *at the time of training*?
                // No, better to store the *result of the crop* based on current ROI.
                // If the user processes a full frame, they set ROI parameters.
                // The training image provided `SetTemplateFromMat` is usually the "Template".
                // In this case, it is the "Background".
                // Let's attempt to crop the training image by the current ROI.

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
            // If ROI is default 0,0,0,0 or invalid, use full image
            if (_roiW <= 0 || _roiH <= 0) return new Rect(0, 0, width, height);

            int x = Math.Max(0, _roiX);
            int y = Math.Max(0, _roiY);
            int w = Math.Min(width - x, _roiW);
            int h = Math.Min(height - y, _roiH);
            return new Rect(x, y, w, h);
        }
    }

    public class PresenceDetectionResult
    {
        public bool IsPresent { get; set; }
        public double Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public int ChangeArea { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 模板匹配结果
    /// </summary>
    public class TemplateMatchResult
    {
        public OpenCvSharp.Point Location { get; set; }
        public double Score { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMatch { get; set; }
        public string Message { get; set; } = string.Empty;
        public Rect BoundingBox => new Rect(Location.X, Location.Y, Width, Height);
    }

    /// <summary>
    /// 算子工厂
    /// </summary>
    public static class OperatorFactory
    {
        public static IImageOperator? Create(string typeId)
        {
            return typeId.ToLower() switch
            {
                "template_match" => new TemplateMatchOp(),
                "feature_match" => new FeatureMatchOp(),
                "orb_match" => new OrbMatchOp(),
                "pyramid_shape_match" => new PyramidShapeMatchOp(),
                "background_diff" => new BackgroundDiffOp(),
                _ => null
            };
        }

        public static IImageOperator? CreateFromConfig(OperatorConfig config)
        {
            var op = Create(config.TypeId);
            if (op == null) return null;

            foreach (var param in config.Parameters)
            {
                op.SetParameter(param.Key, param.Value);
            }
            return op;
        }

        public static List<OperatorInfo> GetAvailableOperators() => new()
        {
            new OperatorInfo { TypeId = "template_match", Name = "模板匹配", Description = "使用模板查找目标位置" },
            new OperatorInfo { TypeId = "feature_match", Name = "特征匹配 (AKAZE)", Description = "基于AKAZE特征点的抗旋转缩放匹配" },
            new OperatorInfo { TypeId = "orb_match", Name = "特征匹配 (ORB)", Description = "基于ORB特征点的高速匹配" },
            new OperatorInfo { TypeId = "pyramid_shape_match", Name = "形状匹配 (金字塔)", Description = "工业级抗光照梯度方向匹配" },
            new OperatorInfo { TypeId = "background_diff", Name = "有无检测 (背景差分)", Description = "基于背景差分的物体有无检测" }
        };
    }

    /// <summary>
    /// 算子信息
    /// </summary>
    public class OperatorInfo
    {
        public string TypeId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
