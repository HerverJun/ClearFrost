// ============================================================================
// 文件名: AppVersion.cs
// 描述:   应用版本信息统一入口
// ============================================================================

using System;
using System.IO;
using System.Reflection;

namespace ClearFrost.Helpers
{
    /// <summary>
    /// 统一读取发布版本、显示版本和缓存版本。
    /// </summary>
    public static class AppVersion
    {
        public static string InformationalVersion { get; } = GetInformationalVersion();

        public static string DisplayVersion { get; } = CreateDisplayVersion(InformationalVersion);

        public static string CacheKey { get; } = SanitizeForPath(InformationalVersion);

        public static string WindowTitle => $"清霜 V{DisplayVersion} 正式版";

        private static string GetInformationalVersion()
        {
            Assembly assembly = typeof(AppVersion).Assembly;
            string? informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return informationalVersion.Trim();
            }

            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        private static string CreateDisplayVersion(string informationalVersion)
        {
            string version = informationalVersion.Trim();
            int metadataIndex = version.IndexOf('+');
            if (metadataIndex >= 0)
            {
                version = version[..metadataIndex];
            }

            int prereleaseIndex = version.IndexOf('-');
            string suffix = string.Empty;
            if (prereleaseIndex >= 0)
            {
                suffix = version[prereleaseIndex..];
                version = version[..prereleaseIndex];
            }

            version = version.TrimStart('v', 'V');
            if (!Version.TryParse(version, out Version? parsed))
            {
                return version + suffix;
            }

            string display = parsed.Build <= 0
                ? $"{parsed.Major}.{parsed.Minor}"
                : $"{parsed.Major}.{parsed.Minor}.{parsed.Build}";

            if (parsed.Revision > 0)
            {
                display += $".{parsed.Revision}";
            }

            return display + suffix;
        }

        private static string SanitizeForPath(string value)
        {
            string sanitized = value.Trim();
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                sanitized = sanitized.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(sanitized) ? "0.0.0" : sanitized;
        }
    }
}
