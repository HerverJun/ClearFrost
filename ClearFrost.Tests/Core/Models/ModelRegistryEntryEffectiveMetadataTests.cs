using System.Text.Json;
using ClearFrost.Core.Models;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Models;

public class ModelRegistryEntryEffectiveMetadataTests
{
    [Fact]
    public void EffectiveMetadata_EmptyEntryFields_FallsBackToManifest()
    {
        var entry = new ModelRegistryEntry
        {
            Labels = new[] { " " },
            PostprocessOptions = new Dictionary<string, string>(),
            Manifest = new ModelPackageManifest
            {
                Labels = new List<string> { "OK", "NG" },
                InputWidth = 640,
                InputHeight = 320,
                TaskType = "Detect",
                PostprocessorKey = "generic-detection",
                ScoreNormalization = "sigmoid",
                PostprocessOptions = new Dictionary<string, string>
                {
                    ["apply_nms"] = "true"
                }
            }
        };

        entry.GetEffectiveLabels().Should().Equal("OK", "NG");
        entry.GetEffectiveInputWidth().Should().Be(640);
        entry.GetEffectiveInputHeight().Should().Be(320);
        entry.GetEffectiveTaskType().Should().Be("Detect");
        entry.GetEffectivePostprocessorKey().Should().Be("generic-detection");
        entry.GetEffectiveScoreNormalization().Should().Be("sigmoid");
        entry.GetEffectivePostprocessOptions().Should().ContainSingle();
        entry.GetEffectivePostprocessOptions().Should().ContainKey("apply_nms").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void EffectiveMetadata_ExplicitEntryFields_TakePrecedenceOverManifest()
    {
        var entry = new ModelRegistryEntry
        {
            Labels = new[] { "Entry" },
            InputWidth = 128,
            InputHeight = 256,
            TaskType = "Classification",
            PostprocessorKey = "classification",
            ScoreNormalization = "softmax",
            PostprocessOptions = new Dictionary<string, string>
            {
                ["top_k"] = "3"
            },
            Manifest = new ModelPackageManifest
            {
                Labels = new List<string> { "Manifest" },
                InputWidth = 640,
                InputHeight = 320,
                TaskType = "Detect",
                PostprocessorKey = "generic-detection",
                ScoreNormalization = "sigmoid",
                PostprocessOptions = new Dictionary<string, string>
                {
                    ["apply_nms"] = "true"
                }
            }
        };

        entry.GetEffectiveLabels().Should().Equal("Entry");
        entry.GetEffectiveInputWidth().Should().Be(128);
        entry.GetEffectiveInputHeight().Should().Be(256);
        entry.GetEffectiveTaskType().Should().Be("Classification");
        entry.GetEffectivePostprocessorKey().Should().Be("classification");
        entry.GetEffectiveScoreNormalization().Should().Be("softmax");
        entry.GetEffectivePostprocessOptions().Should().ContainSingle();
        entry.GetEffectivePostprocessOptions().Should().ContainKey("top_k").WhoseValue.Should().Be("3");
    }

    [Fact]
    public void EffectiveMetadata_Methods_DoNotAddSerializedFields()
    {
        var entry = new ModelRegistryEntry
        {
            Manifest = new ModelPackageManifest
            {
                TaskType = "Detect"
            }
        };

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(entry));
        JsonElement root = document.RootElement;

        root.TryGetProperty("EffectiveTaskType", out _).Should().BeFalse();
        root.TryGetProperty("EffectivePostprocessorKey", out _).Should().BeFalse();
        root.TryGetProperty("EffectiveScoreNormalization", out _).Should().BeFalse();
        root.TryGetProperty("EffectiveLabels", out _).Should().BeFalse();
        root.TryGetProperty("EffectiveInputWidth", out _).Should().BeFalse();
        root.TryGetProperty("EffectiveInputHeight", out _).Should().BeFalse();
        root.TryGetProperty("EffectivePostprocessOptions", out _).Should().BeFalse();
        root.TryGetProperty("GetEffectiveTaskType", out _).Should().BeFalse();
    }
}
