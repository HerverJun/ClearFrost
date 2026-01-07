// ============================================================================
// 文件名: TemplateMatchOp.cs
// 描述:   模板匹配算子 - 使用 OpenCV matchTemplate 查找目标
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;

namespace YOLO.Vision
{
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

                using Mat? inputGrayOwned = input.Channels() > 1 ? new Mat() : null;
                Mat inputGray = inputGrayOwned ?? input;
                if (inputGrayOwned != null) Cv2.CvtColor(input, inputGray, ColorConversionCodes.BGR2GRAY);

                using Mat? templateGrayOwned = _templateImage.Channels() > 1 ? new Mat() : null;
                Mat templateGray = templateGrayOwned ?? _templateImage;
                if (templateGrayOwned != null) Cv2.CvtColor(_templateImage, templateGray, ColorConversionCodes.BGR2GRAY);

                using Mat matchResult = new Mat();
                Cv2.MatchTemplate(inputGray, templateGray, matchResult, _matchMethod);

                Cv2.MinMaxLoc(matchResult, out double minVal, out double maxVal,
                             out OpenCvSharp.Point minLoc, out OpenCvSharp.Point maxLoc);

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

                LastMatchResult = new TemplateMatchResult
                {
                    Location = bestLoc,
                    Score = score,
                    Width = _templateImage.Width,
                    Height = _templateImage.Height,
                    IsMatch = score >= _scoreThreshold
                };

                Mat output = new Mat();
                if (input.Channels() == 1)
                    Cv2.CvtColor(input, output, ColorConversionCodes.GRAY2BGR);
                else
                    input.CopyTo(output);

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
}
