// ============================================================================
// 文件名: YoloNms.cs
// 描述:   YOLO NMS 模块 - 非极大值抑制 (Non-Maximum Suppression)
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ClearFrost.Yolo
{
    partial class YoloDetector
    {
        // ==================== NMS 方法 ====================

        private List<YoloResult> NmsFilter(List<YoloResult> initialFilterList, float iouThreshold, bool globalIou)
        {
            if (initialFilterList.Count == 0)
                return new List<YoloResult>();

            // 先按置信度排序
            SortConfidence(initialFilterList);

            if (globalIou)
            {
                // 全局IoU模式：所有类别一起做NMS
                return NmsFilterGlobal(initialFilterList, iouThreshold);
            }
            else
            {
                // 按类别分组并行处理
                return NmsFilterByClass(initialFilterList, iouThreshold);
            }
        }

        /// <summary>
        /// NMS by class: groups detections by class and processes each group in parallel.
        /// </summary>
        private List<YoloResult> NmsFilterByClass(List<YoloResult> sortedList, float iouThreshold)
        {
            var groups = sortedList.GroupBy(r => r.ClassId);

            ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();

            Parallel.ForEach(groups, group =>
            {
                var groupList = group.ToList();
                var nmsResults = NmsFilterSingleGroup(groupList, iouThreshold);
                foreach (var result in nmsResults)
                {
                    resultBag.Add(result);
                }
            });

            return resultBag
                .OrderByDescending(r => r.Confidence)
                .ToList();
        }

        /// <summary>
        /// Performs NMS on a single group of detections (same class).
        /// Input should already be sorted by confidence in descending order.
        /// </summary>
        private List<YoloResult> NmsFilterSingleGroup(List<YoloResult> sortedGroup, float iouThreshold)
        {
            if (sortedGroup.Count == 0)
                return new List<YoloResult>();

            List<YoloResult> kept = new List<YoloResult>();
            bool[] suppressed = new bool[sortedGroup.Count];

            for (int i = 0; i < sortedGroup.Count; i++)
            {
                if (suppressed[i])
                    continue;

                kept.Add(sortedGroup[i]);

                for (int j = i + 1; j < sortedGroup.Count; j++)
                {
                    if (suppressed[j])
                        continue;

                    float iou = CalculateIntersectionOverUnion(sortedGroup[i], sortedGroup[j]);
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                        sortedGroup[j].Dispose();
                    }
                }
            }

            return kept;
        }

        /// <summary>
        /// Global NMS: all classes are treated together, any overlapping boxes are suppressed.
        /// </summary>
        private List<YoloResult> NmsFilterGlobal(List<YoloResult> sortedList, float iouThreshold)
        {
            if (sortedList.Count == 0)
                return new List<YoloResult>();

            List<YoloResult> kept = new List<YoloResult>();
            bool[] suppressed = new bool[sortedList.Count];

            for (int i = 0; i < sortedList.Count; i++)
            {
                if (suppressed[i])
                    continue;

                kept.Add(sortedList[i]);

                for (int j = i + 1; j < sortedList.Count; j++)
                {
                    if (suppressed[j])
                        continue;

                    float iou = CalculateIntersectionOverUnion(sortedList[i], sortedList[j]);
                    if (iou > iouThreshold)
                    {
                        suppressed[j] = true;
                        sortedList[j].Dispose();
                    }
                }
            }

            return kept;
        }

        private float CalculateIntersectionOverUnion(YoloResult box1, YoloResult box2)
        {
            if (box1.Angle.HasValue && box2.Angle.HasValue)
            {
                return CalculateRotatedIntersectionOverUnion(box1, box2);
            }

            float width1 = box1.Width;
            float height1 = box1.Height;
            float width2 = box2.Width;
            float height2 = box2.Height;

            float x1_min = box1.CenterX - width1 / 2;
            float y1_min = box1.CenterY - height1 / 2;
            float x1_max = box1.CenterX + width1 / 2;
            float y1_max = box1.CenterY + height1 / 2;

            float x2_min = box2.CenterX - width2 / 2;
            float y2_min = box2.CenterY - height2 / 2;
            float x2_max = box2.CenterX + width2 / 2;
            float y2_max = box2.CenterY + height2 / 2;

            float intersectionArea, unionArea;
            float left = Math.Max(x1_min, x2_min);
            float top = Math.Max(y1_min, y2_min);
            float right = Math.Min(x1_max, x2_max);
            float bottom = Math.Min(y1_max, y2_max);

            if (left < right && top < bottom)
            {
                intersectionArea = (right - left) * (bottom - top);
            }
            else
            {
                intersectionArea = 0;
            }
            float area1 = width1 * height1;
            float area2 = width2 * height2;
            unionArea = area1 + area2 - intersectionArea;
            return unionArea <= 0 ? 0 : intersectionArea / unionArea;
        }

        private static float CalculateRotatedIntersectionOverUnion(YoloResult box1, YoloResult box2)
        {
            if (box1.Width <= 0 || box1.Height <= 0 || box2.Width <= 0 || box2.Height <= 0)
            {
                return 0;
            }

            List<PointD> polygon1 = GetRotatedCorners(box1);
            List<PointD> polygon2 = GetRotatedCorners(box2);
            List<PointD> intersection = ClipPolygon(polygon1, polygon2);
            double intersectionArea = PolygonArea(intersection);
            double unionArea = box1.Width * box1.Height + box2.Width * box2.Height - intersectionArea;
            if (unionArea <= 0)
            {
                return 0;
            }

            return (float)(intersectionArea / unionArea);
        }

        private static List<PointD> GetRotatedCorners(YoloResult box)
        {
            double angle = box.Angle ?? 0;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            double halfWidth = box.Width / 2.0;
            double halfHeight = box.Height / 2.0;
            var localCorners = new[]
            {
                new PointD(-halfWidth, -halfHeight),
                new PointD(halfWidth, -halfHeight),
                new PointD(halfWidth, halfHeight),
                new PointD(-halfWidth, halfHeight)
            };

            return localCorners
                .Select(point => new PointD(
                    box.CenterX + point.X * cos - point.Y * sin,
                    box.CenterY + point.X * sin + point.Y * cos))
                .ToList();
        }

        private static List<PointD> ClipPolygon(IReadOnlyList<PointD> subjectPolygon, IReadOnlyList<PointD> clipPolygon)
        {
            List<PointD> output = subjectPolygon.ToList();
            if (output.Count == 0 || clipPolygon.Count < 3)
            {
                return new List<PointD>();
            }

            double orientation = SignedPolygonArea(clipPolygon) >= 0 ? 1 : -1;
            for (int i = 0; i < clipPolygon.Count; i++)
            {
                PointD edgeStart = clipPolygon[i];
                PointD edgeEnd = clipPolygon[(i + 1) % clipPolygon.Count];
                List<PointD> input = output;
                output = new List<PointD>();
                if (input.Count == 0)
                {
                    break;
                }

                PointD previous = input[input.Count - 1];
                foreach (PointD current in input)
                {
                    bool currentInside = IsInside(current, edgeStart, edgeEnd, orientation);
                    bool previousInside = IsInside(previous, edgeStart, edgeEnd, orientation);
                    if (currentInside)
                    {
                        if (!previousInside)
                        {
                            output.Add(LineIntersection(previous, current, edgeStart, edgeEnd));
                        }
                        output.Add(current);
                    }
                    else if (previousInside)
                    {
                        output.Add(LineIntersection(previous, current, edgeStart, edgeEnd));
                    }

                    previous = current;
                }
            }

            return output;
        }

        private static bool IsInside(PointD point, PointD edgeStart, PointD edgeEnd, double orientation)
        {
            double cross = Cross(edgeStart, edgeEnd, point);
            return orientation * cross >= -1e-6;
        }

        private static PointD LineIntersection(PointD line1Start, PointD line1End, PointD line2Start, PointD line2End)
        {
            double a1 = line1End.Y - line1Start.Y;
            double b1 = line1Start.X - line1End.X;
            double c1 = a1 * line1Start.X + b1 * line1Start.Y;

            double a2 = line2End.Y - line2Start.Y;
            double b2 = line2Start.X - line2End.X;
            double c2 = a2 * line2Start.X + b2 * line2Start.Y;

            double determinant = a1 * b2 - a2 * b1;
            if (Math.Abs(determinant) < 1e-9)
            {
                return line1End;
            }

            return new PointD(
                (b2 * c1 - b1 * c2) / determinant,
                (a1 * c2 - a2 * c1) / determinant);
        }

        private static double Cross(PointD edgeStart, PointD edgeEnd, PointD point)
        {
            return (edgeEnd.X - edgeStart.X) * (point.Y - edgeStart.Y) -
                (edgeEnd.Y - edgeStart.Y) * (point.X - edgeStart.X);
        }

        private static double PolygonArea(IReadOnlyList<PointD> polygon)
        {
            return Math.Abs(SignedPolygonArea(polygon));
        }

        private static double SignedPolygonArea(IReadOnlyList<PointD> polygon)
        {
            if (polygon.Count < 3)
            {
                return 0;
            }

            double area = 0;
            for (int i = 0; i < polygon.Count; i++)
            {
                PointD current = polygon[i];
                PointD next = polygon[(i + 1) % polygon.Count];
                area += current.X * next.Y - next.X * current.Y;
            }

            return area / 2.0;
        }

        private readonly struct PointD
        {
            public PointD(double x, double y)
            {
                X = x;
                Y = y;
            }

            public double X { get; }
            public double Y { get; }
        }

        private void SortConfidence(List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                dataList.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            }
        }
    }
}


