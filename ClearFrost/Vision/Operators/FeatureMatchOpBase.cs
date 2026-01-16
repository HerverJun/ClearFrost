// ============================================================================
// 
// 
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 
    /// 
    /// </summary>
    /// 
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

                EnsureTemplateLoaded();

                if (_templateImage == null || _templateImage.Empty())
                {
                    LastMatchResult = null;
                    return input.Clone();
                }

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

                using Mat? inputGrayOwned = input.Channels() > 1 ? new Mat() : null;
                Mat inputGray = inputGrayOwned ?? input;
                if (inputGrayOwned != null) Cv2.CvtColor(input, inputGray, ColorConversionCodes.BGR2GRAY);

                EnsureDetector();

                var (sceneKeyPoints, sceneDescriptors) = DetectAndCompute(_detector!, inputGray, _featureCount);

                using (sceneDescriptors)
                {
                    if (sceneDescriptors.Empty() || sceneKeyPoints.Length < 4)
                    {
                        LastMatchResult = CreateFailedResult($"场景特征点不足 (数量: {sceneKeyPoints.Length} < 4)");
                        return CreateOutputImage(input);
                    }

                    var goodMatches = MatchDescriptors(_templateDescriptors, sceneDescriptors);

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

                validHomography = h;

                if (!mask.Empty()) inliers = Cv2.CountNonZero(mask);

                double inlierRatio = goodMatches.Count > 0 ? (double)inliers / goodMatches.Count : 0;
                bool isInliersPass = inliers >= _scoreThreshold;
                bool isRatioPass = inlierRatio >= 0.25;

                isMatch = isInliersPass && isRatioPass;
                score = inliers;

                if (!isMatch)
                {
                    if (!isInliersPass) failureReason = $"内点数不足 ({inliers} < {_scoreThreshold})";
                    else if (!isRatioPass) failureReason = $"内点比例不足 ({inlierRatio:F2})";
                }
            }
            else
            {
                isMatch = score >= _scoreThreshold;
                if (!isMatch) failureReason = ($"匹配点数不足 ({score} < {_scoreThreshold})");
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
                SetParameterInternal(paramName.ToLower(), value);
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
}

