// ============================================================================
// 文件名: PlcService.cs
// 描述:   PLC 通讯服务实现
//
// 功能:
//   - 多协议 PLC 连接管理 (Mitsubishi, Siemens, Omron, Modbus)
//   - 触发信号监控循环
//   - 检测结果写入
// ============================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using YOLO.Interfaces;

namespace YOLO.Services
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
                // 停止旧的监控
                await StopMonitoringAsync();

                // 断开旧连接
                Disconnect();

                var protocolType = PlcFactory.ParseProtocol(protocol);
                Debug.WriteLine($"[PlcService] 正在连接 {protocolType} @ {ip}:{port}");

                for (int i = 0; i < maxRetries; i++)
                {
                    _plcDevice = PlcFactory.Create(protocolType, ip, port);
                    bool connected = await _plcDevice.ConnectAsync();

                    if (connected)
                    {
                        IsConnected = true;
                        LastError = null;
                        ConnectionChanged?.Invoke(true);
                        Debug.WriteLine($"[PlcService] 连接成功: {_plcDevice.ProtocolName}");
                        return true;
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

        #region 监控管理

        public void StartMonitoring(short triggerAddress, int pollingIntervalMs = 500)
        {
            if (_monitoringCts != null) return;

            _monitoringCts = new CancellationTokenSource();
            var token = _monitoringCts.Token;

            _ = Task.Run(async () =>
            {
                await MonitoringLoop(triggerAddress, pollingIntervalMs, token);
            });

            Debug.WriteLine($"[PlcService] 开始监控触发地址: {triggerAddress}");
        }

        public void StopMonitoring()
        {
            if (_monitoringCts != null)
            {
                _monitoringCts.Cancel();
                _monitoringCts.Dispose();
                _monitoringCts = null;
                Debug.WriteLine("[PlcService] 停止监控");
            }
        }

        private async Task StopMonitoringAsync()
        {
            if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
            {
                _monitoringCts.Cancel();
                await Task.Delay(100); // 给循环时间退出
            }
            _monitoringCts?.Dispose();
            _monitoringCts = null;
        }

        private async Task MonitoringLoop(short triggerAddress, int pollingIntervalMs, CancellationToken token)
        {
            const int triggerDelay = 800;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_plcDevice == null) break;

                    string address = GetPlcAddress(triggerAddress);
                    var (success, value) = await _plcDevice.ReadInt16Async(address);

                    if (success && value == 1)
                    {
                        // 收到触发信号，清零
                        await _plcDevice.WriteInt16Async(address, 0);
                        await Task.Delay(triggerDelay);

                        // 发出事件通知
                        TriggerReceived?.Invoke();
                    }

                    await Task.Delay(pollingIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    ErrorOccurred?.Invoke($"监控异常: {ex.Message}");
                    break;
                }
            }
        }

        #endregion

        #region 结果写入

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

        public async Task WriteReleaseSignalAsync(short resultAddress)
        {
            if (!IsConnected || _plcDevice == null) return;

            string address = GetPlcAddress(resultAddress);
            try
            {
                await _plcDevice.WriteInt16Async(address, 1);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"放行失败: {ex.Message}");
            }
        }

        #endregion

        #region 辅助方法

        private string GetPlcAddress(short address)
        {
            if (_plcDevice == null) return $"D{address}";

            // 根据协议名称判断地址格式
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
