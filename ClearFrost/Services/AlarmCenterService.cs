// ============================================================================
// 文件名: AlarmCenterService.cs
// 描述:   工业运行告警中心，负责告警持久化、恢复和确认
//
// 功能:
//   - 根据健康快照生成活动告警
//   - 记录告警的触发、恢复、确认生命周期
//   - 为前端和诊断提供最近告警快照
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    public enum AlarmSeverity
    {
        Info,
        Warning,
        Critical
    }

    public enum AlarmState
    {
        Active,
        Cleared
    }

    public sealed class AlarmRecord
    {
        public string AlarmId { get; set; } = string.Empty;
        public string AlarmKey { get; set; } = string.Empty;
        public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;
        public AlarmState State { get; set; } = AlarmState.Active;
        public string Source { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string RecommendedAction { get; set; } = string.Empty;
        public string LastInspectionId { get; set; } = string.Empty;
        public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset? ClearedAt { get; set; }
        public string AcknowledgedBy { get; set; } = string.Empty;
        public string AcknowledgedRole { get; set; } = string.Empty;
        public DateTimeOffset? AcknowledgedAt { get; set; }
        public int OccurrenceCount { get; set; } = 1;
        public bool IsAcknowledged => AcknowledgedAt.HasValue;
    }

    public sealed class AlarmSnapshot
    {
        public IReadOnlyList<AlarmRecord> ActiveAlarms { get; init; } = Array.Empty<AlarmRecord>();
        public IReadOnlyList<AlarmRecord> RecentAlarms { get; init; } = Array.Empty<AlarmRecord>();
        public int ActiveCount { get; init; }
        public int UnacknowledgedCount { get; init; }
        public AlarmSeverity HighestSeverity { get; init; } = AlarmSeverity.Info;
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    }

    public sealed class AlarmCenterService
    {
        private const int MaxPersistedAlarms = 500;
        private const int DefaultRecentLimit = 100;
        private static readonly TimeSpan ActiveErrorWindow = TimeSpan.FromMinutes(30);

        private readonly object _sync = new();
        private readonly Func<DateTimeOffset> _clock;
        private string _alarmPath;
        private List<AlarmRecord> _alarms;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public AlarmCenterService(string systemPath)
            : this(systemPath, () => DateTimeOffset.Now)
        {
        }

        internal AlarmCenterService(string systemPath, Func<DateTimeOffset> clock)
        {
            if (string.IsNullOrWhiteSpace(systemPath))
            {
                throw new ArgumentException("系统目录不能为空", nameof(systemPath));
            }

            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _alarmPath = BuildAlarmPath(systemPath);
            _alarms = LoadAlarms(_alarmPath);
        }

        public string AlarmPath => _alarmPath;

        public void Reconfigure(string systemPath)
        {
            if (string.IsNullOrWhiteSpace(systemPath))
            {
                throw new ArgumentException("系统目录不能为空", nameof(systemPath));
            }

            lock (_sync)
            {
                SaveCore();
                _alarmPath = BuildAlarmPath(systemPath);
                _alarms = LoadAlarms(_alarmPath);
            }
        }

        public AlarmSnapshot Evaluate(HealthSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            lock (_sync)
            {
                DateTimeOffset now = _clock();
                AlarmCandidate[] candidates = BuildCandidates(snapshot, now)
                    .GroupBy(candidate => candidate.AlarmKey, StringComparer.Ordinal)
                    .Select(group => group
                        .OrderByDescending(candidate => candidate.Severity)
                        .ThenByDescending(candidate => candidate.OccurrenceCount)
                        .First())
                    .ToArray();
                var activeKeys = new HashSet<string>(candidates.Select(candidate => candidate.AlarmKey), StringComparer.Ordinal);

                foreach (AlarmCandidate candidate in candidates)
                {
                    AlarmRecord? existing = _alarms
                        .Where(alarm => alarm.State == AlarmState.Active)
                        .FirstOrDefault(alarm => string.Equals(alarm.AlarmKey, candidate.AlarmKey, StringComparison.Ordinal));

                    if (existing == null)
                    {
                        _alarms.Add(CreateRecord(candidate, now));
                    }
                    else
                    {
                        existing.Severity = candidate.Severity;
                        existing.Source = candidate.Source;
                        existing.Message = candidate.Message;
                        existing.RecommendedAction = candidate.RecommendedAction;
                        existing.LastInspectionId = candidate.LastInspectionId;
                        existing.LastSeenAt = now;
                        existing.OccurrenceCount = Math.Max(1, existing.OccurrenceCount + candidate.OccurrenceCount);
                    }
                }

                foreach (AlarmRecord alarm in _alarms.Where(alarm => alarm.State == AlarmState.Active).ToArray())
                {
                    if (!activeKeys.Contains(alarm.AlarmKey))
                    {
                        alarm.State = AlarmState.Cleared;
                        alarm.ClearedAt = now;
                        alarm.LastSeenAt = now;
                    }
                }

                PruneCore();
                SaveCore();
                return BuildSnapshotCore(now, DefaultRecentLimit);
            }
        }

        public AlarmSnapshot GetSnapshot(int recentLimit = DefaultRecentLimit)
        {
            lock (_sync)
            {
                return BuildSnapshotCore(_clock(), recentLimit);
            }
        }

        public AlarmRecord Acknowledge(string alarmId, OperatorSession session)
        {
            if (string.IsNullOrWhiteSpace(alarmId))
            {
                throw new ArgumentException("告警编号不能为空", nameof(alarmId));
            }

            ArgumentNullException.ThrowIfNull(session);

            lock (_sync)
            {
                AlarmRecord alarm = _alarms.FirstOrDefault(a => string.Equals(a.AlarmId, alarmId.Trim(), StringComparison.Ordinal))
                    ?? throw new FileNotFoundException($"未找到告警: {alarmId}");
                if (!alarm.IsAcknowledged)
                {
                    alarm.AcknowledgedBy = session.OperatorName;
                    alarm.AcknowledgedRole = session.Role;
                    alarm.AcknowledgedAt = _clock();
                    SaveCore();
                }

                return CloneRecord(alarm);
            }
        }

        public int AcknowledgeAll(OperatorSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            lock (_sync)
            {
                DateTimeOffset now = _clock();
                int count = 0;
                foreach (AlarmRecord alarm in _alarms.Where(alarm => alarm.State == AlarmState.Active && !alarm.IsAcknowledged))
                {
                    alarm.AcknowledgedBy = session.OperatorName;
                    alarm.AcknowledgedRole = session.Role;
                    alarm.AcknowledgedAt = now;
                    count++;
                }

                if (count > 0)
                {
                    SaveCore();
                }

                return count;
            }
        }

        private AlarmSnapshot BuildSnapshotCore(DateTimeOffset now, int recentLimit)
        {
            int safeLimit = recentLimit <= 0 ? DefaultRecentLimit : Math.Min(recentLimit, MaxPersistedAlarms);
            AlarmRecord[] active = _alarms
                .Where(alarm => alarm.State == AlarmState.Active)
                .OrderByDescending(alarm => alarm.Severity)
                .ThenBy(alarm => alarm.RaisedAt)
                .Select(CloneRecord)
                .ToArray();
            AlarmRecord[] recent = _alarms
                .OrderByDescending(alarm => alarm.State == AlarmState.Active)
                .ThenByDescending(alarm => alarm.LastSeenAt)
                .Take(safeLimit)
                .Select(CloneRecord)
                .ToArray();

            return new AlarmSnapshot
            {
                ActiveAlarms = active,
                RecentAlarms = recent,
                ActiveCount = active.Length,
                UnacknowledgedCount = active.Count(alarm => !alarm.IsAcknowledged),
                HighestSeverity = active.Length == 0 ? AlarmSeverity.Info : active.Max(alarm => alarm.Severity),
                UpdatedAt = now
            };
        }

        private static IEnumerable<AlarmCandidate> BuildCandidates(HealthSnapshot snapshot, DateTimeOffset now)
        {
            foreach (MaintenanceAdvice advice in snapshot.MaintenanceAdvices.Where(advice => advice.Level != HealthLevel.Ok))
            {
                string source = NormalizeText(advice.Source, "Maintenance");
                string message = NormalizeText(advice.Message, "维护建议");
                string action = NormalizeText(advice.Action, string.Empty, 512);
                yield return new AlarmCandidate(
                    BuildAlarmKey("Advice", source, message),
                    ToAlarmSeverity(advice.Level),
                    source,
                    message,
                    action,
                    string.Empty,
                    1);
            }

            foreach (HealthError error in snapshot.RecentErrors)
            {
                if (now - error.Timestamp > ActiveErrorWindow)
                {
                    continue;
                }

                string source = NormalizeText(error.Source, "Health");
                string message = NormalizeText(error.Message, "运行错误");
                yield return new AlarmCandidate(
                    BuildAlarmKey("Error", source, message),
                    AlarmSeverity.Warning,
                    source,
                    message,
                    "查看健康趋势和检测日志，确认该错误是否持续出现",
                    error.InspectionId ?? string.Empty,
                    1);
            }

            if (snapshot.HealthLevel == HealthLevel.Critical &&
                string.Equals(snapshot.StorageStatus, "Error", StringComparison.OrdinalIgnoreCase))
            {
                yield return new AlarmCandidate(
                    BuildAlarmKey("Status", "Storage", "存储目录不可写"),
                    AlarmSeverity.Critical,
                    "Storage",
                    "存储目录不可写或磁盘不可用",
                    "立即检查存储路径、磁盘权限和网络盘连接，恢复前不要继续生产",
                    string.Empty,
                    1);
            }
        }

        private static AlarmRecord CreateRecord(AlarmCandidate candidate, DateTimeOffset now)
        {
            return new AlarmRecord
            {
                AlarmId = $"{now.LocalDateTime:yyyyMMddHHmmssfff}_{ShortHash(candidate.AlarmKey, 8)}",
                AlarmKey = candidate.AlarmKey,
                Severity = candidate.Severity,
                State = AlarmState.Active,
                Source = candidate.Source,
                Message = candidate.Message,
                RecommendedAction = candidate.RecommendedAction,
                LastInspectionId = candidate.LastInspectionId,
                RaisedAt = now,
                LastSeenAt = now,
                OccurrenceCount = Math.Max(1, candidate.OccurrenceCount)
            };
        }

        private static AlarmSeverity ToAlarmSeverity(HealthLevel level)
        {
            return level switch
            {
                HealthLevel.Critical => AlarmSeverity.Critical,
                HealthLevel.Warning => AlarmSeverity.Warning,
                _ => AlarmSeverity.Info
            };
        }

        private static string BuildAlarmKey(string kind, string source, string message)
        {
            return $"{kind}:{ShortHash($"{source}|{message}", 16)}";
        }

        private static string ShortHash(string text, int length)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(hash).ToLowerInvariant()[..length];
        }

        private static string BuildAlarmPath(string systemPath)
        {
            string directory = Path.Combine(systemPath, "Alarms");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "alarms.json");
        }

        private static string NormalizeText(string? value, string fallback, int maxLength = 256)
        {
            string normalized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ')
                .Trim();
            while (normalized.Contains("  ", StringComparison.Ordinal))
            {
                normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
            }

            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = fallback;
            }

            return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
        }

        private static List<AlarmRecord> LoadAlarms(string alarmPath)
        {
            try
            {
                if (!File.Exists(alarmPath))
                {
                    return new List<AlarmRecord>();
                }

                string json = File.ReadAllText(alarmPath);
                AlarmRecord[]? records = JsonSerializer.Deserialize<AlarmRecord[]>(json, JsonOptions);
                return records?
                    .Where(record => !string.IsNullOrWhiteSpace(record.AlarmId))
                    .OrderBy(record => record.RaisedAt)
                    .ToList() ?? new List<AlarmRecord>();
            }
            catch
            {
                return new List<AlarmRecord>();
            }
        }

        private void SaveCore()
        {
            AtomicFileWriter.WriteAllText(_alarmPath, JsonSerializer.Serialize(_alarms, JsonOptions));
        }

        private void PruneCore()
        {
            if (_alarms.Count <= MaxPersistedAlarms)
            {
                return;
            }

            _alarms = _alarms
                .OrderByDescending(alarm => alarm.State == AlarmState.Active)
                .ThenByDescending(alarm => alarm.LastSeenAt)
                .Take(MaxPersistedAlarms)
                .OrderBy(alarm => alarm.RaisedAt)
                .ToList();
        }

        private static AlarmRecord CloneRecord(AlarmRecord alarm)
        {
            return new AlarmRecord
            {
                AlarmId = alarm.AlarmId,
                AlarmKey = alarm.AlarmKey,
                Severity = alarm.Severity,
                State = alarm.State,
                Source = alarm.Source,
                Message = alarm.Message,
                RecommendedAction = alarm.RecommendedAction,
                LastInspectionId = alarm.LastInspectionId,
                RaisedAt = alarm.RaisedAt,
                LastSeenAt = alarm.LastSeenAt,
                ClearedAt = alarm.ClearedAt,
                AcknowledgedBy = alarm.AcknowledgedBy,
                AcknowledgedRole = alarm.AcknowledgedRole,
                AcknowledgedAt = alarm.AcknowledgedAt,
                OccurrenceCount = alarm.OccurrenceCount
            };
        }

        private sealed record AlarmCandidate(
            string AlarmKey,
            AlarmSeverity Severity,
            string Source,
            string Message,
            string RecommendedAction,
            string LastInspectionId,
            int OccurrenceCount);
    }
}
