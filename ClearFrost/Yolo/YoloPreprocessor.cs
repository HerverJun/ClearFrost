// ============================================================================
// 文件名: YoloPreprocessor.cs
// 描述:   YOLO 预处理模块 - Letterbox 缩放、图像转 Tensor
// ============================================================================
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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

            int newW = (int)(_inferenceImageWidth * _scale);
            int newH = (int)(_inferenceImageHeight * _scale);

            // Calculate padding to center the image
            _padLeft = (_tensorWidth - newW) / 2;
            _padTop = (_tensorHeight - newH) / 2;

            // Create letterboxed image with YOLO standard gray background
            Bitmap letterboxedImage = new Bitmap(_tensorWidth, _tensorHeight);
            using (Graphics graphics = Graphics.FromImage(letterboxedImage))
            {
                // Fill with YOLO standard gray (114, 114, 114)
                graphics.Clear(Color.FromArgb(LETTERBOX_FILL_COLOR_R, LETTERBOX_FILL_COLOR_G, LETTERBOX_FILL_COLOR_B));
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                graphics.DrawImage(image, _padLeft, _padTop, newW, newH);
            }
            return letterboxedImage;
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
            }
        }

        /// <summary>
        /// 高精度模式：并行图像转 Tensor
        /// </summary>
        private void ImageToTensor_Parallel(Bitmap image, float[] buffer)
        {
            int height = image.Height;
            int width = image.Width;
            int channels = _inputTensorInfo[1];
            int tensorHeight = _inputTensorInfo[2];
            int tensorWidth = _inputTensorInfo[3];
            int channelSize = tensorHeight * tensorWidth;

            BitmapData imageData = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            int stride = imageData.Stride;
            IntPtr scan0 = imageData.Scan0;
            try
            {
                Parallel.For(0, height, y =>
                {
                    for (int x = 0; x < width; x++)
                    {
                        IntPtr pixel = IntPtr.Add(scan0, y * stride + x * 3);
                        // BGR -> RGB, channel-first layout
                        int baseIndex = y * tensorWidth + x;
                        buffer[2 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // B -> channel 2
                        pixel = IntPtr.Add(pixel, 1);
                        buffer[1 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // G -> channel 1
                        pixel = IntPtr.Add(pixel, 1);
                        buffer[0 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // R -> channel 0
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
            IntPtr scan0 = imageData.Scan0;

            float scaledImageWidth = _inferenceImageWidth;
            float scaledImageHeight = _inferenceImageHeight;
            if (scaledImageWidth > _tensorWidth || scaledImageHeight > _tensorHeight)
            {
                _scale = (_tensorWidth / scaledImageWidth) < (_tensorHeight / scaledImageHeight) ? (_tensorWidth / scaledImageWidth) : (_tensorHeight / scaledImageHeight);
                scaledImageWidth = scaledImageWidth * _scale;
                scaledImageHeight = scaledImageHeight * _scale;
            }

            float factor = 1 / _scale;
            for (int y = 0; y < (int)scaledImageHeight; y++)
            {
                for (int x = 0; x < (int)scaledImageWidth; x++)
                {
                    int xPos = (int)(x * factor);
                    int yPos = (int)(y * factor);
                    IntPtr pixel = IntPtr.Add(scan0, yPos * stride + xPos * 3);
                    // BGR -> RGB, channel-first layout
                    int baseIndex = y * tensorWidth + x;
                    buffer[2 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // B -> channel 2
                    pixel = IntPtr.Add(pixel, 1);
                    buffer[1 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // G -> channel 1
                    pixel = IntPtr.Add(pixel, 1);
                    buffer[0 * channelSize + baseIndex] = Marshal.ReadByte(pixel) / PIXEL_NORMALIZE_FACTOR;  // R -> channel 0
                }
            }
            image.UnlockBits(imageData);
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


