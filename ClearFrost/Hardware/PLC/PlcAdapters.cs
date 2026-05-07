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
    /// <summary>
    /// 
    /// </summary>
    public enum PlcProtocolType
    {
        /// 
        Mitsubishi_MC_ASCII,
        /// 
        Mitsubishi_MC_Binary,
        /// 
        Modbus_TCP,
        /// 
        Siemens_S7,
        /// 
        Omron_Fins
    }

    /// <summary>
    /// 
    /// </summary>
    public static class PlcFactory
    {
        /// <summary>
        /// 
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
        /// 
        /// </summary>
        public static IPlcDevice Create(PlcConnectionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            PlcProtocolType protocol = options.ProtocolType;
            string driverProvider = options.DriverProvider;
            string ip = options.Ip;
            int port = options.Port;

            if (string.Equals(driverProvider, "McpX", StringComparison.OrdinalIgnoreCase))
            {
                return protocol switch
                {
                    PlcProtocolType.Mitsubishi_MC_ASCII => new McpXMitsubishiMcAsciiAdapter(ip, port),
                    PlcProtocolType.Mitsubishi_MC_Binary => new McpXMitsubishiMcBinaryAdapter(ip, port),
                    _ => throw new NotSupportedException($"McpX 仅支持三菱协议，当前: {protocol}")
                };
            }

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
        /// 
        /// </summary>
        public static PlcProtocolType ParseProtocol(string protocolStr)
        {
            if (Enum.TryParse<PlcProtocolType>(protocolStr, true, out var result))
                return result;
            return PlcProtocolType.Mitsubishi_MC_ASCII; // 默认值
        }

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
    /// 
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
    /// 
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
    /// 
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
            // 
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
                // 
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
    /// 
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
                // 
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
    /// 
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
            // 
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
                // 
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


