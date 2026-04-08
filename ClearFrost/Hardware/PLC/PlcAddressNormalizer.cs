using System;
using System.Text.RegularExpressions;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 地址规范化与校验。
    /// </summary>
    public static class PlcAddressNormalizer
    {
        private static readonly Regex DigitsRegex = new Regex(@"^\d+$", RegexOptions.Compiled);
        private static readonly Regex MitsubishiRegex = new Regex(@"^D(\d+)$", RegexOptions.Compiled);
        private static readonly Regex SiemensDbWordRegex = new Regex(@"^DB(\d+)\.(\d+)$", RegexOptions.Compiled);

        public static string NormalizeOrThrow(string? rawAddress, PlcProtocolType protocolType)
        {
            if (TryNormalize(rawAddress, protocolType, out string normalized, out string? error))
            {
                return normalized;
            }

            throw new ArgumentException(error ?? "PLC 地址格式无效", nameof(rawAddress));
        }

        public static bool TryNormalize(
            string? rawAddress,
            PlcProtocolType protocolType,
            out string normalized,
            out string? error)
        {
            string compact = Compact(rawAddress);
            normalized = string.Empty;
            error = null;

            if (string.IsNullOrWhiteSpace(compact))
            {
                error = "PLC 地址不能为空";
                return false;
            }

            return protocolType switch
            {
                PlcProtocolType.Mitsubishi_MC_ASCII => TryNormalizeMitsubishi(compact, out normalized, out error),
                PlcProtocolType.Mitsubishi_MC_Binary => TryNormalizeMitsubishi(compact, out normalized, out error),
                PlcProtocolType.Siemens_S7 => TryNormalizeSiemens(compact, out normalized, out error),
                PlcProtocolType.Modbus_TCP => TryNormalizeModbus(compact, out normalized, out error),
                PlcProtocolType.Omron_Fins => TryNormalizeOmron(compact, out normalized, out error),
                _ => Fail("不支持的 PLC 协议", out normalized, out error)
            };
        }

        public static string MigrateLegacyAddress(string? rawAddress, PlcProtocolType protocolType, string fallback)
        {
            string compact = Compact(rawAddress);
            if (string.IsNullOrWhiteSpace(compact))
            {
                return fallback;
            }

            if (DigitsRegex.IsMatch(compact))
            {
                return protocolType switch
                {
                    PlcProtocolType.Mitsubishi_MC_ASCII => $"D{compact}",
                    PlcProtocolType.Mitsubishi_MC_Binary => $"D{compact}",
                    PlcProtocolType.Siemens_S7 => $"DB1.{compact}",
                    PlcProtocolType.Modbus_TCP => compact,
                    PlcProtocolType.Omron_Fins => $"D{compact}",
                    _ => compact
                };
            }

            if (TryNormalize(compact, protocolType, out string normalized, out _))
            {
                return normalized;
            }

            return fallback;
        }

        public static string GetProbeAddress(PlcProtocolType protocolType, string? preferredAddress)
        {
            if (TryNormalize(preferredAddress, protocolType, out string normalized, out _))
            {
                return normalized;
            }

            string compact = Compact(preferredAddress).ToUpperInvariant();
            if (protocolType == PlcProtocolType.Siemens_S7 && SiemensDbWordRegex.IsMatch(compact))
            {
                return compact;
            }

            return protocolType switch
            {
                PlcProtocolType.Mitsubishi_MC_ASCII => "D0",
                PlcProtocolType.Mitsubishi_MC_Binary => "D0",
                PlcProtocolType.Siemens_S7 => "DB1.0",
                PlcProtocolType.Modbus_TCP => "0",
                PlcProtocolType.Omron_Fins => "D0",
                _ => "D0"
            };
        }

        private static bool TryNormalizeMitsubishi(string compact, out string normalized, out string? error)
        {
            if (DigitsRegex.IsMatch(compact))
            {
                normalized = $"D{compact}";
                error = null;
                return true;
            }

            string upper = compact.ToUpperInvariant();
            Match match = MitsubishiRegex.Match(upper);
            if (match.Success)
            {
                normalized = $"D{match.Groups[1].Value}";
                error = null;
                return true;
            }

            return Fail("三菱地址仅支持 D 区，例如 555 或 D555", out normalized, out error);
        }

        private static bool TryNormalizeSiemens(string compact, out string normalized, out string? error)
        {
            string upper = compact.ToUpperInvariant();
            Match match = SiemensDbWordRegex.Match(upper);
            if (!match.Success)
            {
                return Fail("西门子首版仅支持 DB 字地址，例如 DB100.0", out normalized, out error);
            }

            if (!int.TryParse(match.Groups[1].Value, out int dbNumber) || dbNumber < 0)
            {
                return Fail("西门子 DB 块号无效", out normalized, out error);
            }

            if (!int.TryParse(match.Groups[2].Value, out int byteOffset) || byteOffset < 0)
            {
                return Fail("西门子字节偏移无效", out normalized, out error);
            }

            normalized = $"DB{dbNumber}.{byteOffset}";
            error = null;
            return true;
        }

        private static bool TryNormalizeModbus(string compact, out string normalized, out string? error)
        {
            if (!DigitsRegex.IsMatch(compact))
            {
                return Fail("Modbus 地址仅支持纯数字寄存器地址，例如 100", out normalized, out error);
            }

            normalized = compact;
            error = null;
            return true;
        }

        private static bool TryNormalizeOmron(string compact, out string normalized, out string? error)
        {
            if (DigitsRegex.IsMatch(compact))
            {
                normalized = $"D{compact}";
                error = null;
                return true;
            }

            string upper = compact.ToUpperInvariant();
            Match match = MitsubishiRegex.Match(upper);
            if (match.Success)
            {
                normalized = $"D{match.Groups[1].Value}";
                error = null;
                return true;
            }

            return Fail("欧姆龙地址仅支持 D 区，例如 100 或 D100", out normalized, out error);
        }

        private static string Compact(string? rawAddress)
        {
            return (rawAddress ?? string.Empty).Trim().Replace(" ", string.Empty);
        }

        private static bool Fail(string message, out string normalized, out string? error)
        {
            normalized = string.Empty;
            error = message;
            return false;
        }
    }
}
