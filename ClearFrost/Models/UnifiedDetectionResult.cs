// ============================================================================
// 文件名: UnifiedDetectionResult.cs
// 描述:   统一检测结果模型
//
// 功能:
//   - 保存通用检测结论
//   - 保存 YOLO 推理元数据
//   - 提供图像和检测框信息
// ============================================================================

using System.Drawing;
using OpenCvSharp;

namespace ClearFrost.Models
{
    /// <summary>
    /// 统一检测结果。
    /// </summary>
    public class UnifiedDetectionResult
    {
        #region 通用属性

        /// <summary>
        /// 检测是否合格。
        /// </summary>
        public bool IsQualified { get; set; }

        /// <summary>
        /// 检测分数。
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 处理耗时。
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// 结果消息。
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 检测对象列表。
        /// </summary>
        public List<DetectedObject> Objects { get; set; } = new();

        /// <summary>
        /// 原始图像。
        /// </summary>
        public Bitmap? OriginalBitmap { get; set; }

        #endregion

        #region YOLO 结果属性

        /// <summary>
        /// YOLO 检测结果。
        /// </summary>
        public List<ClearFrost.Yolo.YoloResult>? YoloResults { get; set; }

        /// <summary>
        /// 实际使用的模型名称。
        /// </summary>
        public string? UsedModelName { get; set; }

        /// <summary>
        /// 实际使用的模型标签。
        /// </summary>
        public string[]? UsedModelLabels { get; set; }

        /// <summary>
        /// 是否发生模型回退。
        /// </summary>
        public bool WasFallback { get; set; }

        #endregion
    }

    /// <summary>
    /// 单个检测对象。
    /// </summary>
    public class DetectedObject
    {
        /// 标签名称。
        public string Label { get; set; } = string.Empty;

        /// 置信度。
        public double Confidence { get; set; }

        /// 边界框。
        public Rect BoundingBox { get; set; }

        /// 边界框中心点。
        public OpenCvSharp.Point Center => new OpenCvSharp.Point(
            BoundingBox.X + BoundingBox.Width / 2,
            BoundingBox.Y + BoundingBox.Height / 2);
    }
}


