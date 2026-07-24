// ============================================================================
// 文件名: TriggerSourceRuntimeCoordinator.cs
// 描述:   触发源运行态切换策略
// ============================================================================

using System;
using System.Threading.Tasks;
using ClearFrost.Hardware;

namespace ClearFrost.Services
{
    internal static class TriggerSourceRuntimeCoordinator
    {
        public static async Task<bool> RestartAfterConfigurationChangeAsync(
            bool isProductionRunning,
            Func<Task> stopTriggerSourcesAsync,
            Func<Task<bool>> startTriggerSourceAsync,
            Func<string, Task>? logAsync = null,
            string reason = "运行配置变更")
        {
            if (stopTriggerSourcesAsync == null) throw new ArgumentNullException(nameof(stopTriggerSourcesAsync));
            if (startTriggerSourceAsync == null) throw new ArgumentNullException(nameof(startTriggerSourceAsync));

            if (!isProductionRunning)
            {
                if (logAsync != null)
                {
                    await logAsync($"{reason}: 当前未在生产运行，仅更新配置和诊断，不启动 PLC/串口触发源").ConfigureAwait(false);
                }

                return true;
            }

            await stopTriggerSourcesAsync().ConfigureAwait(false);
            return await startTriggerSourceAsync().ConfigureAwait(false);
        }

        public static bool CanWriteManualRelease(TriggerSource triggerSource)
        {
            return triggerSource == TriggerSource.PLC;
        }

        public static bool IsProductionStartCurrent(
            bool isShutdownInProgress,
            bool isProductionRunning,
            int currentGeneration,
            int startGeneration)
        {
            return !isShutdownInProgress &&
                   isProductionRunning &&
                   currentGeneration == startGeneration;
        }
    }
}
