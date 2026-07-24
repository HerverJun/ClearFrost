using ClearFrost.Services;
using FluentAssertions;

namespace ClearFrost.Tests.Services;

public class OperatorFaultMessagesTests
{
    [Theory]
    [InlineData("ReplayEvidenceGateMissing")]
    [InlineData("ReplayEvidencePackageRequired")]
    [InlineData("ReplayEvidenceReportHashMismatch")]
    public void ForCode_严格模型验证错误返回一线中文主提示(string errorCode)
    {
        string message = OperatorFaultMessages.ForCode(errorCode);

        message.Should().Be("当前模型未完成上线验证，请联系工程师完成模型验证，或切换回已验证模型。");
        message.Should().NotContain(errorCode);
    }

    [Theory]
    [InlineData("PrimaryModelReferenceEmpty", "模型未加载：请先在左侧选择主模型。")]
    [InlineData("ModelNotLoaded", "模型未加载：请先在左侧选择主模型。")]
    [InlineData("CameraNotReady", "相机未启动：请点击右下角“启动系统”，或检查相机网线/电源。")]
    [InlineData("PlcNotConnected", "PLC 未连接：请检查 PLC IP、端口和网线。")]
    [InlineData("StartupBlocked", "当前还不能生产：请先处理诊断中心列出的待处理问题。")]
    public void ForCode_常见现场错误返回可操作短句(string errorCode, string expected)
    {
        OperatorFaultMessages.ForCode(errorCode).Should().Be(expected);
    }
}
