using System;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// PLC 字符串读取结果。
    /// </summary>
    public sealed class PlcStringReadResult
    {
        public bool Success { get; init; }

        public string Text { get; init; } = string.Empty;

        public byte[] RawBytes { get; init; } = Array.Empty<byte>();

        public string ErrorMessage { get; init; } = string.Empty;

        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Text) ||
            string.Equals(Text.Trim(), "null", StringComparison.OrdinalIgnoreCase);

        public static PlcStringReadResult Failed(string errorMessage)
        {
            return new PlcStringReadResult
            {
                Success = false,
                ErrorMessage = errorMessage ?? string.Empty
            };
        }
    }
}
