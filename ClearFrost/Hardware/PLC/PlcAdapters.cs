using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HslCommunication;
using HslCommunication.Profinet.Melsec;
using HslCommunication.Profinet.Siemens;
using HslCommunication.Profinet.Omron;
using HslCommunication.ModBus;

namespace ClearFrost.Hardware
{
    // ============================================================================
    // 文件名: PlcAdapters.cs
    // 作者: 蘅芜君
    // 描述:   PLC 协议工厂和 HslCommunication 默认适配器
    //
    // 功能:
    //   - 统一创建不同厂商 PLC 的 IPlcDevice 实现
    //   - 在 Hsl、McpX、HaoCommunication 三类驱动之间按配置分流
    //   - 封装连接、Int16 读写和字节读取的通用错误处理
    // ============================================================================

    /// <summary>
    /// 当前系统支持的 PLC 通讯协议类型。
    /// </summary>
    public enum PlcProtocolType
    {
        /// <summary>三菱 MC 协议 ASCII 报文。</summary>
        Mitsubishi_MC_ASCII,

        /// <summary>三菱 MC 协议二进制报文。</summary>
        Mitsubishi_MC_Binary,

        /// <summary>Modbus TCP 协议。</summary>
        Modbus_TCP,

        /// <summary>西门子 S7 协议。</summary>
        Siemens_S7,

        /// <summary>欧姆龙 FINS 协议。</summary>
        Omron_Fins
    }

    /// <summary>
    /// 根据配置创建 PLC 适配器的工厂。
    /// </summary>
    /// <remarks>
    /// 业务层只认识 <see cref="IPlcDevice"/>，这里集中处理协议枚举、驱动提供方和厂商特定参数。
    /// </remarks>
    public static class PlcFactory
    {
        /// <summary>
        /// 使用基础连接参数创建 PLC 适配器。
        /// </summary>
        public static IPlcDevice Create(string driverProvider, PlcProtocolType protocol, string ip, int port)
        {
            return Create(new PlcConnectionOptions
            {
                DriverProvider = driverProvider,
                Protocol = protocol.ToString(),
                Ip = ip,
                Port = port
            });
        }

        /// <summary>
        /// 根据完整连接配置创建 PLC 适配器。
        /// </summary>
        /// <exception cref="ArgumentNullException">options 为空时抛出。</exception>
        /// <exception cref="NotSupportedException">协议或驱动组合不受支持时抛出。</exception>
        public static IPlcDevice Create(PlcConnectionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            PlcProtocolType protocol = options.ProtocolType;
            string driverProvider = options.DriverProvider;
            string ip = options.Ip;
            int port = options.Port;

            // McpX 只覆盖三菱 MC 协议，用于需要替换 HslCommunication 三菱实现的现场。
            if (string.Equals(driverProvider, "McpX", StringComparison.OrdinalIgnoreCase))
            {
                return protocol switch
                {
                    PlcProtocolType.Mitsubishi_MC_ASCII => new McpXMitsubishiMcAsciiAdapter(ip, port),
                    PlcProtocolType.Mitsubishi_MC_Binary => new McpXMitsubishiMcBinaryAdapter(ip, port),
                    _ => throw new NotSupportedException($"McpX 仅支持三菱协议，当前: {protocol}")
                };
            }

            // HaoCommunication 是信息部特调版通讯库，需走独立反射适配器，避免和 NuGet Hsl DLL 冲突。
            if (string.Equals(driverProvider, "HaoCommunication", StringComparison.OrdinalIgnoreCase))
            {
                return protocol switch
                {
                    PlcProtocolType.Mitsubishi_MC_ASCII => new HaoMitsubishiMcAsciiAdapter(ip, port),
                    PlcProtocolType.Mitsubishi_MC_Binary => new HaoMitsubishiMcBinaryAdapter(ip, port),
                    PlcProtocolType.Modbus_TCP => new HaoModbusTcpAdapter(ip, port),
                    PlcProtocolType.Siemens_S7 => new HaoSiemensS7Adapter(
                        ip,
                        port,
                        options.SiemensCpuModel,
                        options.SiemensRack,
                        options.SiemensSlot),
                    PlcProtocolType.Omron_Fins => new HaoOmronFinsAdapter(ip, port),
                    _ => throw new NotSupportedException($"不支持的协议类型: {protocol}")
                };
            }

            // 默认分支使用 NuGet 版 HslCommunication，覆盖系统常规协议。
            return protocol switch
            {
                PlcProtocolType.Mitsubishi_MC_ASCII => new MitsubishiMcAsciiAdapter(ip, port),
                PlcProtocolType.Mitsubishi_MC_Binary => new MitsubishiMcBinaryAdapter(ip, port),
                PlcProtocolType.Modbus_TCP => new ModbusTcpAdapter(ip, port),
                PlcProtocolType.Siemens_S7 => new SiemensS7Adapter(
                    ip,
                    port,
                    options.SiemensCpuModel,
                    options.SiemensRack,
                    options.SiemensSlot),
                PlcProtocolType.Omron_Fins => new OmronFinsAdapter(ip, port),
                _ => throw new NotSupportedException($"不支持的协议类型: {protocol}")
            };
        }

