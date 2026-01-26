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
        public static IPlcDevice Create(PlcProtocolType protocol, string ip, int port)
        {
            return protocol switch
            {
                PlcProtocolType.Mitsubishi_MC_ASCII => new MitsubishiMcAsciiAdapter(ip, port),
                PlcProtocolType.Mitsubishi_MC_Binary => new MitsubishiMcBinaryAdapter(ip, port),
                PlcProtocolType.Modbus_TCP => new ModbusTcpAdapter(ip, port),
                PlcProtocolType.Siemens_S7 => new SiemensS7Adapter(ip, port),
                PlcProtocolType.Omron_Fins => new OmronFinsAdapter(ip, port),
                _ => throw new NotSupportedException($"��֧�ֵ�Э������: {protocol}")
            };
        }

        /// <summary>
        /// 
        /// </summary>
        public static PlcProtocolType ParseProtocol(string protocolStr)
        {
            if (Enum.TryParse<PlcProtocolType>(protocolStr, true, out var result))
                return result;
            return PlcProtocolType.Mitsubishi_MC_ASCII; // Ĭ��ֵ
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
        {
            // 
            _plc = new SiemensS7Net(SiemensPLCS.S1200, ip);
            if (port != 102) // ��Ĭ�϶˿�ʱ����
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
                // 
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
            _plc.SA1 = 0x00; // Դ�ڵ�
            _plc.DA1 = 0x00; // Ŀ��ڵ�
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


