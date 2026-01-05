using OpenCvSharp;

namespace YOLO.Vision
{
    /// <summary>
    /// 视觉检测模式枚举
    /// </summary>
    public enum VisionMode
    {
        /// <summary>YOLO 深度学习检测</summary>
        YOLO = 0,
        /// <summary>传统视觉算法（模板匹配等）</summary>
        Template = 1
    }

    /// <summary>
    /// 视觉处理器统一接口
    /// 用于管理不同的检测模式（YOLO / 传统视觉）
    /// </summary>
    public interface IVisionProcessor
    {
        /// <summary>
        /// 处理器名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 处理图像并返回检测结果
        /// </summary>
        /// <param name="input">输入图像</param>
        /// <returns>检测结果</returns>
        Task<VisionResult> ProcessAsync(Mat input);

        /// <summary>
        /// 获取处理后的预览图像
        /// </summary>
        /// <param name="input">输入图像</param>
        /// <returns>预览图像</returns>
        Task<Mat> GetPreviewAsync(Mat input);

        /// <summary>
        /// 初始化处理器
        /// </summary>
        void Initialize();

        /// <summary>
        /// 释放资源
        /// </summary>
        void Dispose();
    }

    /// <summary>
    /// 视觉检测结果
    /// </summary>
    public class VisionResult
    {
        /// <summary>是否通过检测（OK）</summary>
        public bool IsPass { get; set; }

        /// <summary>检测得分</summary>
        public double Score { get; set; }

        /// <summary>检测到的对象列表</summary>
        public List<DetectedObject> Objects { get; set; } = new();

        /// <summary>处理时间（毫秒）</summary>
        public double ProcessingTimeMs { get; set; }

        /// <summary>结果描述</summary>
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 检测到的对象
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
