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
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string indexHtml = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        string controllerEvents = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
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
        renderMainJs.Should().Contain("replayApprovalResponse");
        historyJs.Should().Contain("query_manual_review_records");
        historyJs.Should().Contain("save_manual_review");
        historyJs.Should().Contain("create_replay_dataset");
        historyJs.Should().Contain("preview_replay_dataset");
        historyJs.Should().Contain("query_replay_datasets");
        historyJs.Should().Contain("archive_replay_dataset");
        historyJs.Should().Contain("run_replay_comparison");
        historyJs.Should().Contain("cancel_replay_run");
        historyJs.Should().Contain("query_replay_runs");
        historyJs.Should().Contain("query_replay_report");
        historyJs.Should().Contain("query_model_approval_evidence");
        historyJs.Should().Contain("run_replay_integrity_scan");
        historyJs.Should().Contain("approve_replay_candidate");
        historyJs.Should().Contain("requestId");
        indexHtml.Should().Contain("replay-acceptance-panel");
        indexHtml.Should().Contain("manual-review-ground-truth-input");
        indexHtml.Should().Contain("replay-baseline-model");
        indexHtml.Should().Contain("replay-candidate-model");
        indexHtml.Should().Contain("replay-approval-status");
        controllerEvents.Should().Contain("OnQueryManualReviewRecords");
        controllerEvents.Should().Contain("OnSaveManualReview");
        controllerEvents.Should().Contain("OnCreateReplayDataset");
        controllerEvents.Should().Contain("OnPreviewReplayDataset");
        controllerEvents.Should().Contain("OnQueryReplayDatasets");
        controllerEvents.Should().Contain("OnArchiveReplayDataset");
        controllerEvents.Should().Contain("OnRunReplayComparison");
        controllerEvents.Should().Contain("OnCancelReplayRun");
        controllerEvents.Should().Contain("OnQueryReplayRuns");
        controllerEvents.Should().Contain("OnQueryReplayReport");
        controllerEvents.Should().Contain("OnQueryModelApprovalEvidence");
        controllerEvents.Should().Contain("OnRunReplayIntegrityScan");
        controllerEvents.Should().Contain("OnApproveReplayCandidate");
        controller.Should().Contain("SendReplayRunStatus");
        controller.Should().Contain("SendReplayRunCompleted");
        controller.Should().Contain("SendModelApprovalAvailability");
        controller.Should().Contain("SendReplayApprovalResponse");
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
        bundle.Should().Contain("replayApprovalResponse");
        bundle.Should().Contain("create_replay_dataset");
        bundle.Should().Contain("preview_replay_dataset");
        bundle.Should().Contain("query_replay_datasets");
        bundle.Should().Contain("archive_replay_dataset");
        bundle.Should().Contain("cancel_replay_run");
        bundle.Should().Contain("query_replay_runs");
        bundle.Should().Contain("query_replay_report");
        bundle.Should().Contain("query_model_approval_evidence");
        bundle.Should().Contain("run_replay_integrity_scan");
        bundle.Should().Contain("approve_replay_candidate");
    }

    [Fact]
    public void WebUi源码_Replay追溯工具条拥有独立Grid行()
    {
        string root = FindRepositoryRoot();
        string styleCss = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "css", "style.css"));

        styleCss.Should().Contain("grid-template-rows: 126px auto minmax(0, 1fr) 56px;");
        styleCss.Should().Contain(".cf-stitch-page #gallery-modal #replay-acceptance-panel");
        styleCss.Should().Contain("grid-column: 2;");
        styleCss.Should().Contain("grid-row: 2;");
        styleCss.Should().Contain(".cf-stitch-page #gallery-modal #ng-image-grid");
        styleCss.Should().Contain("grid-row: 3;");
        styleCss.Should().Contain(".cf-stitch-page #gallery-modal #trace-pagination");
        styleCss.Should().Contain("grid-row: 4;");
        styleCss.Should().Contain("grid-template-rows: auto 120px auto minmax(0, 1fr) 56px;");
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
