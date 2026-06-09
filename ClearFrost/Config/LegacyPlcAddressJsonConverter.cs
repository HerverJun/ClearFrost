using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClearFrost.Config
{
    /// <summary>
    /// 兼容旧版 PLC 地址数值配置。
    /// </summary>
    public sealed class LegacyPlcAddressJsonConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString() ?? string.Empty;
            }

            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out long longValue))
                {
                    return longValue.ToString();
                }

                if (reader.TryGetDouble(out double doubleValue))
                {
                    return Convert.ToInt64(doubleValue).ToString();
                }
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return string.Empty;
            }

            throw new JsonException("PLC 地址字段必须是字符串或数字");
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value ?? string.Empty);
        }
    }
}
