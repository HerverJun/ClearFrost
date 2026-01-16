// ============================================================================
// 
// 
// ============================================================================
using OpenCvSharp;
using System.Collections.Generic;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 
    /// </summary>
    public class PresenceDetectionResult
    {
        public bool IsPresent { get; set; }
        public double Confidence { get; set; }
        public Rect BoundingBox { get; set; }
        public int ChangeArea { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>
    /// 
    /// </summary>
    public class TemplateMatchResult
    {
        public OpenCvSharp.Point Location { get; set; }
        public double Score { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsMatch { get; set; }
        public string Message { get; set; } = string.Empty;
        public Rect BoundingBox => new Rect(Location.X, Location.Y, Width, Height);
    }

    /// <summary>
    /// 
    /// </summary>
    public static class OperatorFactory
    {
        public static IImageOperator? Create(string typeId)
        {
            return typeId.ToLower() switch
            {
                "template_match" => new TemplateMatchOp(),
                "feature_match" => new FeatureMatchOp(),
                "orb_match" => new OrbMatchOp(),
                "pyramid_shape_match" => new PyramidShapeMatchOp(),
                "background_diff" => new BackgroundDiffOp(),
                _ => null
            };
        }

        public static IImageOperator? CreateFromConfig(OperatorConfig config)
        {
            var op = Create(config.TypeId);
            if (op == null) return null;

            foreach (var param in config.Parameters)
            {
                op.SetParameter(param.Key, param.Value);
            }
            return op;
        }

        public static List<OperatorInfo> GetAvailableOperators() => new()
        {
            new OperatorInfo { TypeId = "template_match", Name = "模板匹配", Description = "使用模板图像定位目标位置" },
            new OperatorInfo { TypeId = "feature_match", Name = "特征匹配 (AKAZE)", Description = "基于AKAZE算法的旋转缩放匹配" },
            new OperatorInfo { TypeId = "orb_match", Name = "特征匹配 (ORB)", Description = "基于ORB算法的高速旋转匹配" },
            new OperatorInfo { TypeId = "pyramid_shape_match", Name = "形状匹配 (金字塔)", Description = "工业级高性能梯度形状匹配" },
            new OperatorInfo { TypeId = "background_diff", Name = "有无检测 (背景差分)", Description = "基于背景灰度差的有无检测" }
        };
    }

    /// <summary>
    /// 
    /// </summary>
    public class OperatorInfo
    {
        public string TypeId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}

