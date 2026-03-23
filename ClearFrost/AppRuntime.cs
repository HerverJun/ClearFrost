using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Config;
using ClearFrost.Hardware;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using ClearFrost.Services;

namespace ClearFrost
{
    internal sealed class AppRuntime : IAsyncDisposable, IDisposable
    {
        private bool _stopRequested;
        private bool _disposed;

        public AppRuntime(AppConfig appConfig)
            : this(
                appConfig,
                CreateCameraManager(appConfig),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null)
        {
        }

        internal AppRuntime(
            AppConfig appConfig,
            CameraManager cameraManager,
            ICameraService? cameraService,
            IPlcService? plcService,
            IDetectionService? detectionService,
            IStorageService? storageService,
            IStatisticsService? statisticsService,
            IDatabaseService? databaseService,
            ImageSaveQueue? imageSaveQueue,
            DetectionRecordQueue? detectionRecordQueue,
            WebUIController? webUIController)
        {
            AppConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));
            CameraManager = cameraManager ?? throw new ArgumentNullException(nameof(cameraManager));
            CameraService = cameraService ?? new CameraService(CameraManager);
            PlcService = plcService ?? new PlcService();
            DetectionService = detectionService ?? new DetectionService(appConfig.EnableGpu);
            StorageService = storageService ?? new StorageService(appConfig.StoragePath);
            StatisticsService = statisticsService ?? new StatisticsService(StorageService.SystemPath.Replace("\\System", ""));
            DatabaseService = databaseService ?? new SqliteDatabaseService();
            ImageSaveQueue = imageSaveQueue ?? new ImageSaveQueue();
            DetectionRecordQueue = detectionRecordQueue ?? new DetectionRecordQueue(DatabaseService);
            WebUIController = webUIController ?? new WebUIController();
        }

        public AppConfig AppConfig { get; }

        public CameraManager CameraManager { get; }

        public ICameraService CameraService { get; }

        public IPlcService PlcService { get; }

        public IDetectionService DetectionService { get; }

        public IStorageService StorageService { get; }

        public IStatisticsService StatisticsService { get; }

        public IDatabaseService DatabaseService { get; }

        public ImageSaveQueue ImageSaveQueue { get; }

        public DetectionRecordQueue DetectionRecordQueue { get; }

        public WebUIController WebUIController { get; }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_stopRequested)
            {
                return;
            }

            _stopRequested = true;

            try
            {
                PlcService.StopMonitoring();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 停止 PLC 监听失败: {ex.Message}");
            }

            try
            {
                CameraService.StopCapture();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 停止相机采集失败: {ex.Message}");
            }

            try
            {
                CameraService.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 关闭相机失败: {ex.Message}");
            }

            try
            {
                StatisticsService.SaveAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 保存统计失败: {ex.Message}");
            }

            await DetectionRecordQueue.StopAsync(cancellationToken).ConfigureAwait(false);
            await ImageSaveQueue.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] StopAsync during dispose failed: {ex.Message}");
            }

            try
            {
                DetectionService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 DetectionService 失败: {ex.Message}");
            }

            try
            {
                StatisticsService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 StatisticsService 失败: {ex.Message}");
            }

            try
            {
                DetectionRecordQueue.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 DetectionRecordQueue 失败: {ex.Message}");
            }

            try
            {
                DatabaseService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 DatabaseService 失败: {ex.Message}");
            }

            try
            {
                ImageSaveQueue.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 ImageSaveQueue 失败: {ex.Message}");
            }

            try
            {
                WebUIController.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 WebUIController 失败: {ex.Message}");
            }

            try
            {
                PlcService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 PlcService 失败: {ex.Message}");
            }

            try
            {
                CameraService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 CameraService 失败: {ex.Message}");
            }

            try
            {
                StorageService.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 StorageService 失败: {ex.Message}");
            }

            try
            {
                CameraManager.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 释放 CameraManager 失败: {ex.Message}");
            }

            try
            {
                WindowHelpers.RestoreSleep();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppRuntime] 恢复休眠策略失败: {ex.Message}");
            }

            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static CameraManager CreateCameraManager(AppConfig appConfig)
        {
            var manager = new CameraManager(appConfig.IsDebugMode);
            manager.LoadFromConfig(appConfig);
            return manager;
        }
    }
}
