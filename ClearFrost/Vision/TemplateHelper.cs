using OpenCvSharp;
using System;

namespace YOLO.Vision
{
    public static class TemplateHelper
    {
        public static string GenerateThumbnail(Mat template)
        {
            if (template == null || template.Empty()) return string.Empty;
            try
            {
                // Fix height to 40px for UI preview
                double scale = 40.0 / Math.Max(1, template.Height);
                using var resized = template.Resize(new OpenCvSharp.Size(0, 0), scale, scale);
                return Convert.ToBase64String(resized.ImEncode(".png"));
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
