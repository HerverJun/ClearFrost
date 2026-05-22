using System;
using System.IO;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    internal sealed class DetectionTraceImageResolution
    {
        public string ImagePath { get; init; } = string.Empty;
        public string RenderedImagePath { get; init; } = string.Empty;
        public bool HasRenderedImage { get; init; }
        public bool UsedDerivedRenderedPath { get; init; }
        public bool MissingRenderedImage { get; init; }
        public string DisplayImagePath => HasRenderedImage ? RenderedImagePath : ImagePath;
    }

    internal static class DetectionTraceImageResolver
    {
        public static DetectionTraceImageResolution Resolve(
            DetectionTraceRecord record,
            Func<string, bool>? fileExists = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            fileExists ??= File.Exists;
            string imagePath = record.ImagePath ?? string.Empty;
            string renderedPath = record.RenderedImagePath ?? string.Empty;

            if (IsUsablePath(renderedPath, fileExists))
            {
                return new DetectionTraceImageResolution
                {
                    ImagePath = imagePath,
                    RenderedImagePath = renderedPath,
                    HasRenderedImage = true
                };
            }

            string derivedPath = TryBuildDerivedRenderedPath(imagePath);
            if (IsUsablePath(derivedPath, fileExists))
            {
                return new DetectionTraceImageResolution
                {
                    ImagePath = imagePath,
                    RenderedImagePath = derivedPath,
                    HasRenderedImage = true,
                    UsedDerivedRenderedPath = true
                };
            }

            return new DetectionTraceImageResolution
            {
                ImagePath = imagePath,
                RenderedImagePath = string.Empty,
                HasRenderedImage = false,
                MissingRenderedImage = !string.IsNullOrWhiteSpace(imagePath)
            };
        }

        private static bool IsUsablePath(string path, Func<string, bool> fileExists)
        {
            return !string.IsNullOrWhiteSpace(path) && fileExists(path);
        }

        private static string TryBuildDerivedRenderedPath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return string.Empty;
            }

            string? directory = Path.GetDirectoryName(imagePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            string name = Path.GetFileNameWithoutExtension(imagePath);
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            return Path.Combine(directory, "Rendered", $"{name}_rendered.jpg");
        }
    }
}
