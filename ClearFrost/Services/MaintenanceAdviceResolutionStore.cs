// ============================================================================
// 文件名: MaintenanceAdviceResolutionStore.cs
// 描述:   维护建议处理与复检闭环记录
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Core.Security;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    internal static class MaintenanceAdviceResolutionStatuses
    {
        public const string Acknowledged = "Acknowledged";
        public const string RecheckPassed = "RecheckPassed";
        public const string RecheckFailed = "RecheckFailed";
    }

    public sealed class MaintenanceAdviceResolutionRecord
    {
        public string AdviceId { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string OperatorId { get; init; } = string.Empty;
        public ProductionRole Role { get; init; } = ProductionRole.Operator;
        public string Notes { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Evidence { get; init; } = string.Empty;
        public string Advice { get; init; } = string.Empty;
        public DateTimeOffset ActionAt { get; init; } = DateTimeOffset.Now;
    }

    internal sealed class MaintenanceAdviceFirstSeenRecord
    {
        public string AdviceId { get; init; } = string.Empty;
        public string Code { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public DateTimeOffset FirstSeenAt { get; init; } = DateTimeOffset.Now;
    }

    internal sealed class MaintenanceAdviceActionResult
    {
        public bool Succeeded { get; init; }
        public bool Cleared { get; init; }
        public string AdviceId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public MaintenanceAdviceResolutionRecord? Record { get; init; }
        public IReadOnlyList<MaintenanceAdviceResolutionRecord> History { get; init; } =
            Array.Empty<MaintenanceAdviceResolutionRecord>();
    }

    internal sealed class ShiftTaskActionResult
    {
        public bool Succeeded { get; init; }
        public bool Cleared { get; init; }
        public string TaskId { get; init; } = string.Empty;
        public string LinkedAdviceId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public MaintenanceAdviceResolutionRecord? Record { get; init; }
        public IReadOnlyList<FieldShiftTask> Tasks { get; init; } = Array.Empty<FieldShiftTask>();
        public IReadOnlyList<MaintenanceAdviceResolutionRecord> History { get; init; } =
            Array.Empty<MaintenanceAdviceResolutionRecord>();
    }

    internal sealed class MaintenanceAdviceResolutionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private string _storePath;
        private string _firstSeenPath;

        public MaintenanceAdviceResolutionStore(string storePath)
        {
            _storePath = ResolveStorePath(storePath);
            _firstSeenPath = ResolveFirstSeenPath(_storePath);
        }

        internal string FirstSeenPath => _firstSeenPath;

        internal string StorePath => _storePath;

        public void UpdateStorePath(string storePath)
        {
            if (string.IsNullOrWhiteSpace(storePath))
            {
                return;
            }

            _storePath = ResolveStorePath(storePath);
            _firstSeenPath = ResolveFirstSeenPath(_storePath);
        }

        public IReadOnlyList<MaintenanceAdviceResolutionRecord> QueryRecent(int limit = 12)
        {
            int safeLimit = Math.Clamp(limit <= 0 ? 12 : limit, 1, 100);
            return LoadAll()
                .OrderByDescending(record => record.ActionAt)
                .Take(safeLimit)
                .ToList();
        }

        public MaintenanceAdviceResolutionRecord? Find(string adviceId)
        {
            if (string.IsNullOrWhiteSpace(adviceId))
            {
                return null;
            }

            return LoadAll()
                .Where(record => string.Equals(record.AdviceId, adviceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.ActionAt)
                .FirstOrDefault();
        }

        public IReadOnlyDictionary<string, DateTimeOffset> CaptureFirstSeenTimes(
            IReadOnlyList<FieldMaintenanceAdvice>? activeAdvice,
            DateTimeOffset? now = null)
        {
            activeAdvice ??= Array.Empty<FieldMaintenanceAdvice>();
            DateTimeOffset effectiveNow = now ?? DateTimeOffset.Now;

            var activeItems = activeAdvice
                .Where(advice => advice != null)
                .Select(advice => new
                {
                    Advice = advice,
                    AdviceId = ResolveAdviceId(advice)
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.AdviceId))
                .GroupBy(item => item.AdviceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            _lock.Wait();
            try
            {
                EnsureStoreFileSafeForWrite(_firstSeenPath);
                Dictionary<string, MaintenanceAdviceFirstSeenRecord> existing = LoadFirstSeenAll()
                    .Where(record => !string.IsNullOrWhiteSpace(record.AdviceId))
                    .GroupBy(record => record.AdviceId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.OrderBy(record => record.FirstSeenAt).First(),
                        StringComparer.OrdinalIgnoreCase);

                var next = new List<MaintenanceAdviceFirstSeenRecord>();
                foreach (var item in activeItems)
                {
                    if (!existing.TryGetValue(item.AdviceId, out MaintenanceAdviceFirstSeenRecord? record))
                    {
                        record = new MaintenanceAdviceFirstSeenRecord
                        {
                            AdviceId = item.AdviceId,
                            Code = item.Advice.Code,
                            Source = item.Advice.Source,
                            Title = item.Advice.Title,
                            FirstSeenAt = effectiveNow
                        };
                    }

                    next.Add(record);
                }

                AtomicFileWriter.WriteAllText(_firstSeenPath, JsonSerializer.Serialize(next, JsonOptions));
                return next.ToDictionary(
                    record => record.AdviceId,
                    record => record.FirstSeenAt,
                    StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<MaintenanceAdviceResolutionRecord> AppendAsync(
            FieldMaintenanceAdvice advice,
            string status,
            string operatorId,
            ProductionRole role,
            string notes,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (advice == null)
            {
                throw new ArgumentNullException(nameof(advice));
            }

            string adviceId = string.IsNullOrWhiteSpace(advice.AdviceId)
                ? FieldDiagnosticsSnapshotFactory.CreateMaintenanceAdviceId(advice.Source, advice.Code, advice.Title)
                : advice.AdviceId;

            var record = new MaintenanceAdviceResolutionRecord
            {
                AdviceId = adviceId,
                Code = advice.Code,
                Source = advice.Source,
                Title = advice.Title,
                Status = status,
                OperatorId = operatorId,
                Role = role,
                Notes = notes ?? string.Empty,
                Message = message ?? string.Empty,
                Evidence = advice.Evidence,
                Advice = advice.Advice,
                ActionAt = DateTimeOffset.Now
            };

            await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureStoreFileSafeForWrite(_storePath);
                List<MaintenanceAdviceResolutionRecord> records = LoadAll()
                    .Where(existing => !string.Equals(existing.AdviceId, adviceId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                records.Add(record);
                AtomicFileWriter.WriteAllText(_storePath, JsonSerializer.Serialize(records, JsonOptions));
                return record;
            }
            finally
            {
                _lock.Release();
            }
        }

        private List<MaintenanceAdviceResolutionRecord> LoadAll()
        {
            try
            {
                if (!File.Exists(_storePath))
                {
                    return new List<MaintenanceAdviceResolutionRecord>();
                }

                if (!IsStoreFileSafeForRead(_storePath))
                {
                    return new List<MaintenanceAdviceResolutionRecord>();
                }

                using FileStream stream = OpenStoreFileForRead(_storePath);
                List<MaintenanceAdviceResolutionRecord>? records =
                    JsonSerializer.Deserialize<List<MaintenanceAdviceResolutionRecord>>(stream, JsonOptions);
                if (!IsStoreFileSafeForRead(_storePath))
                {
                    return new List<MaintenanceAdviceResolutionRecord>();
                }

                return records ??
                       new List<MaintenanceAdviceResolutionRecord>();
            }
            catch
            {
                return new List<MaintenanceAdviceResolutionRecord>();
            }
        }

        private List<MaintenanceAdviceFirstSeenRecord> LoadFirstSeenAll()
        {
            try
            {
                if (!File.Exists(_firstSeenPath))
                {
                    return new List<MaintenanceAdviceFirstSeenRecord>();
                }

                if (!IsStoreFileSafeForRead(_firstSeenPath))
                {
                    return new List<MaintenanceAdviceFirstSeenRecord>();
                }

                using FileStream stream = OpenStoreFileForRead(_firstSeenPath);
                List<MaintenanceAdviceFirstSeenRecord>? records =
                    JsonSerializer.Deserialize<List<MaintenanceAdviceFirstSeenRecord>>(stream, JsonOptions);
                if (!IsStoreFileSafeForRead(_firstSeenPath))
                {
                    return new List<MaintenanceAdviceFirstSeenRecord>();
                }

                return records ??
                       new List<MaintenanceAdviceFirstSeenRecord>();
            }
            catch
            {
                return new List<MaintenanceAdviceFirstSeenRecord>();
            }
        }

        private static string ResolveAdviceId(FieldMaintenanceAdvice advice)
        {
            return string.IsNullOrWhiteSpace(advice.AdviceId)
                ? FieldDiagnosticsSnapshotFactory.CreateMaintenanceAdviceId(advice.Source, advice.Code, advice.Title)
                : advice.AdviceId;
        }

        private static string ResolveStorePath(string storePath)
        {
            string path = string.IsNullOrWhiteSpace(storePath)
                ? Path.Combine(RuntimePaths.DataDirectory, "maintenance-advice-resolution.json")
                : storePath;
            return Path.GetFullPath(path);
        }

        private static string ResolveFirstSeenPath(string storePath)
        {
            if (string.IsNullOrWhiteSpace(storePath))
            {
                return Path.Combine(RuntimePaths.DataDirectory, "maintenance-advice-first-seen.json");
            }

            string directory = Path.GetDirectoryName(storePath) ?? string.Empty;
            string fileName = Path.GetFileNameWithoutExtension(storePath);
            string extension = Path.GetExtension(storePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".json";
            }

            string firstSeenFileName = fileName.EndsWith("-resolution", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^"-resolution".Length] + "-first-seen" + extension
                : fileName + "-first-seen" + extension;
            return string.IsNullOrWhiteSpace(directory)
                ? firstSeenFileName
                : Path.GetFullPath(Path.Combine(directory, firstSeenFileName));
        }

        private static void EnsureStoreFileSafeForWrite(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("维护建议证据目录为空。");
            }

            if (HasReparsePointInPath(directory) ||
                (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath))))
            {
                throw new InvalidOperationException($"维护建议证据文件不能穿过链接目录或链接文件: {fullPath}");
            }

            Directory.CreateDirectory(directory);
            if (HasReparsePointInPath(directory) ||
                (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath))))
            {
                throw new InvalidOperationException($"维护建议证据文件不能穿过链接目录或链接文件: {fullPath}");
            }
        }

        private static bool IsStoreFileSafeForRead(string path)
        {
            return !HasReparsePointInPath(path);
        }

        private static FileStream OpenStoreFileForRead(string path)
        {
            if (!IsStoreFileSafeForRead(path))
            {
                throw new InvalidOperationException($"维护建议证据文件不能穿过链接目录或链接文件: {Path.GetFullPath(path)}");
            }

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (!IsStoreFileSafeForRead(path))
                {
                    throw new InvalidOperationException($"维护建议证据文件不能穿过链接目录或链接文件: {Path.GetFullPath(path)}");
                }

                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static bool HasReparsePointInPath(string path)
        {
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath)))
            {
                return true;
            }

            DirectoryInfo? directory = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath).Directory;
            while (directory != null)
            {
                if (Directory.Exists(directory.FullName) && HasReparsePoint(directory))
                {
                    return true;
                }

                directory = directory.Parent;
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
