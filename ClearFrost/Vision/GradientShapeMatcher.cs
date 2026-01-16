using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenCvSharp;
// ============================================================================
// 文件名: GradientShapeMatcher.cs
// 作者: 蘅芜君
// 描述:   基于梯度形状的模板匹配算子
// 
// 功能:
//   - 训练模板：提取梯度方向特征
//   - 匹配：在图像中搜索最佳匹配位置
//   - 多目标匹配：支持 NMS 非极大值抑制
// ============================================================================

namespace ClearFrost.Vision
{
    /// <summary>
    /// 匹配结果结构体
    /// </summary>
    public readonly struct MatchResult
    {
        public readonly OpenCvSharp.Point Position;      // 匹配结果中心位置
        public readonly double Angle;         // 匹配角度 (度)
        public readonly double Score;         // 匹配得分 (0-100)
        public readonly bool IsValid;         // 是否找到有效匹配

        public MatchResult(OpenCvSharp.Point position, double angle, double score)
        {
            Position = position;
            Angle = angle;
            Score = score;
            IsValid = score > 0;
        }

        public static MatchResult Empty => new MatchResult(new OpenCvSharp.Point(-1, -1), 0, 0);

        public override string ToString() =>
            $"Position: ({Position.X}, {Position.Y}), Angle: {Angle:F2}°, Score: {Score:F2}%";
    }

    /// <summary>
    /// 特征点结构体
    /// </summary>
    internal readonly struct FeaturePoint
    {
        public readonly short X;          // 相对模板中心点X偏移
        public readonly short Y;          // 相对模板中心点Y偏移
        public readonly byte Direction;   // 梯度方向 (0-7)

        public FeaturePoint(short x, short y, byte direction)
        {
            X = x;
            Y = y;
            Direction = direction;
        }
    }

    /// <summary>
    /// 旋转后的模板结构体
    /// </summary>
    internal sealed class RotatedTemplate
    {
        public readonly double Angle;                    // 旋转角度 (度)
        public readonly FeaturePoint[] Features;         // 旋转后的特征点
        public readonly int MinX, MaxX;                  // 实际特征点偏移范围
        public readonly int MinY, MaxY;
        public readonly int Width;                       // 模板包围盒宽度
        public readonly int Height;                      // 模板包围盒高度

        public RotatedTemplate(double angle, FeaturePoint[] features, int minX, int maxX, int minY, int maxY)
        {
            Angle = angle;
            Features = features;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
            Width = maxX - minX + 1;
            Height = maxY - minY + 1;
        }
    }

    /// <summary>
    /// 基于梯度形状的模板匹配器
    /// </summary>
    public sealed class GradientShapeMatcher : IDisposable
    {
        #region Constants & Configuration

        // 梯度方向数量
        private const int NumDirections = 8;
        private const double DirectionStep = Math.PI / 4.0;  // 45度 = π/4

        // 默认配置参数
        private const int DefaultMagnitudeThreshold = 30;    // 默认梯度阈值
        private const int DefaultAngleStep = 1;              // 默认角度步长 (度)
        private const int DefaultPyramidLevels = 3;          // 默认金字塔层数
        private const double DefaultMinFeatureDistance = 2;  // 最小特征点距离

        #endregion

        #region Fields

        private List<RotatedTemplate> _templates;
        private int _magnitudeThreshold;
        private int _angleStep;
        private bool _isDisposed;
        private bool _isTrained;

        // 
        private readonly bool[,] _directionMatchLut;

        #endregion

        #region Constructor

        public GradientShapeMatcher(int magnitudeThreshold = DefaultMagnitudeThreshold,
                                    int angleStep = DefaultAngleStep)
        {
            _magnitudeThreshold = magnitudeThreshold;
            _angleStep = angleStep;
            _templates = new List<RotatedTemplate>();
            _directionMatchLut = BuildDirectionMatchLut();
        }

