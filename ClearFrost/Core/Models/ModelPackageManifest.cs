using System;
using System.Collections.Generic;

namespace ClearFrost.Core.Models
{
    public sealed class ModelPackageManifest
    {
        public string ModelId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = "model.onnx";
        public string ModelHash { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public List<string> Labels { get; set; } = new List<string>();
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public string Description { get; set; } = string.Empty;

        public string EffectiveHash =>
            !string.IsNullOrWhiteSpace(ModelHash) ? ModelHash.Trim() : Sha256.Trim();
    }
}
