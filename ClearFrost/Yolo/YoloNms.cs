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

            // YOLOv26 NMS-free 模式：模型输出已经是最终结果，跳过 NMS 处理
            if (_yoloVersion == 26)
                return initialFilterList;

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

            return resultBag.ToList();
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
                    }
                }
            }

            return kept;
        }

        private float CalculateIntersectionOverUnion(YoloResult box1, YoloResult box2)
        {
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
            return intersectionArea / unionArea;
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


