// ============================================================================
// SerialPhotoelectricTriggerServiceTests.cs - 串口光电触发服务单元测试
// ============================================================================
using ClearFrost.Hardware.Triggers;
using FluentAssertions;
using System.Reflection;

namespace ClearFrost.Tests.Hardware.Triggers;

public class SerialPhotoelectricTriggerServiceTests
{
    private static SerialPhotoelectricTriggerService CreateService()
    {
        return new SerialPhotoelectricTriggerService();
    }

    private static void InvokePrivate(SerialPhotoelectricTriggerService service, string methodName)
    {
        var method = typeof(SerialPhotoelectricTriggerService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull($"方法 {methodName} 应该存在");
        method!.Invoke(service, null);
    }

    private static T GetField<T>(SerialPhotoelectricTriggerService service, string fieldName)
    {
        var field = typeof(SerialPhotoelectricTriggerService).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"字段 {fieldName} 应该存在");
        return (T)field!.GetValue(service)!;
    }

    private static void SetField<T>(SerialPhotoelectricTriggerService service, string fieldName, T value)
    {
        var field = typeof(SerialPhotoelectricTriggerService).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull($"字段 {fieldName} 应该存在");
        field!.SetValue(service, value);
    }

    private static T InvokeStaticPrivate<T>(string methodName, params object?[] args)
    {
        var method = typeof(SerialPhotoelectricTriggerService).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull($"静态方法 {methodName} 应该存在");
        return (T)method!.Invoke(null, args)!;
    }

    [Fact]
    public async Task StartAsync_空串口名称返回失败()
    {
        var service = CreateService();
        bool result = await service.StartAsync("", 9600);

        result.Should().BeFalse();
        service.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StartAsync_空白串口名称返回失败()
    {
        var service = CreateService();
        bool result = await service.StartAsync("   ", 9600);

        result.Should().BeFalse();
        service.LastError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SendTestTriggerAsync_未连接返回失败()
    {
        var service = CreateService();
        bool result = await service.SendTestTriggerAsync();

        result.Should().BeFalse();
        service.LastError.Should().Contain("未打开");
    }

    [Fact]
    public void HandleTriggerFrame_首次触发触发事件()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        InvokePrivate(service, "HandleTriggerFrame");

        triggerCount.Should().Be(1);
        GetField<bool>(service, "_isBlocked").Should().BeTrue();
    }

    [Fact]
    public void HandleTriggerFrame_重复触发不触发事件_阻塞去重()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        InvokePrivate(service, "HandleTriggerFrame");
        InvokePrivate(service, "HandleTriggerFrame");
        InvokePrivate(service, "HandleTriggerFrame");

        triggerCount.Should().Be(1);
        GetField<bool>(service, "_isBlocked").Should().BeTrue();
    }

    [Fact]
    public void HandleResetFrame_重置后允许再次触发()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        // 关闭去抖，避免第二次触发被去抖窗口过滤
        SetField(service, "_debounceMs", 0);

        InvokePrivate(service, "HandleTriggerFrame");
        triggerCount.Should().Be(1);

        InvokePrivate(service, "HandleResetFrame");
        GetField<bool>(service, "_isBlocked").Should().BeFalse();

