// ============================================================================
// 文件名: WebUIController.Messages.cs
// 描述:   WebView2 前端统一消息推送扩展
// ============================================================================

using ClearFrost.Config;
using ClearFrost.Interfaces;
using ClearFrost.Services;
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

        public Task SendModelPackageImportResult(object result)
        {
            PostMessage("modelPackageImportResult", result);
            return Task.CompletedTask;
        }

        public Task SendHistoryRulePreview(object payload)
        {
            PostMessage("historyRulePreview", payload);
            return Task.CompletedTask;
        }

        public Task SendOperatorSession(OperatorSession session)
        {
            session ??= new OperatorSession();
            PostMessage("operatorSession", new
            {
                operatorName = session.OperatorName,
                role = session.Role,
                shiftName = session.ShiftName,
                signedInAt = session.SignedInAt.ToString("o"),
                isSignedIn = session.IsSignedIn
            });
            return Task.CompletedTask;
        }

        public Task SendAlarmSnapshot(AlarmSnapshot snapshot)
        {
            snapshot ??= new AlarmSnapshot();
            PostMessage("alarmSnapshot", new
            {
                activeCount = snapshot.ActiveCount,
                unacknowledgedCount = snapshot.UnacknowledgedCount,
                highestSeverity = snapshot.HighestSeverity.ToString(),
                updatedAt = snapshot.UpdatedAt.ToString("o"),
                activeAlarms = snapshot.ActiveAlarms.Select(ToAlarmPayload).ToArray(),
                recentAlarms = snapshot.RecentAlarms.Select(ToAlarmPayload).ToArray()
            });
            return Task.CompletedTask;
        }

        public Task SendAlarmActionResult(bool success, string message, AlarmRecord? alarm = null)
        {
            PostMessage("alarmActionResult", new
            {
                success,
                message,
                alarm = alarm == null ? null : ToAlarmPayload(alarm)
            });
            return Task.CompletedTask;
        }

        public Task SendBootstrapSnapshot(
            AppConfig config,
            IEnumerable<object> cameras,
            string activeCameraId,
            string[] models,
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

        private static object ToAlarmPayload(AlarmRecord alarm)
        {
            return new
            {
                alarmId = alarm.AlarmId,
                severity = alarm.Severity.ToString(),
                state = alarm.State.ToString(),
                source = alarm.Source,
                message = alarm.Message,
                recommendedAction = alarm.RecommendedAction,
                lastInspectionId = alarm.LastInspectionId,
                raisedAt = alarm.RaisedAt.ToString("o"),
                lastSeenAt = alarm.LastSeenAt.ToString("o"),
                clearedAt = alarm.ClearedAt?.ToString("o") ?? string.Empty,
                acknowledgedBy = alarm.AcknowledgedBy,
                acknowledgedRole = alarm.AcknowledgedRole,
                acknowledgedAt = alarm.AcknowledgedAt?.ToString("o") ?? string.Empty,
                occurrenceCount = alarm.OccurrenceCount,
                isAcknowledged = alarm.IsAcknowledged
            };
        }
    }
}
