// ============================================================================
// 文件名: OperatorSessionService.cs
// 描述:   操作员会话与班次追溯服务
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ClearFrost.Services
{
    public sealed class OperatorSession
    {
        public const string DefaultOperatorName = "未登录";
        public const string DefaultRole = "Operator";

        public string OperatorName { get; init; } = DefaultOperatorName;
        public string Role { get; init; } = DefaultRole;
        public string ShiftName { get; init; } = string.Empty;
        public DateTimeOffset SignedInAt { get; init; } = DateTimeOffset.Now;
        public bool IsSignedIn => !string.Equals(OperatorName, DefaultOperatorName, StringComparison.Ordinal);
    }

    public sealed class OperatorSessionService
    {
        private const string SessionFileName = "operator-session.json";
        private static readonly TimeSpan DefaultSessionMaxAge = TimeSpan.FromHours(12);
        private static readonly TimeSpan FutureClockTolerance = TimeSpan.FromMinutes(5);
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _nowProvider;
        private string _sessionPath;
        private OperatorSession _current;
        private TimeSpan _sessionMaxAge;

        public OperatorSessionService(
            string systemPath,
            TimeSpan? sessionMaxAge = null,
            Func<DateTimeOffset>? nowProvider = null)
        {
            if (string.IsNullOrWhiteSpace(systemPath))
            {
                throw new ArgumentException("系统目录不能为空", nameof(systemPath));
            }

            _nowProvider = nowProvider ?? (() => DateTimeOffset.Now);
            _sessionMaxAge = NormalizeSessionMaxAge(sessionMaxAge);
            Directory.CreateDirectory(systemPath);
            _sessionPath = Path.Combine(systemPath, SessionFileName);
            DateTimeOffset now = _nowProvider();
            OperatorSession? loadedSession = LoadSession();
            _current = loadedSession != null && !IsSessionExpired(loadedSession, now)
                ? loadedSession
                : CreateDefaultSession(now);

            if (loadedSession != null && !ReferenceEquals(loadedSession, _current))
            {
                TrySaveSession(_current);
            }
        }

        public OperatorSession Current
        {
            get
            {
                lock (_sync)
                {
                    return EnsureCurrentSessionLocked();
                }
            }
        }

        public string SessionPath => _sessionPath;

        public TimeSpan SessionMaxAge => _sessionMaxAge;

        public void Reconfigure(string systemPath, TimeSpan? sessionMaxAge = null)
        {
            if (string.IsNullOrWhiteSpace(systemPath))
            {
                throw new ArgumentException("系统目录不能为空", nameof(systemPath));
            }

            lock (_sync)
            {
                if (sessionMaxAge.HasValue)
                {
                    _sessionMaxAge = NormalizeSessionMaxAge(sessionMaxAge);
                }

                Directory.CreateDirectory(systemPath);
                _sessionPath = Path.Combine(systemPath, SessionFileName);
                DateTimeOffset now = _nowProvider();
                OperatorSession? loadedSession = LoadSession();
                _current = loadedSession != null && !IsSessionExpired(loadedSession, now)
                    ? loadedSession
                    : IsSessionExpired(_current, now)
                        ? CreateDefaultSession(now)
                        : _current;
                SaveSession(_current);
            }
        }

        public OperatorSession SignIn(string operatorName, string? role = null, string? shiftName = null, DateTimeOffset? signedInAt = null)
        {
            string normalizedOperatorName = NormalizeOperatorName(operatorName);
            string normalizedRole = NormalizeRole(role);
            DateTimeOffset signInTime = signedInAt ?? DateTimeOffset.Now;
            string normalizedShift = NormalizeShiftName(shiftName, signInTime.LocalDateTime);

            var session = new OperatorSession
            {
                OperatorName = normalizedOperatorName,
                Role = normalizedRole,
                ShiftName = normalizedShift,
                SignedInAt = signInTime
            };

            lock (_sync)
            {
                _current = session;
                SaveSession(session);
                return _current;
            }
        }

        public OperatorSession SignOut(DateTimeOffset? signedOutAt = null)
        {
            OperatorSession session = CreateDefaultSession(signedOutAt ?? _nowProvider());
            lock (_sync)
            {
                _current = session;
                SaveSession(session);
                return _current;
            }
        }

        public OperatorSession SnapshotFor(DateTime timestamp)
        {
            lock (_sync)
            {
                OperatorSession current = EnsureCurrentSessionLocked();
                return new OperatorSession
                {
                    OperatorName = current.OperatorName,
                    Role = current.Role,
                    ShiftName = NormalizeShiftName(current.ShiftName, timestamp),
                    SignedInAt = current.SignedInAt
                };
            }
        }

        public static string ResolveShiftName(DateTime timestamp)
        {
            return ProductionReportExporter.GetShiftName(timestamp);
        }

        private static OperatorSession CreateDefaultSession(DateTimeOffset timestamp)
        {
            return new OperatorSession
            {
                OperatorName = OperatorSession.DefaultOperatorName,
                Role = OperatorSession.DefaultRole,
                ShiftName = ResolveShiftName(timestamp.LocalDateTime),
                SignedInAt = timestamp
            };
        }

        private static string NormalizeOperatorName(string operatorName)
        {
            string normalized = (operatorName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException("操作员不能为空", nameof(operatorName));
            }

            if (normalized.Length > 64)
            {
                throw new ArgumentException("操作员名称不能超过 64 个字符", nameof(operatorName));
            }

            return normalized;
        }

        private static string NormalizeRole(string? role)
        {
            string normalized = OperatorPermissionService.NormalizeRole(role);
            return normalized.Length <= 32 ? normalized : normalized[..32];
        }

        private static string NormalizeShiftName(string? shiftName, DateTime timestamp)
        {
            string normalized = shiftName?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return ResolveShiftName(timestamp);
            }

            return normalized.Length <= 32 ? normalized : normalized[..32];
        }

        private static TimeSpan NormalizeSessionMaxAge(TimeSpan? sessionMaxAge)
        {
            TimeSpan value = sessionMaxAge ?? DefaultSessionMaxAge;
            if (value < TimeSpan.FromHours(1))
            {
                return TimeSpan.FromHours(1);
            }

            if (value > TimeSpan.FromHours(72))
            {
                return TimeSpan.FromHours(72);
            }

            return value;
        }

        private bool IsSessionExpired(OperatorSession session, DateTimeOffset now)
        {
            if (!session.IsSignedIn)
            {
                return false;
            }

            if (session.SignedInAt == default)
            {
                return true;
            }

            TimeSpan age = now - session.SignedInAt;
            return age > _sessionMaxAge || age < -FutureClockTolerance;
        }

        private OperatorSession EnsureCurrentSessionLocked()
        {
            DateTimeOffset now = _nowProvider();
            if (!IsSessionExpired(_current, now))
            {
                return _current;
            }

            _current = CreateDefaultSession(now);
            TrySaveSession(_current);
            return _current;
        }

        private OperatorSession? LoadSession()
        {
            try
            {
                if (!File.Exists(_sessionPath))
                {
                    return null;
                }

                string json = File.ReadAllText(_sessionPath);
                OperatorSession? session = JsonSerializer.Deserialize<OperatorSession>(json);
                if (session == null || string.IsNullOrWhiteSpace(session.OperatorName))
                {
                    return null;
                }

                return new OperatorSession
                {
                    OperatorName = session.OperatorName.Trim(),
                    Role = NormalizeRole(session.Role),
                    ShiftName = NormalizeShiftName(session.ShiftName, _nowProvider().LocalDateTime),
                    SignedInAt = session.SignedInAt == default ? _nowProvider() : session.SignedInAt
                };
            }
            catch
            {
                return null;
            }
        }

        private void SaveSession(OperatorSession session)
        {
            string json = JsonSerializer.Serialize(session, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_sessionPath, json);
        }

        private void TrySaveSession(OperatorSession session)
        {
            try
            {
                SaveSession(session);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OperatorSession] 保存会话状态失败: {ex.Message}");
            }
        }
    }
}
