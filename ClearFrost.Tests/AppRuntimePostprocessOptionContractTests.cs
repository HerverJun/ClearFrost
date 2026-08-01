using System.Reflection;
using ClearFrost.Core.Models;
using FluentAssertions;

namespace ClearFrost.Tests;

public class AppRuntimePostprocessOptionContractTests
{
    [Fact]
    public void DictionaryMatches_DuplicateCasePostprocessKeys_ComparesEffectiveOptions()
    {
        var manifestOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" top_k "] = "2",
            ["TOP_K"] = "ignored",
            [" "] = "ignored"
        };
        var registryOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["top_k"] = "2"
        };

        InvokeDictionaryMatches(manifestOptions, registryOptions).Should().BeTrue();
    }

    [Fact]
    public void DictionaryMatches_NormalizedPostprocessValueMismatch_ReturnsFalse()
    {
        var manifestOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" top_k "] = "2"
        };
        var registryOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["top_k"] = "3"
        };

        InvokeDictionaryMatches(manifestOptions, registryOptions).Should().BeFalse();
    }

    [Fact]
    public void DescribeModelContractMismatch_IncludesActionableFieldDifferences()
    {
        var entry = new ModelRegistryEntry
        {
            InputWidth = 640,
            InputHeight = 640,
            TaskType = "Detect",
            PostprocessOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["top_k"] = "3"
            },
            Labels = new[] { "OK", "Bad" }
        };
        var manifest = new ModelPackageManifest
        {
            InputWidth = 320,
            InputHeight = 640,
            TaskType = "Detect",
            PostprocessOptions = new Dictionary<string, string>
            {
                [" top_k "] = "2"
            },
            Labels = new List<string> { "OK", "NG" }
        };

        string message = InvokeDescribeModelContractMismatch(entry, manifest);

        message.Should().Contain("InputSize manifest=320x640, registry=640x640");
        message.Should().Contain("PostprocessOptions[top_k] manifest=2, registry=3");
        message.Should().Contain("Labels[1] manifest=NG, registry=Bad");
    }

    [Fact]
    public void ModelContractMatchesRegistry_EmptyEntryPostprocessMetadata_UsesManifestFallback()
    {
        var manifest = new ModelPackageManifest
        {
            InputWidth = 640,
            InputHeight = 320,
            TaskType = "Classification",
            PostprocessorKey = "classification",
            ScoreNormalization = "softmax",
            PostprocessOptions = new Dictionary<string, string>
            {
                ["top_k"] = "1"
            },
            Labels = new List<string> { "OK", "NG" }
        };
        var entry = new ModelRegistryEntry
        {
            Manifest = new ModelPackageManifest
            {
                InputWidth = 640,
                InputHeight = 320,
                TaskType = "Classification",
                PostprocessorKey = "classification",
                ScoreNormalization = "softmax",
                PostprocessOptions = new Dictionary<string, string>
                {
                    [" top_k "] = "1"
                },
                Labels = new List<string> { "OK", "NG" }
            }
        };

        InvokeModelContractMatchesRegistry(entry, manifest).Should().BeTrue();
        InvokeDescribeModelContractMismatch(entry, manifest).Should().Be("unknown contract field");
    }

    private static bool InvokeModelContractMatchesRegistry(ModelRegistryEntry entry, ModelPackageManifest manifest)
    {
        MethodInfo method = typeof(global::ClearFrost.AppRuntime).GetMethod(
            "ModelContractMatchesRegistry",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(global::ClearFrost.AppRuntime).FullName, "ModelContractMatchesRegistry");

        return (bool)method.Invoke(null, new object[] { entry, manifest })!;
    }

    private static bool InvokeDictionaryMatches(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        MethodInfo method = typeof(global::ClearFrost.AppRuntime).GetMethod(
            "DictionaryMatches",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(global::ClearFrost.AppRuntime).FullName, "DictionaryMatches");

        return (bool)method.Invoke(null, new object[] { left, right })!;
    }

    private static string InvokeDescribeModelContractMismatch(ModelRegistryEntry entry, ModelPackageManifest manifest)
    {
        MethodInfo method = typeof(global::ClearFrost.AppRuntime).GetMethod(
            "DescribeModelContractMismatch",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(global::ClearFrost.AppRuntime).FullName, "DescribeModelContractMismatch");

        return (string)method.Invoke(null, new object[] { entry, manifest })!;
    }
}
