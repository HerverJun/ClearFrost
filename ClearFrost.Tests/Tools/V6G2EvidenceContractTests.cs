using FluentAssertions;

namespace ClearFrost.Tests.Tools;

public sealed class V6G2EvidenceContractTests
{
    [Fact]
    public void SoakHost_故障证据使用真实生产图路径并允许明确终态()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "tools", "ClearFrost.V6SoakHost", "Program.cs"));
        string runtime = File.ReadAllText(Path.Combine(root, "tools", "ClearFrost.V6SoakHost", "SoakRuntime.cs"));

        source.Should().Contain("faultPlan.TryFailImage(path)");
        source.Should().Contain("image-save-failure");
        source.Should().Contain("queue-pressure");
        string benchmark = File.ReadAllText(Path.Combine(root, "ClearFrost", "Yolo", "YoloBenchmarkProbe.cs"));
        benchmark.Should().Contain("禁止使用 synthetic");
        benchmark.Should().NotContain("new Mat(height, width, MatType.CV_8UC3");
        runtime.Should().Contain("|| explicitFailure;");
        runtime.Should().Contain("RecoveryStatus = recoveryStatus");
    }

    [Fact]
    public void G2EvidenceValidator_保持FailClosed状态和外部输入边界()
    {
        string root = FindRepositoryRoot();
        string validator = File.ReadAllText(Path.Combine(root, "tools", "validate_v6_g2_evidence.ps1"));

        validator.Should().Contain("v6-g2-evidence-validation-1.0");
        validator.Should().Contain("v6-g2-inputs-1.0");
        validator.Should().Contain("v6-g2-model-matrix-1.0");
        validator.Should().Contain("v6-g2-release-lab-1.0");
        validator.Should().Contain("v6-g2-isolated-lab-1.0");
        validator.Should().Contain("v6-g2-soak-1.0");
        validator.Should().Contain("$status -eq \"BLOCKED\"");
        validator.Should().Contain("$status -eq \"NOT_VERIFIED\"");
        validator.Should().Contain("exit 2");
        validator.Should().Contain("release lab must not create a tag or GitHub release");
    }

    [Fact]
    public void V6G2SoakHost_不进入主发布包()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "tools", "ClearFrost.V6SoakHost", "ClearFrost.V6SoakHost.csproj"));
        string publishScript = File.ReadAllText(Path.Combine(root, "tools", "publish_v6_release_lab.ps1"));

        project.Should().Contain("ClearFrost.V6SoakHost");
        publishScript.Should().Contain("Remove-UnlistedExternalFiles");
        publishScript.Should().Contain("MockCamera|SimStress|ClearFrost.Tests|Stub");
        publishScript.Should().Contain("External Detect model fileName is unsafe");
        publishScript.Should().NotContain("ClearFrost.V6SoakHost.dll");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
