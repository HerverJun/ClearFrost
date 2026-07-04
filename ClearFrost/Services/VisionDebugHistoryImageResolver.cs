// ============================================================================
// 文件名: VisionDebugHistoryImageResolver.cs
// 描述:   视觉调试历史样本图片路径解析
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    internal sealed class VisionDebugHistoryImageResolution
    {
        public bool Succeeded => !string.IsNullOrWhiteSpace(ImagePath);
        public string ImagePath { get; init; } = string.Empty;
        public string SourceKind { get; init; } = "Missing";
        public bool UsedRenderedImage { get; init; }
        public string Warning { get; init; } = string.Empty;
        public string FailureReason { get; init; } = string.Empty;
    }

    internal static class VisionDebugHistoryImageResolver
    {
        private const string RenderedWarning = "使用了渲染图，结果仅供参考";

        public static VisionDebugHistoryImageResolution Resolve(
            DetectionRecord record,
            Func<string?, string?> resolvePath)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (resolvePath == null) throw new ArgumentNullException(nameof(resolvePath));

            foreach (PathCandidate candidate in BuildCandidates(record))
            {
                string? resolved = resolvePath(candidate.Path);
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    continue;
                }

                return new VisionDebugHistoryImageResolution
                {
                    ImagePath = resolved,
                    SourceKind = candidate.SourceKind,
                    UsedRenderedImage = candidate.IsRendered,
                    Warning = candidate.IsRendered ? RenderedWarning : string.Empty
                };
            }

            return new VisionDebugHistoryImageResolution
            {
                FailureReason = BuildMissingReason(record)
            };
        }

        private static IEnumerable<PathCandidate> BuildCandidates(DetectionRecord record)
        {
            yield return new PathCandidate(record.ImagePath, "Original", false);

            if (!string.Equals(record.TraceImagePath, record.ImagePath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(record.TraceImagePath, record.RenderedImagePath, StringComparison.OrdinalIgnoreCase) &&
                !LooksLikeRenderedPath(record.TraceImagePath))
            {
                yield return new PathCandidate(record.TraceImagePath, "TraceOriginal", false);
            }

            foreach (string renderedPath in BuildRenderedCandidates(record))
            {
                yield return new PathCandidate(renderedPath, "Rendered", true);
            }
        }

        private static IEnumerable<string> BuildRenderedCandidates(DetectionRecord record)
        {
            var candidates = new[]
            {
                record.RenderedImagePath,
                LooksLikeRenderedPath(record.TraceImagePath) ? record.TraceImagePath : string.Empty,
                TryBuildDerivedRenderedPath(record.ImagePath),
                TryBuildDerivedRenderedPath(record.TraceImagePath)
            };

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string TryBuildDerivedRenderedPath(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return string.Empty;
            }

            string? directory = Path.GetDirectoryName(imagePath);
            string name = Path.GetFileNameWithoutExtension(imagePath);
            string extension = Path.GetExtension(imagePath);
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            extension = string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension;
            return Path.Combine(directory, "Rendered", $"{name}_rendered{extension}");
        }

        private static bool LooksLikeRenderedPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string fileName = Path.GetFileNameWithoutExtension(path);
            string? directory = Path.GetDirectoryName(path);
            return fileName.Contains("_rendered", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(directory) &&
                 directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => string.Equals(segment, "Rendered", StringComparison.OrdinalIgnoreCase)));
        }

        private static string BuildMissingReason(DetectionRecord record)
        {
            if (string.IsNullOrWhiteSpace(record.ImagePath) &&
                string.IsNullOrWhiteSpace(record.TraceImagePath) &&
                string.IsNullOrWhiteSpace(record.RenderedImagePath))
            {
                return "历史记录缺少原图/追溯图路径";
            }

            return "原图、追溯图和渲染图均不存在";
        }

        private readonly record struct PathCandidate(string? Path, string SourceKind, bool IsRendered);
    }
}
