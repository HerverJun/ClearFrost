using Xunit;

namespace ClearFrost.Tests.Yolo;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OnnxRuntimeCollection
{
    public const string Name = "OnnxRuntime";
}
