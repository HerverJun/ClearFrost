// ============================================================================
// 文件名: OperatorFaultMessages.cs
// 描述:   一线操作提示映射
//
// 功能:
//   - 将内部错误码转换为现场可执行中文短句
//   - 保留内部错误码给工程师详情、日志和诊断包
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace ClearFrost.Services
{
    internal static class OperatorFaultMessages
    {
        public const string FieldLightweightModeSummary = "当前为现场轻量模式，未强制模型审批证据。";
        public const string StrictModelGateBlocked =
            "当前模型未完成上线验证，请联系工程师完成模型验证，或切换回已验证模型。";

        private static readonly Dictionary<string, string> Messages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ReplayEvidenceGateMissing"] = StrictModelGateBlocked,
            ["ReplayEvidencePackageRequired"] = StrictModelGateBlocked,
            ["ReplayEvidenceEntryMissing"] = StrictModelGateBlocked,
            ["ReplayEvidenceRequired"] = StrictModelGateBlocked,
            ["ReplayEvidenceMissing"] = StrictModelGateBlocked,
            ["ReplayEvidenceFileMissing"] = StrictModelGateBlocked,
            ["ReplayEvidenceHashMismatch"] = StrictModelGateBlocked,
            ["ReplayEvidenceReportHashMismatch"] = StrictModelGateBlocked,
            ["ReplayEvidenceDatasetHashMismatch"] = StrictModelGateBlocked,
            ["ReplayEvidencePolicyRejected"] = StrictModelGateBlocked,
            ["ApprovedModelNotApproved"] = StrictModelGateBlocked,
            ["ApprovedModelNotReady"] = StrictModelGateBlocked,
            ["LegacyModelNotAllowed"] = StrictModelGateBlocked,
            ["LegacyModelCannotMapToApproved"] = StrictModelGateBlocked,
            ["LegacyModelApprovedMappingAmbiguous"] = StrictModelGateBlocked,
            ["PrimaryModelReferenceEmpty"] = "模型未加载：请先在左侧选择主模型。",
            ["ModelNotLoaded"] = "模型未加载：请先在左侧选择主模型。",
            ["RuntimeModelNotLoaded"] = "模型未加载：请先在左侧选择主模型。",
            ["CameraNotReady"] = "相机未启动：请点击右下角“启动系统”，或检查相机网线/电源。",
            ["PlcNotConnected"] = "PLC 未连接：请检查 PLC IP、端口和网线。",
            ["StartupBlocked"] = "当前还不能生产：请先处理诊断中心列出的待处理问题。"
        };

        public static string ForCode(string? errorCode, string fallback = "")
        {
            string code = errorCode?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(code) && Messages.TryGetValue(code, out string? mapped))
            {
                return mapped ?? string.Empty;
            }

            return string.IsNullOrWhiteSpace(fallback)
                ? "当前还不能生产：请打开现场诊断，按待处理问题处理。"
                : fallback;
        }

        public static string ForActivationFailure(string errorCode, string message)
        {
            string mapped = ForCode(errorCode, string.Empty);
            if (!string.IsNullOrWhiteSpace(mapped) &&
                !string.Equals(mapped, "当前还不能生产：请打开现场诊断，按待处理问题处理。", StringComparison.Ordinal))
            {
                return mapped;
            }

            return string.IsNullOrWhiteSpace(message)
                ? "模型加载失败：请检查模型文件是否存在，或在左侧重新选择主模型。"
                : $"模型加载失败：{message}";
        }

        public static string ForStartupItem(StartupDiagnosticItem item)
        {
            string text = $"{item.Name} {item.Message} {item.Details}";
            if (ContainsAny(text, "Replay evidence", "Approved model", "RequireApprovedModelsForProduction=true", "审批", "凭证"))
            {
                return StrictModelGateBlocked;
            }

            if (ContainsAny(text, "Camera", "相机"))
            {
                return ForCode("CameraNotReady");
            }

            if (ContainsAny(text, "PLC", "address", "协议", "地址"))
            {
                return "PLC 配置异常：请检查 PLC 协议、地址、IP、端口和网线。";
            }

            if (ContainsAny(text, "Storage", "Log directory", "Database directory", "Disk", "目录", "磁盘"))
            {
                return "存储异常：请检查存储目录权限和磁盘剩余空间。";
            }

            if (ContainsAny(text, "WebView2"))
            {
                return "界面运行环境异常：请安装或修复 Microsoft WebView2 Runtime。";
            }

            return ForCode("StartupBlocked");
        }

        public static string ForStartupReport(StartupDiagnosticReport? report, string operation)
        {
            StartupDiagnosticItem? firstBlocking = report?.Items?.FirstOrDefault(item =>
                item.Status == StartupDiagnosticStatus.Fail && item.IsBlocking);
            if (firstBlocking == null)
            {
                return $"启动诊断未通过，已阻止{operation}：请打开现场诊断，按待处理问题处理。";
            }

            return ForStartupItem(firstBlocking);
        }

        private static bool ContainsAny(string text, params string[] tokens)
        {
            return tokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
        }
    }
}
