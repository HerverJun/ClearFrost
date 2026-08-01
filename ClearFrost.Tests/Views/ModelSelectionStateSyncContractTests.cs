using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class ModelSelectionStateSyncContractTests
{
    [Fact]
    public void 模型切换完成后会把前端选择同步回后端真实配置()
    {
        string root = FindRepositoryRoot();
        string initSource = NormalizeLineEndings(File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "主窗口.Init.cs")));
        string visionSource = NormalizeLineEndings(File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "主窗口.Vision.cs")));

        initSource.Should().Contain("private async Task SyncModelSelectionStateAsync()");
        initSource.Should().Contain("await _uiController.InitSettings(_appConfig)");
        initSource.Should().Contain("await _uiController.SendModelList(GetModelListPayload())");
        initSource.Should().Contain("modelId = option.ModelId");
        initSource.Should().Contain("version = option.Version");
        initSource.Should().Contain("sha256 = option.Sha256");
        initSource.Should().Contain("taskType = option.TaskType");
        initSource.Should().Contain("postprocessorKey = option.PostprocessorKey");
        initSource.Should().Contain("scoreNormalization = option.ScoreNormalization");
        initSource.Should().Contain("postprocessOptions = option.PostprocessOptions");
        initSource.Should().Contain("inputWidth = option.InputWidth");
        initSource.Should().Contain("inputHeight = option.InputHeight");
        initSource.Should().Contain("labelCount = option.LabelCount");
        initSource.Should().Contain("同步模型选择状态失败");
        initSource.Should().Contain("_uiController.OnSetAuxiliary1Model += async");
        initSource.Should().Contain("_uiController.OnSetAuxiliary2Model += async");
        CountOccurrences(
                initSource,
                "finally\n                {\n                    await SyncModelSelectionStateAsync();\n                }")
            .Should().BeGreaterThanOrEqualTo(2);

        visionSource.Should().Contain("SafeFireAndForget(SyncModelSelectionStateAsync(), \"同步模型选择状态\")");
        visionSource.Should().Contain("模型名 = _appConfig.CurrentModelFileName ?? string.Empty;");
        visionSource.Should().Contain("finally\n            {\n                await SyncModelSelectionStateAsync();\n            }");
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
