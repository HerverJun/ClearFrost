using FluentAssertions;

using ClearFrost;
using ClearFrost.Models;

namespace ClearFrost.Tests.Views;

public class WebUIHistoryBridgeContractTests
{
    [Fact]
    public void HistoryPanelCommands_ShowVisibleFailureWhenBridgeSendFails()
    {
        string root = FindRepositoryRoot();
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));
        string indexHtml = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));

        foreach (string script in new[] { historyJs, bundleJs })
        {
            script.Should().Contain("const HistoryBridgeErrorMessage = \"历史面板通信失败，请刷新页面后重试\"");
            script.Should().Contain("function sendHistoryCommand(cmd, value = null, onFailure = null, failureMessage = HistoryBridgeErrorMessage)");
            script.Should().Contain("bridge?.sendCommand?.(cmd, value)");
            script.Should().Contain("throw new Error(\"WebViewBridgeUnavailable\")");
            script.Should().Contain("console.error(`History command failed: ${cmd}`, error)");
            script.Should().Contain("window.showToast?.(failureMessage, \"error\", 1800)");
            script.Should().Contain("function setLogHistoryFailure");
            script.Should().Contain("log-history-table");
            script.Should().Contain("colspan=\"3\"");
            script.Should().Contain("log-count-badge");
            script.Should().Contain("function setStatisticsHistoryFailure");
            script.Should().Contain("statistics-history-table");
            script.Should().Contain("colspan=\"5\"");
            script.Should().Contain("function setTraceArchiveFailure");
            script.Should().Contain("function setTraceHourFailure");
            script.Should().Contain("ng-date-list");
            script.Should().Contain("ng-hour-list");
            script.Should().Contain("ng-image-grid");
            script.Should().Contain("const pendingReplayRequests = new Map()");
            script.Should().Contain("function handleHistoryCommandError(event)");
            script.Should().Contain("window.addEventListener(\"cf-command-error\", handleHistoryCommandError)");
            script.Should().Contain("if (cmd === \"get_ng_images\" && requestId && requestId === tracePagerState.pendingRequestId)");
            script.Should().Contain("showTraceLoadFailure(message)");
            script.Should().Contain("updateAuditRecords({ records: [], error: message }, { requestId })");
            script.Should().Contain("updateAuditExport({ path: \"\", error: message }, { requestId })");
            script.Should().Contain("updateAuditChainCommandError(message, requestId)");
            script.Should().Contain("pendingReplayRequests.set(requestId, { statusId, failureText })");
            script.Should().Contain("pendingReplayRequests.delete(requestId)");
            script.Should().Contain("sendHistoryCommand(\"get_detection_logs\", null, () => setLogHistoryFailure())");
            script.Should().Contain("sendHistoryCommand(\"get_statistics_history\", requestedDays, () => setStatisticsHistoryFailure())");
            script.Should().Contain("sendHistoryCommand(\"get_ng_dates\", null, () => setTraceArchiveFailure(), TraceBridgeErrorMessage)");
            script.Should().Contain("sendHistoryCommand(\"get_ng_hours\", selectedDate, () => setTraceHourFailure(), TraceBridgeErrorMessage)");
            script.Should().Contain("sendHistoryCommand(\"get_ng_hours\", date, () => setTraceHourFailure(), TraceBridgeErrorMessage)");
            script.Should().Contain("sendHistoryCommand(\"run_history_rule_preview\", {");
            script.Should().Contain("setHistoryRulePreviewStatus({ status: \"failed\", message: HistoryBridgeErrorMessage });");
        }

        indexHtml.Should().Contain("data-cmd=\"clear_stats_history\"");
        indexHtml.Should().Contain("data-confirm=\"确定要清空所有统计历史数据吗？此操作不可撤销！\"");
    }

    [Fact]
    public void StatisticsHistoryRequest_ClampsDaysAndBackendHonorsValue()
    {
        string root = FindRepositoryRoot();
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));
        string controllerCs = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
        string mainInitCs = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "主窗口.Init.cs"));

        foreach (string script in new[] { historyJs, bundleJs })
        {
            script.Should().Contain("const StatisticsHistoryDefaultDays = 30");
            script.Should().Contain("const StatisticsHistoryMaxDays = 366");
            script.Should().Contain("function normalizeStatisticsHistoryDays(days)");
            script.Should().Contain("sendHistoryCommand(\"get_statistics_history\", requestedDays, () => setStatisticsHistoryFailure())");
        }

        controllerCs.Should().Contain("public sealed class StatisticsHistoryRequestEventArgs : EventArgs");
        controllerCs.Should().Contain("public event EventHandler<StatisticsHistoryRequestEventArgs>? OnGetStatisticsHistory;");
        controllerCs.Should().Contain("int statisticsHistoryDays = TryReadInt32CommandValue(root, out int requestedStatisticsHistoryDays)");
        controllerCs.Should().Contain("OnGetStatisticsHistory?.Invoke(this, new StatisticsHistoryRequestEventArgs(statisticsHistoryDays));");
        controllerCs.Should().Contain("PostMessage(\"statisticsHistory\", BuildStatisticsHistoryRows(history, current, days));");
        mainInitCs.Should().Contain("await _uiController.SendStatisticsHistory(history, stats, e.Days);");
    }

    [Fact]
    public void StatisticsHistoryPayload_过滤与今日同日期的历史记录()
    {
        var current = new DetectionStatistics
        {
            CurrentDate = "2026-07-07",
            TotalCount = 5,
            QualifiedCount = 4,
            UnqualifiedCount = 1
        };
        var history = new StatisticsHistory
        {
            Records =
            [
                new DailyStatisticsRecord
                {
                    Date = "2026-07-07",
                    TotalCount = 100,
                    QualifiedCount = 100,
                    UnqualifiedCount = 0
                },
                new DailyStatisticsRecord
                {
                    Date = "2026-07-06",
                    TotalCount = 8,
                    QualifiedCount = 7,
                    UnqualifiedCount = 1
                },
                new DailyStatisticsRecord
                {
                    Date = "2026-07-05",
                    TotalCount = 6,
                    QualifiedCount = 6,
                    UnqualifiedCount = 0
                }
            ]
        };

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            WebUIController.BuildStatisticsHistoryRows(history, current, days: 2);

        rows.Should().HaveCount(2);
        rows.Select(row => row["date"]).Should().Equal("2026-07-07", "2026-07-06");
        rows[0]["total"].Should().Be(5);
        rows.Should().NotContain(row =>
            string.Equals(row["date"] as string, "2026-07-07", StringComparison.Ordinal) &&
            Equals(row["total"], 100));
    }

    [Fact]
    public void TraceQueryFailures_显示错误而不是空数据()
    {
        string root = FindRepositoryRoot();
        string historyJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "history.js"));
        string bundleJs = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));
        string controllerCs = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));

        foreach (string script in new[] { historyJs, bundleJs })
        {
            script.Should().Contain("setTraceArchiveFailure(error)");
            script.Should().Contain("const error = Array.isArray(data) ? \"\" : (data?.error || data?.Error || \"\")");
            script.Should().Contain("if (error)");
            script.Should().Contain("setTraceHourFailure(error)");
            script.Should().Contain("showTraceLoadFailure(error)");
            script.Should().Contain("tracePagerState.lastHandledRequestId = requestId || tracePagerState.lastHandledRequestId");
        }

        controllerCs.Should().Contain("string message = $\"获取日期列表失败: {ex.Message}\";");
        controllerCs.Should().Contain("string message = $\"获取时段列表失败: {ex.Message}\";");
        controllerCs.Should().Contain("string message = $\"获取追溯图片失败: {ex.Message}\";");
        controllerCs.Should().Contain("await LogToFrontend(message, \"error\");");
        controllerCs.Should().Contain("PostMessage(\"historyDates\", new");
        controllerCs.Should().Contain("PostMessage(\"historyHours\", new");
        controllerCs.Should().Contain("PostMessage(\"historyImages\", new");
        controllerCs.Should().Contain("error = message");
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
