// ============================================================================
// 文件名: IDetectionService.cs
// 描述:   检测服务接口
//
// 功能:
//   - 定义 YOLO 检测的标准接口
//   - 支持多模型管理和自动切换
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using OpenCvSharp;
using ClearFrost.Core.Rules;
using ClearFrost.Yolo;

namespace ClearFrost.Interfaces
{
    /// <summary>
    /// 检测结果数据
    /// </summary>
    public class DetectionResultData
    {
        /// <summary>
        /// 是否合格。只有 IsRuleEvaluated=true 或 HasError=true 时才代表最终 OK/NG。
        /// </summary>
        public bool IsQualified { get; set; }

        /// <summary>
        /// 是否已经完成规则引擎判定。纯推理层返回时该值为 false。
        /// </summary>
        public bool IsRuleEvaluated { get; set; }

        /// <summary>
        /// 规则引擎输出的最终判定。推理层本身不再负责业务规则判定。
        /// </summary>
        public InspectionJudgeResult? JudgeResult { get; set; }

        /// <summary>
        /// YOLO 检测结果列表
        /// </summary>
        public List<YoloResult>? Results { get; set; }

        /// <summary>
        /// 原始图像
        /// </summary>
        public Bitmap? OriginalBitmap { get; set; }

        /// <summary>
        /// 推理耗时 (毫秒)
        /// </summary>
        public long ElapsedMs { get; set; }

        /// <summary>
        /// 使用的模型标签列表
        /// </summary>
        public string[]? UsedModelLabels { get; set; }

        /// <summary>
        /// 使用的模型名称
        /// </summary>
        public string? UsedModelName { get; set; }

        /// <summary>
        /// 是否触发模型回退
        /// </summary>
        public bool WasFallback { get; set; }

        /// <summary>
        /// 检测流程是否发生错误。发生错误时必须按 NG 处理，不能再用空结果重新判定为 OK。
        /// </summary>
        public bool HasError { get; set; }

        /// <summary>
        /// 检测错误说明。
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// 本次检测实际使用的判定规则快照。
        /// </summary>
        public string RuleSetJson { get; set; } = string.Empty;

        /// <summary>
        /// 本次检测用于追溯显示的主要目标标签。
        /// </summary>
        public string TargetLabel { get; set; } = string.Empty;

        /// <summary>
        /// 本次检测用于追溯显示的期望数量。
        /// </summary>
        public int ExpectedCount { get; set; }
    }

    /// <summary>
    /// 检测运行时推理后端状态。
    /// </summary>
    public sealed class DetectionRuntimeStatus
    {
        public bool GpuRequested { get; init; }
        public bool GpuActive { get; init; }
        public int GpuDeviceId { get; init; }
        public string ExecutionProvider { get; init; } = "CPUExecutionProvider";
        public string GpuFailureReason { get; init; } = string.Empty;
    }

    /// <summary>
    /// 检测服务接口
    /// </summary>
    public interface IDetectionService : IDisposable
    {
        #region 事件

        /// <summary>
        /// 检测完成事件
        /// </summary>
        event Action<DetectionResultData>? DetectionCompleted;

        /// <summary>
        /// 模型加载完成事件
        /// </summary>
        event Action<string>? ModelLoaded;

        /// <summary>
        /// 错误发生事件
        /// </summary>
        event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        /// <summary>
        /// 模型是否已加载
        /// </summary>
        bool IsModelLoaded { get; }

        /// <summary>
        /// 当前加载的模型名称
        /// </summary>
        string CurrentModelName { get; }

        /// <summary>
        /// 可用模型列表
        /// </summary>
        IReadOnlyList<string> AvailableModels { get; }

        /// <summary>
        /// 最后一次推理耗时
        /// </summary>
        long LastInferenceMs { get; }

        /// <summary>
        /// 当前检测推理后端状态。
        /// </summary>
        DetectionRuntimeStatus RuntimeStatus { get; }

        #endregion

        #region 方法

        /// <summary>
        /// 加载 YOLO 模型
        /// </summary>
        /// <param name="modelPath">模型文件路径</param>
        /// <param name="useGpu">是否使用 GPU</param>
        /// <returns>是否成功</returns>
        Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0);

        /// <summary>
        /// 扫描并加载默认模型
        /// </summary>
        /// <param name="modelsDirectory">模型目录</param>
        /// <param name="useGpu">是否使用 GPU</param>
        Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0);

        /// <summary>
        /// 切换模型
        /// </summary>
        /// <param name="modelName">模型名称</param>
        Task<bool> SwitchModelAsync(string modelName);

        /// <summary>
        /// 执行检测
        /// </summary>
        /// <param name="image">输入图像 (Mat)</param>
        /// <param name="confidence">置信度阈值</param>
        /// <param name="iouThreshold">IOU 阈值</param>
        /// <param name="fallbackGoal">可选多模型回退目标，不参与最终 OK/NG 判定。</param>
        Task<DetectionResultData> DetectAsync(
            Mat image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal = null,
            MultiModelCandidateEvaluator? candidateEvaluator = null);

        /// <summary>
        /// 执行检测 (Bitmap 输入)
        /// </summary>
        /// <param name="fallbackGoal">可选多模型回退目标，不参与最终 OK/NG 判定。</param>
        Task<DetectionResultData> DetectAsync(
            Bitmap image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal = null,
            MultiModelCandidateEvaluator? candidateEvaluator = null);

        /// <summary>
        /// 生成带标注的结果图像
        /// </summary>
        /// <param name="original">原始图像</param>
        /// <param name="results">检测结果</param>
        /// <param name="labels">标签列表</param>
        Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels);

        /// <summary>
        /// 设置任务类型
        /// </summary>
        void SetTaskMode(int taskType);

        /// <summary>
        /// 启用/禁用多模型回退
        /// </summary>
        void SetEnableFallback(bool enabled);

        /// <summary>
        /// 加载辅助模型1
        /// </summary>
        Task<bool> LoadAuxiliary1ModelAsync(string modelPath);

        /// <summary>
        /// 加载辅助模型2
        /// </summary>
        Task<bool> LoadAuxiliary2ModelAsync(string modelPath);

        /// <summary>
        /// 卸载辅助模型1
        /// </summary>
        void UnloadAuxiliary1Model();

        /// <summary>
        /// 卸载辅助模型2
        /// </summary>
        void UnloadAuxiliary2Model();

        /// <summary>
        /// 获取当前模型标签
        /// </summary>
        string[] GetLabels();

        /// <summary>
        /// 获取最后一次推理的性能指标
        /// </summary>
        object? GetLastMetrics();

        #endregion
    }
}
