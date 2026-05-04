using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
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