        /// <summary>
        /// 将配置文件中的协议字符串转换为协议枚举。
        /// </summary>
        /// <remarks>解析失败时回退到三菱 MC ASCII，保持旧配置兼容。</remarks>
        public static PlcProtocolType ParseProtocol(string protocolStr)
        {
            if (Enum.TryParse<PlcProtocolType>(protocolStr, true, out var result))
                return result;
            return PlcProtocolType.Mitsubishi_MC_ASCII; // 默认值
        }

        /// <summary>
        /// 严格解析协议字符串；用于用户保存和启动诊断，避免拼写错误被静默当作三菱协议。
        /// </summary>
        public static bool TryParseProtocol(string? protocolStr, out PlcProtocolType protocolType)
        {
            string raw = protocolStr?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw) || int.TryParse(raw, out _))
            {
                protocolType = default;
                return false;
            }

            if (Enum.TryParse(raw, true, out protocolType) &&
                Enum.IsDefined(typeof(PlcProtocolType), protocolType))
            {
                return true;
            }

            protocolType = default;
            return false;
        }

        /// <summary>
        /// 校验并规范化驱动提供方名称。
        /// </summary>
        public static bool TryNormalizeDriverProvider(string? driverProvider, out string normalized)
        {
            string raw = driverProvider?.Trim() ?? string.Empty;
            if (string.Equals(raw, "Hsl", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "Hsl";
                return true;
            }

            if (string.Equals(raw, "HaoCommunication", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "HaoCommunication";
                return true;
            }

            if (string.Equals(raw, "McpX", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "McpX";
                return true;
            }

            normalized = string.Empty;
            return false;
        }

        /// <summary>
        /// 校验驱动提供方名称，非法时抛出可展示给用户的异常。
        /// </summary>
        public static string NormalizeDriverProviderOrThrow(string? driverProvider)
        {
            if (TryNormalizeDriverProvider(driverProvider, out string normalized))
            {
                return normalized;
            }

            throw new ArgumentException("PLC 驱动库仅支持 Hsl、HaoCommunication、McpX", nameof(driverProvider));
        }

        /// <summary>
        /// 将配置中的西门子 CPU 型号转换为 HslCommunication 使用的枚举。
        /// </summary>
        /// <remarks>未知型号按 S1200 处理，避免空配置导致连接创建失败。</remarks>
        public static SiemensPLCS ParseSiemensCpuModel(string? cpuModel)
        {
            return cpuModel?.Trim().ToUpperInvariant() switch
            {
                "S1200" => SiemensPLCS.S1200,
                "S1500" => SiemensPLCS.S1500,
                "S300" => SiemensPLCS.S300,
                "S400" => SiemensPLCS.S400,
                _ => SiemensPLCS.S1200
            };
        }
    }

    /// <summary>
    /// 基于 HslCommunication 的三菱 MC ASCII 适配器。
    /// </summary>
    public class MitsubishiMcAsciiAdapter : IPlcDevice
    {
        private readonly MelsecMcAsciiNet _plc;
        private bool _isConnected;

        public string LastError { get; private set; } = string.Empty;
        public bool IsConnected => _isConnected;
        public string ProtocolName => "三菱MC ASCII";

        public MitsubishiMcAsciiAdapter(string ip, int port)
        {
            _plc = new MelsecMcAsciiNet(ip, port);
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var result = await Task.Run(() => _plc.ConnectServer());
                _isConnected = result.IsSuccess;
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _plc.ConnectClose();
                _isConnected = false;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                LastError = ex.Message;
                Debug.WriteLine($"[MitsubishiMcAscii] Disconnect: {ex.Message}");
            }
        }

        public async Task<(bool Success, short Value)> ReadInt16Async(string address)
        {
            try
            {
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, 0);
            }
        }

        public async Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length)
        {
            try
            {
                var result = await Task.Run(() => _plc.Read(address, length));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, Array.Empty<byte>());
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, Array.Empty<byte>());
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                }
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }
    }

    /// <summary>
    /// 基于 HslCommunication 的三菱 MC Binary 适配器。
    /// </summary>
    public class MitsubishiMcBinaryAdapter : IPlcDevice
    {
        private readonly MelsecMcNet _plc;
        private bool _isConnected;

        public string LastError { get; private set; } = string.Empty;
        public bool IsConnected => _isConnected;
        public string ProtocolName => "三菱MC Binary";

        public MitsubishiMcBinaryAdapter(string ip, int port)
        {
            _plc = new MelsecMcNet(ip, port);
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var result = await Task.Run(() => _plc.ConnectServer());
                _isConnected = result.IsSuccess;
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _plc.ConnectClose();
                _isConnected = false;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                LastError = ex.Message;
                Debug.WriteLine($"[MitsubishiMcBinary] Disconnect: {ex.Message}");
            }
        }

        public async Task<(bool Success, short Value)> ReadInt16Async(string address)
        {
            try
            {
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, 0);
            }
        }

        public async Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length)
        {
            try
            {
                var result = await Task.Run(() => _plc.Read(address, length));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, Array.Empty<byte>());
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, Array.Empty<byte>());
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                }
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }
    }

    /// <summary>
    /// 基于 HslCommunication 的 Modbus TCP 适配器。
    /// </summary>
    public class ModbusTcpAdapter : IPlcDevice
    {
        private readonly ModbusTcpNet _plc;
        private bool _isConnected;

        public string LastError { get; private set; } = string.Empty;
        public bool IsConnected => _isConnected;
        public string ProtocolName => "Modbus TCP";

        public ModbusTcpAdapter(string ip, int port)
        {
            _plc = new ModbusTcpNet(ip, port);
            // 默认站号使用 1，和常见 Modbus TCP 从站配置保持一致。
            _plc.Station = 1;
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var result = await Task.Run(() => _plc.ConnectServer());
                _isConnected = result.IsSuccess;
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _plc.ConnectClose();
                _isConnected = false;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                LastError = ex.Message;
                Debug.WriteLine($"[ModbusTcp] Disconnect: {ex.Message}");
            }
        }

        public async Task<(bool Success, short Value)> ReadInt16Async(string address)
        {
            try
            {
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, 0);
            }
        }

        public async Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length)
        {
            try
            {
                var result = await Task.Run(() => _plc.Read(address, length));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, Array.Empty<byte>());
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, Array.Empty<byte>());
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                }
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }
    }

    /// <summary>
    /// 基于 HslCommunication 的西门子 S7 适配器。
    /// </summary>
    public class SiemensS7Adapter : IPlcDevice
    {
        private readonly SiemensS7Net _plc;
        private bool _isConnected;

        public string LastError { get; private set; } = string.Empty;
        public bool IsConnected => _isConnected;
        public string ProtocolName => "西门子S7";

        public SiemensS7Adapter(string ip, int port)
            : this(ip, port, "S1200", 0, 2)
        {
        }

        public SiemensS7Adapter(string ip, int port, string cpuModel, int rack, int slot)
        {
            SiemensPLCS siemensPlcType = PlcFactory.ParseSiemensCpuModel(cpuModel);
            _plc = new SiemensS7Net(siemensPlcType, ip);
            if (port != 102) // 非默认端口时覆盖
            {
                _plc.Port = port;
            }

            if (siemensPlcType == SiemensPLCS.S300 || siemensPlcType == SiemensPLCS.S400)
            {
                // S300/S400 需要机架和槽位；S1200/S1500 通常不依赖这两个参数。
                _plc.Rack = (byte)Math.Clamp(rack, 0, byte.MaxValue);
                _plc.Slot = (byte)Math.Clamp(slot, 0, byte.MaxValue);
            }
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var result = await Task.Run(() => _plc.ConnectServer());
                _isConnected = result.IsSuccess;
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _plc.ConnectClose();
                _isConnected = false;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                LastError = ex.Message;
                Debug.WriteLine($"[SiemensS7] Disconnect: {ex.Message}");
            }
        }

        public async Task<(bool Success, short Value)> ReadInt16Async(string address)
        {
            try
            {
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, 0);
            }
        }

        public async Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length)
        {
            try
            {
                var result = await Task.Run(() => _plc.Read(address, length));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, Array.Empty<byte>());
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, Array.Empty<byte>());
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                }
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }
    }

    /// <summary>
    /// 基于 HslCommunication 的欧姆龙 FINS 适配器。
    /// </summary>
    public class OmronFinsAdapter : IPlcDevice
    {
        private readonly OmronFinsNet _plc;
        private bool _isConnected;

        public string LastError { get; private set; } = string.Empty;
        public bool IsConnected => _isConnected;
        public string ProtocolName => "欧姆龙Fins";

        public OmronFinsAdapter(string ip, int port)
        {
            _plc = new OmronFinsNet(ip, port);
            // 默认节点号为 0，适用于无需显式配置 FINS 源/目标节点的简化连接。
            _plc.SA1 = 0x00; // 源节点
            _plc.DA1 = 0x00; // 目标节点
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                var result = await Task.Run(() => _plc.ConnectServer());
                _isConnected = result.IsSuccess;
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                _plc.ConnectClose();
                _isConnected = false;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                LastError = ex.Message;
                Debug.WriteLine($"[OmronFins] Disconnect: {ex.Message}");
            }
        }

        public async Task<(bool Success, short Value)> ReadInt16Async(string address)
        {
            try
            {
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, 0);
            }
        }

        public async Task<(bool Success, byte[] Value)> ReadBytesAsync(string address, ushort length)
        {
            try
            {
                var result = await Task.Run(() => _plc.Read(address, length));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                    return (false, Array.Empty<byte>());
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return (false, Array.Empty<byte>());
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    _isConnected = false;
                }
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }
    }
}


