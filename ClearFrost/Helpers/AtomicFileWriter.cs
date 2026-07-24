using System;
using System.IO;
using System.Text;

namespace ClearFrost.Helpers
{
    internal static class AtomicFileWriter
    {
        public static void WriteAllText(string targetPath, string content)
        {
            string fullTargetPath = PrepareTargetPath(targetPath);
            string directory = Path.GetDirectoryName(fullTargetPath) ?? string.Empty;

            string tempPath = Path.Combine(
                string.IsNullOrWhiteSpace(directory) ? "." : directory,
                $"{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");
            string backupPath = fullTargetPath + ".bak";

            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                EnsureFileWritePathStillSafe(fullTargetPath, "目标文件");
                if (File.Exists(fullTargetPath))
                {
                    EnsureFileWritePathStillSafe(backupPath, "备份文件");
                    File.Replace(tempPath, fullTargetPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullTargetPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // 临时文件清理失败不应覆盖原始写入错误。
                }
            }
        }

        public static void RestoreAllBytes(string targetPath, byte[] content)
        {
            string fullTargetPath = PrepareTargetPath(targetPath);
            string directory = Path.GetDirectoryName(fullTargetPath) ?? string.Empty;

            string tempPath = Path.Combine(
                string.IsNullOrWhiteSpace(directory) ? "." : directory,
                $"{Path.GetFileName(fullTargetPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllBytes(tempPath, content ?? Array.Empty<byte>());

                EnsureFileWritePathStillSafe(fullTargetPath, "目标文件");
                if (File.Exists(fullTargetPath))
                {
                    File.Replace(tempPath, fullTargetPath, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullTargetPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // 临时文件清理失败不应覆盖原始恢复错误。
                }
            }
        }

        private static string PrepareTargetPath(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("目标文件路径为空。", nameof(targetPath));
            }

            string fullTargetPath = Path.GetFullPath(targetPath);
            string directory = Path.GetDirectoryName(fullTargetPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                EnsureExistingDirectoryAncestorsHaveNoReparsePoint(directory);
                Directory.CreateDirectory(directory);
                EnsureDirectoryPathHasNoReparsePoint(directory);
            }

            EnsureFileWritePathStillSafe(fullTargetPath, "目标文件");
            return fullTargetPath;
        }

        private static void EnsureFileWritePathStillSafe(string fullPath, string pathRole)
        {
            if (Directory.Exists(fullPath))
            {
                throw new IOException($"{pathRole}路径是目录，不能作为文件写入: {fullPath}");
            }

            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                file.Refresh();
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"{pathRole}是链接文件，拒绝写入: {fullPath}");
                }
            }
        }

        private static void EnsureDirectoryPathHasNoReparsePoint(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null)
            {
                current.Refresh();
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"目标目录包含链接目录，拒绝写入: {current.FullName}");
                }

                current = current.Parent;
            }
        }

        private static void EnsureExistingDirectoryAncestorsHaveNoReparsePoint(string directory)
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null && !current.Exists)
            {
                current = current.Parent;
            }

            while (current != null)
            {
                current.Refresh();
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"目标目录包含链接目录，拒绝写入: {current.FullName}");
                }

                current = current.Parent;
            }
        }
    }
}
