// ============================================================================
// 文件名: UnifiedDetectionResult.cs
// 描述:   统一检测结果类型 - 同时支持 YOLO 和传统视觉检测
//
// 设计目标:
//   - 合并原 DetectionResultData 和 VisionResult
//   - 提供统一的检测结果接口
//   - 同时支持深度学习和传统视觉算法
// ============================================================================

using System.Drawing;
using OpenCvSharp;

namespace ClearFrost.Models
{
    /// <summary>
    /// 统一的检测结果类型
    /// </summary>
    public class UnifiedDetectionResult
    {
        #region 通用属性

        /// <summary>
        /// 是否合格/通过
        /// </summary>
        public bool IsQualified { get; set; }

        /// <summary>
        /// 检测得分 (0-1 或 0-100，取决于检测类型)
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 处理耗时 (毫秒)
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// 结果描述/消息
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 检测到的对象列表 (统一格式)
        /// </summary>
        public List<DetectedObject> Objects { get; set; } = new();

        /// <summary>
        /// 原始图像
        /// </summary>
        public Bitmap? OriginalBitmap { get; set; }

        #endregion

        #region YOLO 特有属性

        /// <summary>
        /// YOLO 检测结果列表 (原始格式)
        /// </summary>
        public List<ClearFrost.Yolo.YoloResult>? YoloResults { get; set; }

        /// <summary>
        /// 使用的模型名称
        /// </summary>
        public string? UsedModelName { get; set; }

        /// <summary>
        /// 使用的模型标签列表
        /// </summary>
        public string[]? UsedModelLabels { get; set; }

        /// <summary>
        /// 是否发生了模型回退
        /// </summary>
        public bool WasFallback { get; set; }

        #endregion
    }

    /// <summary>
    /// 检测到的对象 (统一格式)
    /// </summary>
    public class DetectedObject
    {
        /// <summary>对象标签/名称</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>置信度/匹配分数</summary>
        public double Confidence { get; set; }

        /// <summary>边界框</summary>
        public Rect BoundingBox { get; set; }

        /// <summary>中心点</summary>
        public OpenCvSharp.Point Center => new OpenCvSharp.Point(
            BoundingBox.X + BoundingBox.Width / 2,
            BoundingBox.Y + BoundingBox.Height / 2);
    }
}


