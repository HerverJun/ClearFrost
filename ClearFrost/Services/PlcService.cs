using ClearFrost.Hardware;
// ============================================================================
// 文件名: PlcService.cs
// 描述:   PLC 通讯服务实现
//
// 功能:
//   - 多协议 PLC 连接管理 (Mitsubishi, Siemens, Omron, Modbus)
//   - 触发信号监听循环
//   - 结果读写操作
// ============================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Interfaces;

namespace ClearFrost.Services
{
    /// <summary>
    /// PLC 通讯服务实现
    /// </summary>
    public class PlcService : IPlcService
    {
        #region 私有字段

        private IPlcDevice? _plcDevice;
        private CancellationTokenSource? _monitoringCts;
        private bool _isConnecting;
        private bool _disposed;

        #endregion

        #region 事件

        public event Action<bool>? ConnectionChanged;
        public event Action? TriggerReceived;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        public bool IsConnected { get; private set; }
        public string ProtocolName => _plcDevice?.ProtocolName ?? "未连接";
        public string? LastError { get; private set; }

        #endregion

        #region 连接管理

        public async Task<bool> ConnectAsync(string protocol, string ip, int port)
        {
            if (_isConnecting) return false;
            _isConnecting = true;

            const int maxRetries = 3;
            const int retryDelayMs = 2000;

            try
            {
                // 停止旧的监听
                await StopMonitoringAsync();

                // 断开现有连接
                Disconnect();

                var protocolType = PlcFactory.ParseProtocol(protocol);
                Debug.WriteLine($"[PlcService] 正在连接 {protocolType} @ {ip}:{port}");

                for (int i = 0; i < maxRetries; i++)
                {
                    _plcDevice = PlcFactory.Create(protocolType, ip, port);
                    bool socketConnected = await _plcDevice.ConnectAsync();

                    if (socketConnected)
                    {
                        // Socket 连接成功后，进行一次读操作验证 PLC 是否真正可通信
                        // HslCommunication 库的 ConnectServer 仅建立 TCP 连接，不验证 PLC 可用性
                        var (readSuccess, _) = await _plcDevice.ReadInt16Async("D0");
                        if (readSuccess)
                        {
                            IsConnected = true;
                            LastError = null;
                            ConnectionChanged?.Invoke(true);
                            Debug.WriteLine($"[PlcService] 连接成功: {_plcDevice.ProtocolName}");
                            return true;
                        }
                        else
                        {
                            // 读操作失败，说明 PLC 未真正可用
                            LastError = _plcDevice.LastError ?? "PLC 连接验证失败：无法读取测试地址";
                            Debug.WriteLine($"[PlcService] 连接验证失败 (读取 D0 失败): {LastError}");
                            _plcDevice.Disconnect();
                            continue; // 继续重试
                        }
                    }

                    LastError = _plcDevice?.LastError ?? "未知错误";
                    Debug.WriteLine($"[PlcService] 连接失败: {LastError}");

                    if (i < maxRetries - 1)
                    {
                        await Task.Delay(retryDelayMs);
                    }
                }

                ConnectionChanged?.Invoke(false);
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ErrorOccurred?.Invoke($"连接异常: {ex.Message}");
                ConnectionChanged?.Invoke(false);
                return false;
            }
            finally
            {
                _isConnecting = false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _plcDevice?.Disconnect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlcService] 断开连接异常: {ex.Message}");
            }
            finally
            {
                _plcDevice = null;
                IsConnected = false;
            }
        }

        #endregion

        #region 监听功能

        public void StartMonitoring(short triggerAddress, int pollingIntervalMs = 500)
        {
            if (_monitoringCts != null) return;

            _monitoringCts = new CancellationTokenSource();
            var token = _monitoringCts.Token;

            _ = Task.Run(async () =>
            {
                await MonitoringLoop(triggerAddress, pollingIntervalMs, token);
            });

            Debug.WriteLine($"[PlcService] 开始监听触发地址: {triggerAddress}");
        }

