using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class MaintenanceFeatureGateTests
{
    [Fact]
    public void Evaluate_模型包导入默认禁用_返回审计拒绝契约()
    {
        MaintenanceFeatureDecision decision = MaintenanceFeatureGate.Evaluate(MaintenanceFeature.ModelPackageImport);

        decision.Allowed.Should().BeFalse();
        decision.AuditCategory.Should().Be("Model");
        decision.AuditAction.Should().Be("ImportModelPackageBlocked");
        decision.AuditDetail.Should().Contain("ImportUiDisabled");
        decision.UserMessage.Should().Contain("隐藏");
    }

    [Fact]
    public void Evaluate_配置版本恢复默认禁用_保留目标版本并返回拒绝契约()
    {
        MaintenanceFeatureDecision decision = MaintenanceFeatureGate.Evaluate(
            MaintenanceFeature.ConfigVersionRestore,
            "version-001");

        decision.Allowed.Should().BeFalse();
        decision.AuditCategory.Should().Be("ConfigChange");
        decision.AuditAction.Should().Be("RestoreConfigVersionBlocked");
        decision.AuditDetail.Should().Contain("VersionId=version-001");
        decision.AuditDetail.Should().Contain("RestoreDisabled");
        decision.UserMessage.Should().Contain("仅支持保存和查看");
    }

    [Fact]
    public void Evaluate_告警确认默认禁用_返回单条和全部确认拒绝契约()
    {
        MaintenanceFeatureDecision single = MaintenanceFeatureGate.Evaluate(
            MaintenanceFeature.AlarmAcknowledge,
            "alarm-001");
        MaintenanceFeatureDecision all = MaintenanceFeatureGate.Evaluate(MaintenanceFeature.AlarmAcknowledgeAll);

        single.Allowed.Should().BeFalse();
        single.AuditCategory.Should().Be("Alarm");
        single.AuditAction.Should().Be("AcknowledgeBlocked");
        single.AuditDetail.Should().Contain("AlarmId=alarm-001");
        single.UserMessage.Should().Contain("暂未启用");

        all.Allowed.Should().BeFalse();
        all.AuditCategory.Should().Be("Alarm");
        all.AuditAction.Should().Be("AcknowledgeAllBlocked");
        all.AuditDetail.Should().Contain("AcknowledgementWorkflowDisabled");
    }
}
