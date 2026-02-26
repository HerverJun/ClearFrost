// ============================================================================
// 文件名: YoloRenderer.cs
// 描述:   YOLO 渲染模块 - 结果可视化绘制
// ============================================================================
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenCvSharp;

namespace ClearFrost.Yolo
{
    partial class YoloDetector
    {
        // ==================== 渲染方法 ====================

        /// <summary>
        /// Draw inference results on original image
        /// </summary>
        /// <param name="image">Original image</param>
        /// <param name="results">List of results</param>
        /// <param name="labels">Labels array</param>
        public Image GenerateImage(Image image, List<YoloResult> results, string[] labels, Pen? borderPen = null, Font? font = null, SolidBrush? textColorBrush = null, SolidBrush? textBackgroundBrush = null, bool randomMaskColor = true, Color[]? specifiedMaskColors = null, Color? nonMaskBackgroundColor = null, int classificationLimit = 5, float keyPointConfidenceThreshold = 0.5f)
        {
            Bitmap returnImage = new Bitmap(image.Width, image.Height);

            Pen? ownedBorderPen = null;
            Font? ownedFont = null;
            SolidBrush? ownedTextColorBrush = null;
            SolidBrush? ownedTextBackgroundBrush = null;

            try
            {
                if (borderPen == null)
                {
                    int penWidth = (image.Width > image.Height ? image.Height : image.Width) / 235;
                    ownedBorderPen = new Pen(Color.BlueViolet, penWidth);
                    borderPen = ownedBorderPen;
                }
                if (font == null)
                {
                    int fontWidth = (image.Width > image.Height ? image.Height : image.Width) / 90;
                    ownedFont = new Font("SimSun", fontWidth, FontStyle.Bold);
                    font = ownedFont;
                }
                if (textColorBrush == null)
                {
                    ownedTextColorBrush = new SolidBrush(Color.Black);
                    textColorBrush = ownedTextColorBrush;
                }
                if (textBackgroundBrush == null)
                {
                    ownedTextBackgroundBrush = new SolidBrush(Color.Orange);
                    textBackgroundBrush = ownedTextBackgroundBrush;
                }

                using (Graphics g = Graphics.FromImage(returnImage))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.DrawImage(image, 0, 0, image.Width, image.Height);

                    // Classify
                    if (_executionTaskMode == YoloTaskType.Classify)
                    {
                        DrawClassificationResults(g, results, labels, font, textColorBrush, textBackgroundBrush, classificationLimit);
                    }

                    // Draw Mask
                    if (_executionTaskMode == YoloTaskType.SegmentDetectOnly || _executionTaskMode == YoloTaskType.SegmentWithMask)
                    {
                        DrawSegmentationMasks(g, image, results, randomMaskColor, specifiedMaskColors, nonMaskBackgroundColor);
                    }

                    if (_executionTaskMode == YoloTaskType.Detect || _executionTaskMode == YoloTaskType.SegmentWithMask || _executionTaskMode == YoloTaskType.PoseWithKeypoints)
                    {
                        DrawDetectionBoxes(g, results, labels, borderPen, font, textColorBrush, textBackgroundBrush);
                    }

                    if (_executionTaskMode == YoloTaskType.PoseDetectOnly || _executionTaskMode == YoloTaskType.PoseWithKeypoints)
                    {
                        DrawPoseKeypoints(g, image, results, keyPointConfidenceThreshold);
                    }

                    if (_executionTaskMode == YoloTaskType.Obb)
                    {
                        DrawObbResults(g, results, labels, borderPen, font, textColorBrush, textBackgroundBrush);
                    }
                }
            }
            finally
            {
                ownedBorderPen?.Dispose();
                ownedFont?.Dispose();
                ownedTextColorBrush?.Dispose();
                ownedTextBackgroundBrush?.Dispose();
            }

            return returnImage;
        }

        private void DrawClassificationResults(Graphics g, List<YoloResult> results, string[] labels, Font font, SolidBrush textColorBrush, SolidBrush textBackgroundBrush, int classificationLimit)
        {
            RestoreDrawingCoordinates(ref results);
            float xPos = 10;
            float yPos = 10;
            for (int i = 0; i < results.Count; i++)
            {
                if (i >= classificationLimit) break;
                int labelIndex = (int)results[i].BasicData[1];
                string confidence = results[i].BasicData[0].ToString("_0.00");
                string labelName;
                if (labelIndex + 1 > labels.Length)
                {
                    labelName = "No Label Name";
                }
                else
                {
                    labelName = labels[labelIndex];
                }
                string textContent = labelName + confidence;
                float textWidth = g.MeasureString(textContent + "_0.00", font).Width;
                float textHeight = g.MeasureString(textContent + "_0.00", font).Height;
                g.FillRectangle(textBackgroundBrush, xPos, yPos, textWidth * 0.8f, textHeight);
                g.DrawString(textContent, font, textColorBrush, new PointF(xPos, yPos));
                yPos += textHeight;
            }
            RestoreCenterCoordinates(ref results);
        }

