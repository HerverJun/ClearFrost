using System;
using System.Diagnostics;
using OpenCvSharp;
using ClearFrost.Vision;

namespace ClearFrost.Demo
{
    /// <summary>
    /// GradientShapeMatcher 使用示例和性能测试
    /// </summary>
    public class ShapeMatcherDemo
    {
        public static void Main(string[] args)
        {
            Console.WriteLine("=== ClearFrost Shape Matcher Demo ===\n");

            // 运行合成测试
            RunSyntheticTest();

            // 如果提供了图像路径, 运行真实图像测试
            if (args.Length >= 2)
            {
                RunRealImageTest(args[0], args[1]);
            }

            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }

        /// <summary>
        /// 使用合成图像进行测试
        /// </summary>
        static void RunSyntheticTest()
        {
            Console.WriteLine("--- Synthetic Image Test ---\n");

            // 创建模板图像 (简单的十字形状)
            using var template = CreateCrossTemplate(100, 100, 30);

            // 创建场景图像 (包含旋转和平移的目标)
            using var scene = CreateTestScene(640, 480, template, 
                targetX: 320, targetY: 240, targetAngle: 45);

            Console.WriteLine($"Template size: {template.Width}x{template.Height}");
            Console.WriteLine($"Scene size: {scene.Width}x{scene.Height}");
            Console.WriteLine($"Expected: Position (320, 240), Angle: 45°\n");

            // 测试基础版本
            TestBasicMatcher(template, scene);

            // 测试金字塔版本
            TestPyramidMatcher(template, scene);

            // 可视化结果
            VisualizeResult(template, scene, "Synthetic Test");
        }

        /// <summary>
        /// 使用真实图像进行测试
        /// </summary>
        static void RunRealImageTest(string templatePath, string scenePath)
        {
            Console.WriteLine("\n--- Real Image Test ---\n");

            using var template = Cv2.ImRead(templatePath, ImreadModes.Grayscale);
            using var scene = Cv2.ImRead(scenePath, ImreadModes.Grayscale);

            if (template.Empty() || scene.Empty())
            {
                Console.WriteLine("Error: Could not load images.");
                return;
            }

            Console.WriteLine($"Template: {templatePath} ({template.Width}x{template.Height})");
            Console.WriteLine($"Scene: {scenePath} ({scene.Width}x{scene.Height})\n");

            TestBasicMatcher(template, scene);
            TestPyramidMatcher(template, scene);
        }

        static void TestBasicMatcher(Mat template, Mat scene)
        {
            Console.WriteLine("[GradientShapeMatcher - Basic]");

            using var matcher = new GradientShapeMatcher(
                magnitudeThreshold: 30,
                angleStep: 1);

            // 训练
            var trainSw = Stopwatch.StartNew();
            matcher.Train(template, angleRange: 180);
            trainSw.Stop();
            Console.WriteLine($"  Train time: {trainSw.ElapsedMilliseconds} ms");

            // 匹配
            var matchSw = Stopwatch.StartNew();
            var result = matcher.Match(scene, minScore: 60);
            matchSw.Stop();

            Console.WriteLine($"  Match time: {matchSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Result: {result}");

            // 多实例匹配
            matchSw.Restart();
            var allResults = matcher.MatchAll(scene, minScore: 60, maxMatches: 5);
            matchSw.Stop();

            Console.WriteLine($"  MatchAll time: {matchSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Found {allResults.Count} matches\n");
        }

        static void TestPyramidMatcher(Mat template, Mat scene)
        {
            Console.WriteLine("[PyramidShapeMatcher - Accelerated]");

            using var matcher = new PyramidShapeMatcher(
                pyramidLevels: 3,
                magnitudeThreshold: 30,
                angleStep: 1);

            // 训练
            var trainSw = Stopwatch.StartNew();
            matcher.Train(template, angleRange: 180);
            trainSw.Stop();
            Console.WriteLine($"  Train time: {trainSw.ElapsedMilliseconds} ms");

            // 匹配
            var matchSw = Stopwatch.StartNew();
            var result = matcher.Match(scene, minScore: 60);
            matchSw.Stop();

            Console.WriteLine($"  Match time: {matchSw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  Result: {result}\n");
        }

        /// <summary>
        /// 创建十字形状的模板图像
        /// </summary>
        static Mat CreateCrossTemplate(int width, int height, int thickness)
        {
            var template = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));

            int centerX = width / 2;
            int centerY = height / 2;
            int halfThick = thickness / 2;

            // 水平线
            Cv2.Rectangle(template,
                new Point(0, centerY - halfThick),
                new Point(width - 1, centerY + halfThick),
                Scalar.All(255), -1);

            // 垂直线
            Cv2.Rectangle(template,
                new Point(centerX - halfThick, 0),
                new Point(centerX + halfThick, height - 1),
                Scalar.All(255), -1);

