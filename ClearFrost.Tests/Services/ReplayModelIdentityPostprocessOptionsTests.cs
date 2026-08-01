using System.Reflection;
using ClearFrost.Core.Models;
using ClearFrost.Services.Replay;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class ReplayModelIdentityPostprocessOptionsTests
{
    [Fact]
    public void FromRegistryEntry_DuplicateCasePostprocessKeys_KeepsFirstValidOption()
    {
        var entry = new ModelRegistryEntry
        {
            ModelId = "candidate",
            Version = "1",
            ModelHash = new string('a', 64),
            PostprocessOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" top_k "] = "2",
                ["TOP_K"] = "3",
                [" "] = "ignored"
            }
        };

        ReplayModelIdentity identity = ReplayModelIdentity.FromRegistryEntry(entry);

        identity.PostprocessOptions.Should().ContainSingle();
        identity.PostprocessOptions.Should().ContainKey("top_k").WhoseValue.Should().Be("2");
    }

    [Fact]
    public void FromRegistryEntry_EmptyEntryPostprocessOptions_FallsBackToManifestOptions()
    {
        var entry = new ModelRegistryEntry
        {
            ModelId = "candidate",
            Version = "1",
            ModelHash = new string('a', 64),
            Manifest = new ModelPackageManifest
            {
                PostprocessOptions = new Dictionary<string, string>
                {
                    ["apply_nms"] = "true"
                }
            }
        };

        ReplayModelIdentity identity = ReplayModelIdentity.FromRegistryEntry(entry);

        identity.PostprocessOptions.Should().ContainSingle();
        identity.PostprocessOptions.Should().ContainKey("apply_nms").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void FromRegistryEntry_EmptyEntryPostprocessMetadata_FallsBackToManifestMetadata()
    {
        var entry = new ModelRegistryEntry
        {
            ModelId = "candidate",
            Version = "1",
            ModelHash = new string('a', 64),
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

        ReplayModelIdentity identity = ReplayModelIdentity.FromRegistryEntry(entry);

        identity.Labels.Should().Equal("OK", "NG");
        identity.InputWidth.Should().Be(640);
        identity.InputHeight.Should().Be(320);
        identity.TaskType.Should().Be("Detect");
        identity.PostprocessorKey.Should().Be("generic-detection");
        identity.ScoreNormalization.Should().Be("sigmoid");
        identity.PostprocessOptions.Should().ContainSingle();
        identity.PostprocessOptions.Should().ContainKey("apply_nms").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void DatasetManifestSanitize_DuplicateCasePostprocessKeys_KeepsFirstValidOption()
    {
        var model = new ReplayModelIdentity
        {
            ModelId = "candidate",
            Version = "1",
            Sha256 = new string('b', 64),
            PostprocessOptions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [" apply_nms "] = "true",
                ["APPLY_NMS"] = "false",
                [" "] = "ignored"
            }
        };

        ReplayModelIdentity sanitized = InvokeSanitizeModelForManifest(model);

        sanitized.PostprocessOptions.Should().ContainSingle();
        sanitized.PostprocessOptions.Should().ContainKey("apply_nms").WhoseValue.Should().Be("true");
    }

    [Fact]
    public void ReplayApprovalDictionaryMatches_DuplicateCasePostprocessKeys_ComparesEffectiveOptions()
    {
        var manifestOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" apply_nms "] = "true",
            ["APPLY_NMS"] = "ignored",
            [" "] = "ignored"
        };
        var registryOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apply_nms"] = "true"
        };

        InvokeApprovalDictionaryMatches(manifestOptions, registryOptions).Should().BeTrue();
    }

    [Fact]
    public void ReplayApprovalDictionaryMatches_NormalizedPostprocessValueMismatch_ReturnsFalse()
    {
        var manifestOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["apply_nms"] = "true"
        };
        var registryOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["apply_nms"] = "false"
        };

        InvokeApprovalDictionaryMatches(manifestOptions, registryOptions).Should().BeFalse();
    }

    [Fact]
    public void ReplayApprovalDescribeModelContractMismatch_IncludesActionableFieldDifferences()
    {
        var entry = new ModelRegistryEntry
        {
            InputWidth = 640,
            InputHeight = 640,
            TaskType = "Detect",
            PostprocessorKey = "generic-detection",
            PostprocessOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["apply_nms"] = "false"
            },
            Labels = new[] { "OK" }
        };
        var manifest = new ModelPackageManifest
        {
            InputWidth = 640,
            InputHeight = 320,
            TaskType = "Classification",
            PostprocessorKey = "classification",
            PostprocessOptions = new Dictionary<string, string>
            {
                [" apply_nms "] = "true"
            },
            Labels = new List<string> { "OK", "NG" }
        };

        string message = InvokeApprovalDescribeModelContractMismatch(entry, manifest);

        message.Should().Contain("InputSize manifest=640x320, registry=640x640");
        message.Should().Contain("TaskType manifest=Classification, registry=Detect");
        message.Should().Contain("PostprocessorKey manifest=classification, registry=generic-detection");
        message.Should().Contain("PostprocessOptions[apply_nms] manifest=true, registry=false");
        message.Should().Contain("Labels count manifest=2, registry=1");
    }

    [Fact]
    public void ReplayApprovalModelContractMatchesRegistry_EmptyEntryPostprocessMetadata_UsesManifestFallback()
    {
        var manifest = new ModelPackageManifest
        {
            InputWidth = 640,
            InputHeight = 320,
            TaskType = "Detect",
            PostprocessorKey = "generic-detection",
            ScoreNormalization = "sigmoid",
            PostprocessOptions = new Dictionary<string, string>
            {
                ["apply_nms"] = "true"
            },
            Labels = new List<string> { "OK" }
        };
        var entry = new ModelRegistryEntry
        {
            Manifest = new ModelPackageManifest
            {
                InputWidth = 640,
                InputHeight = 320,
                TaskType = "Detect",
                PostprocessorKey = "generic-detection",
                ScoreNormalization = "sigmoid",
                PostprocessOptions = new Dictionary<string, string>
                {
                    [" apply_nms "] = "true"
                },
                Labels = new List<string> { "OK" }
            }
        };

        InvokeApprovalModelContractMatchesRegistry(entry, manifest).Should().BeTrue();
        InvokeApprovalDescribeModelContractMismatch(entry, manifest).Should().Be("unknown contract field");
    }

    private static ReplayModelIdentity InvokeSanitizeModelForManifest(ReplayModelIdentity model)
    {
        MethodInfo method = typeof(FileReplayDatasetStore).GetMethod(
            "SanitizeModelForManifest",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(FileReplayDatasetStore).FullName, "SanitizeModelForManifest");

        return (ReplayModelIdentity)method.Invoke(null, new object[] { model })!;
    }

    private static bool InvokeApprovalDictionaryMatches(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        MethodInfo method = typeof(ReplayApprovalApplicationService).GetMethod(
            "DictionaryMatches",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ReplayApprovalApplicationService).FullName, "DictionaryMatches");

        return (bool)method.Invoke(null, new object[] { left, right })!;
    }

    private static bool InvokeApprovalModelContractMatchesRegistry(ModelRegistryEntry entry, ModelPackageManifest manifest)
    {
        MethodInfo method = typeof(ReplayApprovalApplicationService).GetMethod(
            "ModelContractMatchesRegistry",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ReplayApprovalApplicationService).FullName, "ModelContractMatchesRegistry");

        return (bool)method.Invoke(null, new object[] { entry, manifest })!;
    }

    private static string InvokeApprovalDescribeModelContractMismatch(ModelRegistryEntry entry, ModelPackageManifest manifest)
    {
        MethodInfo method = typeof(ReplayApprovalApplicationService).GetMethod(
            "DescribeModelContractMismatch",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ReplayApprovalApplicationService).FullName, "DescribeModelContractMismatch");

        return (string)method.Invoke(null, new object[] { entry, manifest })!;
    }
}
