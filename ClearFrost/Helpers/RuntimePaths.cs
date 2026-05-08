using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClearFrost.Helpers
{
    /// <summary>
    /// 运行时可写目录约定。
    /// </summary>
    public static class RuntimePaths
    {
        private const string AppFolderName = "ClearFrost";
        private const string OverrideRootEnvVar = "CLEARFROST_APPDATA_ROOT";

        public static string RootPath => EnsureWritableDirectory(GetPreferredRootCandidate());

        public static string ConfigDirectory => EnsureWritableDirectory(Path.Combine(RootPath, "Config"));

        public static string LogsDirectory => EnsureWritableDirectory(Path.Combine(RootPath, "Logs"));

        public static string DataDirectory => EnsureWritableDirectory(Path.Combine(RootPath, "Data"));

        public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

        public static string ProjectPresetsPath => Path.Combine(ConfigDirectory, "project-presets.json");

        public static string BundledConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

        public static string BundledProjectPresetsPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "project-presets.json");

        public static string LegacySharedConfigPath =>
            Path.Combine(GetLegacySharedRootCandidate(), "Config", "config.json");

        public static string LegacySharedProjectPresetsPath =>
            Path.Combine(GetLegacySharedRootCandidate(), "Config", "project-presets.json");

        public static string StartupLogPath => Path.Combine(LogsDirectory, "startup.log");

        public static string PlcDiagLogPath => Path.Combine(LogsDirectory, "plc_diag.log");

        public static string ConfigErrorLogPath => Path.Combine(LogsDirectory, "config_errors.log");

        public static string CrashLogPath(DateTime timestamp) =>
            Path.Combine(LogsDirectory, $"crash_{timestamp:yyyyMMdd}.log");

        public static string DatabasePath => Path.Combine(DataDirectory, "detection.db");

        public static string LegacySharedDatabasePath =>
            Path.Combine(GetLegacySharedRootCandidate(), "Data", "detection.db");

        public static string LegacyDatabasePath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "detection.db");

        private static string GetPreferredRootCandidate()
        {
            string? overrideRoot = Environment.GetEnvironmentVariable(OverrideRootEnvVar);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return overrideRoot.Trim();
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return GetScopedDefaultRootCandidate(localAppData, AppDomain.CurrentDomain.BaseDirectory);
            }

            return GetScopedDefaultRootCandidate(Path.GetTempPath(), AppDomain.CurrentDomain.BaseDirectory);
        }

        private static string GetLegacySharedRootCandidate()
        {
            string? overrideRoot = Environment.GetEnvironmentVariable(OverrideRootEnvVar);
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                return overrideRoot.Trim();
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, AppFolderName);
            }

            return Path.Combine(Path.GetTempPath(), AppFolderName);
        }

        private static string GetScopedDefaultRootCandidate(string parentRoot, string baseDirectory)
        {
            string scopeName = GetInstallationScopeName(baseDirectory);
            return Path.Combine(parentRoot, AppFolderName, scopeName);
        }

        private static string GetInstallationScopeName(string? baseDirectory)
        {
            string normalized = NormalizeBaseDirectory(baseDirectory);
            string leafName = Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(leafName))
            {
                leafName = "instance";
            }

            string safeLeafName = SanitizePathSegment(leafName);
            string hash = ComputeShortHash(normalized);
            return $"{safeLeafName}_{hash}";
        }

        private static string NormalizeBaseDirectory(string? baseDirectory)
        {
            string candidate = string.IsNullOrWhiteSpace(baseDirectory)
                ? AppDomain.CurrentDomain.BaseDirectory
                : baseDirectory;

            string normalized = Path.GetFullPath(candidate);
            return normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string SanitizePathSegment(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            string sanitized = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(sanitized) ? "instance" : sanitized;
        }

        private static string ComputeShortHash(string text)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToUpperInvariant());
            byte[] hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).Substring(0, 12);
        }

        private static string EnsureWritableDirectory(string primaryPath)
        {
            foreach (string candidate in GetCandidates(primaryPath))
            {
                try
                {
                    string normalized = Path.GetFullPath(candidate);
                    Directory.CreateDirectory(normalized);
                    return normalized;
                }
                catch
                {
                    // 尝试下一个候选目录
                }
            }

            return Path.GetFullPath(primaryPath);
        }

        private static IEnumerable<string> GetCandidates(string primaryPath)
        {
            if (!string.IsNullOrWhiteSpace(primaryPath))
            {
                yield return primaryPath;
            }

            string fallbackPath = GetScopedDefaultRootCandidate(Path.GetTempPath(), AppDomain.CurrentDomain.BaseDirectory);
            if (!string.Equals(primaryPath, fallbackPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return fallbackPath;
            }
        }
    }
}
