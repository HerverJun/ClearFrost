// ============================================================================
// 
// 
//
// 
// 
// 
// 
// ============================================================================

using System.Drawing;
using OpenCvSharp;

namespace ClearFrost.Models
{
    /// <summary>
    /// 
    /// </summary>
    public class UnifiedDetectionResult
    {
        #region ͨ������

        /// <summary>
        /// 
        /// </summary>
        public bool IsQualified { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// 
        /// </summary>
        public List<DetectedObject> Objects { get; set; } = new();

        /// <summary>
        /// 
        /// </summary>
        public Bitmap? OriginalBitmap { get; set; }

        #endregion

        #region YOLO ��������

        /// <summary>
        /// 
        /// </summary>
        public List<ClearFrost.Yolo.YoloResult>? YoloResults { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string? UsedModelName { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public string[]? UsedModelLabels { get; set; }

        /// <summary>
        /// 
        /// </summary>
        public bool WasFallback { get; set; }

        #endregion
    }

    /// <summary>
    /// 
    /// </summary>
    public class DetectedObject
    {
        /// 
        public string Label { get; set; } = string.Empty;

        /// 
        public double Confidence { get; set; }

        /// 
        public Rect BoundingBox { get; set; }

        /// 
        public OpenCvSharp.Point Center => new OpenCvSharp.Point(
            BoundingBox.X + BoundingBox.Width / 2,
            BoundingBox.Y + BoundingBox.Height / 2);
    }
}


