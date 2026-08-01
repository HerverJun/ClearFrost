// ============================================================================
// 文件名: IVisionModel.cs
// 描述:   统一的视觉算法推理模型接口，支持 YOLO 及无监督等算法。
// ============================================================================

using System;
using System.Drawing;
using OpenCvSharp;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// 统一的视觉算法推理模型接口。
    /// </summary>
    public interface IVisionModel : IDisposable
    {
        /// <summary>模型识别的标签名称数组</summary>
        string[] Labels { get; }

        /// <summary>上次推理的性能指标</summary>
        InferenceMetrics? LastMetrics { get; }

        /// <summary>是否请求启用 GPU</summary>
        bool RequestedGpu { get; }

        /// <summary>GPU 是否真正激活</summary>
        bool GpuActive { get; }

        /// <summary>GPU 设备 ID</summary>
        int GpuDeviceId { get; }

        /// <summary>当前使用的执行提供程序名称</summary>
        string ExecutionProvider { get; }

        /// <summary>GPU 启动失败原因说明</summary>
        string GpuFailureReason { get; }

        /// <summary>
        /// 执行视觉模型推理
        /// </summary>
        ModelResult Inference(Bitmap image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = -1);

        /// <summary>
        /// 执行视觉模型推理 (OpenCV Mat 直通路径)
        /// </summary>
        ModelResult Inference(Mat image, float confidence = 0.5f, float iouThreshold = 0.3f, bool globalIou = false, int preprocessingMode = -1);
    }
}