        private void DrawSegmentationMasks(Graphics g, Image image, List<YoloResult> results, bool randomMaskColor, Color[]? specifiedMaskColors, Color? nonMaskBackgroundColor)
        {
            RestoreDrawingCoordinates(ref results);
            if (nonMaskBackgroundColor != null)
            {
                using (Bitmap bgImage = new Bitmap(image.Width, image.Height))
                {
                    using (Graphics bgGraphics = Graphics.FromImage(bgImage))
                    {
                        bgGraphics.Clear((Color)nonMaskBackgroundColor);
                    }
                    g.DrawImage(bgImage, PointF.Empty);
                }
            }
            for (int i = 0; i < results.Count; i++)
            {
                Rectangle rect = new Rectangle((int)results[i].BasicData[0], (int)results[i].BasicData[1], (int)results[i].BasicData[2], (int)results[i].BasicData[3]);
                Color color;
                if (specifiedMaskColors == null)
                {
                    if (randomMaskColor)
                    {
                        Random R = new Random();
                        color = Color.FromArgb(180, R.Next(0, 255), R.Next(0, 255), R.Next(0, 255));
                    }
                    else
                    {
                        color = Color.FromArgb(180, 0, 255, 0);
                    }
                }
                else
                {
                    if (results[i].ClassId + 1 > specifiedMaskColors.Length)
                    {
                        color = Color.FromArgb(180, 0, 255, 0);
                    }
                    else
                    {
                        color = specifiedMaskColors[results[i].ClassId];
                    }
                }
                var maskData = results[i].MaskData;
                if (maskData != null && !maskData.Empty())
                {
                    using (Bitmap mask = GenerateMaskImageParallel(maskData, color))
                    {
                        g.DrawImage(mask, rect);
                    }
                }
            }
            RestoreCenterCoordinates(ref results);
        }

        private void DrawDetectionBoxes(Graphics g, List<YoloResult> results, string[] labels, Pen borderPen, Font font, SolidBrush textColorBrush, SolidBrush textBackgroundBrush)
        {
            RestoreDrawingCoordinates(ref results);

            if (IndustrialRenderMode)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.Default;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Default;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SystemDefault;
            }
            else
            {
                // === 极致美观模式 (Ultra Aesthetic Mode) ===
                // 开启最高质量渲染设置，不计性能成本
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            }

            for (int i = 0; i < results.Count; i++)
            {
                var data = results[i].BasicData;
                RectangleF rect = new RectangleF(data[0], data[1], data[2], data[3]);

                int classId = (int)data[5];
                float score = data[4];
                string labelName = (classId < labels.Length) ? labels[classId] : "Unknown";
                string confText = score.ToString("P1"); // 99.5%
                string displayText = $"{labelName}  {confText}";

                Color themeColor = GetColorForClass(classId); // 获取主题色

                if (IndustrialRenderMode)
                {
                    DrawIndustrialDetectionBox(g, rect, displayText, themeColor, font);
                    continue;
                }

                Color whiteColor = Color.FromArgb(240, 255, 255, 255);

                // 1. 【动态辉光】(Outer Glow)
                // 绘制多层高斯模糊模拟的辉光，营造"霓虹灯"或"能量场"效果
                int glowLayers = 6;
                for (int j = 1; j <= glowLayers; j++)
                {
                    int alpha = 50 - (j * 7); // 渐变透明度
                    if (alpha < 0) alpha = 0;
                    float width = 1f + (j * 3f); // 逐渐变宽

                    using (Pen glowPen = new Pen(Color.FromArgb(alpha, themeColor), width))
                    {
                        glowPen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round; // 圆角连接
                        g.DrawRectangle(glowPen, rect.X, rect.Y, rect.Width, rect.Height);
                    }
                }

                // 2. 【核心边框】(Core Border)
                // 使用微弱的渐变色，模拟金属反光质感
                using (var gradientBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    rect, themeColor, Color.FromArgb(255, 255, 255), 45f))
                using (Pen mainPen = new Pen(gradientBrush, 2.0f))
                {
                    g.DrawRectangle(mainPen, rect.X, rect.Y, rect.Width, rect.Height);
                }

