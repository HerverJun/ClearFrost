using System;
using ClearFrost.Services;
using FluentAssertions;
using Xunit;

namespace ClearFrost.Tests.Services;

public class OperatorPermissionServiceTests
{
    [Fact]
    public void Authorize_未登录_拒绝关键操作()
    {
        OperatorPermissionDecision decision = OperatorPermissionService.Authorize(
            new OperatorSession(),
            OperatorPermission.ManageSettings,
            "保存系统设置");

        decision.Allowed.Should().BeFalse();
        decision.Message.Should().Contain("请先登录操作员");
        decision.RequiredRole.Should().Be(OperatorPermissionService.RoleEngineer);
    }

    [Fact]
    public void Authorize_普通操作员_拒绝工程师操作()
    {
        var session = new OperatorSession
        {
            OperatorName = "OP-01",
            Role = OperatorPermissionService.RoleOperator,
            ShiftName = "A班",
            SignedInAt = DateTimeOffset.Now
        };

        OperatorPermissionDecision decision = OperatorPermissionService.Authorize(
            session,
            OperatorPermission.ImportModelPackage,
            "导入模型包");

        decision.Allowed.Should().BeFalse();
        decision.Message.Should().Contain("需要 Engineer");
        decision.OperatorRole.Should().Be(OperatorPermissionService.RoleOperator);
    }

