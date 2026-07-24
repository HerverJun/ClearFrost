using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;

namespace ClearFrost.Core.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ProductionModelReferenceType
    {
        None,
        ApprovedPackage,
        LegacyOnnx
    }

    /// <summary>
    /// Stable persisted identity for a production model slot. Paths are resolved from ModelRegistry at runtime.
    /// </summary>
    public sealed class ProductionModelReference
    {
        public ProductionModelReferenceType Type { get; set; } = ProductionModelReferenceType.None;
        public string ModelId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string LegacyFileName { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsEmpty => Type == ProductionModelReferenceType.None ||
            (Type == ProductionModelReferenceType.ApprovedPackage &&
             string.IsNullOrWhiteSpace(ModelId) &&
             string.IsNullOrWhiteSpace(Version) &&
             string.IsNullOrWhiteSpace(Sha256)) ||
            (Type == ProductionModelReferenceType.LegacyOnnx &&
             string.IsNullOrWhiteSpace(LegacyFileName));

        public ProductionModelReference Clone()
        {
            return new ProductionModelReference
            {
                Type = Type,
                ModelId = ModelId ?? string.Empty,
                Version = Version ?? string.Empty,
                Sha256 = Sha256 ?? string.Empty,
                LegacyFileName = LegacyFileName ?? string.Empty
            };
        }

        public static ProductionModelReference Empty() => new ProductionModelReference();

        public static ProductionModelReference FromApprovedPackage(ModelRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            return new ProductionModelReference
            {
                Type = ProductionModelReferenceType.ApprovedPackage,
                ModelId = entry.ModelId ?? string.Empty,
                Version = entry.Version ?? string.Empty,
                Sha256 = entry.ModelHash ?? string.Empty,
                LegacyFileName = string.Empty
            };
        }

        public static ProductionModelReference FromLegacyOnnx(string fileName, string sha256 = "")
        {
            string normalized = Path.GetFileName(fileName?.Trim() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !normalized.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".onnx";
            }

            return new ProductionModelReference
            {
                Type = string.IsNullOrWhiteSpace(normalized)
                    ? ProductionModelReferenceType.None
                    : ProductionModelReferenceType.LegacyOnnx,
                LegacyFileName = normalized,
                Sha256 = sha256 ?? string.Empty
            };
        }

        public bool IdentityEquals(ProductionModelReference? other)
        {
            if (other == null)
            {
                return IsEmpty;
            }

            if (Type != other.Type)
            {
                return false;
            }

            return Type switch
            {
                ProductionModelReferenceType.ApprovedPackage =>
                    string.Equals(ModelId, other.ModelId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Version, other.Version, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase),
                ProductionModelReferenceType.LegacyOnnx =>
                    string.Equals(LegacyFileName, other.LegacyFileName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(Sha256) ||
                     string.IsNullOrWhiteSpace(other.Sha256) ||
                     string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase)),
                _ => other.IsEmpty
            };
        }

        public string ToSelectionValue()
        {
            return Type switch
            {
                ProductionModelReferenceType.ApprovedPackage =>
                    $"approved:{Encode(ModelId)}:{Encode(Version)}:{NormalizeSha(Sha256)}",
                ProductionModelReferenceType.LegacyOnnx =>
                    $"legacy:{Encode(LegacyFileName)}:{NormalizeSha(Sha256)}",
                _ => string.Empty
            };
        }

        public static bool TryParseSelectionValue(string? value, out ProductionModelReference reference)
        {
            reference = Empty();
            string raw = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            string[] parts = raw.Split(':');
            if (parts.Length == 4 &&
                string.Equals(parts[0], "approved", StringComparison.OrdinalIgnoreCase))
            {
                reference = new ProductionModelReference
                {
                    Type = ProductionModelReferenceType.ApprovedPackage,
                    ModelId = Decode(parts[1]),
                    Version = Decode(parts[2]),
                    Sha256 = parts[3]
                };
                return !string.IsNullOrWhiteSpace(reference.ModelId) &&
                       !string.IsNullOrWhiteSpace(reference.Version) &&
                       !string.IsNullOrWhiteSpace(reference.Sha256);
            }

            if (parts.Length == 3 &&
                string.Equals(parts[0], "legacy", StringComparison.OrdinalIgnoreCase))
            {
                reference = FromLegacyOnnx(Decode(parts[1]), parts[2]);
                return !reference.IsEmpty;
            }

            return false;
        }

        public override string ToString()
        {
            return Type switch
            {
                ProductionModelReferenceType.ApprovedPackage => $"{ModelId}@{Version}#{ShortSha(Sha256)}",
                ProductionModelReferenceType.LegacyOnnx => $"{LegacyFileName}#{ShortSha(Sha256)}",
                _ => string.Empty
            };
        }

        private static string NormalizeSha(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

        private static string ShortSha(string? value)
        {
            string sha = NormalizeSha(value);
            return sha.Length <= 12 ? sha : sha[..12];
        }

        private static string Encode(string? value)
        {
            string base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
            return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static string Decode(string value)
        {
            string padded = (value ?? string.Empty).Replace('-', '+').Replace('_', '/');
            int padding = (4 - padded.Length % 4) % 4;
            padded += new string('=', padding);
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public sealed class ProductionModelResolutionResult
    {
        public bool Succeeded { get; init; }
        public string ErrorCode { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public ProductionModelReference Reference { get; init; } = ProductionModelReference.Empty();
        public ModelRegistryEntry? Entry { get; init; }
        public string ModelPath { get; init; } = string.Empty;

        public static ProductionModelResolutionResult Ok(
            ProductionModelReference reference,
            ModelRegistryEntry entry,
            string modelPath)
        {
            return new ProductionModelResolutionResult
            {
                Succeeded = true,
                Reference = reference.Clone(),
                Entry = entry,
                ModelPath = NormalizePath(modelPath)
            };
        }

        public static ProductionModelResolutionResult Fail(
            ProductionModelReference reference,
            string errorCode,
            string message)
        {
            return new ProductionModelResolutionResult
            {
                Succeeded = false,
                Reference = reference.Clone(),
                ErrorCode = errorCode ?? string.Empty,
                Message = message ?? string.Empty
            };
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path ?? string.Empty;
            }
        }
    }

    public sealed class ProductionModelSelectionOption
    {
        public string Value { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public bool IsApprovedPackage { get; init; }
    }
}