        #endregion

        #region Public API

        /// <summary>
        /// 训练模板
        /// </summary>
        /// <param name="image">模板图像</param>
        /// <param name="angleRange">旋转角度范围（+/-度）</param>
        /// <param name="mask">掩码图像（可选）</param>
        /// <exception cref="ArgumentException">图像为空</exception>
        public void Train(Mat image, int angleRange = 180, Mat? mask = null)
        {
            if (image == null || image.Empty())
                throw new ArgumentException("Template image cannot be null or empty.", nameof(image));

            // 
            using var gray = EnsureGray(image);

            // 
            var baseFeatures = ExtractFeatures(gray, mask);

            if (baseFeatures.Count < 10)
                throw new InvalidOperationException(
                    $"Insufficient features extracted ({baseFeatures.Count}). " +
                    "Try lowering the magnitude threshold or using a different template.");

            int centerX = gray.Width / 2;
            int centerY = gray.Height / 2;

            // 
            _templates.Clear();

            for (int angle = -angleRange; angle <= angleRange; angle += _angleStep)
            {
                var rotatedTemplate = CreateRotatedTemplate(
                    baseFeatures,
                    angle,
                    centerX,
                    centerY,
                    gray.Width,
                    gray.Height);

                _templates.Add(rotatedTemplate);
            }

            _isTrained = true;
        }

        /// <summary>
        /// 在场景图像中查找最佳匹配
        /// </summary>
        /// <param name="sceneImage">场景图像</param>
        /// <param name="minScore">最小匹配得分 (0-100)</param>
        /// <param name="searchRegion">搜索区域（可选）</param>
        /// <returns>最佳匹配结果</returns>
        public MatchResult Match(Mat sceneImage, double minScore = 80, Rect? searchRegion = null)
        {
            if (!_isTrained)
                throw new InvalidOperationException("Matcher has not been trained. Call Train() first.");

            if (sceneImage == null || sceneImage.Empty())
                throw new ArgumentException("Scene image cannot be null or empty.", nameof(sceneImage));

            // 
            using var gray = EnsureGray(sceneImage);

            // 
            var (sceneDirections, sceneMagnitudes) = ComputeSceneGradients(gray);

            // 
            Rect region = searchRegion ?? new Rect(0, 0, gray.Width, gray.Height);

            // 
            var bestMatch = FindBestMatch(sceneDirections, sceneMagnitudes, region, minScore);

            return bestMatch;
        }

        /// <summary>
        /// 查找所有满足条件的匹配结果
        /// </summary>
        /// <param name="sceneImage">场景图像</param>
        /// <param name="minScore">最小匹配得分</param>
        /// <param name="maxMatches">最大返回数量</param>
        /// <param name="minDistance">结果去重最小间距</param>
        /// <returns>匹配结果列表</returns>
        public List<MatchResult> MatchAll(Mat sceneImage, double minScore = 80,
                                          int maxMatches = 10, double minDistance = 20)
        {
            if (!_isTrained)
                throw new InvalidOperationException("Matcher has not been trained. Call Train() first.");

            using var gray = EnsureGray(sceneImage);
            var (sceneDirections, sceneMagnitudes) = ComputeSceneGradients(gray);

            var region = new Rect(0, 0, gray.Width, gray.Height);
            var allMatches = FindAllMatches(sceneDirections, sceneMagnitudes, region, minScore);

            // 
            var results = NonMaximumSuppression(allMatches, minDistance, maxMatches);

            return results;
        }

        #endregion

        #region Feature Extraction

