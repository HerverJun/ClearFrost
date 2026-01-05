using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 匹配结果数据结构
    /// </summary>
    public readonly struct MatchResult
    {
        public readonly OpenCvSharp.Point Position;      // 匹配中心位置
        public readonly double Angle;         // 匹配角度 (度)
        public readonly double Score;         // 匹配分数 (0-100)
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
    /// 特征点结构 - 存储相对坐标和量化方向
    /// </summary>
    internal readonly struct FeaturePoint
    {
        public readonly short X;          // 相对于模板中心的X偏移
        public readonly short Y;          // 相对于模板中心的Y偏移
        public readonly byte Direction;   // 量化方向 (0-7)

        public FeaturePoint(short x, short y, byte direction)
        {
            X = x;
            Y = y;
            Direction = direction;
        }
    }

    /// <summary>
    /// 旋转模板 - 存储某一角度下的所有特征点
    /// </summary>
    internal sealed class RotatedTemplate
    {
        public readonly double Angle;                    // 旋转角度 (度)
        public readonly FeaturePoint[] Features;         // 特征点数组
        public readonly int MinX, MaxX;                  // 实际特征偏移范围
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
    /// 工业级抗光照形状匹配器
    /// 基于梯度方向 (Gradient Orientation) 进行匹配
    /// 原理参考 Halcon create_shape_model / Line2Dup 算法
    /// </summary>
    public sealed class GradientShapeMatcher : IDisposable
    {
        #region Constants & Configuration

        // 方向量化: 8个方向, 每个45度
        private const int NumDirections = 8;
        private const double DirectionStep = Math.PI / 4.0;  // 45度 = π/4

        // 默认参数
        private const int DefaultMagnitudeThreshold = 30;    // 梯度幅值阈值
        private const int DefaultAngleStep = 1;              // 角度步长 (度)
        private const int DefaultPyramidLevels = 3;          // 金字塔层数
        private const double DefaultMinFeatureDistance = 2;  // 最小特征点间距

        #endregion

        #region Fields

        private List<RotatedTemplate> _templates;
        private int _magnitudeThreshold;
        private int _angleStep;
        private bool _isDisposed;
        private bool _isTrained;

        // 预计算的方向差查找表 (用于快速判断方向是否匹配)
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
        /// <param name="image">模板图像 (灰度图)</param>
        /// <param name="angleRange">旋转范围 (正负角度, 例如180表示-180~+180度)</param>
        /// <param name="mask">可选的掩膜图像</param>
        public void Train(Mat image, int angleRange = 180, Mat? mask = null)
        {
            if (image == null || image.Empty())
                throw new ArgumentException("Template image cannot be null or empty.", nameof(image));

            // 确保是灰度图
            using var gray = EnsureGray(image);

            // 提取基准模板 (0度) 的特征点
            var baseFeatures = ExtractFeatures(gray, mask);

            if (baseFeatures.Count < 10)
                throw new InvalidOperationException(
                    $"Insufficient features extracted ({baseFeatures.Count}). " +
                    "Try lowering the magnitude threshold or using a different template.");

            int centerX = gray.Width / 2;
            int centerY = gray.Height / 2;

            // 生成所有旋转角度的模板
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
        /// 在场景图像中匹配模板
        /// </summary>
        /// <param name="sceneImage">场景图像</param>
        /// <param name="minScore">最低匹配分数 (0-100)</param>
        /// <param name="searchRegion">可选的搜索区域</param>
        /// <returns>最佳匹配结果</returns>
        public MatchResult Match(Mat sceneImage, double minScore = 80, Rect? searchRegion = null)
        {
            if (!_isTrained)
                throw new InvalidOperationException("Matcher has not been trained. Call Train() first.");

            if (sceneImage == null || sceneImage.Empty())
                throw new ArgumentException("Scene image cannot be null or empty.", nameof(sceneImage));

            // 确保是灰度图
            using var gray = EnsureGray(sceneImage);

            // 预计算场景图的全图梯度方向矩阵
            var (sceneDirections, sceneMagnitudes) = ComputeSceneGradients(gray);

            // 确定搜索区域
            Rect region = searchRegion ?? new Rect(0, 0, gray.Width, gray.Height);

            // 使用并行搜索所有旋转模板
            var bestMatch = FindBestMatch(sceneDirections, sceneMagnitudes, region, minScore);

            return bestMatch;
        }

        /// <summary>
        /// 在场景图像中查找所有匹配 (多实例匹配)
        /// </summary>
        /// <param name="sceneImage">场景图像</param>
        /// <param name="minScore">最低匹配分数 (0-100)</param>
        /// <param name="maxMatches">最大匹配数量</param>
        /// <param name="minDistance">匹配结果之间的最小距离</param>
        /// <returns>所有匹配结果列表</returns>
        public List<MatchResult> MatchAll(Mat sceneImage, double minScore = 80,
                                          int maxMatches = 10, double minDistance = 20)
        {
            if (!_isTrained)
                throw new InvalidOperationException("Matcher has not been trained. Call Train() first.");

            using var gray = EnsureGray(sceneImage);
            var (sceneDirections, sceneMagnitudes) = ComputeSceneGradients(gray);

            var region = new Rect(0, 0, gray.Width, gray.Height);
            var allMatches = FindAllMatches(sceneDirections, sceneMagnitudes, region, minScore);

            // 非极大值抑制
            var results = NonMaximumSuppression(allMatches, minDistance, maxMatches);

            return results;
        }

        #endregion

        #region Feature Extraction

        /// <summary>
        /// 从图像中提取梯度特征点
        /// </summary>
        private unsafe List<FeaturePoint> ExtractFeatures(Mat gray, Mat? mask)
        {
            int width = gray.Width;
            int height = gray.Height;

            // 计算 Sobel 梯度
            using var gradX = new Mat();
            using var gradY = new Mat();

            Cv2.Sobel(gray, gradX, MatType.CV_16S, 1, 0, 3);
            Cv2.Sobel(gray, gradY, MatType.CV_16S, 0, 1, 3);

            int centerX = width / 2;
            int centerY = height / 2;

            var features = new List<FeaturePoint>(width * height / 16);

            // 使用 unsafe 代码块高速遍历
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

                // 跳过边缘像素
                for (int y = 1; y < height - 1; y++)
                {
                    short* gxRow = gxPtr + y * gxStep;
                    short* gyRow = gyPtr + y * gyStep;
                    byte* maskRow = maskPtr != null ? maskPtr + y * maskStep : null;

                    for (int x = 1; x < width - 1; x++)
                    {
                        // 检查掩膜
                        if (maskRow != null && maskRow[x] == 0)
                            continue;

                        short gx = gxRow[x];
                        short gy = gyRow[x];

                        // 计算梯度幅值
                        int magnitude = FastMagnitude(gx, gy);

                        // 只保留强梯度点
                        if (magnitude < _magnitudeThreshold)
                            continue;

                        // 计算并量化方向 (0-7)
                        byte direction = QuantizeDirection(gx, gy);

                        // 存储相对于中心的偏移
                        short relX = (short)(x - centerX);
                        short relY = (short)(y - centerY);

                        features.Add(new FeaturePoint(relX, relY, direction));
                    }
                }
            }

            // 稀疏化特征点 (可选, 提高速度)
            return SparsifyFeatures(features, DefaultMinFeatureDistance);
        }

        /// <summary>
        /// 稀疏化特征点, 保持空间均匀分布
        /// </summary>
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
        /// 通过坐标变换生成旋转模板
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

            // 方向偏移量 (每45度偏移1个量化级别)
            int directionOffset = (int)Math.Round(angleDeg / 45.0);

            // 规范化到 0-7 范围
            directionOffset = ((directionOffset % NumDirections) + NumDirections) % NumDirections;

            var rotatedFeatures = new FeaturePoint[baseFeatures.Count];

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;

            for (int i = 0; i < baseFeatures.Count; i++)
            {
                var f = baseFeatures[i];

                // 旋转坐标
                double newX = f.X * cosA - f.Y * sinA;
                double newY = f.X * sinA + f.Y * cosA;

                short rx = (short)Math.Round(newX);
                short ry = (short)Math.Round(newY);

                // 旋转方向
                byte newDir = (byte)((f.Direction + directionOffset) % NumDirections);

                rotatedFeatures[i] = new FeaturePoint(rx, ry, newDir);

                // 更新包围盒
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
        /// 预计算场景图的全图梯度方向和幅值
        /// </summary>
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

            // 并行计算梯度方向
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
                        // 使用特殊值表示无效点
                        directions[y, x] = 0xFF;
                    }
                }
            });

            return (directions, magnitudes);
        }

        #endregion

        #region Matching Core

        /// <summary>
        /// 并行搜索所有模板, 找到最佳匹配
        /// </summary>
        private MatchResult FindBestMatch(
            byte[,] sceneDirections,
            ushort[,] sceneMagnitudes,
            Rect searchRegion,
            double minScore)
        {
            int sceneWidth = sceneDirections.GetLength(1);
            int sceneHeight = sceneDirections.GetLength(0);

            // 并发收集结果
            var results = new ConcurrentBag<MatchResult>();
            double minScoreNormalized = minScore / 100.0;

            // 并行遍历所有旋转模板
            Parallel.ForEach(_templates, template =>
            {
                // 计算搜索边界 (考虑模板大小)
                int startX = Math.Max(searchRegion.X - template.MinX, -template.MinX);
                int startY = Math.Max(searchRegion.Y - template.MinY, -template.MinY);
                int endX = Math.Min(searchRegion.X + searchRegion.Width - template.MaxX, sceneWidth - template.MaxX);
                int endY = Math.Min(searchRegion.Y + searchRegion.Height - template.MaxY, sceneHeight - template.MaxY);

                // 步长 (粗搜索用较大步长, 后续可以精细化)
                int stepX = 2;
                int stepY = 2;

                double bestScore = 0;
                int bestX = -1, bestY = -1;

                // 粗搜索
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

                // 如果粗搜索分数足够高, 进行精细搜索
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

            // 找出最佳结果
            MatchResult best = MatchResult.Empty;
            foreach (var r in results)
            {
                if (r.Score > best.Score)
                    best = r;
            }

            return best;
        }

        /// <summary>
        /// 查找所有满足条件的匹配
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
        /// 精细化匹配位置
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
        /// 计算单个位置的匹配分数
        /// 使用方向一致性评分: 只有方向偏差在1以内才计分
        /// </summary>
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

            // 早期终止阈值
            int minRequired = featureCount * 2 / 3;

            for (int i = 0; i < featureCount; i++)
            {
                var f = features[i];

                int sx = cx + f.X;
                int sy = cy + f.Y;

                // 边界检查
                if (sx < 0 || sx >= sceneWidth || sy < 0 || sy >= sceneHeight)
                    continue;

                validCount++;

                byte sceneDir = sceneDirections[sy, sx];

                // 跳过无效点 (低梯度区域)
                if (sceneDir == 0xFF)
                    continue;

                // 使用查找表判断方向是否匹配
                if (_directionMatchLut[f.Direction, sceneDir])
                {
                    matchCount++;
                }

                // 早期终止: 如果剩余点全部匹配也达不到阈值
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
        /// 非极大值抑制, 去除重复检测
        /// </summary>
        private List<MatchResult> NonMaximumSuppression(
            List<MatchResult> matches,
            double minDistance,
            int maxMatches)
        {
            if (matches.Count == 0)
                return new List<MatchResult>();

            // 按分数降序排序
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
        /// 构建方向匹配查找表
        /// 允许偏差在1以内 (包括环绕)
        /// </summary>
        private static bool[,] BuildDirectionMatchLut()
        {
            var lut = new bool[NumDirections, NumDirections];

            for (int t = 0; t < NumDirections; t++)
            {
                for (int s = 0; s < NumDirections; s++)
                {
                    int diff = Math.Abs(t - s);

                    // 考虑环绕 (例如 0 和 7 的差为 1)
                    if (diff > NumDirections / 2)
                        diff = NumDirections - diff;

                    lut[t, s] = diff <= 1;
                }
            }

            return lut;
        }

        /// <summary>
        /// 快速计算梯度幅值 (近似)
        /// 使用 |gx| + |gy| 近似 sqrt(gx² + gy²)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastMagnitude(int gx, int gy)
        {
            int absX = gx >= 0 ? gx : -gx;
            int absY = gy >= 0 ? gy : -gy;

            // 更精确的近似: max + 0.4 * min
            if (absX > absY)
                return absX + (absY * 3 >> 3);
            else
                return absY + (absX * 3 >> 3);
        }

        /// <summary>
        /// 将梯度方向量化为 0-7
        /// 使用 atan2 计算角度, 然后量化
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte QuantizeDirection(int gx, int gy)
        {
            // 计算角度 (-π 到 π)
            double angle = Math.Atan2(gy, gx);

            // 转换到 [0, 2π)
            if (angle < 0)
                angle += 2 * Math.PI;

            // 量化到 0-7
            int quantized = (int)((angle + DirectionStep / 2) / DirectionStep) % NumDirections;

            return (byte)quantized;
        }

        /// <summary>
        /// 确保图像为灰度图
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
