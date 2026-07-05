// ============================================================================
// 文件名: DiagnosticPackageIntegrityVerifier.cs
// 描述:   诊断包完整性校验服务
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClearFrost.Services
{
    public sealed class DiagnosticPackageIntegrityFinding
    {
        public string EntryName { get; init; } = string.Empty;
        public string Severity { get; init; } = "Blocking";
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Recommendation { get; init; } = string.Empty;
        public long ExpectedLengthBytes { get; init; }
        public long ActualLengthBytes { get; init; }
        public string ExpectedSha256 { get; init; } = string.Empty;
        public string ActualSha256 { get; init; } = string.Empty;
    }

    public sealed class DiagnosticPackageIntegrityVerificationResult
    {
        public string PackagePath { get; init; } = string.Empty;
        public string PackageSha256 { get; init; } = string.Empty;
        public string IndexSha256 { get; init; } = string.Empty;
        public int IndexEntryCount { get; init; }
        public int VerifiedEntryCount { get; init; }
        public IReadOnlyList<DiagnosticPackageIntegrityFinding> Findings { get; init; } =
            Array.Empty<DiagnosticPackageIntegrityFinding>();

        public bool Succeeded => Findings.Count == 0;

        public string Status => Findings.Any(finding =>
            string.Equals(finding.Severity, "Blocking", StringComparison.OrdinalIgnoreCase))
                ? "Blocking"
                : Findings.Count > 0
                    ? "Warning"
                    : "Healthy";
    }

    public sealed class DiagnosticPackageIntegrityVerifier
    {
        private const string IndexEntryName = "diagnostic_index.json";
        private static readonly string[] RequiredCoreEntries =
        {
            "diagnostic_manifest.json",
            "field_report.md",
            "config.sanitized.json",
            "recipe.json",
            "recipe_summary.json",
            "model_registry.json",
            "model_registry_diagnostics.json",
            "runtime_model_slots.json",
            "startup_diagnostics.json",
            "startup_blockers.json",
            "health.json",
            "field_diagnostics.json",
            "recent_inspection_timings.json",
            "recent_errors.json",
            "maintenance_advice.json",
            "operation_audit_chain.json",
            "model_probe_summary.json",
            "queue_status.json",
            "recent_records.json",
            "system_info.txt",
            "native_dependencies.txt"
        };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public async Task<DiagnosticPackageIntegrityVerificationResult> VerifyAsync(
            string packagePath,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(packagePath))
            {
                return BuildResult(
                    packagePath ?? string.Empty,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    new[]
                    {
                        Finding(
                            string.Empty,
                            "PackagePathEmpty",
                            "诊断包路径为空。",
                            "请选择要校验的诊断包 zip 文件。")
                    });
            }

            if (!File.Exists(packagePath))
            {
                return BuildResult(
                    packagePath,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    new[]
                    {
                        Finding(
                            packagePath,
                            "PackageMissing",
                            "诊断包文件不存在。",
                            "确认诊断包路径是否正确，或重新导出诊断包。")
                    });
            }

            if (!IsSafeDiagnosticPackageFile(packagePath))
            {
                return BuildResult(
                    packagePath,
                    string.Empty,
                    string.Empty,
                    0,
                    0,
                    new[]
                    {
                        Finding(
                            packagePath,
                            "DiagnosticPackageReparsePoint",
                            "诊断包路径包含链接或重解析点，无法作为可信现场证据校验。",
                            "请从受控诊断包目录选择真实 zip 文件；不要校验符号链接、目录链接或挂载跳转路径。")
                    });
            }

            string packageSha256 = await ComputeFileSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            var findings = new List<DiagnosticPackageIntegrityFinding>();

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(packagePath);
                AddUnsafeEntryNameFindings(archive, findings);
                AddDuplicateEntryFindings(archive, findings);
                AddMissingCoreEntryFindings(archive, findings);

                ZipArchiveEntry? indexEntry = archive.GetEntry(IndexEntryName);
                if (indexEntry == null)
                {
                    findings.Add(Finding(
                        IndexEntryName,
                        "DiagnosticIndexMissing",
                        "诊断包缺少 diagnostic_index.json。",
                        "重新导出诊断包，或确认该包是否来自支持完整性索引的版本。"));
                    return BuildResult(packagePath, packageSha256, string.Empty, 0, 0, findings);
                }

                byte[] indexBytes = await ReadEntryBytesAsync(indexEntry, cancellationToken).ConfigureAwait(false);
                string indexSha256 = ComputeSha256(indexBytes);
                DiagnosticPackageIntegrityIndex? index = TryParseIndex(indexBytes, findings);
                if (index == null)
                {
                    return BuildResult(packagePath, packageSha256, indexSha256, 0, 0, findings);
                }

                ValidateIndexMetadata(index, findings);
                var indexedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int verifiedCount = 0;
                long declaredIndexedBytes = 0;
                foreach (DiagnosticPackageIndexEntry expected in index.Entries ?? Array.Empty<DiagnosticPackageIndexEntry>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(expected.EntryName))
                    {
                        findings.Add(Finding(
                            string.Empty,
                            "DiagnosticIndexEntryNameEmpty",
                            "完整性索引中存在空条目名。",
                            "重新导出诊断包。"));
                        continue;
                    }

                    if (IsUnsafeEntryName(expected.EntryName))
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticIndexEntryUnsafePath",
                            $"完整性索引包含不安全条目路径: {expected.EntryName}",
                            "不要解压或使用该诊断包；重新导出诊断包，并检查传输或归档流程是否被篡改。"));
                        continue;
                    }

                    if (!indexedNames.Add(expected.EntryName))
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticIndexDuplicateEntry",
                            "完整性索引中存在重复条目。",
                            "重新导出诊断包，避免使用该包进行远程排障。"));
                        continue;
                    }

                    if (expected.LengthBytes < 0)
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticIndexEntryLengthInvalid",
                            $"完整性索引条目长度无效: {expected.EntryName}",
                            "重新导出诊断包；索引条目长度必须是非负数。",
                            expected.LengthBytes,
                            0,
                            expected.Sha256,
                            string.Empty));
                    }

                    if (!IsSha256Hex(expected.Sha256))
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticIndexEntrySha256Invalid",
                            $"完整性索引条目 SHA-256 格式无效: {expected.EntryName}",
                            "重新导出诊断包；索引条目 SHA-256 必须是 64 位十六进制字符串。",
                            expected.LengthBytes,
                            0,
                            expected.Sha256,
                            string.Empty));
                    }

                    if (expected.LengthBytes >= 0)
                    {
                        declaredIndexedBytes += expected.LengthBytes;
                    }

                    ZipArchiveEntry? actualEntry = archive.GetEntry(expected.EntryName);
                    if (actualEntry == null)
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticEntryMissing",
                            $"诊断包缺少索引声明的条目: {expected.EntryName}",
                            "重新传输或重新导出诊断包。",
                            expected.LengthBytes,
                            0,
                            expected.Sha256,
                            string.Empty));
                        continue;
                    }

                    byte[] actualBytes = await ReadEntryBytesAsync(actualEntry, cancellationToken).ConfigureAwait(false);
                    string actualSha256 = ComputeSha256(actualBytes);
                    bool lengthMatched = actualBytes.LongLength == expected.LengthBytes;
                    bool hashMatched = string.Equals(
                        actualSha256,
                        expected.Sha256,
                        StringComparison.OrdinalIgnoreCase);

                    if (!lengthMatched)
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticEntryLengthMismatch",
                            $"诊断包条目长度不匹配: {expected.EntryName}",
                            "重新传输或重新导出诊断包。",
                            expected.LengthBytes,
                            actualBytes.LongLength,
                            expected.Sha256,
                            actualSha256));
                    }

                    if (!hashMatched)
                    {
                        findings.Add(Finding(
                            expected.EntryName,
                            "DiagnosticEntryHashMismatch",
                            $"诊断包条目 SHA-256 不匹配: {expected.EntryName}",
                            "重新传输或重新导出诊断包；若仍失败，请检查中间传输或归档流程。",
                            expected.LengthBytes,
                            actualBytes.LongLength,
                            expected.Sha256,
                            actualSha256));
                    }

                    if (lengthMatched && hashMatched)
                    {
                        verifiedCount++;
                    }
                }

                ValidateIndexTotals(index, indexedNames.Count, declaredIndexedBytes, findings);
                AddUnindexedEntryFindings(archive, indexedNames, findings);
                return BuildResult(
                    packagePath,
                    packageSha256,
                    indexSha256,
                    index.EntryCount,
                    verifiedCount,
                    findings);
            }
            catch (InvalidDataException ex)
            {
                findings.Add(Finding(
                    packagePath,
                    "DiagnosticPackageInvalidZip",
                    $"诊断包不是有效 zip 文件: {ex.Message}",
                    "重新传输或重新导出诊断包。"));
            }
            catch (IOException ex)
            {
                findings.Add(Finding(
                    packagePath,
                    "DiagnosticPackageReadFailed",
                    $"读取诊断包失败: {ex.Message}",
                    "确认文件未被占用且当前用户有读取权限。"));
            }

            return BuildResult(packagePath, packageSha256, string.Empty, 0, 0, findings);
        }

        private static DiagnosticPackageIntegrityVerificationResult BuildResult(
            string packagePath,
            string packageSha256,
            string indexSha256,
            int indexEntryCount,
            int verifiedEntryCount,
            IReadOnlyList<DiagnosticPackageIntegrityFinding> findings)
        {
            return new DiagnosticPackageIntegrityVerificationResult
            {
                PackagePath = packagePath,
                PackageSha256 = packageSha256,
                IndexSha256 = indexSha256,
                IndexEntryCount = indexEntryCount,
                VerifiedEntryCount = verifiedEntryCount,
                Findings = findings.ToList()
            };
        }

        private static void AddUnsafeEntryNameFindings(
            ZipArchive archive,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsUnsafeEntryName(entry.FullName))
                {
                    continue;
                }

                findings.Add(Finding(
                    entry.FullName,
                    "DiagnosticEntryUnsafePath",
                    $"诊断包包含不安全条目路径: {entry.FullName}",
                    "不要解压或使用该诊断包；重新导出诊断包，并检查传输或归档流程是否被篡改。"));
            }
        }

        private static void AddMissingCoreEntryFindings(
            ZipArchive archive,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            foreach (string entryName in RequiredCoreEntries)
            {
                if (archive.GetEntry(entryName) != null)
                {
                    continue;
                }

                findings.Add(Finding(
                    entryName,
                    "DiagnosticCoreEntryMissing",
                    $"诊断包缺少必需核心条目: {entryName}",
                    "重新导出诊断包；该包缺少现场排障和追溯所需的基础证据。"));
            }
        }

        private static void AddDuplicateEntryFindings(
            ZipArchive archive,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            foreach (var group in archive.Entries.GroupBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase))
            {
                int count = group.Count();
                if (count <= 1)
                {
                    continue;
                }

                findings.Add(new DiagnosticPackageIntegrityFinding
                {
                    EntryName = group.Key,
                    Severity = "Blocking",
                    ErrorCode = "DiagnosticEntryDuplicated",
                    Message = $"诊断包中存在重复条目: {group.Key} ({count})",
                    Recommendation = "重新导出诊断包，避免读取到不确定的条目内容。"
                });
            }
        }

        private static void ValidateIndexMetadata(
            DiagnosticPackageIntegrityIndex index,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            if (!string.Equals(index.FormatVersion, "1", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    IndexEntryName,
                    "DiagnosticIndexFormatUnsupported",
                    $"诊断包完整性索引格式版本不受支持: {index.FormatVersion}",
                    "重新导出诊断包，或使用匹配版本的软件校验该包。"));
            }

            if (!string.Equals(index.HashAlgorithm, "SHA-256", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding(
                    IndexEntryName,
                    "DiagnosticIndexHashAlgorithmUnsupported",
                    $"诊断包完整性索引声明了不受支持的哈希算法: {index.HashAlgorithm}",
                    "重新导出诊断包，避免使用无法验证的索引。"));
            }

            if (index.EntryCount < 0 || index.TotalUncompressedBytes < 0)
            {
                findings.Add(Finding(
                    IndexEntryName,
                    "DiagnosticIndexTotalsInvalid",
                    "诊断包完整性索引包含无效的负数统计值。",
                    "重新导出诊断包。"));
            }
        }

        private static void ValidateIndexTotals(
            DiagnosticPackageIntegrityIndex index,
            int actualIndexedEntryCount,
            long actualIndexedBytes,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            if (index.EntryCount != actualIndexedEntryCount)
            {
                findings.Add(Finding(
                    IndexEntryName,
                    "DiagnosticIndexEntryCountMismatch",
                    $"诊断包完整性索引条目数不一致: 声明 {index.EntryCount}, 实际 {actualIndexedEntryCount}",
                    "重新导出诊断包，确认索引没有被人工改写。",
                    index.EntryCount,
                    actualIndexedEntryCount));
            }

            if (index.TotalUncompressedBytes != actualIndexedBytes)
            {
                findings.Add(Finding(
                    IndexEntryName,
                    "DiagnosticIndexTotalBytesMismatch",
                    $"诊断包完整性索引总字节数不一致: 声明 {index.TotalUncompressedBytes}, 实际 {actualIndexedBytes}",
                    "重新导出诊断包，确认索引没有被人工改写。",
                    index.TotalUncompressedBytes,
                    actualIndexedBytes));
            }
        }

        private static void AddUnindexedEntryFindings(
            ZipArchive archive,
            HashSet<string> indexedNames,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.Equals(entry.FullName, IndexEntryName, StringComparison.OrdinalIgnoreCase) ||
                    indexedNames.Contains(entry.FullName))
                {
                    continue;
                }

                bool isRequiredCoreEntry = IsRequiredCoreEntry(entry.FullName);
                findings.Add(new DiagnosticPackageIntegrityFinding
                {
                    EntryName = entry.FullName,
                    Severity = isRequiredCoreEntry ? "Blocking" : "Warning",
                    ErrorCode = isRequiredCoreEntry
                        ? "DiagnosticCoreEntryNotIndexed"
                        : "DiagnosticEntryUnindexed",
                    Message = isRequiredCoreEntry
                        ? $"诊断包核心条目未被完整性索引覆盖: {entry.FullName}"
                        : $"诊断包包含未被完整性索引覆盖的条目: {entry.FullName}",
                    Recommendation = isRequiredCoreEntry
                        ? "重新导出诊断包；核心证据必须进入完整性索引才能用于现场追溯。"
                        : "确认该条目是否由人工添加；正式排障建议重新导出诊断包。"
                });
            }
        }

        private static bool IsRequiredCoreEntry(string entryName)
        {
            return RequiredCoreEntries.Any(required =>
                string.Equals(required, entryName, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUnsafeEntryName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return true;
            }

            if (entryName.StartsWith("/", StringComparison.Ordinal) ||
                entryName.StartsWith("\\", StringComparison.Ordinal) ||
                entryName.Contains('\\') ||
                Path.IsPathRooted(entryName))
            {
                return true;
            }

            string normalized = entryName.Replace('\\', '/');
            string firstSegment = normalized.Split('/')[0];
            if (firstSegment.EndsWith(":", StringComparison.Ordinal))
            {
                return true;
            }

            string[] segments = normalized.Split('/');
            return segments.Any(segment =>
                string.IsNullOrWhiteSpace(segment) ||
                string.Equals(segment, "..", StringComparison.Ordinal));
        }

        private static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            return value.All(ch =>
                (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F'));
        }

        private static DiagnosticPackageIntegrityIndex? TryParseIndex(
            byte[] bytes,
            List<DiagnosticPackageIntegrityFinding> findings)
        {
            try
            {
                ReadOnlySpan<byte> jsonBytes = HasUtf8Bom(bytes) ? bytes.AsSpan(3) : bytes;
                return JsonSerializer.Deserialize<DiagnosticPackageIntegrityIndex>(jsonBytes, JsonOptions);
            }
            catch (JsonException ex)
            {
                findings.Add(Finding(
                    IndexEntryName,
                    "DiagnosticIndexInvalidJson",
                    $"diagnostic_index.json 不是有效 JSON: {ex.Message}",
                    "重新导出诊断包。"));
                return null;
            }
        }

        private static DiagnosticPackageIntegrityFinding Finding(
            string entryName,
            string errorCode,
            string message,
            string recommendation,
            long expectedLengthBytes = 0,
            long actualLengthBytes = 0,
            string expectedSha256 = "",
            string actualSha256 = "")
        {
            return new DiagnosticPackageIntegrityFinding
            {
                EntryName = entryName,
                ErrorCode = errorCode,
                Message = message,
                Recommendation = recommendation,
                ExpectedLengthBytes = expectedLengthBytes,
                ActualLengthBytes = actualLengthBytes,
                ExpectedSha256 = expectedSha256,
                ActualSha256 = actualSha256
            };
        }

        private static async Task<byte[]> ReadEntryBytesAsync(
            ZipArchiveEntry entry,
            CancellationToken cancellationToken)
        {
            await using Stream stream = entry.Open();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }

        private static async Task<string> ComputeFileSha256Async(
            string path,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        internal static bool IsSafeDiagnosticPackageFile(string packagePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                {
                    return false;
                }

                string fullPath = Path.GetFullPath(packagePath);
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

        private static string ComputeSha256(byte[] bytes)
        {
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 &&
                   bytes[0] == 0xEF &&
                   bytes[1] == 0xBB &&
                   bytes[2] == 0xBF;
        }
    }
}