    [Theory]
    [InlineData("Engineer")]
    [InlineData("Administrator")]
    [InlineData("工程师")]
    [InlineData("管理员")]
    public void Authorize_工程师或管理员_允许关键操作(string role)
    {
        var session = new OperatorSession
        {
            OperatorName = "ENG-01",
            Role = role,
            ShiftName = "B班",
            SignedInAt = DateTimeOffset.Now
        };

        OperatorPermissionDecision decision = OperatorPermissionService.Authorize(
            session,
            OperatorPermission.ManageCamera,
            "删除相机配置");

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void Authorize_技术员_允许导出诊断包但不能改系统设置()
    {
        var session = new OperatorSession
        {
            OperatorName = "TECH-01",
            Role = OperatorPermissionService.RoleTechnician,
            ShiftName = "C班",
            SignedInAt = DateTimeOffset.Now
        };

        OperatorPermissionDecision diagnostics = OperatorPermissionService.Authorize(
            session,
            OperatorPermission.ExportDiagnostics,
            "导出诊断包");
        OperatorPermissionDecision settings = OperatorPermissionService.Authorize(
            session,
            OperatorPermission.ManageSettings,
            "保存系统设置");

        diagnostics.Allowed.Should().BeTrue();
        diagnostics.RequiredRole.Should().Be(OperatorPermissionService.RoleTechnician);
        settings.Allowed.Should().BeFalse();
        settings.RequiredRole.Should().Be(OperatorPermissionService.RoleEngineer);
    }

    [Theory]
    [InlineData(OperatorPermission.RunManualInspection, "手动检测")]
    [InlineData(OperatorPermission.OperateProductionHardware, "打开生产相机")]
    public void Authorize_现场生产操作_要求已登录操作员(OperatorPermission permission, string operation)
    {
        OperatorPermissionDecision unsignedDecision = OperatorPermissionService.Authorize(
            new OperatorSession(),
            permission,
            operation);

        var operatorSession = new OperatorSession
        {
            OperatorName = "OP-01",
            Role = OperatorPermissionService.RoleOperator,
            ShiftName = "A班",
            SignedInAt = DateTimeOffset.Now
        };
        OperatorPermissionDecision signedDecision = OperatorPermissionService.Authorize(
            operatorSession,
            permission,
            operation);

        unsignedDecision.Allowed.Should().BeFalse();
        unsignedDecision.RequiredRole.Should().Be(OperatorPermissionService.RoleOperator);
        unsignedDecision.Message.Should().Contain("请先登录操作员");
        signedDecision.Allowed.Should().BeTrue();
        signedDecision.RequiredRole.Should().Be(OperatorPermissionService.RoleOperator);
    }

    [Fact]
    public void Authorize_强制放行_要求技术员或更高权限()
    {
        var operatorSession = new OperatorSession
        {
            OperatorName = "OP-01",
            Role = OperatorPermissionService.RoleOperator,
            ShiftName = "A班",
            SignedInAt = DateTimeOffset.Now
        };
        var technicianSession = new OperatorSession
        {
            OperatorName = "TECH-01",
            Role = OperatorPermissionService.RoleTechnician,
            ShiftName = "A班",
            SignedInAt = DateTimeOffset.Now
        };

        OperatorPermissionDecision operatorDecision = OperatorPermissionService.Authorize(
            operatorSession,
            OperatorPermission.ManualRelease,
            "手动放行");
        OperatorPermissionDecision technicianDecision = OperatorPermissionService.Authorize(
            technicianSession,
            OperatorPermission.ManualRelease,
            "手动放行");

        operatorDecision.Allowed.Should().BeFalse();
        operatorDecision.RequiredRole.Should().Be(OperatorPermissionService.RoleTechnician);
        technicianDecision.Allowed.Should().BeTrue();
        technicianDecision.RequiredRole.Should().Be(OperatorPermissionService.RoleTechnician);
    }

    [Fact]
    public void AuthorizeRoleGrant_普通操作员登录_无需上级授权()
    {
        OperatorPermissionDecision decision = OperatorPermissionService.AuthorizeRoleGrant(
            new OperatorSession(),
            OperatorPermissionService.RoleOperator,
            isTrustedSystemPrincipal: false,
            "登录操作员");

        decision.Allowed.Should().BeTrue();
        decision.RequiredRole.Should().Be(OperatorPermissionService.RoleOperator);
    }

    [Fact]
    public void AuthorizeRoleGrant_未登录请求工程师_拒绝()
    {
        OperatorPermissionDecision decision = OperatorPermissionService.AuthorizeRoleGrant(
            new OperatorSession(),
            OperatorPermissionService.RoleEngineer,
            isTrustedSystemPrincipal: false,
            "登录操作员");

        decision.Allowed.Should().BeFalse();
        decision.RequiredRole.Should().Be(OperatorPermissionService.RoleEngineer);
        decision.Message.Should().Contain("需要当前 Engineer 或更高权限确认");
    }

    [Fact]
    public void AuthorizeRoleGrant_当前工程师_允许切换到技术员或工程师()
    {
        var engineerSession = new OperatorSession
        {
            OperatorName = "ENG-01",
            Role = OperatorPermissionService.RoleEngineer,
            ShiftName = "A班",
            SignedInAt = DateTimeOffset.Now
        };

        OperatorPermissionDecision technician = OperatorPermissionService.AuthorizeRoleGrant(
            engineerSession,
            OperatorPermissionService.RoleTechnician,
            isTrustedSystemPrincipal: false,
            "登录操作员");
        OperatorPermissionDecision engineer = OperatorPermissionService.AuthorizeRoleGrant(
            engineerSession,
            OperatorPermissionService.RoleEngineer,
            isTrustedSystemPrincipal: false,
            "登录操作员");

        technician.Allowed.Should().BeTrue();
        engineer.Allowed.Should().BeTrue();
    }

    [Fact]
    public void AuthorizeRoleGrant_受信任系统管理员_允许授予高权限角色()
    {
        OperatorPermissionDecision decision = OperatorPermissionService.AuthorizeRoleGrant(
            new OperatorSession(),
            OperatorPermissionService.RoleAdministrator,
            isTrustedSystemPrincipal: true,
            "登录操作员");

        decision.Allowed.Should().BeTrue();
        decision.RequiredRole.Should().Be(OperatorPermissionService.RoleAdministrator);
    }
}