        /// <summary>
        /// 从图像中提取梯度特征点
        /// </summary>
        /// <param name="gray">灰度图像</param>
        /// <param name="mask">掩码图像</param>
        /// <returns>特征点列表</returns>
        private unsafe List<FeaturePoint> ExtractFeatures(Mat gray, Mat? mask)
        {
            int width = gray.Width;
            int height = gray.Height;

            // 
            using var gradX = new Mat();
            using var gradY = new Mat();

            Cv2.Sobel(gray, gradX, MatType.CV_16S, 1, 0, 3);
            Cv2.Sobel(gray, gradY, MatType.CV_16S, 0, 1, 3);

            int centerX = width / 2;
            int centerY = height / 2;

            var features = new List<FeaturePoint>(width * height / 16);

            // 
            fixed (bool* hasMask = new bool[1])
            {
                byte* maskPtr = null;
                int maskStep = 0;

                if (mask != null && !mask.Empty())
                {
                    maskPtr = mask.DataPointer;
                    maskStep = (int)mask.Step();
                }

                short* gxPtr = (short*)gradX.DataPointer;
                short* gyPtr = (short*)gradY.DataPointer;

                int gxStep = (int)gradX.Step() / sizeof(short);
                int gyStep = (int)gradY.Step() / sizeof(short);

                // 
                for (int y = 1; y < height - 1; y++)
                {
                    short* gxRow = gxPtr + y * gxStep;
                    short* gyRow = gyPtr + y * gyStep;
                    byte* maskRow = maskPtr != null ? maskPtr + y * maskStep : null;

                    for (int x = 1; x < width - 1; x++)
                    {
                        // 
                        if (maskRow != null && maskRow[x] == 0)
                            continue;

                        short gx = gxRow[x];
                        short gy = gyRow[x];

                        // 
                        int magnitude = FastMagnitude(gx, gy);

                        // 
                        if (magnitude < _magnitudeThreshold)
                            continue;

                        // 
                        byte direction = QuantizeDirection(gx, gy);

                        // 
                        short relX = (short)(x - centerX);
                        short relY = (short)(y - centerY);

                        features.Add(new FeaturePoint(relX, relY, direction));
                    }
                }
            }

            // 
            return SparsifyFeatures(features, DefaultMinFeatureDistance);
        }

        /// <summary>
        /// 稀疏化特征点（减少密集特征，提高匹配速度）
        /// </summary>
        /// <param name="features">原始特征点列表</param>
        /// <param name="minDistance">最小间距</param>
        /// <returns>稀疏化后的特征点列表</returns>
        private List<FeaturePoint> SparsifyFeatures(List<FeaturePoint> features, double minDistance)
        {
            if (minDistance <= 0 || features.Count < 100)
                return features;

            double minDistSq = minDistance * minDistance;
            var sparse = new List<FeaturePoint>(features.Count / 4);
            var occupied = new HashSet<long>();

            int gridSize = (int)Math.Ceiling(minDistance);

            foreach (var f in features)
            {
                int gx = f.X / gridSize;
                int gy = f.Y / gridSize;
                long key = ((long)gx << 32) | (uint)gy;

                if (!occupied.Contains(key))
                {
                    sparse.Add(f);
                    occupied.Add(key);
                }
            }

            return sparse;
        }

        #endregion

        #region Template Rotation

        /// <summary>
        /// 创建旋转后的模板特征点集合
        /// </summary>
        private RotatedTemplate CreateRotatedTemplate(
            List<FeaturePoint> baseFeatures,
            double angleDeg,
            int centerX,
            int centerY,
            int templateWidth,
            int templateHeight)
        {
            double angleRad = angleDeg * Math.PI / 180.0;
            double cosA = Math.Cos(angleRad);
            double sinA = Math.Sin(angleRad);

            // 
            int directionOffset = (int)Math.Round(angleDeg / 45.0);

            // 
            directionOffset = ((directionOffset % NumDirections) + NumDirections) % NumDirections;

            var rotatedFeatures = new FeaturePoint[baseFeatures.Count];

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;

            for (int i = 0; i < baseFeatures.Count; i++)
            {
                var f = baseFeatures[i];

                // 
                double newX = f.X * cosA - f.Y * sinA;
                double newY = f.X * sinA + f.Y * cosA;

                short rx = (short)Math.Round(newX);
                short ry = (short)Math.Round(newY);

                // 
                byte newDir = (byte)((f.Direction + directionOffset) % NumDirections);

                rotatedFeatures[i] = new FeaturePoint(rx, ry, newDir);

                // 
                if (rx < minX) minX = rx;
                if (rx > maxX) maxX = rx;
                if (ry < minY) minY = ry;
                if (ry > maxY) maxY = ry;
            }

            return new RotatedTemplate(angleDeg, rotatedFeatures, minX, maxX, minY, maxY);
        }

