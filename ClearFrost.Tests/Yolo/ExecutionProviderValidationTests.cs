using ClearFrost.Yolo;
using FluentAssertions;

namespace ClearFrost.Tests.Yolo;

public class ExecutionProviderValidationTests
{
    [Fact]
    public void MatchingProvider_IsPass()
    {
        ExecutionProviderValidationResult result = ExecutionProviderValidation.Validate(
            "DmlExecutionProvider",
            "DmlExecutionProvider");

        result.Status.Should().Be("PASS");
        result.IsSatisfied.Should().BeTrue();
        result.FailureReason.Should().BeEmpty();
    }

    [Fact]
    public void CpuFallback_WhenDirectMlWasRequested_IsBlocked()
    {
        ExecutionProviderValidationResult result = ExecutionProviderValidation.Validate(
            "DmlExecutionProvider",
            "CPUExecutionProvider");

        result.Status.Should().Be("BLOCKED");
        result.IsSatisfied.Should().BeFalse();
        result.FailureReason.Should().Contain("actual provider was 'CPUExecutionProvider'");
    }

    [Fact]
    public void MissingActualProvider_IsBlocked()
    {
        ExecutionProviderValidationResult result = ExecutionProviderValidation.Validate(
            "DmlExecutionProvider",
            null);

        result.Status.Should().Be("BLOCKED");
        result.FailureReason.Should().Contain("no actual provider evidence");
    }
}
