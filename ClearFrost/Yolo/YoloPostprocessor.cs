// ============================================================================
// 文件名: YoloPostprocessor.cs
// 描述:   YOLO 后处理模块 - 置信度过滤、坐标恢复、Mask 处理
// ============================================================================
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System;
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
            bool includeMaskData = ShouldIncludeMaskData();
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2];
            int dim1 = data.Dimensions[1];
            int dim2 = data.Dimensions[2];
            if (isMidSize)
            {
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dim2; i++)
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < dim1 - 4 - _segWidth; j++)
                    {
                        float conf = dataSpan[(j + 4) * dim2 + i];
                        if (conf >= confidence)
                        {
                            if (tempConfidence < conf)
                            {
                                tempConfidence = conf;
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        YoloResult temp = new YoloResult();
                        if (includeMaskData)
                        {
                            Mat mask = new Mat(1, _segWidth, MatType.CV_32F);
                            for (int ii = 0; ii < _segWidth; ii++)
                            {
                                int pos = dim1 - _segWidth + ii;
                                mask.At<float>(0, ii) = dataSpan[pos * dim2 + i];
                            }

                            temp.MaskData = mask;
                        }
                        temp.SetDetectionData(dataSpan[i], dataSpan[dim2 + i], dataSpan[2 * dim2 + i], dataSpan[3 * dim2 + i], tempConfidence, index);
                        resultList.Add(temp);
                    }
                }
                return resultList;
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
                        YoloResult temp = new YoloResult();
                        if (includeMaskData)
                        {
                            Mat mask = new Mat(1, _segWidth, MatType.CV_32F);
                            for (int ii = 0; ii < _segWidth; ii++)
                            {
                                int pos = i + outputSize - _segWidth + ii;
                                mask.At<float>(0, ii) = dataSpan[pos];
                            }

                            temp.MaskData = mask;
                        }
                        temp.SetDetectionData(dataSpan[i], dataSpan[i + 1], dataSpan[i + 2], dataSpan[i + 3], tempConfidence, index);
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
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dim2; i++)
                {
                    float maxScore = 0f;
                    int maxClassIndex = -1;

                    if (hasObjectness)
                    {
                        if (dataSpan[4 * dim2 + i] < confidence) continue;
                    }

                    int loopStart = hasObjectness ? 5 : boxOffset;
                    float objectness = hasObjectness ? dataSpan[4 * dim2 + i] : 1f;

                    for (int k = loopStart; k < dim1 - extraDecrement; k++)
                    {
                        float score = objectness * dataSpan[k * dim2 + i];
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
                        temp.CenterY = dataSpan[dim2 + i];
                        temp.Width = dataSpan[2 * dim2 + i];
                        temp.Height = dataSpan[3 * dim2 + i];
                        temp.Confidence = maxScore;
                        temp.ClassId = maxClassIndex;
                        resultList.Add(temp);
                    }
                }
                return resultList;
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
                    float objectness = hasObjectness ? dataSpan[i + 4] : 1f;

                    for (int k = loopStart; k < channelCount - extraDecrement; k++)
                    {
                        float score = objectness * dataSpan[i + k];
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
            int rank = data.Dimensions.Length;
            if (rank != 2 && rank != 3)
            {
                return resultList;
            }

            int channelCount = data.Dimensions[rank - 1];
            if (channelCount < BASIC_DATA_LENGTH)
            {
                return resultList;
            }

            int numDetections = rank == 2
                ? data.Dimensions[0]
                : data.Dimensions[1];
            ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
            for (int i = 0; i < numDetections; i++)
            {
                int offset = i * channelCount;
                float conf = dataSpan[offset + 4];
                if (conf < confidence) continue;

                // xyxy 格式转换为 xywh
                float x1 = dataSpan[offset];
                float y1 = dataSpan[offset + 1];
                float x2 = dataSpan[offset + 2];
                float y2 = dataSpan[offset + 3];

                YoloResult result = new YoloResult
                {
                    CenterX = (x1 + x2) / 2,
                    CenterY = (y1 + y2) / 2,
                    Width = x2 - x1,
                    Height = y2 - y1,
                    Confidence = conf,
                    ClassId = (int)dataSpan[offset + 5]
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
            bool includeMaskData = ShouldIncludeMaskData();
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2];
            int dim1 = data.Dimensions[1];
            int dim2 = data.Dimensions[2];
            if (isMidSize)
            {
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dim2; i++)
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    if (dataSpan[4 * dim2 + i] >= confidence)
                    {
                        float objectness = dataSpan[4 * dim2 + i];
                        for (int j = 0; j < dim1 - 5 - _segWidth; j++)
                        {
                            float conf = objectness * dataSpan[(j + 5) * dim2 + i];
                            if (tempConfidence < conf)
                            {
                                tempConfidence = conf;
                                index = j;
                            }
                        }
                        if (index != -1)
                        {
                            YoloResult temp = new YoloResult();
                            if (includeMaskData)
                            {
                                Mat mask = new Mat(1, _segWidth, MatType.CV_32F);
                                for (int ii = 0; ii < _segWidth; ii++)
                                {
                                    int pos = dim1 - _segWidth + ii;
                                    mask.At<float>(0, ii) = dataSpan[pos * dim2 + i];
                                }

                                temp.MaskData = mask;
                            }
                            if (tempConfidence >= confidence)
                            {
                                temp.SetDetectionData(dataSpan[i], dataSpan[dim2 + i], dataSpan[2 * dim2 + i], dataSpan[3 * dim2 + i], tempConfidence, index);
                                resultList.Add(temp);
                            }
                            else
                            {
                                temp.Dispose();
                            }
                        }
                    }
                }
                return resultList;
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
                    if (dataSpan[i + 4] >= confidence)
                    {
                        float objectness = dataSpan[i + 4];
                        for (int j = 0; j < outputSize - 5 - _segWidth; j++)
                        {
                            float conf = objectness * dataSpan[i + 5 + j];
                            if (tempConfidence < conf)
                            {
                                tempConfidence = conf;
                                index = j;
                            }
                        }
                        if (index != -1)
                        {
                            YoloResult temp = new YoloResult();
                            if (includeMaskData)
                            {
                                Mat mask = new Mat(1, _segWidth, MatType.CV_32F);
                                for (int ii = 0; ii < _segWidth; ii++)
                                {
                                    int pos = i + outputSize - _segWidth + ii;
                                    mask.At<float>(0, ii) = dataSpan[pos];
                                }

                                temp.MaskData = mask;
                            }
                            if (tempConfidence >= confidence)
                            {
                                temp.SetDetectionData(dataSpan[i], dataSpan[i + 1], dataSpan[i + 2], dataSpan[i + 3], tempConfidence, index);
                                resultList.Add(temp);
                            }
                            else
                            {
                                temp.Dispose();
                            }
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
                    YoloResult temp = new YoloResult();
                    temp.SetClassificationData(data[0, i], i);
                    resultList.Add(temp);
                }
            }
            SortConfidence(resultList);
            return resultList;
        }

        private List<YoloResult> FilterConfidence_Pose(Tensor<float> data, float confidence)
        {
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2];
            int dim1 = data.Dimensions[1];
            int dim2 = data.Dimensions[2];
            if (isMidSize)
            {
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dim2; i++)
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    int classCount = Math.Max(1, dim1 - 4 - _segWidth - _poseWidth);
                    int keyPointStart = 4 + classCount;
                    for (int j = 0; j < classCount; j++)
                    {
                        float conf = dataSpan[(j + 4) * dim2 + i];
                        if (conf >= confidence)
                        {
                            if (tempConfidence < conf)
                            {
                                tempConfidence = conf;
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        YoloResult temp = new YoloResult();
                        temp.SetDetectionData(dataSpan[i], dataSpan[dim2 + i], dataSpan[2 * dim2 + i], dataSpan[3 * dim2 + i], tempConfidence, index);
                        int poseIndex = 0;
                        PosePoint[] keyPoints = new PosePoint[_poseWidth / 3];
                        for (int ii = 0; ii < _poseWidth; ii += 3)
                        {
                            PosePoint p1 = new PosePoint();
                            p1.X = dataSpan[(keyPointStart + ii) * dim2 + i];
                            p1.Y = dataSpan[(keyPointStart + ii + 1) * dim2 + i];
                            p1.Score = dataSpan[(keyPointStart + ii + 2) * dim2 + i];
                            keyPoints[poseIndex] = p1;
                            poseIndex++;
                        }
                        temp.KeyPoints = keyPoints;
                        resultList.Add(temp);
                    }
                }
                return resultList;
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
                    int classCount = Math.Max(1, outputSize - 4 - _poseWidth);
                    int keyPointStart = 4 + classCount;
                    for (int j = 0; j < classCount; j++)
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
                        YoloResult temp = new YoloResult();
                        temp.SetDetectionData(dataSpan[i], dataSpan[i + 1], dataSpan[i + 2], dataSpan[i + 3], tempConfidence, index);
                        int poseIndex = 0;
                        PosePoint[] keyPoints = new PosePoint[_poseWidth / 3];
                        for (int ii = 0; ii < _poseWidth; ii += 3)
                        {
                            PosePoint p1 = new PosePoint();
                            p1.X = dataSpan[i + keyPointStart + ii];
                            p1.Y = dataSpan[i + keyPointStart + ii + 1];
                            p1.Score = dataSpan[i + keyPointStart + ii + 2];
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
            bool isMidSize = data.Dimensions[1] < data.Dimensions[2];
            int dim1 = data.Dimensions[1];
            int dim2 = data.Dimensions[2];
            if (isMidSize)
            {
                List<YoloResult> resultList = new List<YoloResult>();
                ReadOnlySpan<float> dataSpan = GetTensorSpan(data);
                for (int i = 0; i < dim2; i++)
                {
                    float tempConfidence = 0f;
                    int index = -1;
                    for (int j = 0; j < dim1 - 5; j++)
                    {
                        float conf = dataSpan[(j + 4) * dim2 + i];
                        if (conf >= confidence)
                        {
                            if (tempConfidence < conf)
                            {
                                tempConfidence = conf;
                                index = j;
                            }
                        }
                    }
                    if (index != -1)
                    {
                        YoloResult temp = new YoloResult();
                        temp.SetObbData(dataSpan[i], dataSpan[dim2 + i], dataSpan[2 * dim2 + i], dataSpan[3 * dim2 + i], tempConfidence, index, dataSpan[(dim1 - 1) * dim2 + i]);
                        resultList.Add(temp);
                    }
                }
                return resultList;
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
                        YoloResult temp = new YoloResult();
                        temp.SetObbData(dataSpan[i], dataSpan[i + 1], dataSpan[i + 2], dataSpan[i + 3], tempConfidence, index, dataSpan[i + outputSize - 1]);
                        resultList.Add(temp);
                    }
                }
                return resultList;
            }
        }

        // ==================== 坐标恢复方法 ====================

        private void RestoreCoordinates(ref List<YoloResult> dataList)
        {
            float scale = _scale <= 0 ? 1f : _scale;
            if (dataList.Count > 0)
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    dataList[i].CenterX = (dataList[i].CenterX - _padLeft) / scale;
                    dataList[i].CenterY = (dataList[i].CenterY - _padTop) / scale;
                    dataList[i].Width /= scale;
                    dataList[i].Height /= scale;
                }

                if (dataList[0].KeyPoints != null)
                {
                    for (int i = 0; i < dataList.Count; i++)
                    {
                        if (dataList[i].KeyPoints == null) continue;
                        for (int j = 0; j < dataList[i].KeyPoints.Length; j++)
                        {
                            dataList[i].KeyPoints[j].X = (dataList[i].KeyPoints[j].X - _padLeft) / scale;
                            dataList[i].KeyPoints[j].Y = (dataList[i].KeyPoints[j].Y - _padTop) / scale;
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
                YoloResult item = dataList[i];
                if (item.DataKind == YoloResultDataKind.Classification)
                {
                    continue;
                }

                if (!IsFinite(item.CenterX) ||
                    !IsFinite(item.CenterY) ||
                    !IsFinite(item.Width) ||
                    !IsFinite(item.Height) ||
                    item.Width <= 0 ||
                    item.Height <= 0)
                {
                    item.Dispose();
                    dataList.RemoveAt(i);
                    continue;
                }

                float left = item.CenterX - item.Width / 2;
                float top = item.CenterY - item.Height / 2;
                float right = item.CenterX + item.Width / 2;
                float bottom = item.CenterY + item.Height / 2;
                if (right <= 0 || bottom <= 0 || left >= _inferenceImageWidth || top >= _inferenceImageHeight)
                {
                    item.Dispose();
                    dataList.RemoveAt(i);
                    continue;
                }

                if (!item.Angle.HasValue)
                {
                    float clippedLeft = Math.Clamp(left, 0, _inferenceImageWidth);
                    float clippedTop = Math.Clamp(top, 0, _inferenceImageHeight);
                    float clippedRight = Math.Clamp(right, 0, _inferenceImageWidth);
                    float clippedBottom = Math.Clamp(bottom, 0, _inferenceImageHeight);
                    item.CenterX = (clippedLeft + clippedRight) / 2;
                    item.CenterY = (clippedTop + clippedBottom) / 2;
                    item.Width = clippedRight - clippedLeft;
                    item.Height = clippedBottom - clippedTop;
                }
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private bool ShouldIncludeMaskData()
        {
            return _executionTaskMode != YoloTaskType.SegmentDetectOnly;
        }

        private void RestoreMask(ref List<YoloResult> data, Tensor<float>? output1)
        {
            if (output1 == null) return;
            if (_outputTensorInfo2_Segment == null || _outputTensorInfo2_Segment.Length < 4) return;
            using Mat ot1 = CreateMatFromTensorBuffer(output1, _segWidth, _outputTensorInfo2_Segment[2] * _outputTensorInfo2_Segment[3]);
            if (ot1.Empty()) return;
            for (int i = 0; i < data.Count; i++)
            {
                Mat? currentMask = data[i].MaskData;
                data[i].MaskData = null;
                if (currentMask == null || currentMask.Empty())
                {
                    currentMask?.Dispose();
                    continue;
                }

                using (currentMask)
                using (Mat originalMask = currentMask * ot1)
                {
                    Parallel.For(0, originalMask.Cols, col =>
                    {
                        originalMask.At<float>(0, col) = Sigmoid(originalMask.At<float>(0, col));
                    });

                    using Mat reshapedMask = originalMask.Reshape(1, _outputTensorInfo2_Segment[2], _outputTensorInfo2_Segment[3]);
                    int maskWidth = _outputTensorInfo2_Segment[3];
                    int maskHeight = _outputTensorInfo2_Segment[2];
                    int maskX1 = Math.Clamp((int)Math.Floor((data[i].CenterX - data[i].Width / 2) * _maskScaleW), 0, maskWidth);
                    int maskY1 = Math.Clamp((int)Math.Floor((data[i].CenterY - data[i].Height / 2) * _maskScaleH), 0, maskHeight);
                    int maskX2 = Math.Clamp((int)Math.Ceiling((data[i].CenterX + data[i].Width / 2) * _maskScaleW), 0, maskWidth);
                    int maskY2 = Math.Clamp((int)Math.Ceiling((data[i].CenterY + data[i].Height / 2) * _maskScaleH), 0, maskHeight);
                    int cropWidth = maskX2 - maskX1;
                    int cropHeight = maskY2 - maskY1;
                    if (cropWidth <= 0 || cropHeight <= 0)
                    {
                        continue;
                    }

                    Rect region = new Rect(maskX1, maskY1, cropWidth, cropHeight);
                    using Mat cropped = new Mat(reshapedMask, region);
                    Mat restoredMask = new Mat();
                    float scale = _scale <= 0 ? 1f : _scale;
                    int enlargedWidth = Math.Max(1, (int)Math.Round(cropped.Width / _maskScaleW / scale));
                    int enlargedHeight = Math.Max(1, (int)Math.Round(cropped.Height / _maskScaleH / scale));
                    Cv2.Resize(cropped, restoredMask, new OpenCvSharp.Size(enlargedWidth, enlargedHeight));
                    Cv2.Threshold(restoredMask, restoredMask, 0.5, 1, ThresholdTypes.Binary);
                    data[i].MaskData = restoredMask;
                }
            }
        }

        private float Sigmoid(float value)
        {
            return 1 / (1 + (float)Math.Exp(-value));
        }
    }
}