                // 3. 【四角强化】(Corner Accents)
                // 绘制高亮白色的L型四角，增加科技感和视觉定界
                float cornerLen = Math.Min(rect.Width, rect.Height) / 5.0f;
                if (cornerLen > 25) cornerLen = 25; // 限制最大长度
                if (cornerLen < 5) cornerLen = 5;

                using (Pen cornerPen = new Pen(whiteColor, 3.0f))
                {
                    // 左上
                    g.DrawLine(cornerPen, rect.X, rect.Y + cornerLen, rect.X, rect.Y);
                    g.DrawLine(cornerPen, rect.X, rect.Y, rect.X + cornerLen, rect.Y);
                    // 右上
                    g.DrawLine(cornerPen, rect.Right - cornerLen, rect.Y, rect.Right, rect.Y);
                    g.DrawLine(cornerPen, rect.Right, rect.Y, rect.Right, rect.Y + cornerLen);
                    // 右下
                    g.DrawLine(cornerPen, rect.Right, rect.Bottom - cornerLen, rect.Right, rect.Bottom);
                    g.DrawLine(cornerPen, rect.Right, rect.Bottom, rect.Right - cornerLen, rect.Bottom);
                    // 左下
                    g.DrawLine(cornerPen, rect.X + cornerLen, rect.Bottom, rect.X, rect.Bottom);
                    g.DrawLine(cornerPen, rect.X, rect.Bottom, rect.X, rect.Bottom - cornerLen);
                }

                // 4. 【HUD 标签】(Head-Up Display Label)
                SizeF size = g.MeasureString(displayText, font);
                float labelH = size.Height + 8;
                float labelW = size.Width + 24; // 稍微宽一点，容纳左侧色条
                float labelX = rect.X;
                float labelY = rect.Y - labelH - 6;

                // 边界检查：如果上方放不下，就放到下面，或者框内
                if (labelY < 0) labelY = rect.Y + 6;

                RectangleF labelRect = new RectangleF(labelX, labelY, labelW, labelH);

