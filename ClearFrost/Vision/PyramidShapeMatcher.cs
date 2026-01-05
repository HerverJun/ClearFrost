using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 高级版本: 带金字塔加速的形状匹配器
    /// 适用于大尺寸图像的高速匹配
    /// </summary>
    /// <summary>
    /// 高级版本: 带金字塔加速的形状匹配器
    /// 适用于大尺寸图像的高速匹配
    /// </summary>
    public sealed class PyramidShapeMatcher : IDisposable
    {
        #region Nested Types

        /// <summary>
        /// 金字塔层模板
        /// </summary>
        private sealed class PyramidLevel
        {
            public readonly int Level;
            public readonly double Scale;
            public readonly List<RotatedTemplate> Templates;

            public PyramidLevel(int level, double scale)
            {
                Level = level;
                Scale = scale;
                Templates = new List<RotatedTemplate>();
            }
        }

        /// <summary>
        /// 候选匹配位置
        /// </summary>
        private readonly struct Candidate
        {
            public readonly int X;
            public readonly int Y;
            public readonly int TemplateIndex;
            public readonly double Score;

            public Candidate(int x, int y, int templateIndex, double score)
            {
                X = x;
                Y = y;
                TemplateIndex = templateIndex;
                Score = score;
            }
        }

        #endregion

        #region Constants

        private const int NumDirections = 8;
        private const double DirectionStep = Math.PI / 4.0;
        private const int DefaultMagnitudeThreshold = 30;
        private const int DefaultPyramidLevels = 3;

        #endregion

        #region Fields

        private List<PyramidLevel> _pyramidTemplates;
        private readonly int _pyramidLevels;
        private readonly int _magnitudeThreshold;
        private readonly int _angleStep;
        private readonly bool[,] _directionMatchLut;

        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
        private bool _isTrained;
        private bool _isDisposed;

        #endregion

        #region Constructor

        public PyramidShapeMatcher(
            int pyramidLevels = DefaultPyramidLevels,
            int magnitudeThreshold = DefaultMagnitudeThreshold,
            int angleStep = 1)
        {
            _pyramidLevels = pyramidLevels;
            _magnitudeThreshold = magnitudeThreshold;
            _angleStep = angleStep;
            _pyramidTemplates = new List<PyramidLevel>();
            _directionMatchLut = BuildDirectionMatchLut();
        }

        #endregion

        #region Public API

        /// <summary>
        /// 训练模板 (金字塔版本)
        /// </summary>
        public void Train(Mat image, int angleRange = 180, Mat? mask = null)
        {
            if (image == null || image.Empty())
                throw new ArgumentException("Template image cannot be null or empty.", nameof(image));

            _rwLock.EnterWriteLock();
            try
            {
                using var gray = EnsureGray(image);

                _pyramidTemplates.Clear();

                // 为每个金字塔层级创建模板
                Mat currentImage = gray.Clone();
                Mat? currentMask = mask?.Clone();

                for (int level = 0; level < _pyramidLevels; level++)
                {
                    double scale = Math.Pow(2, level);
                    var pyramidLevel = new PyramidLevel(level, scale);

                    // 提取当前层的特征
                    int thresholdScaled = (int)(_magnitudeThreshold / scale);
                    var baseFeatures = ExtractFeatures(currentImage, currentMask, thresholdScaled);

                    if (baseFeatures.Count >= 5)
                    {
                        int centerX = currentImage.Width / 2;
                        int centerY = currentImage.Height / 2;

                        // 生成所有旋转角度的模板
                        for (int angle = -angleRange; angle <= angleRange; angle += _angleStep)
                        {
                            var template = CreateRotatedTemplate(
                                baseFeatures, angle, centerX, centerY,
                                currentImage.Width, currentImage.Height);
                            pyramidLevel.Templates.Add(template);
                        }
                    }

                    _pyramidTemplates.Add(pyramidLevel);

                    // 降采样到下一层
                    if (level < _pyramidLevels - 1)
                    {
                        var nextImage = new Mat();
                        Cv2.PyrDown(currentImage, nextImage);
                        currentImage.Dispose();
                        currentImage = nextImage;

                        if (currentMask != null)
                        {
                            var nextMask = new Mat();
                            Cv2.PyrDown(currentMask, nextMask);
                            currentMask.Dispose();
                            currentMask = nextMask;
                        }
                    }
                }

                currentImage.Dispose();
                currentMask?.Dispose();

                _isTrained = true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// 金字塔加速匹配
        /// </summary>
        public MatchResult Match(Mat sceneImage, double minScore = 80, Rect? searchRegion = null)
        {
            // Initial check without lock
            if (!_isTrained)
                throw new InvalidOperationException("Matcher has not been trained.");

            using var gray = EnsureGray(sceneImage);

            _rwLock.EnterReadLock();
            try
            {
                // Double check trained state inside lock
                if (!_isTrained) throw new InvalidOperationException("Matcher trained state changed.");

                // 诊断输出
                // Console.WriteLine($"[PyramidMatcher] Scene: {gray.Width}x{gray.Height}, MinScore: {minScore}");
                // ... (Console logging omitted for brevity in lock scope)

                // 构建场景图的金字塔
                var scenePyramid = BuildScenePyramid(gray);

                try
                {
                    // 从最粗层开始搜索获取候选位置和角度
                    var candidates = CoarseSearch(scenePyramid, minScore);

                    if (candidates.Count == 0) return MatchResult.Empty;

                    // 直接在 Level 0 精确匹配（跳过多层精细化，因为多层匹配存在特征对齐问题）
                    var (directions, magnitudes, _, width, height) = scenePyramid[0];
                    var templates = _pyramidTemplates[0].Templates;

                    if (templates.Count == 0) return MatchResult.Empty;

                    double bestScore = 0;
                    int bestX = 0, bestY = 0, bestTi = 0;
                    double scale = Math.Pow(2, _pyramidLevels - 1);  // 从顶层到底层的缩放倍数

                    // 对每个粗搜索候选，在 Level 0 进行精确匹配
                    foreach (var candidate in candidates)
                    {
                        // 将粗搜索坐标缩放到 Level 0
                        int cx = (int)(candidate.X * scale);
                        int cy = (int)(candidate.Y * scale);

                        // 在候选角度范围内搜索最佳匹配
                        // candidate.TemplateIndex 已经是实际的模板索引，直接使用
                        int searchRange = 16;  // 搜索 ±16 个角度
                        int startTi = Math.Max(0, candidate.TemplateIndex - searchRange);
                        int endTi = Math.Min(templates.Count - 1, candidate.TemplateIndex + searchRange);

                        for (int ti = startTi; ti <= endTi; ti++)
                        {
                            var template = templates[ti];
                            var features = template.Features;
                            int fCount = features.Length;
                            if (fCount == 0) continue;

                            // 在邻域内搜索最佳位置（扩大搜索范围以补偿金字塔缩放误差）
                            int searchRadius = 32;  // 从8增加到32，因为从Level 2到Level 0缩放4倍
                            for (int dy = -searchRadius; dy <= searchRadius; dy += 4)
                            {
                                int py = cy + dy;
                                if (py + template.MinY < 0 || py + template.MaxY >= height) continue;

                                for (int dx = -searchRadius; dx <= searchRadius; dx += 4)
                                {
                                    int px = cx + dx;
                                    if (px + template.MinX < 0 || px + template.MaxX >= width) continue;

                                    int matchCount = 0;
                                    for (int fi = 0; fi < fCount; fi++)
                                    {
                                        var f = features[fi];
                                        int idx = (py + f.Y) * width + (px + f.X);
                                        if (idx >= 0 && idx < directions.Length)
                                        {
                                            byte sceneDir = directions[idx];
                                            if (sceneDir != 0xFF && _directionMatchLut[f.Direction, sceneDir])
                                                matchCount++;
                                        }
                                    }

                                    double score = (double)matchCount / fCount;
                                    if (score > bestScore)
                                    {
                                        bestScore = score;
                                        bestX = px; bestY = py; bestTi = ti;
                                    }
                                }
                            }
                        }
                    }

                    if (bestScore >= minScore / 100.0)
                    {
                        var template = templates[bestTi];
                        return new MatchResult(
                            new OpenCvSharp.Point(bestX, bestY),
                            template.Angle,
                            bestScore * 100);
                    }

                    return MatchResult.Empty;
                }
                finally
                {
                    // 清理金字塔
                    foreach (var (dirs, mags, img, w, h) in scenePyramid)
                    {
                        img.Dispose();
                    }
                }
            }
            finally
            {
                _rwLock.ExitReadLock();
            }
        }

        #endregion

        #region Pyramid Processing

        /// <summary>
        /// 构建场景图像的金字塔
        /// </summary>
        private List<(byte[] directions, ushort[] magnitudes, Mat image, int width, int height)> BuildScenePyramid(Mat gray)
        {
            var pyramid = new List<(byte[], ushort[], Mat, int, int)>();

            Mat current = gray.Clone();

            for (int level = 0; level < _pyramidLevels; level++)
            {
                int thresholdScaled = (int)(_magnitudeThreshold / Math.Pow(2, level));
                int w = current.Width;
                int h = current.Height;
                var (dirs, mags) = ComputeSceneGradients(current, thresholdScaled);
                pyramid.Add((dirs, mags, current, w, h));

                if (level < _pyramidLevels - 1)
                {
                    var next = new Mat();
                    Cv2.PyrDown(current, next);
                    current = next;
                }
            }

            return pyramid;
        }

        /// <summary>
        /// 在最粗层进行粗搜索
        /// </summary>
        private unsafe List<Candidate> CoarseSearch(
            List<(byte[] directions, ushort[] magnitudes, Mat image, int width, int height)> pyramid,
            double minScore)
        {
            int topLevel = _pyramidLevels - 1;
            var (directions, magnitudes, _, width, height) = pyramid[topLevel];
            var templates = _pyramidTemplates[topLevel].Templates;

            while (templates.Count == 0 && topLevel > 0)
            {
                topLevel--;
                (directions, magnitudes, _, width, height) = pyramid[topLevel];
                templates = _pyramidTemplates[topLevel].Templates;
            }

            if (templates.Count == 0) return new List<Candidate>();

            var candidates = new ConcurrentBag<Candidate>();
            double threshold = (minScore / 100.0) * 0.5;  // 恢复50%阈值

            int step = 8;  // 增加步长以加速粗搜索
            int angleStride = Math.Max(8, _angleStep);  // 大幅跳过角度以加速

            Parallel.For(0, (templates.Count + angleStride - 1) / angleStride, i =>
            {
                int ti = i * angleStride;
                if (ti >= templates.Count) return;

                var template = templates[ti];
                var features = template.Features;
                int fCount = features.Length;
                if (fCount == 0) return;

                int startX = Math.Max(0, -template.MinX);
                int endX = width - template.MaxX;
                int startY = Math.Max(0, -template.MinY);
                int endY = height - template.MaxY;

                if (startX >= endX || startY >= endY) return;

                for (int y = startY; y < endY; y += step)
                {
                    for (int x = startX; x < endX; x += step)
                    {
                        int matchCount = 0;
                        for (int fi = 0; fi < fCount; fi++)
                        {
                            var f = features[fi];
                            int idx = (y + f.Y) * width + (x + f.X);
                            byte sceneDir = directions[idx];
                            if (sceneDir != 0xFF && _directionMatchLut[f.Direction, sceneDir])
                                matchCount++;
                        }

                        double score = (double)matchCount / fCount;
                        if (score >= threshold)
                        {
                            candidates.Add(new Candidate(x, y, ti, score));
                        }
                    }
                }
            });

            // 限制候选数量，只保留Top 100
            var result = candidates.ToArray();
            if (result.Length > 100)
            {
                Array.Sort(result, (a, b) => b.Score.CompareTo(a.Score));
                return result.Take(100).ToList();
            }
            return result.ToList();
        }

        // RefineAtLevel removed as unused in simplified logic, or kept if needed.
        // For brevity and thread safety focus, ensuring core methods are safe.
        // Assuming RefineAtLevel matches structure of CoarseSearch if used.

        // ... Keeping existing helper methods ...

        #endregion

        #region Core Methods (Same as basic version)

        private unsafe List<FeaturePoint> ExtractFeatures(Mat gray, Mat? mask, int threshold)
        {
            int width = gray.Width;
            int height = gray.Height;

            using var gradX = new Mat();
            using var gradY = new Mat();

            Cv2.Sobel(gray, gradX, MatType.CV_16S, 1, 0, 3);
            Cv2.Sobel(gray, gradY, MatType.CV_16S, 0, 1, 3);

            int centerX = width / 2;
            int centerY = height / 2;

            var features = new List<FeaturePoint>(width * height / 16);

            short* gxPtr = (short*)gradX.DataPointer;
            short* gyPtr = (short*)gradY.DataPointer;

            int gxStep = (int)gradX.Step() / sizeof(short);
            int gyStep = (int)gradY.Step() / sizeof(short);

            byte* maskPtr = null;
            int maskStep = 0;
            if (mask != null && !mask.Empty())
            {
                maskPtr = mask.DataPointer;
                maskStep = (int)mask.Step();
            }

            for (int y = 1; y < height - 1; y++)
            {
                short* gxRow = gxPtr + y * gxStep;
                short* gyRow = gyPtr + y * gyStep;
                byte* maskRow = maskPtr != null ? maskPtr + y * maskStep : null;

                for (int x = 1; x < width - 1; x++)
                {
                    if (maskRow != null && maskRow[x] == 0)
                        continue;

                    short gx = gxRow[x];
                    short gy = gyRow[x];

                    int magnitude = FastMagnitude(gx, gy);

                    if (magnitude < threshold)
                        continue;

                    byte direction = QuantizeDirection(gx, gy);

                    short relX = (short)(x - centerX);
                    short relY = (short)(y - centerY);

                    features.Add(new FeaturePoint(relX, relY, direction));
                }
            }

            // 使用更大的稀疏化距离（8像素）来减少特征数量
            return SparsifyFeatures(features, 8, 2000);  // 最多保留2000个特征
        }

        private List<FeaturePoint> SparsifyFeatures(List<FeaturePoint> features, double minDistance, int maxFeatures = 2000)
        {
            if (minDistance <= 0 || features.Count < 50)
                return features.Count > maxFeatures ? features.Take(maxFeatures).ToList() : features;

            int gridSize = (int)Math.Ceiling(minDistance);
            var sparse = new List<FeaturePoint>(Math.Min(features.Count / 4, maxFeatures));
            var occupied = new HashSet<long>();

            // 先按梯度强度排序（假设强边缘更重要）
            foreach (var f in features)
            {
                if (sparse.Count >= maxFeatures) break;

                // 修复负坐标处理：使用偏移量确保正索引
                int gx = (f.X + 10000) / gridSize;
                int gy = (f.Y + 10000) / gridSize;
                long key = ((long)gx << 32) | (uint)gy;

                if (!occupied.Contains(key))
                {
                    sparse.Add(f);
                    occupied.Add(key);
                }
            }

            // Console.WriteLine($"[Sparsify] {features.Count} -> {sparse.Count} features (grid={gridSize}, max={maxFeatures})");
            return sparse;
        }

        private RotatedTemplate CreateRotatedTemplate(
            List<FeaturePoint> baseFeatures,
            double angleDeg,
            int centerX, int centerY,
            int templateWidth, int templateHeight)
        {
            double angleRad = angleDeg * Math.PI / 180.0;
            double cosA = Math.Cos(angleRad);
            double sinA = Math.Sin(angleRad);

            int directionOffset = (int)Math.Round(angleDeg / 45.0);
            directionOffset = ((directionOffset % NumDirections) + NumDirections) % NumDirections;

            var rotatedFeatures = new FeaturePoint[baseFeatures.Count];

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;

            for (int i = 0; i < baseFeatures.Count; i++)
            {
                var f = baseFeatures[i];

                double newX = f.X * cosA - f.Y * sinA;
                double newY = f.X * sinA + f.Y * cosA;

                short rx = (short)Math.Round(newX);
                short ry = (short)Math.Round(newY);

                byte newDir = (byte)((f.Direction + directionOffset) % NumDirections);

                rotatedFeatures[i] = new FeaturePoint(rx, ry, newDir);

                if (rx < minX) minX = rx;
                if (rx > maxX) maxX = rx;
                if (ry < minY) minY = ry;
                if (ry > maxY) maxY = ry;
            }

            return new RotatedTemplate(angleDeg, rotatedFeatures, minX, maxX, minY, maxY);
        }

        private unsafe (byte[] directions, ushort[] magnitudes) ComputeSceneGradients(Mat gray, int threshold)
        {
            int width = gray.Width;
            int height = gray.Height;

            var directions = new byte[height * width];
            var magnitudes = new ushort[height * width];

            using var gradX = new Mat();
            using var gradY = new Mat();

            Cv2.Sobel(gray, gradX, MatType.CV_16S, 1, 0, 3);
            Cv2.Sobel(gray, gradY, MatType.CV_16S, 0, 1, 3);

            short* gxPtr = (short*)gradX.DataPointer;
            short* gyPtr = (short*)gradY.DataPointer;

            int gxStep = (int)gradX.Step() / sizeof(short);
            int gyStep = (int)gradY.Step() / sizeof(short);

            Parallel.For(0, height, y =>
            {
                short* gxRow = gxPtr + y * gxStep;
                short* gyRow = gyPtr + y * gyStep;
                int offset = y * width;

                for (int x = 0; x < width; x++)
                {
                    short gx = gxRow[x];
                    short gy = gyRow[x];

                    int mag = FastMagnitude(gx, gy);
                    magnitudes[offset + x] = (ushort)Math.Min(mag, ushort.MaxValue);

                    if (mag >= threshold)
                        directions[offset + x] = QuantizeDirection(gx, gy);
                    else
                        directions[offset + x] = 0xFF;
                }
            });

            return (directions, magnitudes);
        }

        #endregion

        #region Helper Methods

        private static bool[,] BuildDirectionMatchLut()
        {
            var lut = new bool[NumDirections, NumDirections];

            for (int t = 0; t < NumDirections; t++)
            {
                for (int s = 0; s < NumDirections; s++)
                {
                    int diff = Math.Abs(t - s);
                    if (diff > NumDirections / 2)
                        diff = NumDirections - diff;
                    lut[t, s] = diff <= 2;  // 放宽容差：从 ±1 改为 ±2 (允许90°偏差)
                }
            }

            return lut;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FastMagnitude(int gx, int gy)
        {
            int absX = gx >= 0 ? gx : -gx;
            int absY = gy >= 0 ? gy : -gy;

            return absX > absY
                ? absX + (absY * 3 >> 3)
                : absY + (absX * 3 >> 3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte QuantizeDirection(int gx, int gy)
        {
            double angle = Math.Atan2(gy, gx);
            if (angle < 0) angle += 2 * Math.PI;
            int quantized = (int)((angle + DirectionStep / 2) / DirectionStep) % NumDirections;
            return (byte)quantized;
        }

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
                _rwLock.EnterWriteLock();
                try
                {
                    _pyramidTemplates?.Clear();
                    _pyramidTemplates = null!;
                    _isTrained = false;
                }
                finally
                {
                    _rwLock.ExitWriteLock();
                }

                _rwLock.Dispose();
                _isDisposed = true;
            }
        }

        #endregion
    }
}