        InvokePrivate(service, "HandleTriggerFrame");
        triggerCount.Should().Be(2);
    }

    [Fact]
    public void HandleResetFrame_重复重置不产生副作用()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        InvokePrivate(service, "HandleResetFrame");
        InvokePrivate(service, "HandleResetFrame");
        InvokePrivate(service, "HandleTriggerFrame");

        triggerCount.Should().Be(1);
        GetField<bool>(service, "_isBlocked").Should().BeTrue();
    }

    [Fact]
    public void HandleTriggerFrame_去抖时间内忽略()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        // 设置 500ms 去抖
        SetField(service, "_debounceMs", 500);

        InvokePrivate(service, "HandleTriggerFrame");
        triggerCount.Should().Be(1);

        // 立刻再次触发，应被去抖忽略
        InvokePrivate(service, "HandleTriggerFrame");
        triggerCount.Should().Be(1);
    }

    [Fact]
    public void HandleTriggerFrame_去抖时间过后允许再次触发()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        SetField(service, "_debounceMs", 1);
        InvokePrivate(service, "HandleTriggerFrame");
        triggerCount.Should().Be(1);

        // 重置 blocked 状态（模拟收到 01 22）
        InvokePrivate(service, "HandleResetFrame");

        // 等待去抖时间过去
        Thread.Sleep(50);

        InvokePrivate(service, "HandleTriggerFrame");
        triggerCount.Should().Be(2);
    }

    [Fact]
    public void Stop_重置阻塞状态()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        InvokePrivate(service, "HandleTriggerFrame");
        GetField<bool>(service, "_isBlocked").Should().BeTrue();

        service.Stop();

        GetField<bool>(service, "_isBlocked").Should().BeFalse();
        service.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void IsPreferredUsbSerial_识别CH340()
    {
        InvokeStaticPrivate<bool>("IsPreferredUsbSerial", "COM3 - USB-SERIAL CH340").Should().BeTrue();
    }

    [Fact]
    public void IsPreferredUsbSerial_识别FTDI()
    {
        InvokeStaticPrivate<bool>("IsPreferredUsbSerial", "COM4 - FTDI USB UART").Should().BeTrue();
    }

    [Fact]
    public void IsPreferredUsbSerial_识别SiliconLabs()
    {
        InvokeStaticPrivate<bool>("IsPreferredUsbSerial", "COM5 - Silicon Labs CP210x").Should().BeTrue();
    }

    [Fact]
    public void IsPreferredUsbSerial_排除普通串口()
    {
        InvokeStaticPrivate<bool>("IsPreferredUsbSerial", "COM1").Should().BeFalse();
    }

    [Fact]
    public void IsPreferredUsbSerial_排除蓝牙串口()
    {
        InvokeStaticPrivate<bool>("IsPreferredUsbSerial", "COM6 - Bluetooth Serial").Should().BeFalse();
    }

    [Fact]
    public void ConnectionChanged_连接状态变更触发事件()
    {
        var service = CreateService();
        var states = new List<bool>();
        service.ConnectionChanged += connected => states.Add(connected);

        // 通过反射调用 SetConnectionState
        var method = typeof(SerialPhotoelectricTriggerService).GetMethod("SetConnectionState", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();
        method!.Invoke(service, new object[] { true });
        method.Invoke(service, new object[] { false });

        states.Should().Equal(new[] { true, false });
    }

    [Fact]
    public async Task ErrorOccurred_空串口启动触发错误事件()
    {
        var service = CreateService();
        string? error = null;
        service.ErrorOccurred += msg => error = msg;

        await service.StartAsync("", 9600);

        error.Should().NotBeNullOrEmpty();
    }

    // ================== 帧解析测试 (粘包/半包/异常帧) ==================

    private static int InvokeScanBuffer(SerialPhotoelectricTriggerService service, byte[] buffer, int length)
    {
        var method = typeof(SerialPhotoelectricTriggerService).GetMethod("ScanBufferForFrames", BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("ScanBufferForFrames 方法应该存在");
        return (int)method!.Invoke(service, new object[] { buffer, length })!;
    }

    [Fact]
    public void ScanBuffer_正常单帧触发一次()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = new byte[] { 0x01, 0x11 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(2);
        triggerCount.Should().Be(1);
    }

    [Fact]
    public void ScanBuffer_粘包双帧触发一次第二个被阻塞过滤()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        // 粘包中包含两个 01 11，第一个触发后进入 blocked 状态，第二个应被忽略
        byte[] buffer = new byte[] { 0x01, 0x11, 0x01, 0x11 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(4);
        triggerCount.Should().Be(1);
        GetField<bool>(service, "_isBlocked").Should().BeTrue();
    }

    [Fact]
    public void ScanBuffer_粘包触发重置触发各一次()
    {
        var service = CreateService();
        SetField(service, "_debounceMs", 0);
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        // 01 11 触发 -> 01 22 重置 -> 01 11 再次触发
        byte[] buffer = new byte[] { 0x01, 0x11, 0x01, 0x22, 0x01, 0x11 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(6);
        triggerCount.Should().Be(2);
    }

    [Fact]
    public void ScanBuffer_粘包混合触发和重置()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = new byte[] { 0x01, 0x11, 0x01, 0x22 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(4);
        triggerCount.Should().Be(1);
        GetField<bool>(service, "_isBlocked").Should().BeFalse();
    }

    [Fact]
    public void ScanBuffer_异常帧不触发()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = new byte[] { 0x01, 0x33, 0x01, 0x44, 0x01, 0x55 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(5, "最后剩1字节无法判断，应保留");
        triggerCount.Should().Be(0);
    }

    [Fact]
    public void ScanBuffer_垃圾数据中解析出正常帧()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = new byte[] { 0x00, 0x00, 0x01, 0x11, 0x00, 0xFF };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(5, "最后剩1字节未处理");
        triggerCount.Should().Be(1);
    }

    [Fact]
    public void ScanBuffer_半包帧头保留()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = new byte[] { 0x01 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(0, "单字节帧头无法判断，应全部保留");
        triggerCount.Should().Be(0);
    }

    [Fact]
    public void ScanBuffer_复杂混合序列()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        // 00: 垃圾, 01 33: 异常帧, 01 11: 触发, 01 22: 重置, 01: 半包帧头
        byte[] buffer = new byte[] { 0x00, 0x01, 0x33, 0x01, 0x11, 0x01, 0x22, 0x01 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(7, "最后剩1字节半包帧头");
        triggerCount.Should().Be(1);
        GetField<bool>(service, "_isBlocked").Should().BeFalse();
    }

    [Fact]
    public void ScanBuffer_01_22后再次01_11可触发()
    {
        var service = CreateService();
        SetField(service, "_debounceMs", 0);
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        // 先触发，再重置，再触发
        byte[] buffer = new byte[] { 0x01, 0x11, 0x01, 0x22, 0x01, 0x11 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(6);
        triggerCount.Should().Be(2);
    }

    [Fact]
    public void ScanBuffer_空缓冲区不处理()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = Array.Empty<byte>();
        int processed = InvokeScanBuffer(service, buffer, 0);

        processed.Should().Be(0);
        triggerCount.Should().Be(0);
    }

    [Fact]
    public void ScanBuffer_单字节非帧头不处理()
    {
        var service = CreateService();
        int triggerCount = 0;
        service.TriggerReceived += () => triggerCount++;

        byte[] buffer = new byte[] { 0x00 };
        int processed = InvokeScanBuffer(service, buffer, buffer.Length);

        processed.Should().Be(0, "单字节无法判断，应全部保留");
        triggerCount.Should().Be(0);
    }
}
