using FluentAssertions;

namespace ClearFrost.Tests.Tools;

public sealed class V6WorkflowContractTests
{
    [Theory]
    [InlineData("ci.yml")]
    [InlineData("v6-promotion.yml")]
    public void ContinueOnError步骤_使用Outcome执行FailClosed判定(string workflowName)
    {
        string workflow = ReadWorkflow(workflowName);
        string gateStep = ExtractStepContaining(workflow, "id: gate");
        string schemaStep = ExtractStepContaining(workflow, "id: evidence-schema");
        string enforcementStep = ExtractStepContaining(workflow, "GATE_OUTCOME:");

        gateStep.Should().Contain("continue-on-error: true");
        schemaStep.Should().Contain("continue-on-error: true");
        enforcementStep.Should().Contain("GATE_OUTCOME: ${{ steps.gate.outcome }}");
        enforcementStep.Should().Contain("EVIDENCE_SCHEMA_OUTCOME: ${{ steps.evidence-schema.outcome }}");
        enforcementStep.Should().Contain("if ($env:GATE_OUTCOME -ne \"success\") {");
        enforcementStep.Should().Contain("if ($env:EVIDENCE_SCHEMA_OUTCOME -ne \"success\") {");
        enforcementStep.Should().Contain("exit 1");
        enforcementStep.Should().NotContain(".conclusion");
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("v6-promotion.yml")]
    public void Evidence上传步骤_失败时仍执行(string workflowName)
    {
        string workflow = ReadWorkflow(workflowName);
        string uploadStep = ExtractStepContaining(workflow, "uses: actions/upload-artifact@v4");

        uploadStep.Should().Contain("if: always()");
        uploadStep.Should().Contain("if-no-files-found: error");
    }

    [Fact]
    public void PromotionWorkflow_明确为无正向授权输入的负向ReadinessGate()
    {
        string workflow = ReadWorkflow("v6-promotion.yml");

        workflow.Should().Contain("Negative readiness gate only");
        workflow.Should().Contain("no authorized positive release inputs or release mutation path exist here");
        workflow.Should().NotContain("workflow_dispatch:\n    inputs:");
    }

    [Fact]
    public void 未执行的正向发布Evidence_保持NotVerified且无成功退出码()
    {
        string root = FindRepositoryRoot();
        string gateScript = Normalize(File.ReadAllText(Path.Combine(root, "tools", "run_v6_gate.ps1")));
        string schemaScript = Normalize(File.ReadAllText(Path.Combine(root, "tools", "validate_v6_evidence.ps1")));
        string liteBlock = ExtractBlock(gateScript, "$litePublishStep = [ordered]@{", "$fullPublishStep = [ordered]@{");
        string fullBlock = ExtractBlock(gateScript, "$fullPublishStep = [ordered]@{", "Add-PromotionBlockingReason");

        foreach (string block in new[] { liteBlock, fullBlock })
        {
            block.Should().Contain("status = \"NOT_VERIFIED\"");
            block.Should().Contain("exitCode = $null");
            block.Should().NotContain("exitCode = 0");
        }

        schemaScript.Should().Contain("[int]$MinimumHermeticTests = 853");
        schemaScript.Should().Contain("NOT_VERIFIED publish evidence must not contain a non-null exitCode");
    }

    private static string ReadWorkflow(string workflowName)
    {
        return Normalize(File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", workflowName)));
    }

    private static string ExtractStepContaining(string workflow, string marker)
    {
        int markerIndex = workflow.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.Should().BeGreaterThanOrEqualTo(0, $"workflow should contain '{marker}'");

        int start = workflow.LastIndexOf("\n      - name:", markerIndex, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"'{marker}' should belong to a named workflow step");
        start++;

        int end = workflow.IndexOf("\n      - name:", markerIndex, StringComparison.Ordinal);
        return end < 0 ? workflow[start..] : workflow[start..end];
    }

    private static string ExtractBlock(string text, string startMarker, string endMarker)
    {
        int start = text.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"script should contain '{startMarker}'");
        int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"script should contain '{endMarker}' after '{startMarker}'");
        return text[start..end];
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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