                // 标签背景：深色玻璃拟态 (Dark Glassmorphism)
                using (var labelBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                    labelRect,
                    Color.FromArgb(230, 10, 10, 16),   // 几乎全黑
                    Color.FromArgb(180, 40, 40, 45),   // 深灰半透
                    0f)) // 水平渐变
                {
                    // 绘制圆角矩形背景 (手动绘制圆角路径)
                    float r = 4; // 圆角半径
                    using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                    {
                        path.AddLine(labelRect.X + r, labelRect.Y, labelRect.Right - r, labelRect.Y);
                        path.AddArc(labelRect.Right - r, labelRect.Y, r, r, 270, 90);
                        path.AddLine(labelRect.Right, labelRect.Y + r, labelRect.Right, labelRect.Bottom - r);
                        path.AddArc(labelRect.Right - r, labelRect.Bottom - r, r, r, 0, 90);
                        path.AddLine(labelRect.Right - r, labelRect.Bottom, labelRect.X + r, labelRect.Bottom);
                        path.AddArc(labelRect.X, labelRect.Bottom - r, r, r, 90, 90);
                        path.AddLine(labelRect.X, labelRect.Bottom - r, labelRect.X, labelRect.Y + r);
                        path.AddArc(labelRect.X, labelRect.Y, r, r, 180, 90);
                        path.CloseFigure();

                        g.FillPath(labelBrush, path);

                        // 标签微弱边框
                        using (Pen labelBorderPen = new Pen(Color.FromArgb(80, 255, 255, 255), 1))
                        {
                            g.DrawPath(labelBorderPen, path);
                        }
                    }
                }

                // 标签左侧：能量条 (Energy Bar)
                using (SolidBrush accentBrush = new SolidBrush(themeColor))
                {
                    g.FillRectangle(accentBrush, labelX + 4, labelY + 4, 3, labelH - 8);
                }

                // 标签文字：带发光/阴影
                using (SolidBrush shadowBrush = new SolidBrush(Color.Black))
                using (SolidBrush textBrush = new SolidBrush(Color.White))
                {
                    // 阴影偏移
                    g.DrawString(displayText, font, shadowBrush, labelX + 11, labelY + 5);
                    // 正文
                    g.DrawString(displayText, font, textBrush, labelX + 10, labelY + 4);
                }
            }
            RestoreCenterCoordinates(ref results);
        }

        private void DrawIndustrialDetectionBox(Graphics g, RectangleF rect, string displayText, Color themeColor, Font font)
        {
            using (Pen boxPen = new Pen(themeColor, 2f))
            {
                g.DrawRectangle(boxPen, rect.X, rect.Y, rect.Width, rect.Height);
            }

            SizeF textSize = g.MeasureString(displayText, font);
            float labelX = rect.X;
            float labelY = rect.Y - textSize.Height - 4;
            if (labelY < 0)
            {
                labelY = rect.Y + 2;
            }

            RectangleF labelRect = new RectangleF(labelX, labelY, textSize.Width + 8, textSize.Height + 4);
            using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(220, 0, 0, 0)))
            using (SolidBrush fgBrush = new SolidBrush(Color.White))
            {
                g.FillRectangle(bgBrush, labelRect);
                g.DrawString(displayText, font, fgBrush, labelX + 4, labelY + 2);
            }
        }

        /// <summary>
        /// 基于类别ID生成高对比度且协调的工业风配色
        /// </summary>
        private Color GetColorForClass(int classId)
        {
            // 预定义的工业风色板（高饱和度、高对比度）
            Color[] palette = new Color[]
            {
                Color.FromArgb(0, 255, 127),   // 春绿 (合格/正常)
                Color.FromArgb(255, 69, 0),    // 橙红 (不合格/警告)
                Color.FromArgb(30, 144, 255),  // 闪耀蓝 (特征/定位)
                Color.FromArgb(255, 215, 0),   // 金色
                Color.FromArgb(255, 0, 255),   // 品红
                Color.FromArgb(0, 255, 255),   // 青色
                Color.FromArgb(226, 43, 43),   // 红色
                Color.FromArgb(138, 43, 226)   // 蓝紫色
            };
            return palette[classId % palette.Length];
        }

        private void DrawPoseKeypoints(Graphics g, Image image, List<YoloResult> results, float keyPointConfidenceThreshold)
        {
            RestoreDrawingCoordinates(ref results);
            if (results.Count > 0 && results[0].KeyPoints.Length == 17)
            {
                Color[] colorGroup = new Color[]
                {
                    Color.Yellow, Color.LawnGreen, Color.LawnGreen,
                    Color.SpringGreen, Color.SpringGreen, Color.Blue,
                    Color.Blue, Color.Firebrick, Color.Firebrick,
                    Color.Firebrick, Color.Firebrick, Color.Blue,
                    Color.Blue, Color.Orange, Color.Orange,
                    Color.Orange, Color.Orange
                };
                int dotRadius = (image.Width > image.Height ? image.Height : image.Width) / 100;
                int lineWidth = (image.Width > image.Height ? image.Height : image.Width) / 150;

                for (int i = 0; i < results.Count; i++)
                {
                    using (Pen lineStyle0 = new Pen(colorGroup[0], lineWidth))
                    using (Pen lineStyle5 = new Pen(colorGroup[5], lineWidth))
                    using (Pen lineStyle1 = new Pen(colorGroup[1], lineWidth))
                    {
                        PointF shoulderCenter = new PointF((results[i].KeyPoints[5].X + results[i].KeyPoints[6].X) / 2 + dotRadius, (results[i].KeyPoints[5].Y + results[i].KeyPoints[6].Y) / 2 + dotRadius);
                        if (results[i].KeyPoints[0].Score > keyPointConfidenceThreshold && results[i].KeyPoints[5].Score > keyPointConfidenceThreshold && results[i].KeyPoints[6].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle0, new PointF(results[i].KeyPoints[0].X + dotRadius, results[i].KeyPoints[0].Y + dotRadius), shoulderCenter);
                        if (results[i].KeyPoints[5].Score > keyPointConfidenceThreshold && results[i].KeyPoints[6].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[5].X + dotRadius, results[i].KeyPoints[5].Y + dotRadius), new PointF(results[i].KeyPoints[6].X + dotRadius, results[i].KeyPoints[6].Y + dotRadius));
                        if (results[i].KeyPoints[11].Score > keyPointConfidenceThreshold && results[i].KeyPoints[12].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[11].X + dotRadius, results[i].KeyPoints[11].Y + dotRadius), new PointF(results[i].KeyPoints[12].X + dotRadius, results[i].KeyPoints[12].Y + dotRadius));
                        if (results[i].KeyPoints[5].Score > keyPointConfidenceThreshold && results[i].KeyPoints[11].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[5].X + dotRadius, results[i].KeyPoints[5].Y + dotRadius), new PointF(results[i].KeyPoints[11].X + dotRadius, results[i].KeyPoints[11].Y + dotRadius));
                        if (results[i].KeyPoints[6].Score > keyPointConfidenceThreshold && results[i].KeyPoints[12].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle5, new PointF(results[i].KeyPoints[6].X + dotRadius, results[i].KeyPoints[6].Y + dotRadius), new PointF(results[i].KeyPoints[12].X + dotRadius, results[i].KeyPoints[12].Y + dotRadius));
                        if (results[i].KeyPoints[0].Score > keyPointConfidenceThreshold && results[i].KeyPoints[1].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle0, new PointF(results[i].KeyPoints[0].X + dotRadius, results[i].KeyPoints[0].Y + dotRadius), new PointF(results[i].KeyPoints[1].X + dotRadius, results[i].KeyPoints[1].Y + dotRadius));
                        if (results[i].KeyPoints[0].Score > keyPointConfidenceThreshold && results[i].KeyPoints[2].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle0, new PointF(results[i].KeyPoints[0].X + dotRadius, results[i].KeyPoints[0].Y + dotRadius), new PointF(results[i].KeyPoints[2].X + dotRadius, results[i].KeyPoints[2].Y + dotRadius));
                        if (results[i].KeyPoints[1].Score > keyPointConfidenceThreshold && results[i].KeyPoints[3].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle1, new PointF(results[i].KeyPoints[1].X + dotRadius, results[i].KeyPoints[1].Y + dotRadius), new PointF(results[i].KeyPoints[3].X + dotRadius, results[i].KeyPoints[3].Y + dotRadius));
                        if (results[i].KeyPoints[2].Score > keyPointConfidenceThreshold && results[i].KeyPoints[4].Score > keyPointConfidenceThreshold) g.DrawLine(lineStyle1, new PointF(results[i].KeyPoints[2].X + dotRadius, results[i].KeyPoints[2].Y + dotRadius), new PointF(results[i].KeyPoints[4].X + dotRadius, results[i].KeyPoints[4].Y + dotRadius));

                        for (int j = 5; j < results[i].KeyPoints.Length - 2; j++)
                        {
                            if (results[i].KeyPoints[j].Score > keyPointConfidenceThreshold && results[i].KeyPoints[j + 2].Score > keyPointConfidenceThreshold)
                            {
                                if (j != 9 && j != 10)
                                {
                                    using (Pen lineStyleJ = new Pen(colorGroup[j + 2], lineWidth))
                                    {
                                        g.DrawLine(lineStyleJ, new PointF(results[i].KeyPoints[j].X + dotRadius, results[i].KeyPoints[j].Y + dotRadius), new PointF(results[i].KeyPoints[j + 2].X + dotRadius, results[i].KeyPoints[j + 2].Y + dotRadius));
                                    }
                                }
                            }
                        }
                        for (int j = 0; j < results[i].KeyPoints.Length; j++)
                        {
                            if (results[i].KeyPoints[j].Score > keyPointConfidenceThreshold)
                            {
                                Rectangle position = new Rectangle((int)results[i].KeyPoints[j].X, (int)results[i].KeyPoints[j].Y, dotRadius * 2, dotRadius * 2);
                                using (SolidBrush dotBrush = new SolidBrush(colorGroup[j]))
                                {
                                    g.FillEllipse(dotBrush, position);
                                }
                            }
                        }
                    }
                }
            }
            else if (results.Count > 0)
            {
                Color[] colorGroup = new Color[]
                {
                    Color.Yellow, Color.Red, Color.SpringGreen,
                    Color.Blue, Color.Firebrick, Color.Blue,
                    Color.Orange, Color.Beige, Color.LightGreen,
                    Color.DarkGreen, Color.Magenta, Color.White,
                    Color.OrangeRed, Color.Orchid, Color.PaleGoldenrod,
                    Color.PaleGreen, Color.PaleTurquoise, Color.PaleVioletRed,
                    Color.PaleGreen, Color.PaleTurquoise
                };
                int dotRadius = (image.Width > image.Height ? image.Height : image.Width) / 100;
                foreach (var item in results)
                {
                    for (int i = 0; i < item.KeyPoints.Length; i++)
                    {
                        if (item.KeyPoints[i].Score > keyPointConfidenceThreshold)
                        {
                            Rectangle position = new Rectangle((int)item.KeyPoints[i].X, (int)item.KeyPoints[i].Y, dotRadius * 2, dotRadius * 2);
                            using (SolidBrush dotBrush = new SolidBrush(i > 20 ? Color.SaddleBrown : colorGroup[i]))
                            {
                                g.FillEllipse(dotBrush, position);
                            }
                        }
                    }
                }
            }
            RestoreCenterCoordinates(ref results);
        }

        private void DrawObbResults(Graphics g, List<YoloResult> results, string[] labels, Pen borderPen, Font font, SolidBrush textColorBrush, SolidBrush textBackgroundBrush)
        {
            for (int i = 0; i < results.Count; i++)
            {
                string confidence = results[i].BasicData[4].ToString("_0.00");
                string textContent;
                if ((int)results[i].BasicData[5] + 1 > labels.Length)
                {
                    textContent = confidence;
                }
                else
                {
                    textContent = labels[(int)results[i].BasicData[5]] + confidence;
                }
                float textWidth = g.MeasureString(textContent + "_0.00", font).Width;
                float textHeight = g.MeasureString(textContent + "_0.00", font).Height;
                ObbRectangle obb = ConvertObbCoordinates(results[i]);
                PointF[] pf = { obb.pt1, obb.pt2, obb.pt3, obb.pt4, obb.pt1 };
                g.DrawLines(borderPen, pf);
                PointF bottomRight = pf[0];
                foreach (var point in pf)
                {
                    if (point.X >= bottomRight.X && point.Y >= bottomRight.Y)
                    {
                        bottomRight = point;
                    }
                }
                g.FillRectangle(textBackgroundBrush, bottomRight.X - borderPen.Width / 2 - 1, bottomRight.Y + borderPen.Width / 2 - 1, textWidth * 0.8f, textHeight);
                g.DrawString(textContent, font, textColorBrush, bottomRight.X - borderPen.Width / 2 - 1, bottomRight.Y + borderPen.Width / 2 - 1);
            }
        }

        private unsafe Bitmap GenerateMaskImageParallel(Mat matData, Color color)
        {
            Bitmap maskImage = new Bitmap(matData.Width, matData.Height, PixelFormat.Format32bppArgb);
            BitmapData maskImageData = maskImage.LockBits(new Rectangle(0, 0, maskImage.Width, maskImage.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            int height = maskImage.Height;
            int width = maskImage.Width;

            try
            {
                byte* scan0 = (byte*)maskImageData.Scan0.ToPointer();
                int stride = maskImageData.Stride;

                Parallel.For(0, height, i =>
                {
                    Span<byte> colorInfo = stackalloc byte[4];
                    colorInfo[0] = color.B;
                    colorInfo[1] = color.G;
                    colorInfo[2] = color.R;
                    colorInfo[3] = color.A;

                    byte* rowStart = scan0 + (i * stride);
                    for (int j = 0; j < width; j++)
                    {
                        if (matData.At<float>(i, j) == 1)
                        {
                            byte* pixel = rowStart + (j * 4);
                            pixel[0] = colorInfo[0];
                            pixel[1] = colorInfo[1];
                            pixel[2] = colorInfo[2];
                            pixel[3] = colorInfo[3];
                        }
                    }
                });
            }
            finally
            {
                maskImage.UnlockBits(maskImageData);
            }

            return maskImage;
        }

        /// <returns>Returns ObbRectangle structure, representing logic of four points</returns>
        public ObbRectangle ConvertObbCoordinates(YoloResult data)
        {
            float x = data.BasicData[0];
            float y = data.BasicData[1];
            float w = data.BasicData[2];
            float h = data.BasicData[3];
            float r = data.BasicData[6];
            float cos_value = (float)Math.Cos(r);
            float sin_value = (float)Math.Sin(r);
            float[] vec1 = { w / 2 * cos_value, w / 2 * sin_value };
            float[] vec2 = { -h / 2 * sin_value, h / 2 * cos_value };
            ObbRectangle obbRectangle = new ObbRectangle();
            obbRectangle.pt1 = new PointF(x + vec1[0] + vec2[0], y + vec1[1] + vec2[1]);
            obbRectangle.pt2 = new PointF(x + vec1[0] - vec2[0], y + vec1[1] - vec2[1]);
            obbRectangle.pt3 = new PointF(x - vec1[0] - vec2[0], y - vec1[1] - vec2[1]);
            obbRectangle.pt4 = new PointF(x - vec1[0] + vec2[0], y - vec1[1] + vec2[1]);
            return obbRectangle;
        }
    }
}