            return template;
        }

        /// <summary>
        /// 创建包含目标的测试场景
        /// </summary>
        static Mat CreateTestScene(int width, int height, Mat template,
            int targetX, int targetY, double targetAngle)
        {
            var scene = new Mat(height, width, MatType.CV_8UC1, Scalar.All(30));

            // 添加噪声
            var noise = new Mat(height, width, MatType.CV_8UC1);
            Cv2.Randu(noise, 0, 20);
            Cv2.Add(scene, noise, scene);
            noise.Dispose();

            // 旋转模板
            var center = new Point2f(template.Width / 2f, template.Height / 2f);
            using var rotMat = Cv2.GetRotationMatrix2D(center, targetAngle, 1.0);

            using var rotatedTemplate = new Mat();
            Cv2.WarpAffine(template, rotatedTemplate, rotMat, 
                new Size(template.Width, template.Height),
                InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

            // 将旋转后的模板放置到场景中
            int startX = targetX - rotatedTemplate.Width / 2;
            int startY = targetY - rotatedTemplate.Height / 2;

            for (int y = 0; y < rotatedTemplate.Height; y++)
            {
                for (int x = 0; x < rotatedTemplate.Width; x++)
                {
                    int sx = startX + x;
                    int sy = startY + y;

                    if (sx >= 0 && sx < width && sy >= 0 && sy < height)
                    {
                        byte val = rotatedTemplate.At<byte>(y, x);
                        if (val > 0)
                        {
                            scene.Set(sy, sx, val);
                        }
                    }
                }
            }

            // 添加一些干扰图案
            Cv2.Circle(scene, new Point(100, 100), 40, Scalar.All(200), 3);
            Cv2.Rectangle(scene, new Point(500, 350), new Point(580, 430), Scalar.All(180), 2);

            return scene;
        }

        /// <summary>
        /// 可视化匹配结果
        /// </summary>
        static void VisualizeResult(Mat template, Mat scene, string windowTitle)
        {
            using var matcher = new GradientShapeMatcher(magnitudeThreshold: 30, angleStep: 1);
            matcher.Train(template, angleRange: 180);

            var result = matcher.Match(scene, minScore: 50);

            if (!result.IsValid)
            {
                Console.WriteLine("No match found for visualization.");
                return;
            }

            // 转换为彩色图像以便绘制
            using var display = new Mat();
            Cv2.CvtColor(scene, display, ColorConversionCodes.GRAY2BGR);

            // 绘制匹配中心
            Cv2.Circle(display, result.Position, 10, new Scalar(0, 255, 0), 2);

            // 绘制旋转的边界框
            DrawRotatedRect(display, result.Position, 
                new Size(template.Width, template.Height), 
                result.Angle);

            // 添加文字信息
            string info = $"Score: {result.Score:F1}%, Angle: {result.Angle:F1}°";
            Cv2.PutText(display, info, 
                new Point(10, 30), 
                HersheyFonts.HersheySimplex, 0.7, 
                new Scalar(0, 255, 255), 2);

            // 显示
            Cv2.ImShow(windowTitle, display);
            Cv2.WaitKey(0);
            Cv2.DestroyAllWindows();
        }

        /// <summary>
        /// 绘制旋转的矩形
        /// </summary>
        static void DrawRotatedRect(Mat image, Point center, Size size, double angle)
        {
            double angleRad = angle * Math.PI / 180.0;
            double cosA = Math.Cos(angleRad);
            double sinA = Math.Sin(angleRad);

            int hw = size.Width / 2;
            int hh = size.Height / 2;

            // 四个角点 (相对于中心)
            Point2d[] corners = {
                new Point2d(-hw, -hh),
                new Point2d(hw, -hh),
                new Point2d(hw, hh),
                new Point2d(-hw, hh)
            };

            // 旋转并平移
            Point[] rotatedCorners = new Point[4];
            for (int i = 0; i < 4; i++)
            {
                double rx = corners[i].X * cosA - corners[i].Y * sinA + center.X;
                double ry = corners[i].X * sinA + corners[i].Y * cosA + center.Y;
                rotatedCorners[i] = new Point((int)rx, (int)ry);
            }

            // 绘制矩形边
            for (int i = 0; i < 4; i++)
            {
                Cv2.Line(image, rotatedCorners[i], rotatedCorners[(i + 1) % 4],
                    new Scalar(0, 0, 255), 2);
            }
        }
    }

    /// <summary>
    /// 扩展的工具类
    /// </summary>
    public static class ShapeMatcherExtensions
    {
        /// <summary>
        /// 在图像上绘制匹配结果
        /// </summary>
        public static void DrawMatchResult(this Mat image, MatchResult result, 
            Size templateSize, Scalar color, int thickness = 2)
        {
            if (!result.IsValid) return;

            // 绘制中心点
            Cv2.Circle(image, result.Position, 5, color, -1);

            // 绘制旋转矩形
            var rect = new RotatedRect(
                new Point2f(result.Position.X, result.Position.Y),
                new Size2f(templateSize.Width, templateSize.Height),
                (float)result.Angle);

            var vertices = rect.Points();
            for (int i = 0; i < 4; i++)
            {
                Cv2.Line(image,
                    new Point((int)vertices[i].X, (int)vertices[i].Y),
                    new Point((int)vertices[(i + 1) % 4].X, (int)vertices[(i + 1) % 4].Y),
                    color, thickness);
            }
        }

        /// <summary>
        /// 使用 ROI 区域进行局部匹配
        /// </summary>
        public static MatchResult MatchInROI(this GradientShapeMatcher matcher, 
            Mat scene, Rect roi, double minScore = 80)
        {
            return matcher.Match(scene, minScore, roi);
        }
    }
}
