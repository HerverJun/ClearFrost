// ============================================================================
// 文件名: InspectionIdGenerator.cs
// 描述:   检测追溯 ID 生成器
// ============================================================================

using System;
using System.Linq;

namespace ClearFrost.Core.Inspection
{
    /// <summary>
    /// 生成适合日志、数据库索引和文件名的检测追溯 ID。
    /// </summary>
    public static class InspectionIdGenerator
    {
        private static readonly object SyncRoot = new object();
        private static long _lastUnixMillisecond;
        private static int _sequence;

        public static string Next(string triggerSource, DateTimeOffset? now = null)
        {
            DateTimeOffset timestamp = now ?? DateTimeOffset.Now;
            long currentMs = timestamp.ToUnixTimeMilliseconds();
            int sequence;

            lock (SyncRoot)
            {
                if (currentMs == _lastUnixMillisecond)
                {
                    sequence = ++_sequence;
                }
                else
                {
                    _lastUnixMillisecond = currentMs;
                    _sequence = 1;
                    sequence = _sequence;
                }
            }

            string source = NormalizeTriggerSource(triggerSource);
            return $"CF-{timestamp:yyyyMMdd-HHmmssfff}-{source}-{sequence:000000}";
        }

        private static string NormalizeTriggerSource(string triggerSource)
        {
            if (string.IsNullOrWhiteSpace(triggerSource))
            {
                return "UNKNOWN";
            }

            if (triggerSource.Contains("PLC", StringComparison.OrdinalIgnoreCase))
            {
                return "PLC";
            }

            if (triggerSource.Contains("手动", StringComparison.OrdinalIgnoreCase)
                || triggerSource.Contains("MANUAL", StringComparison.OrdinalIgnoreCase))
            {
                return "MANUAL";
            }

            string source = new string(triggerSource
                .Trim()
                .ToUpperInvariant()
                .Select(ch => IsAsciiLetterOrDigit(ch) ? ch : '-')
                .ToArray());

            source = string.Join("-", source.Split('-', StringSplitOptions.RemoveEmptyEntries));
            return string.IsNullOrWhiteSpace(source) ? "UNKNOWN" : source;
        }

        private static bool IsAsciiLetterOrDigit(char ch)
        {
            return ch is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9';
        }
    }
}
