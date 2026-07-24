// ============================================================================
// 文件名: ModelPackagePathGuard.cs
// 描述:   模型包路径安全边界
// ============================================================================

using System;
using System.IO;
using System.Linq;

namespace ClearFrost.Core.Models
{
    internal static class ModelPackagePathGuard
    {
        public static bool TryResolveModelPath(
            string packageDirectory,
            string modelFileName,
            out string modelPath,
            out string error,
            string subject = "Model file path")
        {
            modelPath = Path.Combine(packageDirectory, modelFileName ?? string.Empty);
            error = string.Empty;

            string trimmed = modelFileName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                error = $"{subject} is empty.";
                return false;
            }

            if (Path.IsPathRooted(trimmed))
            {
                error = $"{subject} must be relative to package directory.";
                return false;
            }

            string[] segments = trimmed.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
            if (segments.Any(segment =>
                string.Equals(segment, "..", StringComparison.Ordinal) ||
                segment.IndexOfAny(invalidFileNameChars) >= 0))
            {
                error = $"{subject} contains invalid path segments.";
                return false;
            }

            try
            {
                string packageRoot = Path.GetFullPath(packageDirectory);
                string fullModelPath = Path.GetFullPath(Path.Combine(packageRoot, trimmed));
                if (!fullModelPath.StartsWith(EnsureTrailingDirectorySeparator(packageRoot), StringComparison.OrdinalIgnoreCase))
                {
                    error = $"{subject} must stay within package directory.";
                    return false;
                }

                modelPath = fullModelPath;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                error = $"{subject} is invalid: {ex.Message}";
                return false;
            }
        }

        public static bool ModelPathHasReparsePoint(string packageDirectory, string modelPath)
        {
            try
            {
                string packageRoot = Path.GetFullPath(packageDirectory);
                string fullModelPath = Path.GetFullPath(modelPath);
                string relativePath = Path.GetRelativePath(packageRoot, fullModelPath);
                string[] segments = relativePath.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                if (segments.Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
                {
                    return true;
                }

                string current = packageRoot;
                foreach (string segment in segments.Take(Math.Max(segments.Length - 1, 0)))
                {
                    current = Path.Combine(current, segment);
                    if (Directory.Exists(current) && HasReparsePoint(new DirectoryInfo(current)))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        public static bool DirectoryPathHasReparsePoint(string directory)
        {
            try
            {
                var current = new DirectoryInfo(Path.GetFullPath(directory));
                while (current != null)
                {
                    current.Refresh();
                    if (current.Exists && HasReparsePoint(current))
                    {
                        return true;
                    }

                    current = current.Parent;
                }

                return false;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        public static string GetFullPathSafe(string value)
        {
            try
            {
                return Path.GetFullPath(value);
            }
            catch
            {
                return value;
            }
        }

        public static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            return Path.EndsInDirectorySeparator(path)
                ? path
                : path + Path.DirectorySeparatorChar;
        }
    }
}
