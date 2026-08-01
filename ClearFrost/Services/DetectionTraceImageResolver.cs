using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    internal sealed class DetectionTraceImageResolution
    {
        public string ImagePath { get; init; } = string.Empty;
        public string RenderedImagePath { get; init; } = string.Empty;
        public bool HasRenderedImage { get; init; }
        public bool UsedDerivedRenderedPath { get; init; }
        public bool UsedFallbackImagePath { get; init; }
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

            fileExists ??= SafeFileExists;
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

        public static DetectionTraceImageResolution Resolve(
            DetectionTraceRecord record,
            string imageBasePath,
            Func<string, bool>? fileExists = null,
            Func<string, IEnumerable<string>>? enumerateFiles = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            fileExists ??= SafeFileExists;
            DetectionTraceImageResolution resolved = Resolve(record, fileExists);
            if (IsUsablePath(resolved.ImagePath, fileExists))
            {
                return resolved;
            }

            string fallbackImagePath = TryResolveFallbackImagePath(
                record,
                imageBasePath,
                enumerateFiles ?? EnumerateTopLevelImageFiles);
            if (string.IsNullOrWhiteSpace(fallbackImagePath))
            {
                return resolved;
            }

            string renderedPath = resolved.HasRenderedImage
                ? resolved.RenderedImagePath
                : TryBuildDerivedRenderedPath(fallbackImagePath);
            bool hasRenderedImage = IsUsablePath(renderedPath, fileExists);

            return new DetectionTraceImageResolution
            {
                ImagePath = fallbackImagePath,
                RenderedImagePath = hasRenderedImage ? renderedPath : string.Empty,
                HasRenderedImage = hasRenderedImage,
                UsedDerivedRenderedPath = hasRenderedImage && !resolved.HasRenderedImage,
                UsedFallbackImagePath = true,
                MissingRenderedImage = !hasRenderedImage
            };
        }

        private static bool IsUsablePath(string path, Func<string, bool> fileExists)
        {
            return !string.IsNullOrWhiteSpace(path) && fileExists(path);
        }

        private static bool SafeFileExists(string path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path) &&
                       File.Exists(path) &&
                       IsSafeFilePath(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsSafeFilePath(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(directory) || DirectoryPathHasReparsePoint(directory))
                {
                    return false;
                }

                var file = new FileInfo(fullPath);
                file.Refresh();
                return file.Exists && !HasReparsePoint(file);
            }
            catch
            {
                return false;
            }
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

        private static string TryResolveFallbackImagePath(
            DetectionTraceRecord record,
            string imageBasePath,
            Func<string, IEnumerable<string>> enumerateFiles)
        {
            if (string.IsNullOrWhiteSpace(imageBasePath))
            {
                return string.Empty;
            }

            IReadOnlyList<string> directories = BuildFallbackDirectories(record, imageBasePath);
            if (directories.Count == 0)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(record.InspectionId))
            {
                foreach (string directory in directories)
                {
                    string matchedById = FindImageByInspectionId(record, enumerateFiles(directory));
                    if (!string.IsNullOrWhiteSpace(matchedById))
                    {
                        return matchedById;
                    }
                }
            }

            foreach (string directory in directories)
            {
                string matchedByTime = FindImageByTimestamp(record, enumerateFiles(directory));
                if (!string.IsNullOrWhiteSpace(matchedByTime))
                {
                    return matchedByTime;
                }
            }

            return string.Empty;
        }

        private static IReadOnlyList<string> BuildFallbackDirectories(DetectionTraceRecord record, string imageBasePath)
        {
            string basePath;
            try
            {
                basePath = Path.GetFullPath(imageBasePath);
            }
            catch
            {
                return Array.Empty<string>();
            }

            string currentRoot = record.IsQualified ? "Qualified" : "Unqualified";
            string legacyRoot = record.IsQualified ? "OK" : "NG";
            string zhDate = record.Timestamp.ToString("yyyy年MM月dd日", CultureInfo.InvariantCulture);
            string isoDate = record.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string hour = record.Timestamp.ToString("HH", CultureInfo.InvariantCulture);

            var directories = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in new[] { currentRoot, legacyRoot })
            {
                foreach (string date in new[] { zhDate, isoDate })
                {
                    AddDirectoryCandidate(directories, seen, Path.Combine(basePath, root, date, hour));
                    AddDirectoryCandidate(directories, seen, Path.Combine(basePath, root, date));
                }
            }

            return directories;
        }

        private static void AddDirectoryCandidate(List<string> directories, HashSet<string> seen, string path)
        {
            if (seen.Add(path))
            {
                directories.Add(path);
            }
        }

        private static IEnumerable<string> EnumerateTopLevelImageFiles(string directory)
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    return Array.Empty<string>();
                }

                if (DirectoryPathHasReparsePoint(directory))
                {
                    return Array.Empty<string>();
                }

                return Directory.EnumerateFiles(directory)
                    .Where(path => IsImageFile(path) && IsSafeFilePath(path))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
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
            catch
            {
                return true;
            }
        }

        private static bool HasReparsePoint(FileSystemInfo info)
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

        private static string FindImageByInspectionId(DetectionTraceRecord record, IEnumerable<string> files)
        {
            string inspectionId = record.InspectionId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(inspectionId))
            {
                return string.Empty;
            }

            string safeInspectionId = SanitizeFileNamePart(inspectionId);
            IEnumerable<string> matches = files.Where(path =>
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                return fileName.Contains(inspectionId, StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(safeInspectionId, inspectionId, StringComparison.Ordinal) &&
                     fileName.Contains(safeInspectionId, StringComparison.OrdinalIgnoreCase));
            });

            return PickBestFile(record, matches, requireTimestampWindow: false);
        }

        private static string FindImageByTimestamp(DetectionTraceRecord record, IEnumerable<string> files)
        {
            return PickBestFile(record, files, requireTimestampWindow: true);
        }

        private static string PickBestFile(
            DetectionTraceRecord record,
            IEnumerable<string> files,
            bool requireTimestampWindow)
        {
            const double timestampToleranceMs = 3000;

            return files
                .Where(IsImageFile)
                .Select(path => new
                {
                    Path = path,
                    ResultScore = MatchesExpectedResultPrefix(record, path) ? 0 : 1,
                    DeltaMs = GetTimestampDeltaMs(path, record.Timestamp)
                })
                .Where(item => !requireTimestampWindow || item.DeltaMs <= timestampToleranceMs)
                .OrderBy(item => item.ResultScore)
                .ThenBy(item => item.DeltaMs)
                .ThenBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Path)
                .FirstOrDefault() ?? string.Empty;
        }

        private static bool MatchesExpectedResultPrefix(DetectionTraceRecord record, string path)
        {
            string fileName = Path.GetFileName(path);
            string expectedPrefix = record.IsQualified ? "PASS" : "FAIL";
            return fileName.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static double GetTimestampDeltaMs(string path, DateTime target)
        {
            if (TryParseTimestampFromFileName(path, target.Date, out DateTime timestamp))
            {
                return Math.Abs((timestamp - target).TotalMilliseconds);
            }

            try
            {
                return Math.Abs((File.GetLastWriteTime(path) - target).TotalMilliseconds);
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static bool TryParseTimestampFromFileName(string path, DateTime date, out DateTime timestamp)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int cfIndex = name.IndexOf("CF-", StringComparison.OrdinalIgnoreCase);
            if (cfIndex >= 0 && cfIndex + 21 <= name.Length)
            {
                string token = name.Substring(cfIndex + 3, 18);
                if (DateTime.TryParseExact(
                    token,
                    "yyyyMMdd-HHmmssfff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out timestamp))
                {
                    return true;
                }
            }

            if (name.Length >= 10 &&
                char.IsDigit(name[0]) && char.IsDigit(name[1]) &&
                char.IsDigit(name[2]) && char.IsDigit(name[3]) &&
                char.IsDigit(name[4]) && char.IsDigit(name[5]) &&
                name[6] == '_' &&
                char.IsDigit(name[7]) && char.IsDigit(name[8]) && char.IsDigit(name[9]))
            {
                string legacyToken = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "_" + name.Substring(0, 10);
                if (DateTime.TryParseExact(
                    legacyToken,
                    "yyyyMMdd_HHmmss_fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out timestamp))
                {
                    return true;
                }
            }

            timestamp = default;
            return false;
        }

        private static bool IsImageFile(string path)
        {
            string extension = Path.GetExtension(path);
            return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalidChars = Path.GetInvalidFileNameChars().ToHashSet();
            var builder = new System.Text.StringBuilder(value.Length);
            foreach (char ch in value.Trim())
            {
                builder.Append(invalidChars.Contains(ch) || char.IsControl(ch) ? '_' : ch);
            }

            return builder.ToString().Trim(' ', '.', '_');
        }
    }
}
