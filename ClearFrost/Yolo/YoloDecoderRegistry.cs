// ============================================================================
// 文件名: YoloDecoderRegistry.cs
// 作者: 蘅芜君
// 描述:   YOLO 输出解码器注册表
// ============================================================================
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ClearFrost.Yolo
{
    internal delegate List<YoloResult> YoloDecodeFunc(Tensor<float> output, float confidence);

    internal sealed class YoloDecoderDescriptor
    {
        public YoloDecoderDescriptor(
            string name,
            YoloOutputLayout layout,
            Func<YoloOutputLayout, Tensor<float>, int, bool> canDecode,
            YoloDecodeFunc decode)
        {
            Name = name;
            Layout = layout;
            CanDecode = canDecode;
            Decode = decode;
        }

        public string Name { get; }
        public YoloOutputLayout Layout { get; }
        public Func<YoloOutputLayout, Tensor<float>, int, bool> CanDecode { get; }
        public YoloDecodeFunc Decode { get; }
    }

    internal sealed class YoloDecoderRegistry
    {
        private readonly IReadOnlyList<YoloDecoderDescriptor> _decoders;

        public YoloDecoderRegistry(IEnumerable<YoloDecoderDescriptor> decoders)
        {
            _decoders = decoders.ToArray();
        }

        public YoloDecoderDescriptor Resolve(YoloOutputLayout layout, Tensor<float> output, int yoloVersion)
        {
            YoloDecoderDescriptor? decoder = _decoders.FirstOrDefault(item => item.CanDecode(layout, output, yoloVersion));
            if (decoder == null)
            {
                throw new NotSupportedException(
                    $"未注册 YOLO 输出解码器: layout={layout}, version={yoloVersion}, shape=[{string.Join(", ", output.Dimensions.ToArray())}]");
            }

            return decoder;
        }
    }

    partial class YoloDetector
    {
        private YoloDecoderRegistry? _decoderRegistry;

        private YoloDecoderRegistry DecoderRegistry => _decoderRegistry ??= CreateDecoderRegistry();

        private YoloDecoderRegistry CreateDecoderRegistry()
        {
            return new YoloDecoderRegistry(new[]
            {
                new YoloDecoderDescriptor(
                    "classification",
                    YoloOutputLayout.Classification,
                    (layout, output, _) =>
                        layout == YoloOutputLayout.Classification &&
                        output.Dimensions.Length == 2,
                    FilterConfidence_Classify),

                new YoloDecoderDescriptor(
                    "decoded-xyxy",
                    YoloOutputLayout.DecodedXyxy,
                    (layout, _, _) => layout == YoloOutputLayout.DecodedXyxy,
                    FilterConfidence_Yolo26_Detect),

                new YoloDecoderDescriptor(
                    "raw-yolo6",
                    YoloOutputLayout.RawYoloNoObjectness,
                    (layout, output, version) =>
                        layout == YoloOutputLayout.RawYoloNoObjectness &&
                        version == 6 &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Yolo6_Detect),

                new YoloDecoderDescriptor(
                    "raw-yolo-no-objectness",
                    YoloOutputLayout.RawYoloNoObjectness,
                    (layout, output, _) =>
                        layout == YoloOutputLayout.RawYoloNoObjectness &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Yolo8_9_11_Detect),

                new YoloDecoderDescriptor(
                    "raw-yolo-objectness",
                    YoloOutputLayout.RawYoloObjectness,
                    (layout, output, _) =>
                        layout == YoloOutputLayout.RawYoloObjectness &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Yolo5_Detect),

                new YoloDecoderDescriptor(
                    "segment-yolo5-objectness",
                    YoloOutputLayout.SegmentRaw,
                    (layout, output, version) =>
                        layout == YoloOutputLayout.SegmentRaw &&
                        version < 8 &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Yolo5_Segment),

                new YoloDecoderDescriptor(
                    "segment-yolo-no-objectness",
                    YoloOutputLayout.SegmentRaw,
                    (layout, output, version) =>
                        layout == YoloOutputLayout.SegmentRaw &&
                        version >= 8 &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Yolo8_11_Segment),

                new YoloDecoderDescriptor(
                    "pose-raw",
                    YoloOutputLayout.PoseRaw,
                    (layout, output, _) =>
                        layout == YoloOutputLayout.PoseRaw &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Pose),

                new YoloDecoderDescriptor(
                    "obb-raw",
                    YoloOutputLayout.ObbRaw,
                    (layout, output, _) =>
                        layout == YoloOutputLayout.ObbRaw &&
                        output.Dimensions.Length == 3,
                    FilterConfidence_Obb)
            });
        }

        private List<YoloResult> DecodeModelOutput(Tensor<float> output, float confidence, YoloOutputLayout layout)
        {
            YoloDecoderDescriptor decoder = DecoderRegistry.Resolve(layout, output, _yoloVersion);
            return decoder.Decode(output, confidence);
        }
    }
}
