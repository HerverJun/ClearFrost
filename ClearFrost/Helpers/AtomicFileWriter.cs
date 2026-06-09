using System;
using System.IO;
using System.Text;

namespace ClearFrost.Helpers
{
    internal static class AtomicFileWriter
    {
        public static void WriteAllText(string targetPath, string content)
        {
            string directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = Path.Combine(
                string.IsNullOrWhiteSpace(directory) ? "." : directory,
                $"{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
            string backupPath = targetPath + ".bak";

            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                if (File.Exists(targetPath))
                {
                    File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, targetPath);
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
    }
}
