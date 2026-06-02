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
using System.Globalization;
using System.Text;
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
        private Task? _monitoringTask;
        private bool _isConnecting;
        private bool _disposed;
        private readonly object _stateLock = new object();
        private long _lastAcceptedTriggerTicks;

        private string _lastProtocol = "Mitsubishi_MC_ASCII";
        private string _lastDriverProvider = "Hsl";
        private string _lastIp = "127.0.0.1";
        private int _lastPort = 0;
        private string _lastTriggerAddress = string.Empty;
        private bool _monitoringStopRequested;
        private PlcProtocolMode _lastProtocolMode = PlcProtocolMode.Legacy;
        private string _lastTriggerSeqAddress = string.Empty;
        private string _lastSiemensCpuModel = "S1200";
        private int _lastSiemensRack;
        private int _lastSiemensSlot = 2;
        private int _lastPollingIntervalMs = 500;
        private int _lastTriggerDelayMs = 800;

        private static readonly TimeSpan TriggerDebounceWindow = TimeSpan.FromSeconds(2);
        private const int ReconnectRetryDelayMs = 2000;

        #endregion

        #region 事件

        public event Action<bool>? ConnectionChanged;
        public event Action? TriggerReceived;
        public event Action<PlcTriggerContext>? TriggerContextReceived;
        public event Action<string>? ErrorOccurred;

        #endregion

        #region 属性

        public bool IsConnected { get; private set; }
        public string ProtocolName => _plcDevice?.ProtocolName ?? "未连接";
        public string? LastError { get; private set; }

        #endregion

        #region 连接管理

        public async Task<bool> ConnectAsync(PlcConnectionOptions options)
        {
            if (_isConnecting) return false;
            _isConnecting = true;
            bool connectionResetStarted = false;

            try
            {
                const int maxRetries = 3;
                options ??= new PlcConnectionOptions();
                var protocolType = options.ProtocolType;

                string nextProtocol = string.IsNullOrWhiteSpace(options.Protocol) ? protocolType.ToString() : options.Protocol;
                string nextDriverProvider = string.IsNullOrWhiteSpace(options.DriverProvider) ? "Hsl" : options.DriverProvider;
                string nextTriggerAddress = NormalizeAddress(
                    options.TriggerAddress ?? string.Empty,
                    protocolType,
                    nextDriverProvider,
                    required: false);

                // 停止旧的监听
                await StopMonitoringAsync();

                // 断开现有连接
                connectionResetStarted = true;
                Disconnect();

                _lastProtocol = nextProtocol;
                _lastDriverProvider = nextDriverProvider;
                _lastIp = options.Ip ?? string.Empty;
                _lastPort = options.Port;
                _lastSiemensCpuModel = string.IsNullOrWhiteSpace(options.SiemensCpuModel) ? "S1200" : options.SiemensCpuModel;
                _lastSiemensRack = options.SiemensRack;
                _lastSiemensSlot = options.SiemensSlot;
                _lastTriggerAddress = nextTriggerAddress;

                Debug.WriteLine($"[PlcService] 正在连接 {_lastDriverProvider}/{protocolType} @ {_lastIp}:{_lastPort}");

                for (int i = 0; i < maxRetries; i++)
                {
                    _plcDevice?.Disconnect();
                    _plcDevice = null;

                    _plcDevice = PlcFactory.Create(BuildLastConnectionOptions());
                    bool socketConnected = await _plcDevice.ConnectAsync();

                    if (socketConnected)
                    {
                        // Socket 连接成功后，进行一次读操作验证 PLC 是否真正可通信
                        // HslCommunication 库的 ConnectServer 仅建立 TCP 连接，不验证 PLC 可用性
                        string testAddress = GetConnectivityProbeAddress(protocolType, _lastTriggerAddress);
                        var (readSuccess, _) = await _plcDevice.ReadInt16Async(testAddress);
                        if (readSuccess)
                        {
                            LastError = null;
                            SetConnectionState(true);
                            Debug.WriteLine($"[PlcService] 连接成功: {_plcDevice.ProtocolName}");
                            return true;
                        }
                        else
                        {
                            // 读操作失败，说明 PLC 未真正可用
                            LastError = _plcDevice.LastError ?? "PLC 连接验证失败：无法读取测试地址";
                            Debug.WriteLine($"[PlcService] 连接验证失败 (读取 {testAddress} 失败): {LastError}");
                            _plcDevice.Disconnect();
                            _plcDevice = null;
                            continue; // 继续重试
                        }
                    }

                    LastError = _plcDevice?.LastError ?? "未知错误";
                    Debug.WriteLine($"[PlcService] 连接失败: {LastError}");
                    _plcDevice?.Disconnect();
                    _plcDevice = null;

                    if (i < maxRetries - 1)
                    {
                        await Task.Delay(ReconnectRetryDelayMs);
                    }
                }

                SetConnectionState(false);
                return false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ErrorOccurred?.Invoke($"连接异常: {ex.Message}");
                if (connectionResetStarted)
                {
                    try
                    {
                        _plcDevice?.Disconnect();
                    }
                    catch (Exception disconnectEx)
                    {
                        Debug.WriteLine($"[PlcService] 连接异常后断开失败: {disconnectEx.Message}");
                    }

                    _plcDevice = null;
                    SetConnectionState(false);
                }

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
                SetConnectionState(false);
            }
        }

        #endregion

        #region 监听功能

        public bool StartMonitoring(
            string triggerAddress,
            int pollingIntervalMs = 500,
            int triggerDelayMs = 800,
            PlcMonitoringOptions? options = null)
        {
            if (_monitoringTask != null && !_monitoringTask.IsCompleted) return true;

            options ??= new PlcMonitoringOptions();
            try
            {
                _lastTriggerAddress = NormalizeAddressForCurrentDriver(triggerAddress ?? string.Empty, required: true);
                _lastTriggerSeqAddress = NormalizeAddressForCurrentDriver(options.TriggerSeqAddress ?? string.Empty, required: false);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ErrorOccurred?.Invoke($"PLC监听地址无效: {ex.Message}");
                return false;
            }

            _lastPollingIntervalMs = Math.Max(50, pollingIntervalMs);
            _lastTriggerDelayMs = Math.Max(0, triggerDelayMs);
            _lastProtocolMode = options.ProtocolMode;
            Interlocked.Exchange(ref _lastAcceptedTriggerTicks, 0);
            _monitoringStopRequested = false;

            _monitoringCts = new CancellationTokenSource();
            var token = _monitoringCts.Token;

            _monitoringTask = Task.Run(async () =>
            {
                await MonitoringLoop(_lastTriggerAddress, _lastPollingIntervalMs, _lastTriggerDelayMs, token);
            }, token);

            Debug.WriteLine($"[PlcService] 开始监听触发地址: {_lastTriggerAddress}, 轮询间隔: {_lastPollingIntervalMs}ms, 触发延迟: {_lastTriggerDelayMs}ms, 模式: {_lastProtocolMode}");
            return true;
        }

        public void StopMonitoring()
        {
            if (_monitoringCts != null)
            {
                _monitoringStopRequested = true;
                _monitoringCts.Cancel();
                try
                {
                    _monitoringTask?.Wait(200);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlcService] 停止监听等待异常: {ex.Message}");
                }
                _monitoringCts.Dispose();
                _monitoringCts = null;
                _monitoringTask = null;
                Debug.WriteLine("[PlcService] 停止监听");
            }
        }

        private async Task StopMonitoringAsync()
        {
            if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
            {
                _monitoringStopRequested = true;
                _monitoringCts.Cancel();
            }
            if (_monitoringTask != null)
            {
                try
                {
                    await _monitoringTask;
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PlcService] StopMonitoringAsync 异常: {ex.Message}");
                }
            }

            _monitoringCts?.Dispose();
            _monitoringCts = null;
            _monitoringTask = null;
        }

        private async Task MonitoringLoop(string triggerAddress, int pollingIntervalMs, int triggerDelayMs, CancellationToken token)
        {
            int pollCount = 0;

            Debug.WriteLine($"[PlcService] ▶ 监听循环启动 - 地址: {triggerAddress}, 间隔: {pollingIntervalMs}ms, 延迟: {triggerDelayMs}ms");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_plcDevice == null || !_plcDevice.IsConnected)
                    {
                        Debug.WriteLine("[PlcService] ⚠ PLC未连接，尝试自动重连...");
                        bool reconnected = await TryReconnectAsync(token);
                        if (!reconnected)
                        {
                            await Task.Delay(ReconnectRetryDelayMs, token);
                            continue;
                        }
                    }

                    var plc = _plcDevice;
                    if (plc == null)
                    {
                        await Task.Delay(ReconnectRetryDelayMs, token);
                        continue;
                    }

                    string address = triggerAddress;
                    var (success, value) = await plc.ReadInt16Async(address);
                    pollCount++;

                    if (!success)
                    {
                        throw new InvalidOperationException(plc.LastError ?? "读取触发地址失败");
                    }

                    // 每10次轮询输出一次状态（避免日志过多）
                    if (pollCount % 10 == 0)
                    {
                        Debug.WriteLine($"[PlcService] 📡 轮询 #{pollCount} - 地址:{address} 读取:成功 值:{value}");
                    }

                    if (value == 1)
                    {
                        Debug.WriteLine($"[PlcService] 🎯 检测到触发信号! 地址:{address} 值:{value}");
                        int? triggerSeq = null;
                        if (_lastProtocolMode == PlcProtocolMode.HandshakeV1)
                        {
                            var triggerSeqResult = await TryReadTriggerSeqAsync(plc);
                            if (!triggerSeqResult.Success)
                            {
                                throw new InvalidOperationException(LastError ?? "读取 TriggerSeq 失败");
                            }

                            triggerSeq = triggerSeqResult.Value;
                        }

                        // 收到触发信号，复位
                        bool resetSuccess = await plc.WriteInt16Async(address, 0);
                        Debug.WriteLine($"[PlcService] ↩ 复位信号 - {(resetSuccess ? "成功" : "失败")}");
                        if (!resetSuccess)
                        {
                            LastError = plc.LastError ?? "复位触发信号失败";
                            SyncConnectionStateFromDevice(plc);
                            throw new InvalidOperationException(LastError);
                        }

                        // 显式 2 秒防抖：窗口内只接受第一个触发
                        long nowTicks = DateTime.UtcNow.Ticks;
                        long lastTicks = Interlocked.Read(ref _lastAcceptedTriggerTicks);
                        if (lastTicks > 0 && (nowTicks - lastTicks) > 0 &&
                            TimeSpan.FromTicks(nowTicks - lastTicks) < TriggerDebounceWindow)
                        {
                            Debug.WriteLine("[PlcService] ⏱ 触发落入2秒防抖窗口，已忽略");
                            await Task.Delay(pollingIntervalMs, token);
                            continue;
                        }

                        Interlocked.Exchange(ref _lastAcceptedTriggerTicks, nowTicks);
                        await Task.Delay(triggerDelayMs, token);

                        // 触发事件通知
                        if (_lastProtocolMode == PlcProtocolMode.HandshakeV1)
                        {
                            var context = new PlcTriggerContext
                            {
                                TriggerSource = "PLC",
                                TriggerSeq = triggerSeq,
                                TriggerTime = DateTimeOffset.Now
                            };

                            Debug.WriteLine($"[PlcService] 📤 触发 TriggerContextReceived 事件: TriggerSeq={triggerSeq?.ToString() ?? "-"}");
                            TriggerContextReceived?.Invoke(context);
                            Debug.WriteLine("[PlcService] ✅ TriggerContextReceived 事件已发送");
                        }
                        else
                        {
                            Debug.WriteLine("[PlcService] 📤 触发 TriggerReceived 事件...");
                            TriggerReceived?.Invoke();
                            Debug.WriteLine("[PlcService] ✅ TriggerReceived 事件已发送");
                        }
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
                    SetConnectionState(false);

                    try
                    {
                        _plcDevice?.Disconnect();
                    }
                    catch (Exception disconnectEx)
                    {
                        Debug.WriteLine($"[PlcService] 监听异常后断开失败: {disconnectEx.Message}");
                    }

                    _plcDevice = null;

                    try
                    {
                        await Task.Delay(ReconnectRetryDelayMs, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            if (!_monitoringStopRequested)
            {
                SetConnectionState(false);
            }

            _monitoringStopRequested = false;
            Debug.WriteLine($"[PlcService] ⏹ 监听循环结束 - 共轮询 {pollCount} 次");
        }

        #endregion

        #region 结果读写

        public async Task<bool> WriteResultAsync(string resultAddress, bool isQualified)
        {
            if (!TryGetConnectedDeviceForOperation(resultAddress, out var plc, out string address))
            {
                return false;
            }

            try
            {
                bool success = await plc.WriteInt16Async(address, (short)(isQualified ? 1 : 0));
                if (!success)
                {
                    LastError = plc.LastError;
                    SyncConnectionStateFromDevice(plc);
                    ErrorOccurred?.Invoke($"写入失败: {LastError}");
                }
                return success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SyncConnectionStateFromDevice(plc);
                ErrorOccurred?.Invoke($"写入异常: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> WriteResultAsync(string resultAddress, short valueToWrite)
        {
            if (!TryGetConnectedDeviceForOperation(resultAddress, out var plc, out string address))
            {
                return false;
            }

            try
            {
                bool success = await plc.WriteInt16Async(address, valueToWrite);
                if (!success)
                {
                    LastError = plc.LastError;
                    SyncConnectionStateFromDevice(plc);
                    ErrorOccurred?.Invoke($"写入失败: {LastError}");
                }
                return success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SyncConnectionStateFromDevice(plc);
                ErrorOccurred?.Invoke($"写入异常: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> WriteReleaseSignalAsync(string resultAddress)
        {
            if (!TryGetConnectedDeviceForOperation(resultAddress, out var plc, out string address))
            {
                return false;
            }

            try
            {
                bool success = await plc.WriteInt16Async(address, 1);
                if (!success)
                {
                    LastError = plc.LastError;
                    SyncConnectionStateFromDevice(plc);
                    ErrorOccurred?.Invoke($"放行失败: {LastError}");
                }

                return success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SyncConnectionStateFromDevice(plc);
                ErrorOccurred?.Invoke($"放行失败: {ex.Message}");
                return false;
            }
        }

        public async Task<(bool Success, string Value)> ReadStringAsync(string startAddress, int wordLength, string encodingName)
        {
            if (!TryGetConnectedDeviceForOperation(startAddress, out var plc, out string address))
            {
                return (false, string.Empty);
            }

            int safeWordLength = Math.Clamp(wordLength, 1, 64);

            try
            {
                ushort readLength = GetStringReadLength(safeWordLength);
                var (success, bytes) = await plc.ReadBytesAsync(address, readLength);
                if (!success)
                {
                    LastError = plc.LastError;
                    SyncConnectionStateFromDevice(plc);
                    ErrorOccurred?.Invoke($"读取条码失败: {LastError}");
                    return (false, string.Empty);
                }

                int byteCount = 0;
                for (int index = 0; index < bytes.Length; index++)
                {
                    if (bytes[index] != 0)
                    {
                        bytes[byteCount++] = bytes[index];
                    }
                }

                string decodedValue = ResolveTextEncoding(encodingName)
                    .GetString(bytes, 0, byteCount)
                    .Trim('\0', ' ', '\r', '\n', '\t');
                return (true, decodedValue);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SyncConnectionStateFromDevice(plc);
                ErrorOccurred?.Invoke($"读取条码异常: {ex.Message}");
                return (false, string.Empty);
            }
        }

        #endregion

        #region 辅助方法

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

        private bool TryGetConnectedDeviceForOperation(
            string rawAddress,
            out IPlcDevice plc,
            out string normalizedAddress)
        {
            plc = null!;
            normalizedAddress = string.Empty;

            if (!IsConnected || _plcDevice == null || string.IsNullOrWhiteSpace(rawAddress))
            {
                return false;
            }

            if (!_plcDevice.IsConnected)
            {
                SyncConnectionStateFromDevice(_plcDevice);
                LastError = string.IsNullOrWhiteSpace(_plcDevice.LastError)
                    ? "PLC 未连接"
                    : _plcDevice.LastError;
                return false;
            }

            try
            {
                normalizedAddress = NormalizeAddressForCurrentDriver(rawAddress, required: true);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ErrorOccurred?.Invoke($"PLC地址无效: {ex.Message}");
                return false;
            }

            plc = _plcDevice;
            return true;
        }

        private string NormalizeAddressForCurrentDriver(string rawAddress, bool required)
        {
            return NormalizeAddress(
                rawAddress,
                PlcFactory.ParseProtocol(_lastProtocol),
                _lastDriverProvider,
                required);
        }

        private static string NormalizeAddress(
            string rawAddress,
            PlcProtocolType protocolType,
            string driverProvider,
            bool required)
        {
            if (string.IsNullOrWhiteSpace(rawAddress))
            {
                return required
                    ? PlcAddressNormalizer.NormalizeOrThrow(rawAddress, protocolType)
                    : string.Empty;
            }

            string normalized = PlcAddressNormalizer.NormalizeOrThrow(rawAddress, protocolType);
            PlcAddressNormalizer.EnsureDriverSupportsAddress(
                normalized,
                protocolType,
                driverProvider);
            return normalized;
        }

        private void SyncConnectionStateFromDevice(IPlcDevice plc)
        {
            if (!plc.IsConnected)
            {
                SetConnectionState(false);
            }
        }

        private async Task<bool> TryReconnectAsync(CancellationToken token)
        {
            if (_isConnecting || string.IsNullOrWhiteSpace(_lastIp))
                return false;

            _isConnecting = true;
            try
            {
                var protocolType = PlcFactory.ParseProtocol(_lastProtocol);

                _plcDevice?.Disconnect();
                _plcDevice = PlcFactory.Create(BuildLastConnectionOptions());

                bool socketConnected = await _plcDevice.ConnectAsync();
                if (!socketConnected)
                {
                    LastError = _plcDevice.LastError;

                    try
                    {
                        _plcDevice.Disconnect();
                    }
                    catch (Exception disconnectEx)
                    {
                        Debug.WriteLine($"[PlcService] 自动重连连接失败后断开异常: {disconnectEx.Message}");
                    }

                    _plcDevice = null;
                    SetConnectionState(false);
                    return false;
                }

                string testAddress = GetConnectivityProbeAddress(protocolType, _lastTriggerAddress);
                var (readSuccess, _) = await _plcDevice.ReadInt16Async(testAddress);
                if (!readSuccess)
                {
                    LastError = _plcDevice.LastError;
                    _plcDevice.Disconnect();
                    _plcDevice = null;
                    SetConnectionState(false);
                    return false;
                }

                LastError = null;
                SetConnectionState(true);
                Debug.WriteLine($"[PlcService] 自动重连成功: {_lastDriverProvider}/{protocolType} @ {_lastIp}:{_lastPort}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;

                try
                {
                    _plcDevice?.Disconnect();
                }
                catch (Exception disconnectEx)
                {
                    Debug.WriteLine($"[PlcService] 自动重连异常后断开失败: {disconnectEx.Message}");
                }

                _plcDevice = null;
                SetConnectionState(false);
                ErrorOccurred?.Invoke($"自动重连失败: {ex.Message}");
                return false;
            }
            finally
            {
                _isConnecting = false;
            }
        }

        private async Task<(bool Success, int? Value)> TryReadTriggerSeqAsync(IPlcDevice plc)
        {
            if (string.IsNullOrWhiteSpace(_lastTriggerSeqAddress))
            {
                return (true, null);
            }

            try
            {
                var (success, value) = await plc.ReadInt16Async(_lastTriggerSeqAddress);
                if (!success)
                {
                    LastError = plc.LastError;
                    SyncConnectionStateFromDevice(plc);
                    ErrorOccurred?.Invoke($"读取 TriggerSeq 失败: {LastError}");
                    return (false, null);
                }

                return (true, value);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                SyncConnectionStateFromDevice(plc);
                ErrorOccurred?.Invoke($"读取 TriggerSeq 异常: {ex.Message}");
                return (false, null);
            }
        }

        private PlcConnectionOptions BuildLastConnectionOptions()
        {
            return new PlcConnectionOptions
            {
                Protocol = _lastProtocol,
                DriverProvider = _lastDriverProvider,
                Ip = _lastIp,
                Port = _lastPort,
                SiemensCpuModel = _lastSiemensCpuModel,
                SiemensRack = _lastSiemensRack,
                SiemensSlot = _lastSiemensSlot,
                TriggerAddress = _lastTriggerAddress
            };
        }

        private static string GetConnectivityProbeAddress(PlcProtocolType protocolType, string preferredAddress)
        {
            return PlcAddressNormalizer.GetProbeAddress(protocolType, preferredAddress);
        }

        private ushort GetStringReadLength(int safeWordLength)
        {
            PlcProtocolType protocolType = PlcFactory.ParseProtocol(_lastProtocol);
            if (protocolType == PlcProtocolType.Modbus_TCP)
            {
                return (ushort)safeWordLength;
            }

            return (ushort)(safeWordLength * 2);
        }

        private static string OffsetWordAddress(string startAddress, PlcProtocolType protocolType, int wordOffset)
        {
            if (wordOffset <= 0)
            {
                return startAddress;
            }

            string compact = (startAddress ?? string.Empty).Trim().Replace(" ", string.Empty).ToUpperInvariant();
            if (protocolType == PlcProtocolType.Siemens_S7)
            {
                if (compact.StartsWith("DB", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = compact.Substring(2).Split('.');
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dbNumber) &&
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int byteOffset))
                    {
                        return $"DB{dbNumber}.{byteOffset + wordOffset * 2}";
                    }
                }

                if ((compact.StartsWith("M") || compact.StartsWith("I") || compact.StartsWith("Q")) &&
                    int.TryParse(compact.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int miqOffset))
                {
                    return $"{compact[0]}{miqOffset + wordOffset * 2}";
                }
            }

            if ((protocolType == PlcProtocolType.Mitsubishi_MC_ASCII ||
                 protocolType == PlcProtocolType.Mitsubishi_MC_Binary ||
                 protocolType == PlcProtocolType.Omron_Fins) &&
                compact.StartsWith("D", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(compact.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dNumber))
            {
                return $"D{dNumber + wordOffset}";
            }

            if (int.TryParse(compact, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                return (number + wordOffset).ToString(CultureInfo.InvariantCulture);
            }

            return startAddress ?? string.Empty;
        }

        private static Encoding ResolveTextEncoding(string? encodingName)
        {
            string normalized = (encodingName ?? "ASCII").Trim().ToUpperInvariant();
            if (normalized is "UTF8" or "UTF-8")
            {
                return Encoding.UTF8;
            }

            if (normalized is "GBK" or "GB2312" or "936")
            {
                try
                {
                    return Encoding.GetEncoding(936);
                }
                catch
                {
                    return Encoding.UTF8;
                }
            }

            return Encoding.ASCII;
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
