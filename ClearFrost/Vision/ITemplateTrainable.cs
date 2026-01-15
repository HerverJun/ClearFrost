using OpenCvSharp;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 支持模板训练的算子接口
    /// </summary>
    public interface ITemplateTrainable
    {
        /// <summary>
        /// 使用Mat图像设置模板
        /// </summary>
        void SetTemplateFromMat(Mat template);

        /// <summary>
        /// 是否已训练
        /// </summary>
        bool IsTrained { get; }
    }
}
