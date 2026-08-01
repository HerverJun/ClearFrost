// ============================================================================
// 文件名: ModelResult.cs
// 描述:   通用视觉算法模型推理结果数据包装。
// ============================================================================

using System;
using System.Collections.Generic;

namespace ClearFrost.Yolo
{
    /// <summary>
    /// 通用视觉算法模型推理结果。
    /// </summary>
    public sealed class ModelResult : IDisposable
    {
        private bool _disposed;

        /// <summary>
        /// 最终的缺陷或检测目标列表。如果是无监督模型，它可以是伪造的缺陷框或空。
        /// </summary>
        public List<YoloResult> Results { get; set; } = new List<YoloResult>();

        /// <summary>
        /// 该模型本次推理给出的异常得分（适用于无监督）。
        /// </summary>
        public float AnomalyScore { get; set; }

        /// <summary>
        /// 模型是否认为该产品合格。
        /// </summary>
        public bool IsQualified { get; set; } = true;

        /// <summary>
        /// 运行时异常信息（如模型推理出错）。
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// 推理过程是否出错。
        /// </summary>
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        /// <summary>
        /// 释放结果列表中的托管/非托管资源。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (Results != null)
            {
                foreach (var r in Results)
                {
                    r.Dispose();
                }
                Results.Clear();
            }
        }
    }
}
