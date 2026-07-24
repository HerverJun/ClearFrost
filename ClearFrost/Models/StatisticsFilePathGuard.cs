// ============================================================================
// 文件名: StatisticsFilePathGuard.cs
// 描述:   统计证据文件路径安全边界
// ============================================================================

using System;
using System.IO;

namespace ClearFrost.Models
{
    internal static class StatisticsFilePathGuard
    {
        public static void EnsureDirectorySafeForWrite(string directory, string displayName)
        {
            string fullDirectory = Path.GetFullPath(directory);
            EnsureExistingDirectoryAncestorsHaveNoReparsePoint(fullDirectory, displayName);
            Directory.CreateDirectory(fullDirectory);
            if (DirectoryPathHasReparsePoint(fullDirectory))
            {
                throw new IOException($"{displayName}包含链接目录，拒绝写入: {fullDirectory}");
            }
        }

        public static void EnsureFileSafeForRead(string path, string displayName)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(directory) || DirectoryPathHasReparsePoint(directory))
            {
                throw new IOException($"{displayName}目录包含链接目录，拒绝读取: {directory}");
            }

            var file = new FileInfo(fullPath);
            file.Refresh();
            if (!file.Exists)
            {
                throw new FileNotFoundException($"{displayName}不存在。", fullPath);
            }

            if (HasReparsePoint(file))
            {
                throw new IOException($"{displayName}是链接文件，拒绝读取: {fullPath}");
            }
        }

        private static void EnsureExistingDirectoryAncestorsHaveNoReparsePoint(string directory, string displayName)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null)
            {
                current.Refresh();
                if (current.Exists && HasReparsePoint(current))
                {
                    throw new IOException($"{displayName}祖先目录包含链接目录，拒绝写入: {current.FullName}");
                }

                current = current.Parent;
            }
        }

        private static bool DirectoryPathHasReparsePoint(string directory)
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
    }
}
