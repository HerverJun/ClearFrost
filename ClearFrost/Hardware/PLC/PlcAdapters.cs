using System;
using System.Diagnostics;
using System.Threading.Tasks;
using HslCommunication;
using HslCommunication.Profinet.Melsec;
using HslCommunication.Profinet.Siemens;
using HslCommunication.Profinet.Omron;
using HslCommunication.ModBus;

namespace YOLO
{
    /// <summary>
    /// PLC协议类型枚举
    /// </summary>
    public enum PlcProtocolType
    {
        /// <summary>三菱MC ASCII协议</summary>
        Mitsubishi_MC_ASCII,
        /// <summary>三菱MC Binary协议</summary>
        Mitsubishi_MC_Binary,
        /// <summary>Modbus TCP协议</summary>
        Modbus_TCP,
        /// <summary>西门子S7协议 (S7-1200/1500)</summary>
        Siemens_S7,
        /// <summary>欧姆龙Fins TCP协议</summary>
        Omron_Fins
    }

    /// <summary>
    /// PLC设备工厂类
    /// </summary>
    public static class PlcFactory
    {
        /// <summary>
        /// 根据协议类型创建对应的PLC设备适配器
        /// </summary>
        public static IPlcDevice Create(PlcProtocolType protocol, string ip, int port)
        {
            return protocol switch
            {
                PlcProtocolType.Mitsubishi_MC_ASCII => new MitsubishiMcAsciiAdapter(ip, port),
                PlcProtocolType.Mitsubishi_MC_Binary => new MitsubishiMcBinaryAdapter(ip, port),
                PlcProtocolType.Modbus_TCP => new ModbusTcpAdapter(ip, port),
                PlcProtocolType.Siemens_S7 => new SiemensS7Adapter(ip, port),
                PlcProtocolType.Omron_Fins => new OmronFinsAdapter(ip, port),
                _ => throw new NotSupportedException($"不支持的协议类型: {protocol}")
            };
        }

        /// <summary>
        /// 从字符串解析协议类型
        /// </summary>
        public static PlcProtocolType ParseProtocol(string protocolStr)
        {
            if (Enum.TryParse<PlcProtocolType>(protocolStr, true, out var result))
                return result;
            return PlcProtocolType.Mitsubishi_MC_ASCII; // 默认值
        }
    }

    /// <summary>
    /// 三菱MC ASCII协议适配器（原有实现）
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
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return (false, 0);
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// 三菱MC Binary协议适配器
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
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return (false, 0);
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Modbus TCP协议适配器
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
            // Modbus默认站号为1
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
                // Modbus地址格式：直接使用数字地址
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return (false, 0);
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// 西门子S7协议适配器 (支持S7-1200/1500)
    /// </summary>
    public class SiemensS7Adapter : IPlcDevice
    {
        private readonly SiemensS7Net _plc;
        private bool _isConnected;

        public string LastError { get; private set; } = string.Empty;
        public bool IsConnected => _isConnected;
        public string ProtocolName => "西门子S7";

        public SiemensS7Adapter(string ip, int port)
        {
            // 默认使用S7-1200，端口通常为102
            _plc = new SiemensS7Net(SiemensPLCS.S1200, ip);
            if (port != 102) // 非默认端口时设置
            {
                _plc.Port = port;
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
                // 西门子地址格式：DB1.0, M0, I0, Q0 等
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return (false, 0);
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// 欧姆龙Fins TCP协议适配器
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
            // 默认节点地址
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
                // 欧姆龙地址格式：D100, W100, H100 等
                var result = await Task.Run(() => _plc.ReadInt16(address));
                if (!result.IsSuccess)
                {
                    LastError = result.Message;
                    return (false, 0);
                }
                return (true, result.Content);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return (false, 0);
            }
        }

        public async Task<bool> WriteInt16Async(string address, short value)
        {
            try
            {
                var result = await Task.Run(() => _plc.Write(address, value));
                if (!result.IsSuccess)
                    LastError = result.Message;
                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }
    }
}