        public void StopMonitoring()
        {
            if (_monitoringCts != null)
            {
                _monitoringCts.Cancel();
                _monitoringCts.Dispose();
                _monitoringCts = null;
                Debug.WriteLine("[PlcService] 停止监听");
            }
        }

        private async Task StopMonitoringAsync()
        {
            if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
            {
                _monitoringCts.Cancel();
                await Task.Delay(100); // 等待循环退出
            }
            _monitoringCts?.Dispose();
            _monitoringCts = null;
        }

        private async Task MonitoringLoop(short triggerAddress, int pollingIntervalMs, CancellationToken token)
        {
            const int triggerDelay = 800;
            int pollCount = 0;

            Debug.WriteLine($"[PlcService] ▶ 监听循环启动 - 地址: {triggerAddress}, 间隔: {pollingIntervalMs}ms");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_plcDevice == null)
                    {
                        Debug.WriteLine("[PlcService] ⚠ PLC设备为空，退出监听");
                        break;
                    }

                    string address = GetPlcAddress(triggerAddress);
                    var (success, value) = await _plcDevice.ReadInt16Async(address);
                    pollCount++;

                    // 每10次轮询输出一次状态（避免日志过多）
                    if (pollCount % 10 == 0)
                    {
                        Debug.WriteLine($"[PlcService] 📡 轮询 #{pollCount} - 地址:{address} 读取:{(success ? "成功" : "失败")} 值:{value}");
                    }

                    if (success && value == 1)
                    {
                        Debug.WriteLine($"[PlcService] 🎯 检测到触发信号! 地址:{address} 值:{value}");

                        // 收到触发信号，复位
                        bool resetSuccess = await _plcDevice.WriteInt16Async(address, 0);
                        Debug.WriteLine($"[PlcService] ↩ 复位信号 - {(resetSuccess ? "成功" : "失败")}");

                        await Task.Delay(triggerDelay);

                        // 触发事件通知
                        Debug.WriteLine("[PlcService] 📤 触发 TriggerReceived 事件...");
                        TriggerReceived?.Invoke();
                        Debug.WriteLine("[PlcService] ✅ TriggerReceived 事件已发送");
                    }

                    await Task.Delay(pollingIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[PlcService] ⏹ 监听循环被取消");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlcService] ❌ 监听异常: {ex.Message}");
                    LastError = ex.Message;
                    ErrorOccurred?.Invoke($"监听异常: {ex.Message}");
                    break;
                }
            }

            Debug.WriteLine($"[PlcService] ⏹ 监听循环结束 - 共轮询 {pollCount} 次");
        }

        #endregion

        #region 结果读写

        public async Task WriteResultAsync(short resultAddress, bool isQualified)
        {
            if (!IsConnected || _plcDevice == null) return;

            string address = GetPlcAddress(resultAddress);
            try
            {
                bool success = await _plcDevice.WriteInt16Async(address, (short)(isQualified ? 1 : 0));
                if (!success)
                {
                    LastError = _plcDevice.LastError;
                    ErrorOccurred?.Invoke($"写入失败: {LastError}");
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ErrorOccurred?.Invoke($"写入异常: {ex.Message}");
            }
        }

        public async Task<bool> WriteReleaseSignalAsync(short resultAddress)
        {
            if (!IsConnected || _plcDevice == null) return false;

            string address = GetPlcAddress(resultAddress);
            try
            {
                return await _plcDevice.WriteInt16Async(address, 1);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"放行失败: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 辅助方法

        private string GetPlcAddress(short address)
        {
            if (_plcDevice == null) return $"D{address}";

            // 根据协议类型判断地址格式
            string protocol = _plcDevice.ProtocolName?.ToLower() ?? "";

            if (protocol.Contains("modbus"))
                return address.ToString();
            if (protocol.Contains("siemens") || protocol.Contains("s7"))
                return $"DB1.{address}";

            return $"D{address}";
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
            Disconnect();

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
