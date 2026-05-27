using System;
using System.IO;
using System.Text;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class ProductionReportExporterTests
{
    [Fact]
    public void Export_检测记录_写入汇总和追溯字段()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string reportPath = Path.Combine(tempDir, "reports", "report.csv");
            var exporter = new ProductionReportExporter();
            var records = new[]
            {
                new DetectionRecord
                {
                    Id = 2,
                    Timestamp = new DateTime(2026, 5, 4, 17, 30, 0, 123),
                    IsQualified = false,
                    InspectionId = "CF-002",
                    ProductBarcode = "SN,002",
                    OperatorName = "OP-02",
                    OperatorRole = "Operator",
                    ShiftName = "B班",
                    TriggerSource = "PLC",
                    TriggerSeq = 12,
                    ResultSeq = 12,
                    RecipeId = "recipe-a",
                    RecipeVersion = "1.0",
                    ModelId = "model-a",
                    ModelVersion = "v1",
                    UsedModelName = "model-a.onnx",
                    CameraId = "cam-01",
                    TargetLabel = "screw",
                    ExpectedCount = 4,
                    ActualCount = 3,
                    InferenceMs = 21,
                    TotalMs = 88,
                    CaptureMs = 12,
                    PlcWriteMs = 4,
                    ErrorStage = "Judge",
                    ErrorCode = "CountMismatch",
                    ErrorMessage = "数量不足",
                    ImagePath = @"C:\Trace\FAIL.jpg",
                    RenderedImagePath = @"C:\Trace\Rendered\FAIL.jpg",
                    ImageHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    RenderedImageHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                },
                new DetectionRecord
                {
                    Id = 1,
                    Timestamp = new DateTime(2026, 5, 4, 9, 0, 0),
                    IsQualified = true,
                    InspectionId = "CF-001",
                    ProductBarcode = "SN-001",
                    OperatorName = "OP-01",
                    OperatorRole = "Engineer",
                    ShiftName = "A班",
                    ModelName = "legacy.onnx"
                }
            };

            ProductionReportExportResult result = exporter.Export(records, new ProductionReportExportOptions
            {
                FilePath = reportPath,
                StartTime = new DateTime(2026, 5, 4, 8, 0, 0),
                EndTime = new DateTime(2026, 5, 4, 18, 0, 0),
                IsQualified = null,
                OperatorName = "operator-a"
            });

            result.TotalCount.Should().Be(2);
            result.QualifiedCount.Should().Be(1);
            result.UnqualifiedCount.Should().Be(1);
            result.QualifiedRate.Should().Be(50);
            File.Exists(reportPath).Should().BeTrue();

            string content = File.ReadAllText(reportPath, Encoding.UTF8);
            content.Should().Contain("导出操作员,operator-a");
            content.Should().Contain("合格率,50.00%");
            content.Should().Contain("2026-05-04 09:00:00.000,A班,OP-01,Engineer,OK,CF-001");
            content.Should().Contain("2026-05-04 17:30:00.123,B班,OP-02,Operator,NG,CF-002,\"SN,002\"");
            content.Should().Contain("CountMismatch,数量不足");
            content.Should().Contain("原图SHA256,复查图SHA256");
            content.Should().Contain("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa,bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData(1, "C班")]
    [InlineData(8, "A班")]
    [InlineData(15, "A班")]
    [InlineData(16, "B班")]
    [InlineData(23, "B班")]
    public void GetShiftName_按小时返回班次(int hour, string expected)
    {
        ProductionReportExporter.GetShiftName(new DateTime(2026, 5, 4, hour, 0, 0))
            .Should()
            .Be(expected);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostReportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
