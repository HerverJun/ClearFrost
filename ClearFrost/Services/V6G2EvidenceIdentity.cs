using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    /// <summary>
    /// Shared identity attached to every V6 G2 evidence report.
    /// </summary>
    public sealed class V6G2EvidenceIdentity
    {
        public string CommitSha { get; init; } = string.Empty;
        public string ProductVersion { get; init; } = string.Empty;
        public string InputManifestSha256 { get; init; } = string.Empty;
        public string DetectModelSha256 { get; init; } = string.Empty;
        public string ValidationImageSha256 { get; init; } = string.Empty;
        public string DllSha256 { get; init; } = string.Empty;
        public string Provider { get; init; } = "NOT_VERIFIED";
        public string MachineIdentityDigest { get; init; } = string.Empty;
        public string RunStartedAtUtc { get; init; } = string.Empty;
        public string RunFinishedAtUtc { get; init; } = string.Empty;

        public static V6G2EvidenceIdentity Create(
            string root,
            string? inputManifestPath,
            string? detectModelPath,
            string? validationImagePath,
            string? dllPath,
            string provider,
            DateTimeOffset runStartedAtUtc,
            DateTimeOffset? runFinishedAtUtc = null)
        {
            string effectiveDllPath = string.IsNullOrWhiteSpace(dllPath)
                ? typeof(AppRuntime).Assembly.Location
                : ResolvePath(root, dllPath);

            return new V6G2EvidenceIdentity
            {
                CommitSha = ResolveCommitSha(root),
                ProductVersion = AppVersion.InformationalVersion,
                InputManifestSha256 = ComputeFileSha256(ResolveOptionalPath(root, inputManifestPath)),
                DetectModelSha256 = ComputeFileSha256(ResolveOptionalPath(root, detectModelPath)),
                ValidationImageSha256 = ComputeFileSha256(ResolveOptionalPath(root, validationImagePath)),
                DllSha256 = ComputeFileSha256(effectiveDllPath),
                Provider = string.IsNullOrWhiteSpace(provider) ? "NOT_VERIFIED" : provider,
                MachineIdentityDigest = ResolveMachineIdentityDigest(),
                RunStartedAtUtc = runStartedAtUtc.ToUniversalTime().ToString("O"),
                RunFinishedAtUtc = (runFinishedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O")
            };
        }

        private static string ResolveOptionalPath(string root, string? path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : ResolvePath(root, path);
        }

        private static string ResolvePath(string root, string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }

        private static string ResolveCommitSha(string root)
        {
            string? environmentSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
            if (IsSha1(environmentSha))
            {
                return environmentSha!.Trim();
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        WorkingDirectory = root,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.StartInfo.ArgumentList.Add("rev-parse");
                process.StartInfo.ArgumentList.Add("HEAD");
                process.Start();
                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && IsSha1(output) ? output : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveMachineIdentityDigest()
        {
            string? supplied = Environment.GetEnvironmentVariable("CLEARFROST_V6_MACHINE_IDENTITY_DIGEST");
            if (!string.IsNullOrWhiteSpace(supplied) && supplied.Trim().Length == 64)
            {
                return supplied.Trim().ToUpperInvariant();
            }

            string payload = string.Join(
                "|",
                Environment.MachineName,
                Environment.OSVersion.VersionString,
                RuntimeInformation.OSArchitecture);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        }

        public static string ComputeFileSha256(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }

            try
            {
                using FileStream stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsSha1(string? value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 40 &&
                value.Trim().All(Uri.IsHexDigit);
        }
    }
}
