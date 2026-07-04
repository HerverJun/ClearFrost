using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Core.Inspection;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Hardware;
using ClearFrost.Interfaces;
using ClearFrost.Models;
using ClearFrost.Services;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

#pragma warning disable CS0067
public class InspectionPipelineServiceTests
{
    [Fact]
    public async Task ExecuteAsync_成功链路_写入Plc保存追溯并返回阶段结果()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: true, barcodeRequired: true);
            var camera = new FakeCameraService(new Mat(32, 32, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService { BarcodeValue = " SN-001 " };
            var detection = new FakeDetectionService
            {
                DetectionResult = new DetectionResultData
                {
                    Results = new List<YoloResult> { Detection(16, 16, 8, 8, 0.95f, 0) },
                    UsedModelName = "primary.onnx",
                    UsedModelLabels = new[] { "part" },
                    FallbackAttemptCount = 1,
                    FallbackSkippedReason = "FallbackDisabled"
                }
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-SUCCESS-001", triggerSeq: 7);

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("PLC半自动", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeTrue();
            result.FinalResultCount.Should().Be(1);
            result.ProductBarcode.Should().Be("SN-001");
            result.HasFrame.Should().BeTrue();
            context.ResultSeq.Should().Be(7);
            context.TraceStatus.Should().Be(TraceStatus.Queued);
            context.FallbackAttemptCount.Should().Be(1);
            context.FallbackSkippedReason.Should().Be("FallbackDisabled");
            context.ImageQueuePending.Should().BeGreaterThanOrEqualTo(0);
            context.RecordQueuePending.Should().BeGreaterThanOrEqualTo(0);
            result.FallbackAttemptCount.Should().Be(1);
            result.FallbackSkippedReason.Should().Be("FallbackDisabled");
            statistics.Total.Should().Be(1);
            statistics.Qualified.Should().Be(1);
            camera.CaptureCalls.Should().Be(1);
            detection.DetectMatCalls.Should().Be(1);
            plc.WrittenValues.Should().Contain(config.PlcOkValue);
            result.Stages.Select(stage => stage.Stage).Should().Contain(new[]
            {
                InspectionStage.Barcode,
                InspectionStage.Capture,
                InspectionStage.Inference,
                InspectionStage.RoiFilter,
                InspectionStage.PlcWrite
            });
            database.SavedRecords.Should().ContainSingle();
            database.SavedRecords[0].InspectionId.Should().Be(context.InspectionId);
            database.SavedRecords[0].ProductBarcode.Should().Be("SN-001");
            database.SavedRecords[0].IsQualified.Should().BeTrue();
            database.SavedRecords[0].ActualCount.Should().Be(1);
            File.Exists(database.SavedRecords[0].ImagePath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_必填条码为空_不取图不推理并按Ng追溯()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: true, barcodeRequired: true);
            var camera = new FakeCameraService(new Mat(16, 16, MatType.CV_8UC3, Scalar.All(80)));
            var plc = new FakePlcService { BarcodeValue = " " };
            var detection = new FakeDetectionService();
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-BARCODE-FAIL", triggerSeq: null);

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("PLC半自动", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeFalse();
            result.HasFrame.Should().BeFalse();
            context.ErrorStage.Should().Be(nameof(InspectionStage.Barcode));
            context.ErrorCode.Should().Be("NoBarcode");
            context.TraceStatus.Should().Be(TraceStatus.Partial);
            camera.CaptureCalls.Should().Be(0);
            detection.DetectMatCalls.Should().Be(0);
            plc.WrittenValues.Should().Contain(config.PlcNgValue);
            statistics.Unqualified.Should().Be(1);
            database.SavedRecords.Should().ContainSingle();
            database.SavedRecords[0].IsQualified.Should().BeFalse();
            database.SavedRecords[0].ErrorCode.Should().Be("NoBarcode");
            database.SavedRecords[0].TraceStatus.Should().Be(TraceStatus.Partial);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_取图失败_重试后写Ng且不调用推理()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: false, barcodeRequired: false);
            config.MaxRetryCount = 1;
            config.RetryIntervalMs = 0;
            var camera = new FakeCameraService(null) { LastErrorOverride = "timeout" };
            var plc = new FakePlcService();
            var detection = new FakeDetectionService();
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-CAPTURE-FAIL", triggerSeq: null);

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("手动", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeFalse();
            result.AttemptCount.Should().Be(2);
            result.HasFrame.Should().BeFalse();
            context.ErrorStage.Should().Be(nameof(InspectionStage.Capture));
            context.ErrorCode.Should().Be("CaptureFrameFailed");
            camera.CaptureCalls.Should().Be(4);
            detection.DetectMatCalls.Should().Be(0);
            plc.WrittenValues.Should().Contain(config.PlcNgValue);
            database.SavedRecords.Should().ContainSingle();
            database.SavedRecords[0].ErrorCode.Should().Be("CaptureFrameFailed");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_首帧取图失败_自动恢复后继续检测()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: false, barcodeRequired: false);
            config.MaxRetryCount = 0;
            var camera = FakeCameraService.WithCaptureSequence(
                null,
                new Mat(32, 32, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService();
            var detection = new FakeDetectionService
            {
                DetectionResult = new DetectionResultData
                {
                    Results = new List<YoloResult> { Detection(16, 16, 8, 8, 0.95f, 0) },
                    UsedModelName = "primary.onnx",
                    UsedModelLabels = new[] { "part" }
                }
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-CAPTURE-RECOVER", triggerSeq: 3);

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("PLC半自动", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeTrue();
            result.AttemptCount.Should().Be(1);
            camera.CaptureCalls.Should().Be(2);
            camera.StartCaptureCalls.Should().Be(1);
            detection.DetectMatCalls.Should().Be(1);
            plc.WrittenValues.Should().Contain(config.PlcOkValue);
            database.SavedRecords.Should().ContainSingle()
                .Which.IsQualified.Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_顺序规则忽略Roi内非期望标签_返回Ok()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            var ruleSet = new InspectionRuleSet
            {
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "person-dog-y-order",
                        Type = InspectionRuleTypes.OrderedLabels,
                        ExpectedLabels = new List<string> { "person", "dog" },
                        SortBy = "CenterY",
                        Direction = "TopToBottom",
                        ExpectedCount = 2
                    }
                }
            };
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: false, barcodeRequired: false);
            config.InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(ruleSet);
            var camera = new FakeCameraService(new Mat(400, 400, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService();
            var detection = new FakeDetectionService
            {
                DetectionResult = new DetectionResultData
                {
                    Results = new List<YoloResult>
                    {
                        Detection(100, 100, 50, 160, 0.94f, 0),
                        Detection(120, 150, 70, 90, 0.88f, 2),
                        Detection(130, 260, 70, 120, 0.91f, 1),
                        Detection(310, 110, 50, 160, 0.95f, 0)
                    },
                    UsedModelName = "primary.onnx",
                    UsedModelLabels = new[] { "person", "dog", "backpack" },
                    FallbackAttemptCount = 1,
                    FallbackSkippedReason = "FallbackDisabled"
                }
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue,
                new[] { 0f, 0f, 0.5f, 1f });
            InspectionContext context = CreateContext("CF-RULE-ROI-ORDER", triggerSeq: null);

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("手动", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeTrue();
            result.FinalResultCount.Should().Be(3);
            result.JudgeResult.Should().NotBeNull();
            result.JudgeResult!.IsQualified.Should().BeTrue();
            result.JudgeResult.RuleResults.Should().ContainSingle()
                .Which.Actual.Should().Be("person -> dog");
            statistics.Qualified.Should().Be(1);
            plc.WrittenValues.Should().Contain(config.PlcOkValue);
            database.SavedRecords.Should().ContainSingle();
            database.SavedRecords[0].IsQualified.Should().BeTrue();
            database.SavedRecords[0].ActualCount.Should().Be(3);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_串口光电触发_跳过Plc条码读取和结果写入()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: true, barcodeRequired: true);
            config.TriggerSource = TriggerSource.SerialPhotoelectric;
            var camera = new FakeCameraService(new Mat(32, 32, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService { BarcodeValue = " " };
            var detection = new FakeDetectionService
            {
                DetectionResult = new DetectionResultData
                {
                    Results = new List<YoloResult> { Detection(16, 16, 8, 8, 0.95f, 0) },
                    UsedModelName = "primary.onnx",
                    UsedModelLabels = new[] { "part" }
                }
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-SERIAL-001", triggerSeq: null, triggerSource: "串口光电");

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("串口光电", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeTrue();
            result.BarcodeEnabled.Should().BeFalse();
            result.Stages.Select(stage => stage.Stage).Should().NotContain(InspectionStage.Barcode);
            result.Stages.Select(stage => stage.Stage).Should().NotContain(InspectionStage.PlcWrite);
            plc.ReadStringCalls.Should().Be(0);
            plc.WrittenValues.Should().BeEmpty();
            camera.CaptureCalls.Should().Be(1);
            detection.DetectMatCalls.Should().Be(1);
            database.SavedRecords.Should().ContainSingle();
            database.SavedRecords[0].ProductBarcode.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HandshakeV1_开始阶段不重复TriggerAck且完成后恢复Ready()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: false, barcodeRequired: false);
            config.PlcProtocolMode = PlcProtocolMode.HandshakeV1;
            config.PlcResultAckTimeoutMs = 50;
            var camera = new FakeCameraService(new Mat(32, 32, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService();
            var detection = new FakeDetectionService
            {
                DetectionResult = new DetectionResultData
                {
                    Results = new List<YoloResult> { Detection(16, 16, 8, 8, 0.95f, 0) },
                    UsedModelName = "primary.onnx",
                    UsedModelLabels = new[] { "part" }
                }
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-HANDSHAKE-001", triggerSeq: 42, triggerSource: "PLC");
            context.PlcTriggerAccepted = true;
            plc.ReadSequence.Enqueue((true, 0));
            plc.ReadSequence.Enqueue((true, 1));

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("PLC", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeTrue();
            plc.Writes.Should().Contain(write => write.Address == config.PlcVisionBusyAddress && write.Value == 0);
            plc.Writes.Should().Contain(write => write.Address == config.PlcResultAddress && write.Value == config.PlcOkValue);
            plc.Writes.Should().ContainSingle(write => write.Address == config.PlcResultValidAddress && write.Value == 1);
            plc.Writes.Should().Contain(write => write.Address == config.PlcTriggerAckAddress && write.Value == 0);
            plc.Writes.Last(write => write.Address == config.PlcVisionReadyAddress).Value.Should().Be(1);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HandshakeV1_取消时仍执行一次终态握手()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: false, barcodeRequired: false);
            config.PlcProtocolMode = PlcProtocolMode.HandshakeV1;
            config.PlcResultAckTimeoutMs = 50;
            var camera = new FakeCameraService(new Mat(32, 32, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService();
            plc.ReadSequence.Enqueue((true, 0));
            plc.ReadSequence.Enqueue((true, 1));
            var detection = new FakeDetectionService
            {
                ThrowOnDetect = new OperationCanceledException("cancelled")
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-HANDSHAKE-CANCEL", triggerSeq: 43, triggerSource: "PLC");
            context.PlcTriggerAccepted = true;

            Func<Task> act = async () => await service.ExecuteAsync(
                new InspectionPipelineRequest("PLC", context.InspectionId, context.TriggerSeq, context),
                default);

            await act.Should().ThrowAsync<OperationCanceledException>();
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            plc.Writes.Should().ContainSingle(write => write.Address == config.PlcResultValidAddress && write.Value == 1);
            plc.Writes.Should().ContainSingle(write => write.Address == config.PlcInspectionDoneAddress && write.Value == 1);
            plc.Writes.Last(write => write.Address == config.PlcVisionReadyAddress).Value.Should().Be(1);
            database.SavedRecords.Should().ContainSingle();
            DetectionRecord record = database.SavedRecords[0];
            record.ErrorCode.Should().Be("OperationCanceled");
            record.TerminalHandshakeAttempted.Should().BeTrue();
            record.TerminalHandshakeSucceeded.Should().BeTrue();
            record.CycleSucceeded.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExecuteAsync_HandshakeV1_产品Ok但Ack超时_CycleSucceeded为False并持久化终态错误()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            AppConfig config = CreateConfig(tempDir, barcodeEnabled: false, barcodeRequired: false);
            config.PlcProtocolMode = PlcProtocolMode.HandshakeV1;
            config.PlcResultAckTimeoutMs = 1;
            var camera = new FakeCameraService(new Mat(32, 32, MatType.CV_8UC3, Scalar.All(120)));
            var plc = new FakePlcService();
            plc.ReadSequence.Enqueue((true, 0));
            var detection = new FakeDetectionService
            {
                DetectionResult = new DetectionResultData
                {
                    Results = new List<YoloResult> { Detection(16, 16, 8, 8, 0.95f, 0) },
                    UsedModelName = "primary.onnx",
                    UsedModelLabels = new[] { "part" }
                }
            };
            var statistics = new FakeStatisticsService();
            var database = new RecordingDatabaseService();
            using var imageQueue = new ImageSaveQueue();
            using var recordQueue = new DetectionRecordQueue(database);
            InspectionPipelineService service = CreateService(
                config,
                camera,
                plc,
                detection,
                statistics,
                database,
                imageQueue,
                recordQueue);
            InspectionContext context = CreateContext("CF-HANDSHAKE-ACK-TIMEOUT", triggerSeq: 44, triggerSource: "PLC");
            context.PlcTriggerAccepted = true;

            using InspectionPipelineResult result = await service.ExecuteAsync(
                new InspectionPipelineRequest("PLC", context.InspectionId, context.TriggerSeq, context),
                default);
            await recordQueue.StopAsync();
            await imageQueue.StopAsync();

            result.FinalQualified.Should().BeTrue();
            result.StatusLevel.Should().Be("error");
            result.StatusMessage.Should().Contain("产品判定为 OK").And.Contain("PLC 终态失败");
            context.TerminalHandshakeAttempted.Should().BeTrue();
            context.TerminalHandshakeSucceeded.Should().BeFalse();
            context.TerminalHandshakeErrorCode.Should().Be("HandshakeV1.AckTimeout");
            context.CycleSucceeded.Should().BeFalse();
            database.SavedRecords.Should().ContainSingle();
            DetectionRecord record = database.SavedRecords[0];
            record.IsQualified.Should().BeTrue();
            record.TerminalHandshakeAttempted.Should().BeTrue();
            record.TerminalHandshakeSucceeded.Should().BeFalse();
            record.TerminalHandshakeErrorCode.Should().Be("HandshakeV1.AckTimeout");
            record.CycleSucceeded.Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static InspectionPipelineService CreateService(
        AppConfig config,
        FakeCameraService camera,
        FakePlcService plc,
        FakeDetectionService detection,
        FakeStatisticsService statistics,
        RecordingDatabaseService database,
        ImageSaveQueue imageQueue,
        DetectionRecordQueue recordQueue,
        float[]? roiSnapshot = null)
    {
        var storage = new FakeStorageService(config.StoragePath);
        var recipeManager = new RecipeManager(Path.Combine(config.StoragePath, "default_recipe.json"));
        recipeManager.LoadOrCreateDefault(config);
        var modelRegistry = new ModelRegistry();
        var healthMonitor = new HealthMonitor(
            camera,
            plc,
            detection,
            storage,
            imageQueue,
            recordQueue);

        return new InspectionPipelineService(
            config,
            camera,
            detection,
            plc,
            storage,
            statistics,
            imageQueue,
            recordQueue,
            recipeManager,
            modelRegistry,
            healthMonitor,
            () => roiSnapshot,
            () => "CAM-1");
    }

    private static AppConfig CreateConfig(string tempDir, bool barcodeEnabled, bool barcodeRequired)
    {
        var ruleSet = new InspectionRuleSet
        {
            Rules = new List<InspectionRule>
            {
                new InspectionRule
                {
                    Name = "part-count",
                    Type = InspectionRuleTypes.Count,
                    Label = "part",
                    Operator = InspectionRuleOperators.Equal,
                    Count = 1
                }
            }
        };

        return new AppConfig
        {
            StoragePath = tempDir,
            BarcodeEnabled = barcodeEnabled,
            BarcodeRequired = barcodeRequired,
            PlcOkValue = 1,
            PlcNgValue = 0,
            PlcProtocolMode = PlcProtocolMode.Legacy,
            InspectionRuleSetJson = InspectionRuleSetSerializer.Serialize(ruleSet)
        };
    }

    private static InspectionContext CreateContext(string inspectionId, int? triggerSeq, string triggerSource = "TEST")
    {
        return new InspectionContext
        {
            InspectionId = inspectionId,
            TriggerTime = DateTimeOffset.Now,
            TriggerSource = triggerSource,
            TriggerSeq = triggerSeq,
            CurrentStage = InspectionStage.Triggered,
            TraceStatus = TraceStatus.Unknown
        };
    }

    private static YoloResult Detection(float centerX, float centerY, float width, float height, float confidence, int classId)
    {
        var result = new YoloResult();
        result.SetDetectionData(centerX, centerY, width, height, confidence, classId);
        return result;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostPipelineTests",
            Guid.NewGuid().ToString("N"));
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

    private sealed class FakeCameraService : ICameraService
    {
        private readonly Mat? _frame;
        private readonly Queue<Mat?> _captureSequence = new Queue<Mat?>();
        private readonly List<Mat?> _ownedSequenceFrames = new List<Mat?>();

        public FakeCameraService(Mat? frame)
        {
            _frame = frame;
        }

        public static FakeCameraService WithCaptureSequence(params Mat?[] frames)
        {
            var service = new FakeCameraService(null);
            foreach (Mat? frame in frames)
            {
                service._captureSequence.Enqueue(frame);
                service._ownedSequenceFrames.Add(frame);
            }

            return service;
        }

        public event Action<Mat>? FrameCaptured;
        public event Action<bool>? ConnectionChanged;
        public event Action<string>? ErrorOccurred;

        public int CaptureCalls { get; private set; }
        public int StartCaptureCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public string? LastErrorOverride { get; set; }

        public bool IsOpen => true;
        public string CameraName => "FakeCamera";
        public string? LastError => LastErrorOverride;
        public Mat? LastFrame => _frame;
        public bool IsGrabbing => true;

        public bool Open(string serialNumber, string manufacturer)
        {
            OpenCalls++;
            return true;
        }

        public void Close()
        {
            CloseCalls++;
        }

        public void StartCapture()
        {
            StartCaptureCalls++;
        }

        public void StopCapture() { }
        public void TriggerOnce() { }

        public Mat? CaptureFrame(int timeoutMs = 3000)
        {
            CaptureCalls++;
            Mat? source = _captureSequence.Count > 0 ? _captureSequence.Dequeue() : _frame;
            if (source == null)
            {
                LastErrorOverride ??= "timeout";
                return null;
            }

            LastErrorOverride = null;
            return source.Clone();
        }

        public void SetExposure(double exposureUs) { }
        public void SetGain(double gain) { }
        public void Dispose()
        {
            _frame?.Dispose();
            foreach (Mat? frame in _ownedSequenceFrames)
            {
                frame?.Dispose();
            }
        }
    }

    private sealed class FakePlcService : IPlcService
    {
        public event Action<bool>? ConnectionChanged;
        public event Action? TriggerReceived;
        public event Action<PlcTriggerContext>? TriggerContextReceived;
        public event Action<string>? ErrorOccurred;

        public bool IsConnected { get; init; } = true;
        public string ProtocolName => "Fake";
        public string? LastError { get; init; }
        public string BarcodeValue { get; init; } = "SN-DEFAULT";
        public List<short> WrittenValues { get; } = new List<short>();
        public List<(string Address, short Value)> Writes { get; } = new List<(string Address, short Value)>();
        public Queue<(bool Success, short Value)> ReadSequence { get; } = new Queue<(bool Success, short Value)>();
        public int ReadStringCalls { get; private set; }

        public Task<bool> ConnectAsync(PlcConnectionOptions options) => Task.FromResult(true);
        public void Disconnect() { }
        public bool StartMonitoring(
            string triggerAddress,
            int pollingIntervalMs = 500,
            int triggerDelayMs = 800,
            PlcMonitoringOptions? options = null)
        {
            return true;
        }

        public void StopMonitoring() { }
        public Task StopMonitoringAsync(System.Threading.CancellationToken cancellationToken = default)
        {
            StopMonitoring();
            return Task.CompletedTask;
        }
        public Task<bool> WriteResultAsync(string resultAddress, bool isQualified)
            => WriteResultAsync(resultAddress, (short)(isQualified ? 1 : 0));

        public Task<bool> WriteResultAsync(string resultAddress, short valueToWrite)
        {
            Writes.Add((resultAddress, valueToWrite));
            WrittenValues.Add(valueToWrite);
            return Task.FromResult(true);
        }

        public Task<bool> WriteReleaseSignalAsync(string resultAddress) => Task.FromResult(true);
        public Task<(bool Success, short Value)> ReadWordAsync(string address)
        {
            if (ReadSequence.Count > 0)
            {
                return Task.FromResult(ReadSequence.Dequeue());
            }

            return Task.FromResult((true, (short)0));
        }
        public Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
        {
            ReadStringCalls++;
            return Task.FromResult((true, BarcodeValue));
        }
        public void Dispose() { }
    }

    private sealed class FakeDetectionService : IDetectionService
    {
        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        public DetectionResultData DetectionResult { get; init; } = new DetectionResultData
        {
            Results = new List<YoloResult>(),
            UsedModelName = "fake.onnx",
            UsedModelLabels = new[] { "part" }
        };
        public Exception? ThrowOnDetect { get; init; }

        public int DetectMatCalls { get; private set; }
        public bool IsModelLoaded => true;
        public string CurrentModelName => "fake.onnx";
        public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
        public long LastInferenceMs => 0;
        public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();
        public DetectionRuntimeModelSnapshot RuntimeModelSnapshot { get; } = new DetectionRuntimeModelSnapshot();

        public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
        public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(true);
        public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(true);

        public Task<DetectionResultData> DetectAsync(
            Mat image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal = null,
            MultiModelCandidateEvaluator? candidateEvaluator = null)
        {
            DetectMatCalls++;
            if (ThrowOnDetect != null)
            {
                throw ThrowOnDetect;
            }

            return Task.FromResult(DetectionResult);
        }

        public Task<DetectionResultData> DetectAsync(
            Bitmap image,
            float confidence,
            float iouThreshold,
            InspectionFallbackGoal? fallbackGoal = null,
            MultiModelCandidateEvaluator? candidateEvaluator = null)
            => Task.FromResult(DetectionResult);

        public Bitmap GenerateResultImage(Bitmap original, List<YoloResult> results, string[] labels)
            => new Bitmap(original);
        public void SetTaskMode(int taskType) { }
        public void SetEnableFallback(bool enabled) { }
        public Task<bool> LoadAuxiliary1ModelAsync(string modelPath) => Task.FromResult(true);
        public Task<bool> LoadAuxiliary2ModelAsync(string modelPath) => Task.FromResult(true);
        public void UnloadPrimaryModel() { }
        public void UnloadAuxiliary1Model() { }
        public void UnloadAuxiliary2Model() { }
        public string[] GetLabels() => new[] { "part" };
        public object? GetLastMetrics() => null;
        public void Dispose() { }
    }

    private sealed class FakeStatisticsService : IStatisticsService
    {
        private readonly StatisticsHistory _history = new StatisticsHistory();
        private readonly DetectionStatistics _stats = new DetectionStatistics();

        public event Action<StatisticsSnapshot>? StatisticsUpdated;
        public event Action? DayReset;

        public int Total { get; private set; }
        public int Qualified { get; private set; }
        public int Unqualified { get; private set; }
        public StatisticsSnapshot Current => new StatisticsSnapshot
        {
            TotalCount = Total,
            QualifiedCount = Qualified,
            UnqualifiedCount = Unqualified
        };
        public int TodayQualified => Qualified;
        public int TodayUnqualified => Unqualified;
        public int TodayTotal => Total;
        public IReadOnlyList<DailyStatisticsRecord> History => Array.Empty<DailyStatisticsRecord>();

        public void RecordDetection(bool isQualified)
        {
            Total++;
            if (isQualified)
            {
                Qualified++;
            }
            else
            {
                Unqualified++;
            }
        }

        public void ResetToday() { }
        public bool CheckAndResetForNewDay() => false;
        public void SaveAll() { }
        public void ClearHistory() { }
        public void LoadAll() { }
        public void UpdateStoragePath(string basePath) { }
        public (StatisticsHistory history, DetectionStatistics stats) GetStatisticsData() => (_history, _stats);
        public void Dispose() { }
    }

    private sealed class FakeStorageService : IStorageService
    {
        public FakeStorageService(string basePath)
        {
            ImageBasePath = Path.Combine(basePath, "Images");
            LogBasePath = Path.Combine(basePath, "Logs");
            SystemPath = Path.Combine(basePath, "System");
            BaseStoragePath = basePath;
            Directory.CreateDirectory(ImageBasePath);
            Directory.CreateDirectory(LogBasePath);
            Directory.CreateDirectory(SystemPath);
        }

        public string ImageBasePath { get; }
        public string LogBasePath { get; }
        public string SystemPath { get; }
        public string BaseStoragePath { get; private set; }
        public List<string> DetectionLogs { get; } = new List<string>();
        public List<string> ErrorLogs { get; } = new List<string>();

        public void SaveDetectionImage(Bitmap bitmap, bool isQualified) { }
        public void SaveDetectionImageAsync(Bitmap bitmap, bool isQualified) { }
        public void WriteDetectionLog(string content, bool isQualified) => DetectionLogs.Add(content);
        public void WriteStartupLog(string action, string? serialNumber = null) { }
        public void WriteErrorLog(string message) => ErrorLogs.Add(message);
        public void CleanOldData(int retainDays) { }
        public double GetDiskFreeSpaceGb() => 100.0;
        public double PerformEmergencyCleanup() => 100.0;
        public void EnsureDirectoriesExist() { }
        public void UpdateStoragePath(string storagePath) => BaseStoragePath = storagePath;
        public void Dispose() { }
    }

    private sealed class RecordingDatabaseService : IDatabaseService
    {
        public List<DetectionRecord> SavedRecords { get; } = new List<DetectionRecord>();

        public Task InitializeAsync() => Task.CompletedTask;

        public Task SaveDetectionRecordAsync(DetectionRecord record)
        {
            SavedRecords.Add(record);
            return Task.CompletedTask;
        }

        public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
            => Task.FromResult(new List<DetectionRecord>());

        public Task<DetectionRecord?> GetDetectionRecordByIdAsync(long id)
            => Task.FromResult<DetectionRecord?>(null);

        public Task<List<DetectionRecord>> GetDetectionRecordsByInspectionIdAsync(string inspectionId)
            => Task.FromResult(new List<DetectionRecord>());

        public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
            => Task.FromResult(new List<DetectionTraceRecord>());

        public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
            => Task.FromResult(new DetectionTracePage());

        public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
            => Task.FromResult(new List<DetectionRecord>());

        public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
            => Task.FromResult(new List<string>());

        public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
            => Task.FromResult(new List<string>());

        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
            => Task.FromResult((0, 0, 0));

        public Task<int> CleanupOldRecordsAsync(int daysToKeep)
            => Task.FromResult(0);

        public void Dispose() { }
    }
}
#pragma warning restore CS0067