        #endregion

        #region Scene Gradient Computation

        /// <summary>
        /// 计算场景图像的梯度方向和幅值
        /// </summary>
        /// <param name="gray">灰度场景图像</param>
        /// <returns>方向矩阵和幅值矩阵</returns>
        private unsafe (byte[,] directions, ushort[,] magnitudes) ComputeSceneGradients(Mat gray)
        {
            int width = gray.Width;
            int height = gray.Height;

            var directions = new byte[height, width];
            var magnitudes = new ushort[height, width];

            using var gradX = new Mat();
            using var gradY = new Mat();

            Cv2.Sobel(gray, gradX, MatType.CV_16S, 1, 0, 3);
            Cv2.Sobel(gray, gradY, MatType.CV_16S, 0, 1, 3);

            short* gxPtr = (short*)gradX.DataPointer;
            short* gyPtr = (short*)gradY.DataPointer;

            int gxStep = (int)gradX.Step() / sizeof(short);
            int gyStep = (int)gradY.Step() / sizeof(short);

            // 
            Parallel.For(0, height, y =>
            {
                short* gxRow = gxPtr + y * gxStep;
                short* gyRow = gyPtr + y * gyStep;

                for (int x = 0; x < width; x++)
                {
                    short gx = gxRow[x];
                    short gy = gyRow[x];

                    int mag = FastMagnitude(gx, gy);
                    magnitudes[y, x] = (ushort)Math.Min(mag, ushort.MaxValue);

                    if (mag >= _magnitudeThreshold)
                    {
                        directions[y, x] = QuantizeDirection(gx, gy);
                    }
                    else
                    {
                        // 
                        directions[y, x] = 0xFF;
                    }
                }
            });

            return (directions, magnitudes);
        }

        #endregion

        #region Matching Core

        /// <summary>
        /// 核心匹配算法：在梯度图中寻找最佳匹配
        /// </summary>
        private MatchResult FindBestMatch(
            byte[,] sceneDirections,
            ushort[,] sceneMagnitudes,
            Rect searchRegion,
            double minScore)
        {
            int sceneWidth = sceneDirections.GetLength(1);
            int sceneHeight = sceneDirections.GetLength(0);

            // 
            var results = new ConcurrentBag<MatchResult>();
            double minScoreNormalized = minScore / 100.0;

            // 
            Parallel.ForEach(_templates, template =>
            {
                // 
                int startX = Math.Max(searchRegion.X - template.MinX, -template.MinX);
                int startY = Math.Max(searchRegion.Y - template.MinY, -template.MinY);
                int endX = Math.Min(searchRegion.X + searchRegion.Width - template.MaxX, sceneWidth - template.MaxX);
                int endY = Math.Min(searchRegion.Y + searchRegion.Height - template.MaxY, sceneHeight - template.MaxY);

                // 
                int stepX = 2;
                int stepY = 2;

                double bestScore = 0;
                int bestX = -1, bestY = -1;

                // 
                for (int y = startY; y < endY; y += stepY)
                {
                    for (int x = startX; x < endX; x += stepX)
                    {
                        double score = ComputeMatchScore(
                            sceneDirections,
                            sceneMagnitudes,
                            template,
                            x, y);

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestX = x;
                            bestY = y;
                        }
                    }
                }

                // 
                if (bestScore >= minScoreNormalized * 0.8 && bestX >= 0)
                {
                    (bestX, bestY, bestScore) = RefineMatch(
                        sceneDirections,
                        sceneMagnitudes,
                        template,
                        bestX, bestY,
                        stepX, stepY);
                }

                if (bestScore >= minScoreNormalized)
                {
                    results.Add(new MatchResult(
                        new OpenCvSharp.Point(bestX, bestY),
                        template.Angle,
                        bestScore * 100));
                }
            });

