// ============================================================================
// 文件名: PyramidShapeMatchOp.cs
// 描述:   金字塔形状匹配算子 - 工业级抗光照梯度方向匹配
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using ShapeMatcher = ClearFrost.Vision;

namespace ClearFrost.Vision
{
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
}

