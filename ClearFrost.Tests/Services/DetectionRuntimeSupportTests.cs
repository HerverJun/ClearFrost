using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services
{
    public class DetectionRuntimeSupportTests
    {
        [Fact]
        public async Task TryEnterAsync_忙碌时返回Busy并累加计数()
        {
            using var gate = new DetectionTriggerGate();

            DetectionTriggerDecision first = await gate.TryEnterAsync(false);
            DetectionTriggerDecision second = await gate.TryEnterAsync(false);

            first.Accepted.Should().BeTrue();
            second.Accepted.Should().BeFalse();
            second.DropReason.Should().Be(DetectionDropReason.Busy);
            gate.GetSnapshot().BusyCount.Should().Be(1);

            gate.Release();
        }

        [Fact]
        public async Task TryEnterAsync_关闭中返回Shutdown并累加计数()
        {
            using var gate = new DetectionTriggerGate();

            DetectionTriggerDecision decision = await gate.TryEnterAsync(true);

            decision.Accepted.Should().BeFalse();
            decision.DropReason.Should().Be(DetectionDropReason.Shutdown);
            gate.GetSnapshot().ShutdownCount.Should().Be(1);
        }

        [Fact]
        public async Task TryEnterAsync_防抖时返回Debounce并累加计数()
        {
            using var gate = new DetectionTriggerGate();

            DetectionTriggerDecision decision = await gate.TryEnterAsync(false, isDebounced: true);

            decision.Accepted.Should().BeFalse();
            decision.DropReason.Should().Be(DetectionDropReason.Debounce);
            gate.GetSnapshot().DebounceCount.Should().Be(1);
        }

        [Fact]
        public void ImageSavePayload_Create会克隆源图像()
        {
            using var source = new Mat(2, 2, MatType.CV_8UC1, Scalar.All(10));
            using ImageSavePayload payload = ImageSavePayload.Create(source, "dummy.jpg");

            source.SetTo(Scalar.All(0));

            payload.Image.At<byte>(0, 0).Should().Be(10);
            payload.Image.At<byte>(1, 1).Should().Be(10);
        }

        [Fact]
        public void ImageSavePayload_CreateReadOnlyView会复用源图像像素缓冲()
        {
            using var source = new Mat(2, 2, MatType.CV_8UC1, Scalar.All(10));
            using ImageSavePayload payload = ImageSavePayload.CreateReadOnlyView(source, "dummy.jpg");

            source.SetTo(Scalar.All(20));

            payload.Image.At<byte>(0, 0).Should().Be(20);
            payload.Image.At<byte>(1, 1).Should().Be(20);
        }

        [Fact]
        public void ImageSavePayload_Create会携带JPEG质量和用途()
        {
            using var source = new Mat(2, 2, MatType.CV_8UC3, Scalar.All(42));
            using ImageSavePayload payload = ImageSavePayload.Create(
                source,
                "trace.jpg",
                jpegQuality: 70,
                purpose: ImageSavePurpose.TraceOriginal);

            payload.JpegQuality.Should().Be(70);
            payload.Purpose.Should().Be(ImageSavePurpose.TraceOriginal);

            ImageEncodingParam[] parameters = ImageSaveQueue.BuildEncodingParams(payload);
            parameters.Should().ContainSingle();
            parameters[0].EncodingId.Should().Be(ImwriteFlags.JpegQuality);
            parameters[0].Value.Should().Be(70);
        }

        [Fact]
        public void ImageSavePayload_记录估算内存字节数()
        {
            using var source = new Mat(3, 5, MatType.CV_8UC3, Scalar.All(42));
            using ImageSavePayload payload = ImageSavePayload.CreateReadOnlyView(source, "trace.jpg");

            payload.EstimatedBytes.Should().Be(source.Step() * source.Rows);
        }

        [Fact]
        public void DetectionTraceImageResolver_优先使用带框图并在缺失时回退原图()
        {
            const string imagePath = @"C:\Trace\FAIL_CF-1.jpg";
            const string renderedPath = @"C:\Trace\Rendered\FAIL_CF-1_rendered.jpg";

            var record = new DetectionTraceRecord
            {
                ImagePath = imagePath,
                RenderedImagePath = renderedPath
            };

            DetectionTraceImageResolution resolved = DetectionTraceImageResolver.Resolve(
                record,
                path => string.Equals(path, renderedPath, StringComparison.OrdinalIgnoreCase));

            resolved.HasRenderedImage.Should().BeTrue();
            resolved.RenderedImagePath.Should().Be(renderedPath);
            resolved.DisplayImagePath.Should().Be(renderedPath);

            DetectionTraceImageResolution fallback = DetectionTraceImageResolver.Resolve(
                new DetectionTraceRecord { ImagePath = imagePath },
                path => string.Equals(path, imagePath, StringComparison.OrdinalIgnoreCase));

            fallback.HasRenderedImage.Should().BeFalse();
            fallback.DisplayImagePath.Should().Be(imagePath);
            fallback.MissingRenderedImage.Should().BeTrue();
        }

        [Fact]
        public void DetectionTraceImageResolver_路径为空时按旧目录时间回退原图()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            try
            {
                string imageDir = Path.Combine(tempDir, "NG", "2026-01-27");
                Directory.CreateDirectory(imageDir);
                string imagePath = Path.Combine(imageDir, "144650_563.jpg");
                File.WriteAllText(imagePath, "legacy image");
                File.SetLastWriteTime(imagePath, new DateTime(2026, 1, 27, 14, 46, 50, 563));

                var record = new DetectionTraceRecord
                {
                    Timestamp = new DateTime(2026, 1, 27, 14, 46, 50),
                    IsQualified = false
                };

                DetectionTraceImageResolution resolved = DetectionTraceImageResolver.Resolve(record, tempDir);

                resolved.ImagePath.Should().Be(imagePath);
                resolved.UsedFallbackImagePath.Should().BeTrue();
                resolved.HasRenderedImage.Should().BeFalse();
                resolved.MissingRenderedImage.Should().BeTrue();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void DetectionTraceImageResolver_路径为空时按InspectionId回退并识别复查图()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            try
            {
                string imageDir = Path.Combine(tempDir, "Unqualified", "2026年05月19日", "10");
                string renderedDir = Path.Combine(imageDir, "Rendered");
                Directory.CreateDirectory(renderedDir);

                const string inspectionId = "CF-20260519-104743741-TEST-000001";
                string imagePath = Path.Combine(imageDir, $"FAIL_{inspectionId}.jpg");
                string renderedPath = Path.Combine(renderedDir, $"FAIL_{inspectionId}_rendered.jpg");
                File.WriteAllText(imagePath, "original image");
                File.WriteAllText(renderedPath, "rendered image");

                var record = new DetectionTraceRecord
                {
                    Timestamp = new DateTime(2026, 5, 19, 10, 47, 43, 741),
                    IsQualified = false,
                    InspectionId = inspectionId
                };

                DetectionTraceImageResolution resolved = DetectionTraceImageResolver.Resolve(record, tempDir);

                resolved.ImagePath.Should().Be(imagePath);
                resolved.RenderedImagePath.Should().Be(renderedPath);
                resolved.HasRenderedImage.Should().BeTrue();
                resolved.UsedFallbackImagePath.Should().BeTrue();
                resolved.UsedDerivedRenderedPath.Should().BeTrue();
                resolved.DisplayImagePath.Should().Be(renderedPath);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        [Fact]
        public void BuildTraceImageFileName_包含安全化条码()
        {
            MethodInfo? method = typeof(global::ClearFrost.主窗口).GetMethod(
                "BuildTraceImageFileName",
                BindingFlags.Static | BindingFlags.NonPublic);

            method.Should().NotBeNull();
            string fileName = (string)method!.Invoke(
                null,
                new object?[] { false, "CF-20260504-123456-MANUAL-000001", "SN:ABC/001" })!;

            fileName.Should().StartWith("FAIL_SN-");
            fileName.Should().Contain("SN_ABC_001");
            fileName.Should().Contain("CF-20260504-123456-MANUAL-000001");
            fileName.Should().EndWith(".jpg");
        }

        [Fact]
        public void ExceptionMessageFormatter_OnnxNative初始化失败_展开内层异常并给出诊断()
        {
            var exception = new TypeInitializationException(
                "Microsoft.ML.OnnxRuntime.NativeMethods",
                new DllNotFoundException("Unable to load DLL 'onnxruntime.dll'."));

            string message = ExceptionMessageFormatter.FormatForLog(exception);

            message.Should().Contain("ONNX Runtime 原生库初始化失败");
            message.Should().Contain("onnxruntime.dll");
            message.Should().Contain("Unable to load DLL");
        }

        [Fact]
        public async Task DetectionRecordQueue_StopAsync会排空已入队记录()
        {
            var database = new RecordingDatabaseService();
            using var queue = new DetectionRecordQueue(database);

            queue.Enqueue(new DetectionPersistencePayload
            {
                Timestamp = DateTime.Now,
                IsQualified = true,
                ModelName = "model-a",
                ActualCount = 1,
                ProductBarcode = "SN-QUEUE-001",
                BarcodeReadSucceeded = true
            }).Should().BeTrue();

            queue.Enqueue(new DetectionPersistencePayload
            {
                Timestamp = DateTime.Now,
                IsQualified = false,
                ModelName = "model-b",
                ActualCount = 2
            }).Should().BeTrue();

            await queue.StopAsync();

            database.SavedRecords.Should().HaveCount(2);
            database.SavedRecords[0].ModelName.Should().Be("model-a");
            database.SavedRecords[0].ProductBarcode.Should().Be("SN-QUEUE-001");
            database.SavedRecords[0].BarcodeReadSucceeded.Should().BeTrue();
            database.SavedRecords[1].ModelName.Should().Be("model-b");
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

            public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
                => Task.FromResult(new List<DetectionTraceRecord>());

            public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
                => Task.FromResult(new DetectionTracePage());

            public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
                => Task.FromResult(new List<string>());

            public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
                => Task.FromResult(new List<string>());

            public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
                => Task.FromResult((0, 0, 0));

            public Task<int> CleanupOldRecordsAsync(int daysToKeep)
                => Task.FromResult(0);

            public void Dispose()
            {
            }
        }
    }
}
