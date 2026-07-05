using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIVisionDebugContractTests
{
    [Fact]
    public void WebUi算法调试_关键元素命令和消息存在()
    {
        string root = FindRepositoryRoot();
        string index = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "index.html"));
        string controller = File.ReadAllText(Path.Combine(root, "ClearFrost", "Views", "WebUIController.cs"));
        string renderMain = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "render-main.js"));
        string state = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "state.js"));
        string bundle = File.ReadAllText(Path.Combine(root, "ClearFrost", "html", "js", "bundle.js"));

        index.Should().Contain("id=\"vision-debug-modal\"");
        index.Should().Contain("视觉调试（工程师）");
        index.Should().Contain("用于工程师验证模型、规则和当前相机画面；一线生产无需操作。");
        index.Should().Contain("这是工程师调试工具，生产操作无需进入。");
        index.Should().Contain("第一步：选择场景");
        index.Should().Contain("第二步：获取图片");
        index.Should().Contain("第三步：运行验证");
        index.Should().Contain("还没有可调试图片。请先启动相机并获取一帧，或从历史记录选择样本。");
        index.Should().Contain("本地图片导入暂未开放，请先使用当前相机或历史样本。");
        index.Should().Contain("规则摘要");
        index.Should().Contain("高级：查看/编辑规则 JSON");
        index.Should().Contain("id=\"vision-debug-run-current\"");
        index.Should().Contain("id=\"vision-debug-template-select\"");
        index.Should().Contain("value=\"classification_judge\"");
        index.Should().Contain("value=\"segmentation_area\"");
        index.Should().Contain("value=\"obb_angle\"");
        index.Should().Contain("value=\"pose_keypoints\"");
        index.Should().Contain("id=\"vision-debug-confidence\"");
        index.Should().Contain("id=\"vision-debug-iou\"");
        index.Should().Contain("id=\"vision-debug-target-label\"");
        index.Should().Contain("id=\"vision-debug-target-count\"");
        index.Should().Contain("id=\"vision-debug-roi-enabled\"");
        index.Should().Contain("id=\"vision-debug-preprocess-help\"");
        index.Should().Contain("data-value=\"w5_screw_count\"");
        index.Should().Contain("data-value=\"w6_screw_count\"");
        index.Should().Contain("data-value=\"n5_remote_missing_part\"");
        index.Should().Contain("data-value=\"n6_remote_missing_part\"");
        index.Should().Contain("data-value=\"electric_heating_screw_count\"");
        index.Should().Contain("id=\"vision-debug-batch-limit\"");
        index.Should().Contain("id=\"vision-debug-run-batch\"");
        index.Should().Contain("id=\"vision-debug-param-diff\"");
        index.Should().Contain("id=\"vision-debug-batch-summary\"");
        index.Should().Contain("id=\"vision-debug-task-summary\"");
        index.Should().Contain("分类 Top1");
        index.Should().Contain("分割面积/覆盖率");
        index.Should().Contain("OBB 角度");
        index.Should().Contain("姿态关键点");
        index.Should().Contain("id=\"vision-debug-box-list\"");
        index.Should().Contain("id=\"vision-debug-rule-details\"");
        index.Should().Contain("id=\"vision-debug-overlay\"");
        index.Should().NotContain("Blob 检测");
        index.Should().NotContain("圆检测");
        index.Should().NotContain("模板匹配");

        controller.Should().Contain("OnVisionDebugCommand");
        controller.Should().Contain("case \"vision_debug_run_current\"");
        controller.Should().Contain("case \"vision_debug_run_history\"");
        controller.Should().Contain("case \"vision_debug_run_batch\"");
        controller.Should().Contain("case \"vision_debug_save_params\"");
        controller.Should().Contain("case \"vision_debug_apply_template\"");
        controller.Should().Contain("PostMessage(\"visionDebugResult\"");

        renderMain.Should().Contain("CF_COORDINATE_MAPPING");
        renderMain.Should().Contain("function openVisionDebugPanel");
        renderMain.Should().Contain("function applySelectedVisionDebugTemplate");
        renderMain.Should().Contain("validateVisionDebugRuleJson");
        renderMain.Should().Contain("updateVisionDebugRuleSummary");
        renderMain.Should().Contain("function runVisionDebugCurrent");
        renderMain.Should().Contain("function runVisionDebugBatch");
        renderMain.Should().Contain("projectDefaultTemplates");
        renderMain.Should().Contain("规则 JSON 无效");
        renderMain.Should().Contain("本地图片导入暂未开放，请先使用当前相机或历史样本。");
        renderMain.Should().Contain("renderVisionDebugParameterComparison");
        renderMain.Should().Contain("renderVisionDebugBatchReplay");
        renderMain.Should().Contain("function renderVisionDebugDeepLearning");
        renderMain.Should().Contain("分类判定");
        renderMain.Should().Contain("分割面积");
        renderMain.Should().Contain("OBB 角度");
        renderMain.Should().Contain("姿态关键点");
        renderMain.Should().Contain("关联目标框");
        renderMain.Should().Contain("function redrawVisionDebugOverlay");
        renderMain.Should().Contain("registerMessageHandler(\"visionDebugResult\"");
        state.Should().Contain("applyVisionDebugResult");

        bundle.Should().Contain("function openVisionDebugPanel");
        bundle.Should().Contain("registerMessageHandler(\"visionDebugResult\"");
        bundle.Should().Contain("calculateImageContentMapping");
        bundle.Should().Contain("vision_debug_run_batch");
        bundle.Should().Contain("规则 JSON 无效");
        bundle.Should().Contain("本地图片导入暂未开放，请先使用当前相机或历史样本。");
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
