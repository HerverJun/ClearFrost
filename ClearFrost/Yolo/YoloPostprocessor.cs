// ============================================================================
// 文件名: YoloPostprocessor.cs
// 描述:   YOLO 后处理模块 - 置信度过滤、坐标恢复、Mask 处理
// ============================================================================
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClearFrost.Yolo
{
    partial class YoloDetector
    {
        // ==================== 置信度过滤方法 ====================

        private static ReadOnlySpan<float> GetTensorSpan(Tensor<float> data)
        {
            if (data is DenseTensor<float> dense)
            {
                return dense.Buffer.Span;
            }

            return data.ToArray().AsSpan();
        }

        private static unsafe Mat CreateMatFromTensorBuffer(Tensor<float> data, int rows, int cols)
        {
            if (data is DenseTensor<float> dense)
            {
                var destination = new Mat(rows, cols, MatType.CV_32F);
                ReadOnlySpan<float> span = dense.Buffer.Span;
                fixed (float* srcPtr = span)
                {
                    using var source = new Mat(rows, cols, MatType.CV_32F, (IntPtr)srcPtr);
                    source.CopyTo(destination);
                }

                return destination;
            }

            return new Mat(rows, cols, MatType.CV_32F, data.ToArray());
        }

        private List<YoloResult> FilterConfidence_Yolo8_11_Segment(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < data.Dimensions[1] - 4 - _segWidth; j++)
                    {
                        if (data[0, j + 4, i] >= confidence)
                        {
                            if (tempConfidence < data[0, j + 4, i])
                            {
                                tempConfidence = data[0, j + 4, i];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                        basicData[0] = data[0, 0, i];
                        basicData[1] = data[0, 1, i];
                        basicData[2] = data[0, 2, i];
                        basicData[3] = data[0, 3, i];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        for (int ii = 0; ii < _segWidth; ii++)
                        {
                            int pos = data.Dimensions[1] - _segWidth + ii;
                            mask.At<float>(0, ii) = data[0, pos, i];
                        }
                        temp.MaskData = mask;
                        temp.BasicData = basicData;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dataSpan.Length; i += outputSize)
                {
                    tempConfidence = 0f;
                    index = -1;
                    for (int j = 0; j < outputSize - 4 - _segWidth; j++)
                    {
                        if (dataSpan[i + 4 + j] > confidence)
                        {
                            if (tempConfidence < dataSpan[i + 4 + j])
                            {
                                tempConfidence = dataSpan[i + 4 + j];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                        basicData[0] = dataSpan[i];
                        basicData[1] = dataSpan[i + 1];
                        basicData[2] = dataSpan[i + 2];
                        basicData[3] = dataSpan[i + 3];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        for (int ii = 0; ii < _segWidth; ii++)
                        {
                            int pos = i + outputSize - _segWidth + ii;
                            mask.At<float>(0, ii) = dataSpan[pos];
                        }
                        temp.MaskData = mask;
                        temp.BasicData = basicData;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }

        private List<YoloResult> FilterConfidenceGeneric(Tensor<float> data, float confidence, int boxOffset, bool hasObjectness)
        {
            int extraDecrement = _segWidth + _poseWidth;

            bool isMidSize = data.Dimensions[1] < data.Dimensions[2];
            int dim1 = data.Dimensions[1];
            int dim2 = data.Dimensions[2];

            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, dim2, i =>
                {
                    float maxScore = 0f;
                    int maxClassIndex = -1;

                    if (hasObjectness)
                    {
                        if (data[0, 4, i] < confidence) return;
                    }

                    int loopStart = hasObjectness ? 5 : boxOffset;

                    for (int k = loopStart; k < dim1 - extraDecrement; k++)
                    {
                        float score = data[0, k, i];
                        if (score >= confidence)
                        {
                            if (score > maxScore)
                            {
                                maxScore = score;
                                maxClassIndex = k - boxOffset;
                            }
                        }
                    }

                    if (maxClassIndex != -1)
                    {
                        YoloResult temp = new YoloResult();
                        temp.CenterX = data[0, 0, i];
                        temp.CenterY = data[0, 1, i];
                        temp.Width = data[0, 2, i];
                        temp.Height = data[0, 3, i];
                        temp.Confidence = maxScore;
                        temp.ClassId = maxClassIndex;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                int channelCount = dim2;

                for (int i = 0; i < dataSpan.Length; i += channelCount)
                {
                    float maxScore = 0f;
                    int maxClassIndex = -1;

                    if (hasObjectness)
                    {
                        if (dataSpan[i + 4] < confidence) continue;
                    }

                    int loopStart = hasObjectness ? 5 : boxOffset;

                    for (int k = loopStart; k < channelCount - extraDecrement; k++)
                    {
                        float score = dataSpan[i + k];
                        if (score >= confidence)
                        {
                            if (score > maxScore)
                            {
                                maxScore = score;
                                maxClassIndex = k - boxOffset;
                            }
                        }
                    }

                    if (maxClassIndex != -1)
                    {
                        YoloResult temp = new YoloResult();
                        temp.CenterX = dataSpan[i];
                        temp.CenterY = dataSpan[i + 1];
                        temp.Width = dataSpan[i + 2];
                        temp.Height = dataSpan[i + 3];
                        temp.Confidence = maxScore;
                        temp.ClassId = maxClassIndex;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }

        /// <summary>
        /// YOLOv26 NMS-free 检测后处理
        /// 输出格式: [batch, 300, 6] 其中 6 = [x1, y1, x2, y2, conf, class]
        /// 坐标格式为 xyxy (角点坐标)，需转换为 xywh (中心点 + 宽高)
        /// </summary>
        private List<YoloResult> FilterConfidence_Yolo26_Detect(Tensor<float> data, float confidence)
        {
            List<YoloResult> resultList = new List<YoloResult>();
            int numDetections = data.Dimensions[1];  // 通常为 300

            for (int i = 0; i < numDetections; i++)
            {
                float conf = data[0, i, 4];
                if (conf < confidence) continue;

                // xyxy 格式转换为 xywh
                float x1 = data[0, i, 0];
                float y1 = data[0, i, 1];
                float x2 = data[0, i, 2];
                float y2 = data[0, i, 3];

                YoloResult result = new YoloResult
                {
                    CenterX = (x1 + x2) / 2,
                    CenterY = (y1 + y2) / 2,
                    Width = x2 - x1,
                    Height = y2 - y1,
                    Confidence = conf,
                    ClassId = (int)data[0, i, 5]
                };
                resultList.Add(result);
            }
            return resultList;
        }

        private List<YoloResult> FilterConfidence_Yolo8_9_11_Detect(Tensor<float> data, float confidence)
        {
            return FilterConfidenceGeneric(data, confidence, 4, false);
        }

        private List<YoloResult> FilterConfidence_Yolo5_Segment(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    if (data[0, 4, i] >= confidence)
                    {
                        for (int j = 0; j < data.Dimensions[1] - 5 - _segWidth; j++)
                        {
                            if (tempConfidence < data[0, j + 5, i])
                            {
                                tempConfidence = data[0, j + 5, i];
                                index = j;
                            }
                        }
                        if (index != -1)
                        {
                            float[] basicData = new float[BASIC_DATA_LENGTH];
                            YoloResult temp = new YoloResult();
                            Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                            basicData[0] = data[0, 0, i];
                            basicData[1] = data[0, 1, i];
                            basicData[2] = data[0, 2, i];
                            basicData[3] = data[0, 3, i];
                            basicData[4] = tempConfidence;
                            basicData[5] = index;
                            for (int ii = 0; ii < _segWidth; ii++)
                            {
                                int pos = data.Dimensions[1] - _segWidth + ii;
                                mask.At<float>(0, ii) = data[0, pos, i];
                            }
                            temp.MaskData = mask;
                            temp.BasicData = basicData;
                            resultBag.Add(temp);
                        }
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dataSpan.Length; i += outputSize)
                {
                    if (dataSpan[i + 4] >= confidence)
                    {
                        tempConfidence = 0f;
                        for (int j = 0; j < outputSize - 5 - _segWidth; j++)
                        {
                            if (tempConfidence < dataSpan[i + 5 + j])
                            {
                                tempConfidence = dataSpan[i + 5 + j];
                                index = j;
                            }
                        }
                        if (index != -1)
                        {
                            float[] basicData = new float[BASIC_DATA_LENGTH];
                            YoloResult temp = new YoloResult();
                            Mat mask = new Mat(1, DEFAULT_MASK_CHANNELS, MatType.CV_32F);
                            basicData[0] = dataSpan[i];
                            basicData[1] = dataSpan[i + 1];
                            basicData[2] = dataSpan[i + 2];
                            basicData[3] = dataSpan[i + 3];
                            basicData[4] = dataSpan[i + 4];
                            basicData[5] = index;
                            for (int ii = 0; ii < _segWidth; ii++)
                            {
                                int pos = i + outputSize - _segWidth + ii;
                                mask.At<float>(0, ii) = dataSpan[pos];
                            }
                            temp.BasicData = basicData;
                            temp.MaskData = mask;
                            resultList.Add(temp);
                        }
                    }
                }
                return resultList;
            }
        }

        private List<YoloResult> FilterConfidence_Yolo5_Detect(Tensor<float> data, float confidence)
        {
            return FilterConfidenceGeneric(data, confidence, 5, true);
        }

        private List<YoloResult> FilterConfidence_Yolo6_Detect(Tensor<float> data, float confidence)
        {
            return FilterConfidenceGeneric(data, confidence, 5, false);
        }

        private List<YoloResult> FilterConfidence_Classify(Tensor<float> data, float confidence)
        {
            List<YoloResult> resultList = new List<YoloResult>();
            for (int i = 0; i < data.Dimensions[1]; i++)
            {
                if (data[0, i] >= confidence)
                {
                    float[] filterInfo = new float[2];
                    YoloResult temp = new YoloResult();
                    filterInfo[0] = data[0, i];
                    filterInfo[1] = i;
                    temp.BasicData = filterInfo;
                    resultList.Add(temp);
                }
            }
            SortConfidence(resultList);
            return resultList;
        }

        private List<YoloResult> FilterConfidence_Pose(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < data.Dimensions[1] - 4 - _segWidth - _poseWidth; j++)
                    {
                        if (data[0, j + 4, i] >= confidence)
                        {
                            if (tempConfidence < data[0, j + 4, i])
                            {
                                tempConfidence = data[0, j + 4, i];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        basicData[0] = data[0, 0, i];
                        basicData[1] = data[0, 1, i];
                        basicData[2] = data[0, 2, i];
                        basicData[3] = data[0, 3, i];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        temp.BasicData = basicData;
                        int poseIndex = 0;
                        PosePoint[] keyPoints = new PosePoint[_poseWidth / 3];
                        for (int ii = 0; ii < _poseWidth; ii += 3)
                        {
                            PosePoint p1 = new PosePoint();
                            p1.X = data[0, 5 + ii, i];
                            p1.Y = data[0, 6 + ii, i];
                            p1.Score = data[0, 7 + ii, i];
                            keyPoints[poseIndex] = p1;
                            poseIndex++;
                        }
                        temp.KeyPoints = keyPoints;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                for (int i = 0; i < dataSpan.Length; i += outputSize)
                {
                    tempConfidence = 0f;
                    index = -1;
                    for (int j = 0; j < outputSize - 4 - _poseWidth; j++)
                    {
                        if (dataSpan[i + 4 + j] > confidence)
                        {
                            if (tempConfidence < dataSpan[i + 4 + j])
                            {
                                tempConfidence = dataSpan[i + 4 + j];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[BASIC_DATA_LENGTH];
                        YoloResult temp = new YoloResult();
                        basicData[0] = dataSpan[i];
                        basicData[1] = dataSpan[i + 1];
                        basicData[2] = dataSpan[i + 2];
                        basicData[3] = dataSpan[i + 3];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        temp.BasicData = basicData;
                        int poseIndex = 0;
                        PosePoint[] keyPoints = new PosePoint[_poseWidth / 3];
                        for (int ii = 0; ii < _poseWidth; ii += 3)
                        {
                            PosePoint p1 = new PosePoint();
                            p1.X = dataSpan[i + 5 + ii];
                            p1.Y = dataSpan[i + 6 + ii];
                            p1.Score = dataSpan[i + 7 + ii];
                            keyPoints[poseIndex] = p1;
                            poseIndex++;
                        }
                        temp.KeyPoints = keyPoints;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }

        private List<YoloResult> FilterConfidence_Obb(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2] ? true : false;
            if (isMidSize)
            {
                ConcurrentBag<YoloResult> resultBag = new ConcurrentBag<YoloResult>();
                int outputSize = data.Dimensions[1];
                Parallel.For(0, data.Dimensions[2], i =>
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < data.Dimensions[1] - 5; j++)
                    {
                        if (data[0, j + 4, i] >= confidence)
                        {
                            if (tempConfidence < data[0, j + 4, i])
                            {
                                tempConfidence = data[0, j + 4, i];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[7];
                        YoloResult temp = new YoloResult();
                        basicData[0] = data[0, 0, i];
                        basicData[1] = data[0, 1, i];
                        basicData[2] = data[0, 2, i];
                        basicData[3] = data[0, 3, i];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        basicData[6] = data[0, outputSize - 1, i];
                        temp.BasicData = basicData;
                        resultBag.Add(temp);
                    }
                });
                return resultBag.ToList<YoloResult>();
            }
            else
            {
                List<YoloResult> resultList = new List<YoloResult>();
                int outputSize = data.Dimensions[2];
                float tempConfidence = 0f;
                int index = -1;
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dataSpan.Length; i += outputSize)
                {
                    tempConfidence = 0f;
                    index = -1;
                    for (int j = 0; j < outputSize - 5; j++)
                    {
                        if (dataSpan[i + 4 + j] > confidence)
                        {
                            if (tempConfidence < dataSpan[i + 4 + j])
                            {
                                tempConfidence = dataSpan[i + 4 + j];
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        float[] basicData = new float[7];
                        YoloResult temp = new YoloResult();
                        basicData[0] = dataSpan[i];
                        basicData[1] = dataSpan[i + 1];
                        basicData[2] = dataSpan[i + 2];
                        basicData[3] = dataSpan[i + 3];
                        basicData[4] = tempConfidence;
                        basicData[5] = index;
                        basicData[6] = dataSpan[i + outputSize - 1];
                        temp.BasicData = basicData;
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }

        // ==================== 坐标恢复方法 ====================

        private void RestoreCoordinates(ref List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX = (dataList[i].CenterX - _padLeft) / _scale;
                    dataList[i].CenterY = (dataList[i].CenterY - _padTop) / _scale;
                    dataList[i].Width /= _scale;
                    dataList[i].Height /= _scale;
                }

                if (dataList[0].KeyPoints != null)
                {
                    for (int i = 0; i < dataList.Count; i++)
                    {
                        if (dataList[i].KeyPoints == null) continue;
                        for (int j = 0; j < dataList[i].KeyPoints.Length; j++)
                        {
                            dataList[i].KeyPoints[j].X = (dataList[i].KeyPoints[j].X - _padLeft) / _scale;
                            dataList[i].KeyPoints[j].Y = (dataList[i].KeyPoints[j].Y - _padTop) / _scale;
                        }
                    }
                }
            }
        }

        private void RestoreDrawingCoordinates(ref List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX -= dataList[i].Width / 2;
                    dataList[i].CenterY -= dataList[i].Height / 2;
                }
            }
        }

        private void RestoreCenterCoordinates(ref List<YoloResult> dataList)
        {
            if (dataList.Count > 0)
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX += dataList[i].Width / 2;
                    dataList[i].CenterY += dataList[i].Height / 2;
                }
            }
        }

        private void RemoveOutOfBoundsCoordinates(ref List<YoloResult> dataList)
        {
            for (int i = dataList.Count - 1; i >= 0; i--)
            {
                if (dataList[i].CenterX > _inferenceImageWidth ||
                    dataList[i].CenterY > _inferenceImageHeight ||
                    dataList[i].Width > _inferenceImageWidth ||
                    dataList[i].Height > _inferenceImageHeight)
                {
                    dataList.RemoveAt(i);
                }
            }
        }

        private void RestoreMask(ref List<YoloResult> data, Tensor<float>? output1)
        {
            if (output1 == null) return;
            if (_outputTensorInfo2_Segment == null || _outputTensorInfo2_Segment.Length < 4) return;
            using Mat ot1 = CreateMatFromTensorBuffer(output1, _segWidth, _outputTensorInfo2_Segment[2] * _outputTensorInfo2_Segment[3]);
            if (ot1.Empty()) return;
            for (int i = 0; i < data.Count; i++)
            {
                var currentMask = data[i].MaskData;
                if (currentMask == null || currentMask.Empty()) continue;
                Mat originalMask = currentMask * ot1;
                Parallel.For(0, originalMask.Cols, col =>
                {
                    originalMask.At<float>(0, col) = Sigmoid(originalMask.At<float>(0, col));
                });
                Mat reshapedMask = originalMask.Reshape(1, _outputTensorInfo2_Segment[2], _outputTensorInfo2_Segment[3]);
                int maskX1 = Math.Abs((int)((data[i].CenterX - data[i].Width / 2) * _maskScaleW));
                int maskY1 = Math.Abs((int)((data[i].CenterY - data[i].Height / 2) * _maskScaleH));
                int maskX2 = (int)(data[i].Width * _maskScaleW);
                int maskY2 = (int)(data[i].Height * _maskScaleH);
                if (maskX2 + maskX1 > _outputTensorInfo2_Segment[3]) maskX2 = _outputTensorInfo2_Segment[3] - maskX1;
                if (maskY1 + maskY2 > _outputTensorInfo2_Segment[2]) maskY2 = _outputTensorInfo2_Segment[2] - maskY1;
                Rect region = new Rect(maskX1, maskY1, maskX2, maskY2);
                Mat cropped = new Mat(reshapedMask, region);
                Mat restoredMask = new Mat();
                int enlargedWidth = (int)(cropped.Width / _maskScaleW / _scale);
                int enlargedHeight = (int)(cropped.Height / _maskScaleH / _scale);
                Cv2.Resize(cropped, restoredMask, new OpenCvSharp.Size(enlargedWidth, enlargedHeight));
                Cv2.Threshold(restoredMask, restoredMask, 0.5, 1, ThresholdTypes.Binary);
                data[i].MaskData = restoredMask;
            }
        }

        private float Sigmoid(float value)
        {
            return 1 / (1 + (float)Math.Exp(-value));
        }
    }
}


