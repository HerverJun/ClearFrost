using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIReplayContractTests
{
    [Fact]
    public void WebUi源码_包含Replay闭环消息Normalize和Render契约()
    {
        string root = FindRepositoryRoot();
        string stateJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "state.js"));
        string renderMainJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.Messages.cs"));

        foreach (string field in new[]
        {
            "candidateNewMissedDetectionCount",
            "candidateFixedMissedDetectionCount",
            "candidateNewFalseRejectCount",
            "candidateFixedFalseRejectCount",
            "changedDecisionCount",
            "approvalAvailable",
            "rejectionReasons"
        })
        {
            stateJs.Should().Contain(field);
            renderMainJs.Should().Contain(field);
        }

        stateJs.Should().Contain("normalizeReplayMessage");
        stateJs.Should().Contain("normalizeManualReviewMessage");
        stateJs.Should().Contain("applyReplayUpdate");
        stateJs.Should().Contain("applyManualReviewUpdate");
        renderMainJs.Should().Contain("renderReplayStatus");
        renderMainJs.Should().Contain("renderManualReviewStatus");
        renderMainJs.Should().Contain("manualReviewRecords");
        renderMainJs.Should().Contain("manualReviewResponse");
        renderMainJs.Should().Contain("datasetCreateStatus");
        renderMainJs.Should().Contain("replayRunProgress");
        renderMainJs.Should().Contain("replayRunCompleted");
        renderMainJs.Should().Contain("replayRunFailed");
        renderMainJs.Should().Contain("replayRunCanceled");
        renderMainJs.Should().Contain("modelApprovalAvailability");
        controller.Should().Contain("SendReplayRunStatus");
        controller.Should().Contain("SendReplayRunCompleted");
        controller.Should().Contain("SendModelApprovalAvailability");
        controller.Should().Contain("SendManualReviewRecords");
        controller.Should().Contain("SendManualReviewResponse");
    }

    [Fact]
    public void WebUiBundle_与源JS保持一致并包含Replay契约()
    {
        string root = FindRepositoryRoot();
        string jsRoot = Path.Combine(root, "ClearFrost", "html", "js");
        string[] files =
        {
            "bridge.js",
            "state.js",
            "render-main.js",
            "settings.js",
            "camera.js",
            "history.js",
            "roi.js",
            "boot.js",
            "app.js",
            "ui.js"
        };
        string expected = string.Join(Environment.NewLine, files.Select(file => File.ReadAllText(Path.Combine(jsRoot, file))));
        string bundle = File.ReadAllText(Path.Combine(jsRoot, "bundle.js"));

        bundle.Should().Be(expected);
        bundle.Should().Contain("normalizeReplayMessage");
        bundle.Should().Contain("normalizeManualReviewMessage");
        bundle.Should().Contain("replayRunCompleted");
        bundle.Should().Contain("manualReviewResponse");
        bundle.Should().Contain("modelApprovalAvailability");
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
