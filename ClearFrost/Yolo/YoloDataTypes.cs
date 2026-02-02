// ============================================================================
// 文件名: YoloDataTypes.cs
// 描述:   YOLO 数据结构定义 - 结果类、姿态点、OBB矩形、性能指标等
// ============================================================================
using OpenCvSharp;
using System;
using System.Drawing;

namespace ClearFrost.Yolo
{
    public class YoloResult : IDisposable
    {
        // 基础检测数据 - 强类型属性
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Confidence { get; set; }
        public int ClassId { get; set; }

        // OBB 旋转角度（仅 OBB 任务使用）
        public float? Angle { get; set; }

        // 兼容属性 - 保持向后兼容
        public float[] BasicData
        {
            get => Angle.HasValue
                ? new float[] { CenterX, CenterY, Width, Height, Confidence, ClassId, Angle.Value }
                : new float[] { CenterX, CenterY, Width, Height, Confidence, ClassId };
            set
            {
                if (value.Length >= 6)
                {
                    CenterX = value[0];
                    CenterY = value[1];
                    Width = value[2];
                    Height = value[3];
                    Confidence = value[4];
                    ClassId = (int)value[5];
                    if (value.Length >= 7) Angle = value[6];
                }
            }
        }

        // 计算属性
        public float Left => CenterX - Width / 2;
        public float Top => CenterY - Height / 2;
        public float Right => CenterX + Width / 2;
        public float Bottom => CenterY + Height / 2;
        public RectangleF BoundingBox => new RectangleF(Left, Top, Width, Height);
        public float Area => Width * Height;

        // 分割和姿态数据
        public Mat? MaskData { get; set; }
        public PosePoint[] KeyPoints { get; set; } = Array.Empty<PosePoint>();

        // IDisposable 实现
        private bool _disposed = false;

        public void Dispose()
        {
            if (_disposed) return;
            MaskData?.Dispose();
            MaskData = null;
            _disposed = true;
        }
    }

    public class PosePoint
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Score { get; set; }
    }

    class KalmanFilterTracker
    {
        KalmanFilter kalman = new KalmanFilter(4, 2, 0);
        KalmanFilterTracker()
        {
            kalman.MeasurementMatrix = new Mat(2, 4, MatType.CV_32F, new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0
            });
            kalman.TransitionMatrix = new Mat(4, 4, MatType.CV_32F, new float[]
            {
                1, 0, 1, 0,
                0, 1, 0, 1,
                0, 0, 1, 0,
                0, 0, 0, 1
            });
            kalman.ControlMatrix = new Mat(4, 2, MatType.CV_32F, new float[]
            {
                0, 0,
                0, 0,
                1, 0,
                0, 1
            });
            kalman.ProcessNoiseCov = new Mat(4, 4, MatType.CV_32F, new float[]
            {
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, 0,
                0, 0, 0, 1
            });
            kalman.MeasurementNoiseCov = new Mat(2, 2, MatType.CV_32F, new float[]
            {
                1, 0,
                0, 1
            });
        }
        public PointF PredictNextPosition()
        {
            Mat prediction = kalman.Predict();
            PointF result = new PointF(prediction.At<float>(0), prediction.At<float>(1));
            return result;
        }
        public void UpdateCorrectCoordinates(PointF correctedPoint)
        {
            Mat correction = new Mat(2, 1, MatType.CV_32F, new float[] { correctedPoint.X, correctedPoint.Y });
            kalman.Correct(correction);
        }
    }

    public struct ObbRectangle
    {
        public PointF pt1;
        public PointF pt2;
        public PointF pt3;
        public PointF pt4;
    }

    /// <summary>
    /// 推理性能指标类，用于测量各阶段耗时
    /// </summary>
    public class InferenceMetrics
    {
        /// <summary>
        /// 预处理耗时（毫秒）：包括图像缩放、tensor转换
        /// </summary>
        public double PreprocessMs { get; set; }

        /// <summary>
        /// 推理耗时（毫秒）：ONNX Runtime 模型执行时间
        /// </summary>
        public double InferenceMs { get; set; }

        /// <summary>
        /// 后处理耗时（毫秒）：包括置信度过滤、NMS、坐标恢复
        /// </summary>
        public double PostprocessMs { get; set; }

        /// <summary>
        /// 总耗时（毫秒）
        /// </summary>
        public double TotalMs => PreprocessMs + InferenceMs + PostprocessMs;

        /// <summary>
        /// 推理帧率 (FPS)
        /// </summary>
        public double FPS => TotalMs > 0 ? 1000.0 / TotalMs : 0;

        /// <summary>
        /// 检测到的目标数量
        /// </summary>
        public int DetectionCount { get; set; }

        public override string ToString() =>
            $"Preprocess: {PreprocessMs:F2}ms, Inference: {InferenceMs:F2}ms, " +
            $"Postprocess: {PostprocessMs:F2}ms, Total: {TotalMs:F2}ms, FPS: {FPS:F1}, Detections: {DetectionCount}";
    }
}


