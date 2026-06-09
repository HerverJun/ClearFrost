// ============================================================================
// 文件名: SerialPhotoelectricTriggerService.cs
// 描述:   串口光电触发服务实现
//
// 协议:
//   - 01 11: 光电遮挡（clear -> blocked 边沿触发一次）
//   - 01 22: 光电恢复（收到后才允许下一次 01 11 触发）
//   - 重复 01 11 在 blocked 状态下不重复触发
//
// 性能:
//   - 后台阻塞读，不轮询
//   - 不阻塞 UI 线程
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ClearFrost.Hardware.Triggers
{
    /// <summary>
    /// 串口光电触发服务实现
    /// </summary>
    public class SerialPhotoelectricTriggerService : ISerialPhotoelectricTriggerService
    {
        #region 私有字段

        private SerialPort? _serialPort;
        private Task? _readTask;
        private CancellationTokenSource? _cts;
        private readonly object _stateLock = new object();
        private bool _disposed;
        private bool _isBlocked;
        private long _lastTriggerTicks;
        private long _lastReconnectErrorTicks;
        private string _portName = string.Empty;
        private int _baudRate = 9600;
        private int _debounceMs = 50;
        private int _timeoutMs = 1000;

        private static readonly byte[] TriggerFrame = new byte[] { 0x01, 0x11 };
        private static readonly byte[] ResetFrame = new byte[] { 0x01, 0x22 };
        private const int ReconnectRetryDelayMs = 2000;
        private const int ReconnectErrorLogIntervalMs = 10000;

        #endregion

        #region 事件

        public event Action<bool>? ConnectionChanged;
        public event Action? TriggerReceived;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        public bool IsConnected { get; private set; }
        public string? LastError { get; private set; }

        #endregion

        #region 公共方法

        public Task<bool> StartAsync(string portName, int baudRate, int debounceMs = 50, int timeoutMs = 1000)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                LastError = "串口名称不能为空";
                ErrorOccurred?.Invoke(LastError);
                return Task.FromResult(false);
            }

            Stop();

            _portName = portName.Trim();
            _baudRate = baudRate;
            _debounceMs = Math.Max(0, debounceMs);
            _timeoutMs = Math.Max(100, timeoutMs);

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _serialPort = new SerialPort(_portName, _baudRate)
            {
                DataBits = 8,
                Parity = Parity.None,
                StopBits = StopBits.One,
                ReadTimeout = _timeoutMs,
                WriteTimeout = _timeoutMs,
                DtrEnable = false,
                RtsEnable = false
            };

            try
            {
                _serialPort.Open();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
            }
            catch (Exception ex)
            {
                LastError = $"打开串口失败: {ex.Message}";
                ErrorOccurred?.Invoke(LastError);
                _serialPort.Dispose();
                _serialPort = null;
                _cts.Dispose();
                _cts = null;
                return Task.FromResult(false);
            }

            LastError = null;
            SetConnectionState(true);
            _readTask = Task.Run(() => ReadLoopAsync(token), token);
            return Task.FromResult(true);
        }

        public void Stop()
        {
            _cts?.Cancel();

            try
            {
                _serialPort?.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialTrigger] 关闭串口异常: {ex.Message}");
            }

            if (_readTask != null)
            {
                try
                {
                    _readTask.Wait(500);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SerialTrigger] 等待读取任务结束异常: {ex.Message}");
                }
                _readTask = null;
            }

            _serialPort?.Dispose();
            _serialPort = null;
            _cts?.Dispose();
            _cts = null;

            lock (_stateLock)
            {
                _isBlocked = false;
            }

            SetConnectionState(false);
        }

        public Task<bool> SendTestTriggerAsync()
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                LastError = "串口未打开，无法发送测试帧";
                ErrorOccurred?.Invoke(LastError);
                return Task.FromResult(false);
            }

            try
            {
                _serialPort.Write(TriggerFrame, 0, TriggerFrame.Length);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                LastError = $"发送测试帧失败: {ex.Message}";
                ErrorOccurred?.Invoke(LastError);
                return Task.FromResult(false);
            }
        }

        public Task<SerialPhotoelectricPortInfo[]> GetAvailablePortsAsync()
        {
            return Task.Run(() =>
            {
                Dictionary<string, string> friendlyNames = ReadSerialPortFriendlyNamesFromRegistry();

                var candidates = SerialPort.GetPortNames()
                    .Select(port => port.Trim().ToUpperInvariant())
                    .Where(IsComPortName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ExtractComPortNumber)
                    .ThenBy(port => port, StringComparer.OrdinalIgnoreCase)
                    .Select(port =>
                    {
                        string friendlyName = friendlyNames.TryGetValue(port, out string? name)
                            ? name
                            : port;
                        string displayName = string.Equals(friendlyName, port, StringComparison.OrdinalIgnoreCase)
                            ? port
                            : $"{port} - {friendlyName}";

                        return new SerialPhotoelectricPortCandidate(
                            port,
                            displayName,
                            ScoreSerialPhotoelectricPort(displayName));
                    })
                    .Where(candidate => !LooksLikeBluetoothSerialPort(candidate.DisplayName))
                    .ToArray();

                SerialPhotoelectricPortCandidate? preferred = candidates
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => ExtractComPortNumber(candidate.PortName))
                    .FirstOrDefault();

                preferred ??= candidates.Length == 1
                    ? candidates[0]
                    : candidates
                        .OrderBy(candidate => ExtractComPortNumber(candidate.PortName))
                        .FirstOrDefault();

                string preferredPortName = preferred?.PortName ?? string.Empty;

                return candidates
                    .OrderByDescending(candidate => string.Equals(candidate.PortName, preferredPortName, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => ExtractComPortNumber(candidate.PortName))
                    .Select(candidate => new SerialPhotoelectricPortInfo
                    {
                        Name = candidate.PortName,
                        DisplayName = candidate.DisplayName,
                        IsPreferred = string.Equals(candidate.PortName, preferredPortName, StringComparison.OrdinalIgnoreCase)
                    })
                    .ToArray();
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            GC.SuppressFinalize(this);
        }

        #endregion

        #region 私有方法

        private async Task ReadLoopAsync(CancellationToken token)
        {
            byte[] buffer = new byte[256];
            int framePosition = 0;

            Debug.WriteLine($"[SerialTrigger] 读取循环启动: {_serialPort?.PortName}, 波特率 {_serialPort?.BaudRate}");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        TryReopenSerialPort();
                        await Task.Delay(ReconnectRetryDelayMs, token);
                        continue;
                    }

                    int bytesRead = _serialPort.Read(buffer, framePosition, buffer.Length - framePosition);
                    if (bytesRead <= 0)
                    {
                        await Task.Delay(10, token);
                        continue;
                    }

                    int totalLength = framePosition + bytesRead;
                    int processed = ScanBufferForFrames(buffer, totalLength);

                    // 保留未处理字节到缓冲区开头
                    int remaining = totalLength - processed;
                    if (remaining > 0 && remaining < buffer.Length)
                    {
                        Array.Copy(buffer, processed, buffer, 0, remaining);
                        framePosition = remaining;
                    }
                    else
                    {
                        framePosition = 0;
                    }
                }
                catch (TimeoutException)
                {
                    // 读取超时是正常的，继续循环
                    // 保留已积累的半包数据，避免帧头在超时前刚好到达却被丢弃
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[SerialTrigger] 读取循环被取消");
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SerialTrigger] 读取异常: {ex.Message}");
                    LastError = ex.Message;
                    NotifyReconnectError($"串口读取异常: {ex.Message}");
                    SetConnectionState(false);

                    try
                    {
                        _serialPort?.Close();
                    }
                    catch { }

                    await Task.Delay(ReconnectRetryDelayMs, token);
                }
            }

            SetConnectionState(false);
            Debug.WriteLine("[SerialTrigger] 读取循环结束");
        }

        private void HandleTriggerFrame()
        {
            long nowTicks = Environment.TickCount64;
            long lastTicks = Interlocked.Read(ref _lastTriggerTicks);
            long elapsed = nowTicks - lastTicks;
            if (lastTicks > 0 && elapsed >= 0 && elapsed < _debounceMs)
            {
                Debug.WriteLine("[SerialTrigger] 触发落入去抖窗口，已忽略");
                return;
            }

            lock (_stateLock)
            {
                if (_isBlocked)
                {
                    Debug.WriteLine("[SerialTrigger] 已处于 blocked 状态，重复 01 11 不触发");
                    return;
                }

                _isBlocked = true;
            }

            Interlocked.Exchange(ref _lastTriggerTicks, nowTicks);
            Debug.WriteLine("[SerialTrigger] 收到 01 11，触发检测");
            TriggerReceived?.Invoke();
        }

        private bool TryReopenSerialPort()
        {
            if (_serialPort == null || string.IsNullOrWhiteSpace(_portName))
            {
                return false;
            }

            try
            {
                if (_serialPort.IsOpen)
                {
                    return true;
                }

                _serialPort.Open();
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                LastError = null;
                SetConnectionState(true);
                Debug.WriteLine($"[SerialTrigger] 串口重连成功: {_portName}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"串口重连失败: {ex.Message}";
                NotifyReconnectError(LastError);
                SetConnectionState(false);
                return false;
            }
        }

        private void NotifyReconnectError(string message)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastReconnectErrorTicks);
            if (last > 0 && now - last >= 0 && now - last < ReconnectErrorLogIntervalMs)
            {
                return;
            }

            Interlocked.Exchange(ref _lastReconnectErrorTicks, now);
            ErrorOccurred?.Invoke(message);
        }

        private void HandleResetFrame()
        {
            lock (_stateLock)
            {
                bool wasBlocked = _isBlocked;
                _isBlocked = false;
                if (wasBlocked)
                {
                    Debug.WriteLine("[SerialTrigger] 收到 01 22，状态重置为 clear");
                }
            }
        }

        /// <summary>
        /// 扫描缓冲区解析帧，返回已处理字节数
        /// </summary>
        private int ScanBufferForFrames(byte[] buffer, int length)
        {
            int processed = 0;
            while (processed < length - 1)
            {
                if (buffer[processed] != 0x01)
                {
                    processed++;
                    continue;
                }

                byte secondByte = buffer[processed + 1];
                if (secondByte == 0x11)
                {
                    HandleTriggerFrame();
                    processed += 2;
                }
                else if (secondByte == 0x22)
                {
                    HandleResetFrame();
                    processed += 2;
                }
                else
                {
                    processed++;
                }
            }

            return processed;
        }

        private void SetConnectionState(bool connected)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = IsConnected != connected;
                IsConnected = connected;
            }

            if (changed)
            {
                ConnectionChanged?.Invoke(connected);
            }
        }

        private static Dictionary<string, string> ReadSerialPortFriendlyNamesFromRegistry()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] roots =
            {
                @"SYSTEM\CurrentControlSet\Enum\USB",
                @"SYSTEM\CurrentControlSet\Enum\BTHENUM",
                @"SYSTEM\CurrentControlSet\Enum\FTDIBUS",
                @"SYSTEM\CurrentControlSet\Enum\SERENUM",
                @"SYSTEM\CurrentControlSet\Enum\ROOT"
            };

            foreach (string rootPath in roots)
            {
                try
                {
                    using RegistryKey? root = Registry.LocalMachine.OpenSubKey(rootPath);
                    if (root != null)
                    {
                        ReadSerialPortFriendlyNamesFromRegistry(root, result, depth: 0);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SerialTrigger] 读取注册表串口分支失败: {rootPath}, {ex.Message}");
                }
            }

            return result;
        }

        private static void ReadSerialPortFriendlyNamesFromRegistry(
            RegistryKey key,
            Dictionary<string, string> result,
            int depth)
        {
            if (depth > 8)
            {
                return;
            }

            try
            {
                if (key.GetValue("FriendlyName") is string friendlyName &&
                    TryExtractComPortName(friendlyName, out string portName))
                {
                    result[portName] = friendlyName;
                }

                foreach (string subKeyName in key.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? subKey = key.OpenSubKey(subKeyName);
                        if (subKey != null)
                        {
                            ReadSerialPortFriendlyNamesFromRegistry(subKey, result, depth + 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SerialTrigger] 跳过串口注册表子项: {subKeyName}, {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SerialTrigger] 枚举串口注册表失败: {ex.Message}");
            }
        }

        private static bool TryExtractComPortName(string friendlyName, out string portName)
        {
            portName = string.Empty;
            int start = friendlyName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                return false;
            }

            int end = friendlyName.IndexOf(')', start);
            if (end <= start + 1)
            {
                return false;
            }

            string candidate = friendlyName.Substring(start + 1, end - start - 1).Trim().ToUpperInvariant();
            if (!IsComPortName(candidate))
            {
                return false;
            }

            portName = candidate;
            return true;
        }

        private static bool IsComPortName(string value)
        {
            if (value.Length <= 3 || !value.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (int index = 3; index < value.Length; index++)
            {
                if (!char.IsDigit(value[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static int ExtractComPortNumber(string portName)
        {
            return int.TryParse(portName.AsSpan(3), out int number) ? number : int.MaxValue;
        }

        private static int ScoreSerialPhotoelectricPort(string displayName)
        {
            string normalized = displayName.ToLowerInvariant();
            if (LooksLikeBluetoothSerialPort(normalized))
            {
                return -100;
            }

            int score = 0;
            if (normalized.Contains("ch340", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("ch341", StringComparison.OrdinalIgnoreCase))
            {
                score += 120;
            }

            if (normalized.Contains("usb-serial", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("usb serial", StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            if (normalized.Contains("cp210", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("ftdi", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("prolific", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("silicon labs", StringComparison.OrdinalIgnoreCase))
            {
                score += 90;
            }

            if (normalized.Contains("usb", StringComparison.OrdinalIgnoreCase))
            {
                score += 70;
            }

            if (normalized.Contains("serial", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains("串行", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            return score;
        }

        private static bool IsPreferredUsbSerial(string portEntry)
        {
            return ScoreSerialPhotoelectricPort(portEntry) > 0;
        }

        private static bool LooksLikeBluetoothSerialPort(string displayName)
        {
            return displayName.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
                   displayName.Contains("蓝牙", StringComparison.OrdinalIgnoreCase) ||
                   displayName.Contains("bthenum", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record SerialPhotoelectricPortCandidate(string PortName, string DisplayName, int Score);

        #endregion
    }
}
