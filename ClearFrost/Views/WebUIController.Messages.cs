// ============================================================================
// 文件名: WebUIController.Messages.cs
// 描述:   WebView2 前端统一消息推送扩展
// ============================================================================

using ClearFrost.Config;
using ClearFrost.Interfaces;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ClearFrost
{
    public partial class WebUIController
    {
        public Task SendUiCommand(string action, object? payload = null)
        {
            PostMessage("uiCommand", new { action = action, payload = payload });
            return Task.CompletedTask;
        }

        public Task SendProjectPresets(ProjectPresetStore.Snapshot snapshot)
        {
            PostMessage("projectPresets", new
            {
                presets = snapshot.Presets,
                path = snapshot.Path
            });
            return Task.CompletedTask;
        }

        public Task SendBootstrapSnapshot(
            AppConfig config,
            IEnumerable<object> cameras,
            string activeCameraId,
            string[] models,
            StatisticsSnapshot stats,
            object health,
            string storagePath)
        {
            PostMessage("bootstrapSnapshot", new
            {
                config = config,
                cameras = cameras,
                activeCameraId = activeCameraId,
                models = models,
                stats = new
                {
                    total = stats.TotalCount,
                    ok = stats.QualifiedCount,
                    ng = stats.UnqualifiedCount
                },
                health = health,
                storagePath = storagePath
            });
            return Task.CompletedTask;
        }
    }
}
