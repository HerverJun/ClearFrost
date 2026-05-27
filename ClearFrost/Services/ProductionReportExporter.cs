// ============================================================================
// 文件名: ProductionReportExporter.cs
// 描述:   生产追溯报表 CSV 导出
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    public sealed class ProductionReportExportOptions
    {
        public string FilePath { get; init; } = string.Empty;
        public DateTime? StartTime { get; init; }
        public DateTime? EndTime { get; init; }
        public bool? IsQualified { get; init; }
        public string OperatorName { get; init; } = string.Empty;
        public string Title { get; init; } = "ClearFrost 生产追溯报表";
    }

    public sealed class ProductionReportExportResult
    {
        public string FilePath { get; init; } = string.Empty;
        public int TotalCount { get; init; }
        public int QualifiedCount { get; init; }
        public int UnqualifiedCount { get; init; }
        public double QualifiedRate { get; init; }
    }

    public sealed class ProductionReportExporter
    {
        public ProductionReportExportResult Export(
            IEnumerable<DetectionRecord> records,
            ProductionReportExportOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(options.FilePath))
            {
                throw new ArgumentException("报表路径不能为空", nameof(options));
            }

            DetectionRecord[] orderedRecords = (records ?? Array.Empty<DetectionRecord>())
                .OrderBy(r => r.Timestamp)
                .ThenBy(r => r.Id)
                .ToArray();

            int total = orderedRecords.Length;
            int qualified = orderedRecords.Count(r => r.IsQualified);
            int unqualified = total - qualified;
            double qualifiedRate = total > 0 ? qualified * 100d / total : 0d;

            string? directory = Path.GetDirectoryName(options.FilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var writer = new StreamWriter(
                options.FilePath,
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            WriteSummary(writer, options, total, qualified, unqualified, qualifiedRate);
            WriteRows(writer, orderedRecords);

            return new ProductionReportExportResult
            {
                FilePath = options.FilePath,
                TotalCount = total,
                QualifiedCount = qualified,
                UnqualifiedCount = unqualified,
                QualifiedRate = Math.Round(qualifiedRate, 2)
            };
        }

        public static string GetShiftName(DateTime timestamp)
        {
            int hour = timestamp.Hour;
            if (hour >= 8 && hour < 16)
            {
                return "A班";
            }

            if (hour >= 16)
            {
                return "B班";
            }

            return "C班";
        }

        private static void WriteSummary(
            TextWriter writer,
            ProductionReportExportOptions options,
            int total,
            int qualified,
            int unqualified,
            double qualifiedRate)
        {
            writer.WriteLine(Csv("报表", options.Title));
            writer.WriteLine(Csv("导出时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            writer.WriteLine(Csv("导出操作员", string.IsNullOrWhiteSpace(options.OperatorName) ? Environment.UserName : options.OperatorName.Trim()));
            writer.WriteLine(Csv("时间范围", $"{FormatTime(options.StartTime)} ~ {FormatTime(options.EndTime)}"));
            writer.WriteLine(Csv("结果筛选", FormatResultFilter(options.IsQualified)));
            writer.WriteLine(Csv("总数", total.ToString(CultureInfo.InvariantCulture)));
            writer.WriteLine(Csv("OK", qualified.ToString(CultureInfo.InvariantCulture)));
            writer.WriteLine(Csv("NG", unqualified.ToString(CultureInfo.InvariantCulture)));
            writer.WriteLine(Csv("合格率", $"{qualifiedRate:F2}%"));
            writer.WriteLine();
        }

        private static void WriteRows(TextWriter writer, IReadOnlyList<DetectionRecord> records)
        {
            writer.WriteLine(Csv(
                "检测时间",
                "班次",
                "操作员",
                "角色",
                "结果",
                "检测ID",
                "条码",
                "触发源",
                "触发序号",
                "结果序号",
                "配方ID",
                "配方版本",
                "模型ID",
                "模型版本",
                "模型名称",
                "相机ID",
                "目标标签",
                "期望数量",
                "实际数量",
                "推理ms",
                "总耗时ms",
                "采集ms",
                "PLC写入ms",
                "追溯状态",
                "错误阶段",
                "错误代码",
                "错误信息",
                "图像路径",
                "复查图路径",
                "原图SHA256",
                "复查图SHA256"));

            foreach (DetectionRecord record in records)
            {
                writer.WriteLine(Csv(
                    record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    string.IsNullOrWhiteSpace(record.ShiftName) ? GetShiftName(record.Timestamp) : record.ShiftName,
                    record.OperatorName,
                    record.OperatorRole,
                    record.IsQualified ? "OK" : "NG",
                    record.InspectionId,
                    record.ProductBarcode,
                    record.TriggerSource,
                    record.TriggerSeq?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    record.ResultSeq?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    record.RecipeId,
                    record.RecipeVersion,
                    record.ModelId,
                    record.ModelVersion,
                    string.IsNullOrWhiteSpace(record.UsedModelName) ? record.ModelName : record.UsedModelName,
                    record.CameraId,
                    record.TargetLabel,
                    record.ExpectedCount.ToString(CultureInfo.InvariantCulture),
                    record.ActualCount.ToString(CultureInfo.InvariantCulture),
                    record.InferenceMs.ToString(CultureInfo.InvariantCulture),
                    record.TotalMs.ToString(CultureInfo.InvariantCulture),
                    record.CaptureMs.ToString(CultureInfo.InvariantCulture),
                    record.PlcWriteMs.ToString(CultureInfo.InvariantCulture),
                    record.TraceStatus.ToString(),
                    record.ErrorStage,
                    record.ErrorCode,
                    record.ErrorMessage,
                    record.ImagePath,
                    record.RenderedImagePath,
                    record.ImageHash,
                    record.RenderedImageHash));
            }
        }

        private static string FormatTime(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "-";
        }

        private static string FormatResultFilter(bool? isQualified)
        {
            if (!isQualified.HasValue)
            {
                return "全部";
            }

            return isQualified.Value ? "OK" : "NG";
        }

        private static string Csv(params string?[] fields)
        {
            return string.Join(",", fields.Select(EscapeCsv));
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            bool needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
            if (!needsQuotes)
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
