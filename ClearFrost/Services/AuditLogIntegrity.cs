// ============================================================================
// 文件名: AuditLogIntegrity.cs
// 描述:   审计日志链式完整性摘要
// ============================================================================

using System;
using System.Security.Cryptography;
using System.Text;

namespace ClearFrost.Services
{
    public static class AuditLogIntegrity
    {
        public const string GenesisHash = "GENESIS";
        public const string LegacyStatus = "Legacy";
        public const string ValidStatus = "Valid";
        public const string TamperedStatus = "Tampered";
        public const string Header = "时间\t结果\t类别\t操作\t详情\tPrevHash\tHash";

        public static string ComputeHash(
            string timestamp,
            string status,
            string category,
            string action,
            string detail,
            string previousHash)
        {
            string canonical = string.Join('\t',
                timestamp ?? string.Empty,
                status ?? string.Empty,
                category ?? string.Empty,
                action ?? string.Empty,
                detail ?? string.Empty,
                previousHash ?? string.Empty);
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static bool IsSha256Hash(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }

            foreach (char c in value)
            {
                bool isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
