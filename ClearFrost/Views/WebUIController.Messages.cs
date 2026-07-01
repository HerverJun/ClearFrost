// ============================================================================
// 文件名: WebUIController.Messages.cs
// 描述:   WebView2 前端统一消息推送扩展
// ============================================================================

using ClearFrost.Config;
using ClearFrost.Interfaces;
using ClearFrost.Services.Replay;
using System.Collections.Generic;
using System.Linq;
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

        public Task SendDatasetCreateStatus(object payload, string? requestId = null)
        {
            PostMessage("datasetCreateStatus", payload, requestId);
            return Task.CompletedTask;
        }

        public Task SendManualReviewRecords(IEnumerable<ManualReviewTraceItem> records, string? requestId = null)
        {
            PostMessage("manualReviewRecords", new
            {
                records = records ?? System.Array.Empty<ManualReviewTraceItem>()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendManualReviewResponse(ManualReviewSaveResult result, string? requestId = null)
        {
            PostMessage("manualReviewResponse", result, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayRunStatus(ReplayRunProgress progress, string? requestId = null)
        {
            string messageType = progress.Status switch
            {
                ReplayRunStatuses.Completed => "replayRunCompleted",
                ReplayRunStatuses.Failed => "replayRunFailed",
                ReplayRunStatuses.Canceled => "replayRunCanceled",
                _ => "replayRunProgress"
            };

            PostMessage(messageType, progress, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayRunCompleted(ReplayRunReport report, string? requestId = null)
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
                reportHash = report.ReportHash,
                policyHash = report.PolicyHash,
                recipeHash = report.RecipeHash,
                ruleSetHash = report.RuleSetHash,
                approvalAvailable = report.Metrics.CandidateNewMissedDetectionCount == 0 &&
                    report.Metrics.CandidateNewFalseRejectCount == 0
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendModelApprovalAvailability(bool available, IEnumerable<string> rejectionReasons, string? requestId = null)
        {
            PostMessage("modelApprovalAvailability", new
            {
                approvalAvailable = available,
                rejectionReasons = rejectionReasons ?? System.Array.Empty<string>()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayApprovalResponse(ReplayApprovalResult result, string? requestId = null)
        {
            PostMessage("replayApprovalResponse", new
            {
                succeeded = result.Succeeded,
                errorCode = result.ErrorCode,
                message = result.Message,
                isFaulted = result.IsFaulted,
                evidenceId = result.Evidence?.EvidenceId ?? string.Empty,
                evidenceHash = result.Evidence?.EvidenceHash ?? string.Empty,
                datasetHash = result.Evidence?.DatasetHash ?? string.Empty,
                reportHash = result.Evidence?.ReplayReportHash ?? string.Empty,
                compensationFailures = result.CompensationFailures ?? System.Array.Empty<string>()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayDatasets(IEnumerable<ReplayDatasetSummary> datasets, string? requestId = null)
        {
            PostMessage("replayDatasets", new
            {
                datasets = datasets ?? System.Array.Empty<ReplayDatasetSummary>()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayDatasetPreview(ReplayDatasetSnapshot snapshot, string? requestId = null)
        {
            PostMessage("replayDatasetPreview", new
            {
                datasetId = snapshot.DatasetId,
                datasetHash = snapshot.DatasetHash,
                sampleCount = snapshot.Samples.Count,
                recipeId = snapshot.Recipe.RecipeId,
                recipeVersion = snapshot.Recipe.RecipeVersion,
                samples = snapshot.Samples.Take(20).Select(sample => new
                {
                    sample.SampleId,
                    sample.DetectionRecordId,
                    sample.InspectionId,
                    sample.GroundTruth,
                    sample.SystemDecision,
                    sample.ImageHash
                }).ToArray()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayRuns(IEnumerable<ReplayRunRecord> runs, string? requestId = null)
        {
            PostMessage("replayRuns", new
            {
                runs = runs ?? System.Array.Empty<ReplayRunRecord>()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayReport(ReplayRunReport report, string? requestId = null)
        {
            PostMessage("replayReport", report, requestId);
            return Task.CompletedTask;
        }

        public Task SendModelApprovalEvidence(IEnumerable<ModelApprovalEvidence> evidence, string? requestId = null)
        {
            PostMessage("modelApprovalEvidence", new
            {
                evidence = evidence ?? System.Array.Empty<ModelApprovalEvidence>()
            }, requestId);
            return Task.CompletedTask;
        }

        public Task SendReplayIntegrityScan(ReplayIntegrityScanResult result, string? requestId = null)
        {
            PostMessage("replayIntegrityScan", result, requestId);
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
