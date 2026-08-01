using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
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
        public async Task ImageSaveQueue_写图返回False会计入失败()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostImageQueueTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                using var queue = new ImageSaveQueue(
                    capacity: 4,
                    maxBufferedBytes: 1024 * 1024,
                    imageWriter: _ => false);
                using var image = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(128));

                queue.Enqueue(image, Path.Combine(tempDir, "failed-write.jpg")).Should().BeTrue();

                await queue.StopAsync();

                queue.SavedCount.Should().Be(0);
                queue.FailedCount.Should().Be(1);
                queue.PendingCount.Should().Be(0);
                queue.DroppedCount.Should().Be(0);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public async Task ImageSaveQueue_拒绝链接目录目标且不调用写图器()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostImageQueueTests", Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), "ClearFrostImageQueueTests", Guid.NewGuid().ToString("N"));
            string linkedDir = string.Empty;
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(externalDir);
            int writeCalls = 0;

            try
            {
                linkedDir = Path.Combine(tempDir, "linked-images");
                if (!TryCreateDirectorySymbolicLink(linkedDir, externalDir))
                {
                    return;
                }

                using var queue = new ImageSaveQueue(
                    capacity: 4,
                    maxBufferedBytes: 1024 * 1024,
                    imageWriter: payload =>
                    {
                        Interlocked.Increment(ref writeCalls);
                        File.WriteAllText(payload.Path, "external image");
                        return true;
                    });
                using var image = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(128));

                queue.Enqueue(image, Path.Combine(linkedDir, "frame.jpg")).Should().BeTrue();

                await queue.StopAsync();

                writeCalls.Should().Be(0);
                queue.SavedCount.Should().Be(0);
                queue.FailedCount.Should().Be(1);
                Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
            }
            finally
            {
                TryDeleteDirectoryLink(linkedDir);
                DeleteDirectory(tempDir);
                DeleteDirectory(externalDir);
            }
        }

        [Fact]
        public async Task ImageSaveQueue_队列满时丢弃最旧待写项并保持计数一致()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostImageQueueTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            using var firstWriteEntered = new ManualResetEventSlim(false);
            using var releaseWrites = new ManualResetEventSlim(false);
            var savedNames = new ConcurrentQueue<string>();
            int writeCalls = 0;

            try
            {
                using var queue = new ImageSaveQueue(
                    capacity: 2,
                    maxBufferedBytes: long.MaxValue,
                    imageWriter: payload =>
                    {
                        if (Interlocked.Increment(ref writeCalls) == 1)
                        {
                            firstWriteEntered.Set();
                            if (!releaseWrites.Wait(TimeSpan.FromSeconds(5)))
                            {
                                return false;
                            }
                        }

                        savedNames.Enqueue(Path.GetFileName(payload.Path));
                        return true;
                    });
                using var image = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(128));

                queue.Enqueue(image, Path.Combine(tempDir, "frame-1.jpg")).Should().BeTrue();
                firstWriteEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

                queue.Enqueue(image, Path.Combine(tempDir, "frame-2.jpg")).Should().BeTrue();
                queue.Enqueue(image, Path.Combine(tempDir, "frame-3.jpg")).Should().BeTrue();
                queue.PendingCount.Should().Be(2);

                queue.Enqueue(image, Path.Combine(tempDir, "frame-4.jpg")).Should().BeTrue();

                queue.DroppedCount.Should().Be(1);
                queue.PendingCount.Should().Be(2);

                releaseWrites.Set();
                await queue.StopAsync();

                queue.SavedCount.Should().Be(3);
                queue.FailedCount.Should().Be(0);
                queue.DroppedCount.Should().Be(1);
                queue.PendingCount.Should().Be(0);
                savedNames.Should().BeEquivalentTo(new[]
                {
                    "frame-1.jpg",
                    "frame-3.jpg",
                    "frame-4.jpg"
                });
            }
            finally
            {
                releaseWrites.Set();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void DetectionTraceOutbox_拒绝链接Outbox目录且不写入外部目标()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostTraceOutboxTests", Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), "ClearFrostTraceOutboxTests", Guid.NewGuid().ToString("N"));
            string dataDir = Path.Combine(tempDir, "Data");
            string linkedOutbox = Path.Combine(dataDir, "outbox");
            Directory.CreateDirectory(dataDir);
            Directory.CreateDirectory(externalDir);

            try
            {
                if (!TryCreateDirectorySymbolicLink(linkedOutbox, externalDir))
                {
                    return;
                }

                DetectionTraceOutbox.Append(CreateTracePayload(), "linked-outbox", dataDir);

                Directory.EnumerateFileSystemEntries(externalDir).Should().BeEmpty();
            }
            finally
            {
                TryDeleteDirectoryLink(linkedOutbox);
                DeleteDirectory(tempDir);
                DeleteDirectory(externalDir);
            }
        }

        [Fact]
        public void DetectionTraceOutbox_拒绝链接Outbox文件且不修改外部文件()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "ClearFrostTraceOutboxTests", Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), "ClearFrostTraceOutboxTests", Guid.NewGuid().ToString("N"));
            string dataDir = Path.Combine(tempDir, "Data");
            string outboxDir = Path.Combine(dataDir, "outbox");
            string linkedOutboxFile = Path.Combine(outboxDir, $"detection-trace-{DateTime.Now:yyyyMMdd}.ndjson");
            Directory.CreateDirectory(outboxDir);
            Directory.CreateDirectory(externalDir);

            try
            {
                string externalFile = Path.Combine(externalDir, "external.ndjson");
                File.WriteAllText(externalFile, "external trace");
                if (!TryCreateFileSymbolicLink(linkedOutboxFile, externalFile))
                {
                    return;
                }

                DetectionTraceOutbox.Append(CreateTracePayload(), "linked-file", dataDir);

                File.ReadAllText(externalFile).Should().Be("external trace");
            }
            finally
            {
                TryDeleteFileLink(linkedOutboxFile);
                DeleteDirectory(tempDir);
                DeleteDirectory(externalDir);
            }
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
        public void DetectionTraceImageResolver_旧目录回退忽略非法时间片段()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

            try
            {
                string imageDir = Path.Combine(tempDir, "NG", "2026-01-27");
                Directory.CreateDirectory(imageDir);
                string invalidImagePath = Path.Combine(imageDir, "146050_563.jpg");
                File.WriteAllText(invalidImagePath, "invalid legacy timestamp");
                File.SetLastWriteTime(invalidImagePath, new DateTime(2026, 1, 26, 0, 0, 0));

                var record = new DetectionTraceRecord
                {
                    Timestamp = new DateTime(2026, 1, 27, 15, 0, 50, 563),
                    IsQualified = false
                };

                DetectionTraceImageResolution resolved = DetectionTraceImageResolver.Resolve(record, tempDir);

                resolved.ImagePath.Should().BeEmpty();
                resolved.UsedFallbackImagePath.Should().BeFalse();
                resolved.HasRenderedImage.Should().BeFalse();
            }
            finally
            {
                DeleteDirectory(tempDir);
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
        public void DetectionTraceImageResolver_拒绝链接带框图并回退原图()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string renderedPath = string.Empty;

            try
            {
                string imagePath = Path.Combine(tempDir, "FAIL_CF-TRACE-001.jpg");
                string renderedDir = Path.Combine(tempDir, "Rendered");
                renderedPath = Path.Combine(renderedDir, "FAIL_CF-TRACE-001_rendered.jpg");
                string externalRendered = Path.Combine(externalDir, "external-rendered.jpg");
                Directory.CreateDirectory(renderedDir);
                Directory.CreateDirectory(externalDir);
                File.WriteAllText(imagePath, "trusted original");
                File.WriteAllText(externalRendered, "external rendered");
                if (!TryCreateFileSymbolicLink(renderedPath, externalRendered))
                {
                    return;
                }

                var record = new DetectionTraceRecord
                {
                    ImagePath = imagePath,
                    RenderedImagePath = renderedPath
                };

                DetectionTraceImageResolution resolved = DetectionTraceImageResolver.Resolve(record);

                resolved.HasRenderedImage.Should().BeFalse();
                resolved.RenderedImagePath.Should().BeEmpty();
                resolved.DisplayImagePath.Should().Be(imagePath);
                resolved.MissingRenderedImage.Should().BeTrue();
                File.ReadAllText(externalRendered).Should().Be("external rendered");
            }
            finally
            {
                TryDeleteFileLink(renderedPath);
                DeleteDirectory(tempDir);
                DeleteDirectory(externalDir);
            }
        }

        [Fact]
        public void DetectionTraceImageResolver_兜底扫描跳过链接原图()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string externalDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            string linkedImage = string.Empty;

            try
            {
                string imageDir = Path.Combine(tempDir, "Unqualified", "2026年07月05日", "11");
                Directory.CreateDirectory(imageDir);
                Directory.CreateDirectory(externalDir);
                const string inspectionId = "CF-20260705-112233444-TEST-000001";
                string externalImage = Path.Combine(externalDir, "external-original.jpg");
                linkedImage = Path.Combine(imageDir, $"FAIL_{inspectionId}.jpg");
                File.WriteAllText(externalImage, "external original");
                if (!TryCreateFileSymbolicLink(linkedImage, externalImage))
                {
                    return;
                }

                var record = new DetectionTraceRecord
                {
                    Timestamp = new DateTime(2026, 7, 5, 11, 22, 33, 444),
                    IsQualified = false,
                    InspectionId = inspectionId
                };

                DetectionTraceImageResolution resolved = DetectionTraceImageResolver.Resolve(record, tempDir);

                resolved.ImagePath.Should().BeEmpty();
                resolved.UsedFallbackImagePath.Should().BeFalse();
                resolved.HasRenderedImage.Should().BeFalse();
                File.ReadAllText(externalImage).Should().Be("external original");
            }
            finally
            {
                TryDeleteFileLink(linkedImage);
                DeleteDirectory(tempDir);
                DeleteDirectory(externalDir);
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

        private static DetectionPersistencePayload CreateTracePayload()
        {
            return new DetectionPersistencePayload
            {
                Timestamp = new DateTime(2026, 7, 5, 10, 0, 0),
                IsQualified = false,
                InspectionId = "CF-TRACE-OUTBOX-001",
                TriggerSource = "TEST",
                ModelName = "model-a",
                TargetLabel = "part",
                ExpectedCount = 1,
                ActualCount = 0
            };
        }

        private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
        {
            try
            {
                FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
                link.Refresh();
                return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                return false;
            }
        }

        private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
        {
            try
            {
                FileSystemInfo link = Directory.CreateSymbolicLink(linkPath, targetPath);
                link.Refresh();
                return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
                return false;
            }
        }

        private static void TryDeleteFileLink(string linkPath)
        {
            if (string.IsNullOrWhiteSpace(linkPath))
            {
                return;
            }

            try
            {
                var info = new FileInfo(linkPath);
                info.Refresh();
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    info.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
            }
        }

        private static void TryDeleteDirectoryLink(string linkPath)
        {
            if (string.IsNullOrWhiteSpace(linkPath))
            {
                return;
            }

            try
            {
                var info = new DirectoryInfo(linkPath);
                info.Refresh();
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    info.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
            {
            }
        }

        private static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            var info = new DirectoryInfo(path);
            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
                return;
            }

            Directory.Delete(path, recursive: true);
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

            public void Dispose()
            {
            }
        }
    }
}
