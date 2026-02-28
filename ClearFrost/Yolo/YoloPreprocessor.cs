// ============================================================================
// 文件名: YoloPreprocessor.cs
// 描述:   YOLO 预处理模块 - Letterbox 缩放、图像转 Tensor
// ============================================================================
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace ClearFrost.Yolo
{
    partial class YoloDetector
    {
        // ==================== 预处理方法 ====================

        /// <summary>
        /// 调整图像大小（简单缩放，无 letterbox）
        /// </summary>
        private Bitmap ResizeImage(Bitmap image)
        {
            float scaledImageWidth = _inferenceImageWidth;
            float scaledImageHeight = _inferenceImageHeight;
            if (scaledImageWidth > _tensorWidth || scaledImageHeight > _tensorHeight)
            {
                _scale = (_tensorWidth / scaledImageWidth) < (_tensorHeight / scaledImageHeight) ? (_tensorWidth / scaledImageWidth) : (_tensorHeight / scaledImageHeight);
                scaledImageWidth = scaledImageWidth * _scale;
                scaledImageHeight = scaledImageHeight * _scale;
            }
            Bitmap scaledImage = new Bitmap((int)scaledImageWidth, (int)scaledImageHeight);
            using (Graphics graphics = Graphics.FromImage(scaledImage))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Default;
                graphics.DrawImage(image, 0, 0, scaledImageWidth, scaledImageHeight);
            }
            return scaledImage;
        }

        /// <summary>
        /// Letterbox resize: scales image while preserving aspect ratio and pads to target size.
        /// Uses YOLO standard gray color (114, 114, 114) for padding.
        /// </summary>
        private Bitmap LetterboxResize(Bitmap image)
        {
            float scaleW = (float)_tensorWidth / _inferenceImageWidth;
            float scaleH = (float)_tensorHeight / _inferenceImageHeight;
            _scale = Math.Min(scaleW, scaleH);

            int newW = (int)Math.Round(_inferenceImageWidth * _scale);
            int newH = (int)Math.Round(_inferenceImageHeight * _scale);

            // Calculate padding to center the image
            _padLeft = (_tensorWidth - newW) / 2;
            _padTop = (_tensorHeight - newH) / 2;
            int padRight = Math.Max(0, _tensorWidth - newW - _padLeft);
            int padBottom = Math.Max(0, _tensorHeight - newH - _padTop);

            using Mat src = BitmapConverter.ToMat(image);
            using Mat resized = new Mat();
            using Mat letterboxed = new Mat();

            Cv2.Resize(src, resized, new OpenCvSharp.Size(newW, newH), 0, 0, InterpolationFlags.Linear);
            Cv2.CopyMakeBorder(
                resized,
                letterboxed,
                _padTop,
                padBottom,
                _padLeft,
                padRight,
                BorderTypes.Constant,
                new Scalar(LETTERBOX_FILL_COLOR_B, LETTERBOX_FILL_COLOR_G, LETTERBOX_FILL_COLOR_R));

            return BitmapConverter.ToBitmap(letterboxed);
        }

        /// <summary>
        /// Mat 版 Letterbox resize：保持宽高比并填充到模型输入尺寸。
        /// </summary>
        private Mat LetterboxResizeMat(Mat image)
        {
            float scaleW = (float)_tensorWidth / _inferenceImageWidth;
            float scaleH = (float)_tensorHeight / _inferenceImageHeight;
            _scale = Math.Min(scaleW, scaleH);

            int newW = (int)Math.Round(_inferenceImageWidth * _scale);
            int newH = (int)Math.Round(_inferenceImageHeight * _scale);

            _padLeft = (_tensorWidth - newW) / 2;
            _padTop = (_tensorHeight - newH) / 2;
            int padRight = Math.Max(0, _tensorWidth - newW - _padLeft);
            int padBottom = Math.Max(0, _tensorHeight - newH - _padTop);

            using Mat resized = new Mat();
            Cv2.Resize(image, resized, new OpenCvSharp.Size(newW, newH), 0, 0, InterpolationFlags.Linear);

            Mat letterboxed = new Mat();
            Cv2.CopyMakeBorder(
                resized,
                letterboxed,
                _padTop,
                padBottom,
                _padLeft,
                padRight,
                BorderTypes.Constant,
                new Scalar(LETTERBOX_FILL_COLOR_B, LETTERBOX_FILL_COLOR_G, LETTERBOX_FILL_COLOR_R));
            return letterboxed;
        }

        /// <summary>
        /// Ensures tensor buffer is allocated and ready for use.
        /// </summary>
        private void EnsureTensorBuffer()
        {
            int requiredLength = _inputTensorInfo[1] * _inputTensorInfo[2] * _inputTensorInfo[3];
            if (!_tensorBufferInitialized || _tensorBuffer == null || _tensorBuffer.Length != requiredLength)
            {
                _tensorBuffer = new float[requiredLength];
                _tensorBufferInitialized = true;
                _cachedInputTensor = null;
            }
        }

        /// <summary>
        /// 高精度模式：并行图像转 Tensor
        /// </summary>
        private unsafe void ImageToTensor_Parallel(Bitmap image, float[] buffer)
        {
            int height = image.Height;
            int width = image.Width;
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;

            BitmapData imageData = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = imageData.Stride;
            byte* scan0 = (byte*)imageData.Scan0.ToPointer();
            try
            {
                Parallel.For(0, height, y =>
                {
                    byte* rowStart = scan0 + (y * stride);
                    for (int x = 0; x < width; x++)
                    {
                        byte* pixel = rowStart + (x * 3);
                        // BGR -> RGB, channel-first layout
                        int baseIndex = y * tensorWidth + x;
                        buffer[2 * channelSize + baseIndex] = pixel[0] / PIXEL_NORMALIZE_FACTOR;  // B -> channel 2
                        buffer[1 * channelSize + baseIndex] = pixel[1] / PIXEL_NORMALIZE_FACTOR;  // G -> channel 1
                        buffer[0 * channelSize + baseIndex] = pixel[2] / PIXEL_NORMALIZE_FACTOR;  // R -> channel 0
                    }
                });
            }
            finally
            {
                image.UnlockBits(imageData);
            }
        }

        /// <summary>
        /// 高速模式：无插值图像转 Tensor
        /// </summary>
        private void ImageToTensor_NoInterpolation(Bitmap image, float[] buffer)
        {
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;

            BitmapData imageData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = imageData.Stride;

            float scaledImageWidth = _inferenceImageWidth;
            float scaledImageHeight = _inferenceImageHeight;
            if (scaledImageWidth > _tensorWidth || scaledImageHeight > _tensorHeight)
            {
                _scale = (_tensorWidth / scaledImageWidth) < (_tensorHeight / scaledImageHeight) ? (_tensorWidth / scaledImageWidth) : (_tensorHeight / scaledImageHeight);
                scaledImageWidth = scaledImageWidth * _scale;
                scaledImageHeight = scaledImageHeight * _scale;
            }

            float factor = 1 / _scale;
            unsafe
            {
                byte* scan0 = (byte*)imageData.Scan0.ToPointer();

                for (int y = 0; y < (int)scaledImageHeight; y++)
                {
                    for (int x = 0; x < (int)scaledImageWidth; x++)
                    {
                        int xPos = (int)(x * factor);
                        int yPos = (int)(y * factor);
                        byte* pixel = scan0 + (yPos * stride) + (xPos * 3);
                        // BGR -> RGB, channel-first layout
                        int baseIndex = y * tensorWidth + x;
                        buffer[2 * channelSize + baseIndex] = pixel[0] / PIXEL_NORMALIZE_FACTOR;  // B -> channel 2
                        buffer[1 * channelSize + baseIndex] = pixel[1] / PIXEL_NORMALIZE_FACTOR;  // G -> channel 1
                        buffer[0 * channelSize + baseIndex] = pixel[2] / PIXEL_NORMALIZE_FACTOR;  // R -> channel 0
                    }
                }
            }
            image.UnlockBits(imageData);
        }

        /// <summary>
        /// Mat 并行转 Tensor（跳过 Bitmap 中间层）。
        /// </summary>
        private unsafe void MatToTensor_Parallel(Mat image, float[] buffer)
        {
            int height = image.Rows;
            int width = image.Cols;
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;
            int channels = image.Channels();
            long step = image.Step();

            byte* scan0 = (byte*)image.DataPointer;

            Parallel.For(0, height, y =>
            {
                byte* rowStart = scan0 + (y * step);
                for (int x = 0; x < width; x++)
                {
                    byte* pixel = rowStart + (x * channels);
                    int baseIndex = y * tensorWidth + x;
                    if (channels >= 3)
                    {
                        buffer[2 * channelSize + baseIndex] = pixel[0] / PIXEL_NORMALIZE_FACTOR;
                        buffer[1 * channelSize + baseIndex] = pixel[1] / PIXEL_NORMALIZE_FACTOR;
                        buffer[0 * channelSize + baseIndex] = pixel[2] / PIXEL_NORMALIZE_FACTOR;
                    }
                    else
                    {
                        buffer[baseIndex] = pixel[0] / PIXEL_NORMALIZE_FACTOR;
                    }
                }
            });
        }

        /// <summary>
        /// Mat 高速无插值转 Tensor。
        /// </summary>
        private unsafe void MatToTensor_NoInterpolation(Mat image, float[] buffer)
        {
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;
            int channels = image.Channels();
            long step = image.Step();

            float scaledImageWidth = _inferenceImageWidth;
            float scaledImageHeight = _inferenceImageHeight;
            if (scaledImageWidth > _tensorWidth || scaledImageHeight > _tensorHeight)
            {
                _scale = (_tensorWidth / scaledImageWidth) < (_tensorHeight / scaledImageHeight)
                    ? (_tensorWidth / scaledImageWidth)
                    : (_tensorHeight / scaledImageHeight);
                scaledImageWidth *= _scale;
                scaledImageHeight *= _scale;
            }

            float factor = 1 / _scale;
            byte* scan0 = (byte*)image.DataPointer;

            for (int y = 0; y < (int)scaledImageHeight; y++)
            {
                for (int x = 0; x < (int)scaledImageWidth; x++)
                {
                    int xPos = (int)(x * factor);
                    int yPos = (int)(y * factor);
                    byte* pixel = scan0 + (yPos * step) + (xPos * channels);
                    int baseIndex = y * tensorWidth + x;
                    if (channels >= 3)
                    {
                        buffer[2 * channelSize + baseIndex] = pixel[0] / PIXEL_NORMALIZE_FACTOR;
                        buffer[1 * channelSize + baseIndex] = pixel[1] / PIXEL_NORMALIZE_FACTOR;
                        buffer[0 * channelSize + baseIndex] = pixel[2] / PIXEL_NORMALIZE_FACTOR;
                    }
                    else
                    {
                        buffer[baseIndex] = pixel[0] / PIXEL_NORMALIZE_FACTOR;
                    }
                }
            }
        }

        /// <summary>
        /// Bitmap 转字节数组
        /// </summary>
        public byte[] BitmapToBytes(Bitmap image)
        {
            byte[] result = null;
            using (System.IO.MemoryStream stream = new System.IO.MemoryStream())
            {
                image.Save(stream, ImageFormat.Bmp);
                result = stream.ToArray();
            }
            return result;
        }
    }
}


