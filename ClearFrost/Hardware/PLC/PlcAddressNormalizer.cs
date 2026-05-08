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
        private static readonly Regex MitsubishiDecimalWordRegex = new Regex(@"^(D|M|S|T|C|R)(\d+)$", RegexOptions.Compiled);
        private static readonly Regex MitsubishiHexWordRegex = new Regex(@"^(X|Y)([0-9A-F]+)$", RegexOptions.Compiled);
        private static readonly Regex MitsubishiBitAddressRegex = new Regex(@"^(?:D|M|S|T|C|R|X|Y)[0-9A-F]+\.\d+$", RegexOptions.Compiled);
        private static readonly Regex SiemensDbByteRegex = new Regex(@"^DB(\d+)\.(\d+)$", RegexOptions.Compiled);
        private static readonly Regex SiemensDbTypedByteRegex = new Regex(@"^DB(\d+)\.DB[BWD](\d+)$", RegexOptions.Compiled);
        private static readonly Regex SiemensWordAreaRegex = new Regex(@"^(M|I|Q|AI|AQ)(\d+)$", RegexOptions.Compiled);
        private static readonly Regex SiemensBitAddressRegex = new Regex(@"^(?:[MIQ]\d+\.\d+|DB\d+\.(?:\d+|DBX\d+)\.\d+)$", RegexOptions.Compiled);
        private static readonly Regex OmronWordRegex = new Regex(@"^(D|CIO|C|W|H|A)(\d+)$", RegexOptions.Compiled);
        private static readonly Regex OmronBitAddressRegex = new Regex(@"^(?:D|CIO|C|W|H|A)\d+\.\d+$", RegexOptions.Compiled);

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

        public static bool IsSupportedByDriver(
            string normalizedAddress,
            PlcProtocolType protocolType,
            string? driverProvider,
            out string? error)
        {
            error = null;
            if (!string.Equals(driverProvider, "McpX", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (protocolType is not (PlcProtocolType.Mitsubishi_MC_ASCII or PlcProtocolType.Mitsubishi_MC_Binary))
            {
                error = "McpX 仅支持三菱协议";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(normalizedAddress) &&
                normalizedAddress.Trim().StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            error = "McpX 当前业务适配器仅支持三菱 D 区 Int16/字节地址，例如 D100";
            return false;
        }

        public static void EnsureDriverSupportsAddress(
            string normalizedAddress,
            PlcProtocolType protocolType,
            string? driverProvider)
        {
            if (!IsSupportedByDriver(normalizedAddress, protocolType, driverProvider, out string? error))
            {
                throw new ArgumentException(error ?? "PLC 驱动不支持该地址", nameof(normalizedAddress));
            }
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
            if (MitsubishiBitAddressRegex.IsMatch(upper))
            {
                return Fail(
                    "三菱当前信号读写使用 Int16/字地址，不支持位地址；请使用 D100、M100、X10 或 Y10",
                    out normalized,
                    out error);
            }

            Match match = MitsubishiDecimalWordRegex.Match(upper);
            if (match.Success)
            {
                normalized = $"{match.Groups[1].Value}{match.Groups[2].Value}";
                error = null;
                return true;
            }

            match = MitsubishiHexWordRegex.Match(upper);
            if (match.Success)
            {
                normalized = $"{match.Groups[1].Value}{match.Groups[2].Value}";
                error = null;
                return true;
            }

            return Fail(
                "三菱地址仅支持 D/M/X/Y/S/T/C/R 字地址，例如 D100、M100、X10、Y10",
                out normalized,
                out error);
        }

        private static bool TryNormalizeSiemens(string compact, out string normalized, out string? error)
        {
            string upper = compact.ToUpperInvariant();
            if (SiemensBitAddressRegex.IsMatch(upper))
            {
                return Fail(
                    "西门子当前信号读写使用 Int16/字节地址，不支持位地址；请使用 DB100.0、DB100.DBW0、M0、I0 或 Q0",
                    out normalized,
                    out error);
            }

            Match match = SiemensDbByteRegex.Match(upper);
            if (match.Success)
            {
                return TryNormalizeSiemensDbByteMatch(match, out normalized, out error);
            }

            match = SiemensDbTypedByteRegex.Match(upper);
            if (match.Success)
            {
                return TryNormalizeSiemensDbByteMatch(match, out normalized, out error);
            }

            match = SiemensWordAreaRegex.Match(upper);
            if (match.Success)
            {
                normalized = $"{match.Groups[1].Value}{match.Groups[2].Value}";
                error = null;
                return true;
            }

            return Fail(
                "西门子地址仅支持 DB / M / I / Q 字节地址，例如 DB100.0、DB100.DBW0、M0、I0、Q0",
                out normalized,
                out error);
        }

        private static bool TryNormalizeSiemensDbByteMatch(Match match, out string normalized, out string? error)
        {
            if (!int.TryParse(match.Groups[1].Value, out int dbNumber) || dbNumber < 1)
            {
                return Fail("西门子 DB 块号必须大于等于 1", out normalized, out error);
            }

            if (!int.TryParse(match.Groups[2].Value, out int byteOffset) || byteOffset < 0)
            {
                return Fail("西门子 DB 字节偏移无效", out normalized, out error);
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
            if (OmronBitAddressRegex.IsMatch(upper))
            {
                return Fail(
                    "欧姆龙当前信号读写使用 Int16/字地址，不支持位地址；请使用 D100、C100、W100、H100 或 A100",
                    out normalized,
                    out error);
            }

            Match match = OmronWordRegex.Match(upper);
            if (match.Success)
            {
                string area = match.Groups[1].Value == "CIO" ? "C" : match.Groups[1].Value;
                normalized = $"{area}{match.Groups[2].Value}";
                error = null;
                return true;
            }

            return Fail(
                "欧姆龙地址仅支持 D/CIO(C)/W/H/A 字地址，例如 D100、CIO100、W100、H100",
                out normalized,
                out error);
        }

        private static string Compact(string? rawAddress)
        {
            return Regex.Replace((rawAddress ?? string.Empty).Trim(), @"\s+", string.Empty);
        }

        private static bool Fail(string message, out string normalized, out string? error)
        {
            normalized = string.Empty;
            error = message;
            return false;
        }
    }
}
