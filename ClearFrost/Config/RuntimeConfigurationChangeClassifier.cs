// ============================================================================
// 文件名: RuntimeConfigurationChangeClassifier.cs
// 描述:   运行态配置变更分类
// ============================================================================

using System;

namespace ClearFrost.Config
{
    internal static class RuntimeConfigurationChangeClassifier
    {
        public static bool ShouldIgnoreForSystemConfigChange(string propertyName)
        {
            return string.Equals(propertyName, nameof(AppConfig.CurrentOperatorId), StringComparison.Ordinal);
        }
    }
}