            // 
            MatchResult best = MatchResult.Empty;
            foreach (var r in results)
            {
                if (r.Score > best.Score)
                    best = r;
            }

            return best;
        }

        /// <summary>
        /// 核心匹配算法：寻找所有匹配可能
        /// </summary>
        private List<MatchResult> FindAllMatches(
            byte[,] sceneDirections,
            ushort[,] sceneMagnitudes,
            Rect searchRegion,
            double minScore)
        {
            int sceneWidth = sceneDirections.GetLength(1);
            int sceneHeight = sceneDirections.GetLength(0);

            var results = new ConcurrentBag<MatchResult>();
            double minScoreNormalized = minScore / 100.0;

            Parallel.ForEach(_templates, template =>
            {
                int startX = Math.Max(searchRegion.X - template.MinX, -template.MinX);
                int startY = Math.Max(searchRegion.Y - template.MinY, -template.MinY);
                int endX = Math.Min(searchRegion.X + searchRegion.Width - template.MaxX, sceneWidth - template.MaxX);
                int endY = Math.Min(searchRegion.Y + searchRegion.Height - template.MaxY, sceneHeight - template.MaxY);

                int stepX = 3;
                int stepY = 3;

                for (int y = startY; y < endY; y += stepY)
                {
                    for (int x = startX; x < endX; x += stepX)
                    {
                        double score = ComputeMatchScore(
                            sceneDirections,
                            sceneMagnitudes,
                            template,
                            x, y);

                        if (score >= minScoreNormalized * 0.9)
                        {
                            var (rx, ry, rs) = RefineMatch(
                                sceneDirections,
                                sceneMagnitudes,
                                template,
                                x, y, stepX, stepY);

                            if (rs >= minScoreNormalized)
                            {
                                results.Add(new MatchResult(
                                    new OpenCvSharp.Point(rx, ry),
                                    template.Angle,
                                    rs * 100));
                            }
                        }
                    }
                }
            });

            return new List<MatchResult>(results);
        }

        /// <summary>
        /// 细化匹配位置（在邻域内搜索更高分数）
        /// </summary>
        private (int x, int y, double score) RefineMatch(
            byte[,] sceneDirections,
            ushort[,] sceneMagnitudes,
            RotatedTemplate template,
            int centerX, int centerY,
            int rangeX, int rangeY)
        {
            double bestScore = 0;
            int bestX = centerX, bestY = centerY;

            for (int dy = -rangeY; dy <= rangeY; dy++)
            {
                for (int dx = -rangeX; dx <= rangeX; dx++)
                {
                    double score = ComputeMatchScore(
                        sceneDirections,
                        sceneMagnitudes,
                        template,
                        centerX + dx,
                        centerY + dy);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestX = centerX + dx;
                        bestY = centerY + dy;
                    }
                }
            }

