using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        public string ProductAssemblySha256 { get; init; } = string.Empty;
        public string ExternalDependencySetDigest { get; init; } = string.Empty;
        public IReadOnlyList<V6G2ExternalDependency> ExternalDependencies { get; init; } = Array.Empty<V6G2ExternalDependency>();
        public string CandidateDigest { get; init; } = string.Empty;
        public string EvidenceSetId { get; init; } = string.Empty;
        public string OrchestratorRunId { get; init; } = string.Empty;
        public string WorkflowRunId { get; init; } = string.Empty;
        /// <summary>Report-local provider statement; non-inference reports use NOT_APPLICABLE.</summary>
        public string Provider { get; init; } = "NOT_APPLICABLE";
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
            DateTimeOffset? runFinishedAtUtc = null,
            IEnumerable<V6G2ExternalDependency>? externalDependencies = null)
        {
            string effectiveDllPath = string.IsNullOrWhiteSpace(dllPath)
                ? typeof(AppRuntime).Assembly.Location
                : ResolvePath(root, dllPath);
            IReadOnlyList<V6G2ExternalDependency> dependencies = NormalizeExternalDependencies(
                externalDependencies ?? ReadExternalDependencies(root, inputManifestPath));
            string inputManifestSha256 = ComputeFileSha256(ResolveOptionalPath(root, inputManifestPath));
            string detectModelSha256 = ComputeFileSha256(ResolveOptionalPath(root, detectModelPath));
            string validationImageSha256 = ComputeFileSha256(ResolveOptionalPath(root, validationImagePath));
            string productAssemblySha256 = ComputeFileSha256(effectiveDllPath);
            string externalDependencySetDigest = ComputeExternalDependencySetDigest(dependencies);
            string commitSha = ResolveCommitSha(root);
            string productVersion = AppVersion.InformationalVersion;
            string candidateDigest = ComputeDigest(string.Join("\n", new[]
            {
                commitSha, productVersion, inputManifestSha256, detectModelSha256,
                validationImageSha256, productAssemblySha256, externalDependencySetDigest
            }));
            string orchestratorRunId = ResolveOrchestratorRunId();
            string workflowRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID")?.Trim() ?? string.Empty;

            return new V6G2EvidenceIdentity
            {
                CommitSha = commitSha,
                ProductVersion = productVersion,
                InputManifestSha256 = inputManifestSha256,
                DetectModelSha256 = detectModelSha256,
                ValidationImageSha256 = validationImageSha256,
                ProductAssemblySha256 = productAssemblySha256,
                ExternalDependencySetDigest = externalDependencySetDigest,
                ExternalDependencies = dependencies,
                CandidateDigest = candidateDigest,
                EvidenceSetId = ResolveEvidenceSetId(candidateDigest, orchestratorRunId),
                OrchestratorRunId = orchestratorRunId,
                WorkflowRunId = workflowRunId,
                Provider = string.IsNullOrWhiteSpace(provider) ? "NOT_APPLICABLE" : provider,
                MachineIdentityDigest = ResolveMachineIdentityDigest(),
                RunStartedAtUtc = runStartedAtUtc.ToUniversalTime().ToString("O"),
                RunFinishedAtUtc = (runFinishedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O")
            };
        }

        private static string ResolveOptionalPath(string root, string? path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : ResolvePath(root, path);
        }

        public static string ComputeExternalDependencySetDigest(IEnumerable<V6G2ExternalDependency>? dependencies)
        {
            IReadOnlyList<V6G2ExternalDependency> normalized = NormalizeExternalDependencies(dependencies);
            string canonical = string.Join("\n", normalized.Select(item => string.Join("|", new[]
            {
                item.Name,
                item.Version,
                item.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.Sha256,
                item.Role
            })));
            return ComputeDigest(canonical);
        }

        private static IReadOnlyList<V6G2ExternalDependency> NormalizeExternalDependencies(IEnumerable<V6G2ExternalDependency>? dependencies)
        {
            return (dependencies ?? Array.Empty<V6G2ExternalDependency>())
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .Select(item => new V6G2ExternalDependency
                {
                    Name = item.Name.Trim(),
                    Version = item.Version?.Trim() ?? string.Empty,
                    Bytes = Math.Max(0, item.Bytes),
                    Sha256 = item.Sha256?.Trim().ToUpperInvariant() ?? string.Empty,
                    Role = item.Role?.Trim() ?? string.Empty
                })
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .ToArray();
        }

        private static IReadOnlyList<V6G2ExternalDependency> ReadExternalDependencies(string root, string? manifestPath)
        {
            string path = ResolveOptionalPath(root, manifestPath);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Array.Empty<V6G2ExternalDependency>();
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("dependencies", out JsonElement dependencies) ||
                    dependencies.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<V6G2ExternalDependency>();
                }

                return dependencies.EnumerateArray().Select(item => new V6G2ExternalDependency
                {
                    Name = ReadString(item, "name", "fileName"),
                    Version = ReadString(item, "version"),
                    Bytes = ReadInt64(item, "expectedBytes", "bytes"),
                    Sha256 = ReadString(item, "expectedSha256", "sha256"),
                    Role = ReadString(item, "role")
                }).ToArray();
            }
            catch
            {
                return Array.Empty<V6G2ExternalDependency>();
            }
        }

        private static string ReadString(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static long ReadInt64(JsonElement element, params string[] names)
        {
            foreach (string name in names)
            {
                if (element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result))
                {
                    return result;
                }
            }

            return 0;
        }

        private static string ResolveOrchestratorRunId()
        {
            string? explicitValue = Environment.GetEnvironmentVariable("CLEARFROST_V6_G2_ORCHESTRATOR_RUN_ID");
            if (!string.IsNullOrWhiteSpace(explicitValue))
            {
                return explicitValue.Trim();
            }

            string? githubRun = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
            string? githubAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT");
            return string.IsNullOrWhiteSpace(githubRun)
                ? "local-unbound"
                : $"github:{githubRun.Trim()}:{githubAttempt?.Trim() ?? "1"}";
        }

        private static string ResolveEvidenceSetId(string candidateDigest, string orchestratorRunId)
        {
            string? explicitValue = Environment.GetEnvironmentVariable("CLEARFROST_V6_G2_EVIDENCE_SET_ID");
            return string.IsNullOrWhiteSpace(explicitValue)
                ? ComputeDigest($"{candidateDigest}|{orchestratorRunId}")
                : explicitValue.Trim();
        }

        private static string ComputeDigest(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty)));

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

    /// <summary>
    /// A name-sorted external binary record contributing to the dependency-set digest.
    /// </summary>
    public sealed class V6G2ExternalDependency
    {
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public long Bytes { get; init; }
        public string Sha256 { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
    }
}
