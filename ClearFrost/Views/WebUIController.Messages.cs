// ============================================================================
// 文件名: WebUIController.Messages.cs
// 描述:   WebView2 前端统一消息推送扩展
// ============================================================================

using ClearFrost.Config;
using ClearFrost.Interfaces;
using ClearFrost.Services.Replay;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClearFrost
{
    public partial class WebUIController
    {
        public Task SendUiCommand(string action, object? payload = null)
        {
            PostMessage("uiCommand", new { action = action, payload = payload });
            return Task.CompletedTask;
        }

        public Task SendProjectPresets(ProjectPresetStore.Snapshot snapshot)
        {
            PostMessage("projectPresets", new
            {
                presets = snapshot.Presets,
                path = snapshot.Path
            });
            return Task.CompletedTask;
        }

        public Task SendModelLabels(string[] labels)
        {
            PostMessage("modelLabels", new { labels = labels ?? System.Array.Empty<string>() });
            return Task.CompletedTask;
        }

        public Task SendHistoryRulePreview(object payload)
        {
            PostMessage("historyRulePreview", payload);
            return Task.CompletedTask;
        }

        public Task SendDatasetCreateStatus(object payload)
        {
            PostMessage("datasetCreateStatus", payload);
            return Task.CompletedTask;
        }

        public Task SendManualReviewRecords(IEnumerable<ManualReviewTraceItem> records)
        {
            PostMessage("manualReviewRecords", new
            {
                records = records ?? System.Array.Empty<ManualReviewTraceItem>()
            });
            return Task.CompletedTask;
        }

        public Task SendManualReviewResponse(ManualReviewSaveResult result)
        {
            PostMessage("manualReviewResponse", result);
            return Task.CompletedTask;
        }

        public Task SendReplayRunStatus(ReplayRunProgress progress)
        {
            string messageType = progress.Status switch
            {
                ReplayRunStatuses.Completed => "replayRunCompleted",
                ReplayRunStatuses.Failed => "replayRunFailed",
                ReplayRunStatuses.Canceled => "replayRunCanceled",
                _ => "replayRunProgress"
            };

            PostMessage(messageType, progress);
            return Task.CompletedTask;
        }

        public Task SendReplayRunCompleted(ReplayRunReport report)
        {
            PostMessage("replayRunCompleted", new
            {
                runId = report.RunId,
                datasetId = report.DatasetId,
                datasetHash = report.DatasetHash,
                status = report.Status,
                metrics = report.Metrics,
                reportJsonPath = report.ReportJsonPath,
                reportCsvPath = report.ReportCsvPath,
                approvalAvailable = report.Metrics.CandidateNewMissedDetectionCount == 0 &&
                    report.Metrics.CandidateNewFalseRejectCount == 0
            });
            return Task.CompletedTask;
        }

        public Task SendModelApprovalAvailability(bool available, IEnumerable<string> rejectionReasons)
        {
            PostMessage("modelApprovalAvailability", new
            {
                approvalAvailable = available,
                rejectionReasons = rejectionReasons ?? System.Array.Empty<string>()
            });
            return Task.CompletedTask;
        }

        public Task SendBootstrapSnapshot(
            AppConfig config,
            IEnumerable<object> cameras,
            string activeCameraId,
            object models,
            StatisticsSnapshot stats,
            object health,
            string storagePath)
        {
            PostMessage("bootstrapSnapshot", new
            {
                config = config,
                cameras = cameras,
                activeCameraId = activeCameraId,
                models = models,
                stats = new
                {
                    total = stats.TotalCount,
                    ok = stats.QualifiedCount,
                    ng = stats.UnqualifiedCount
                },
                health = health,
                storagePath = storagePath
            });
            return Task.CompletedTask;
        }
    }
}