            return (bestX, bestY, bestScore);
        }

        /// <summary>
        /// 计算单个位置的匹配得分
        /// </summary>
        /// <param name="sceneDirections">场景方向图</param>
        /// <param name="sceneMagnitudes">场景梯度幅值图</param>
        /// <param name="template">旋转后的模板</param>
        /// <param name="cx">中心X坐标</param>
        /// <param name="cy">中心Y坐标</param>
        /// <returns>匹配得分 (0.0 - 1.0)</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private double ComputeMatchScore(
            byte[,] sceneDirections,
            ushort[,] sceneMagnitudes,
            RotatedTemplate template,
            int cx, int cy)
        {
            int sceneWidth = sceneDirections.GetLength(1);
            int sceneHeight = sceneDirections.GetLength(0);

            int matchCount = 0;
            int validCount = 0;

            var features = template.Features;
            int featureCount = features.Length;

            // 
            int minRequired = featureCount * 2 / 3;

            for (int i = 0; i < featureCount; i++)
            {
                var f = features[i];

                int sx = cx + f.X;
                int sy = cy + f.Y;

                // 
                if (sx < 0 || sx >= sceneWidth || sy < 0 || sy >= sceneHeight)
                    continue;

                validCount++;

                byte sceneDir = sceneDirections[sy, sx];

                // 
                if (sceneDir == 0xFF)
                    continue;

                // 
                if (_directionMatchLut[f.Direction, sceneDir])
                {
                    matchCount++;
                }

                // 
                int remaining = featureCount - i - 1;
                if (matchCount + remaining < minRequired)
                    break;
            }

            if (validCount == 0)
                return 0;

            return (double)matchCount / featureCount;
        }

        #endregion

        #region Non-Maximum Suppression

        /// <summary>
        /// 非极大值抑制（去除重叠的检测结果）
        /// </summary>
        /// <param name="matches">原始匹配列表</param>
        /// <param name="minDistance">最小距离阈值</param>
        /// <param name="maxMatches">最大保留数量</param>
        /// <returns>抑制后的结果列表</returns>
        private List<MatchResult> NonMaximumSuppression(
            List<MatchResult> matches,
            double minDistance,
            int maxMatches)
        {
            if (matches.Count == 0)
                return new List<MatchResult>();

            // 
            matches.Sort((a, b) => b.Score.CompareTo(a.Score));

            var results = new List<MatchResult>();
            double minDistSq = minDistance * minDistance;

            foreach (var match in matches)
            {
                bool suppress = false;

                foreach (var existing in results)
                {
                    double dx = match.Position.X - existing.Position.X;
                    double dy = match.Position.Y - existing.Position.Y;

                    if (dx * dx + dy * dy < minDistSq)
                    {
                        suppress = true;
                        break;
                    }
                }

                if (!suppress)
                {
                    results.Add(match);

                    if (results.Count >= maxMatches)
                        break;
                }
            }

            return results;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// 
        /// 
        /// </summary>
        private static bool[,] BuildDirectionMatchLut()
        {
            var lut = new bool[NumDirections, NumDirections];

            for (int t = 0; t < NumDirections; t++)
            {
                for (int s = 0; s < NumDirections; s++)
                {
                    int diff = Math.Abs(t - s);

                    // 
                    if (diff > NumDirections / 2)
                        diff = NumDirections - diff;

                    lut[t, s] = diff <= 1;
                }
            }

            return lut;
        }

        /// <summary>
        /// 
        /// 
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastMagnitude(int gx, int gy)
        {
            int absX = gx >= 0 ? gx : -gx;
            int absY = gy >= 0 ? gy : -gy;

            // 
            if (absX > absY)
                return absX + (absY * 3 >> 3);
            else
                return absY + (absX * 3 >> 3);
        }

        /// <summary>
        /// 
        /// 
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte QuantizeDirection(int gx, int gy)
        {
            // 
            double angle = Math.Atan2(gy, gx);

            // 
            if (angle < 0)
                angle += 2 * Math.PI;

            // 
            int quantized = (int)((angle + DirectionStep / 2) / DirectionStep) % NumDirections;

            return (byte)quantized;
        }

        /// <summary>
        /// 
        /// </summary>
        private static Mat EnsureGray(Mat image)
        {
            if (image.Channels() == 1)
                return image.Clone();

            var gray = new Mat();
            Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _templates?.Clear();
                _templates = null!;
                _isDisposed = true;
            }
        }

        #endregion
    }
}

