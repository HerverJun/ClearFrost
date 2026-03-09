using System;
using System.Threading.Tasks;
using McpXLib;
using McpXLib.Enums;

namespace ClearFrost.Hardware
{
    // ============================================================================
    // 文件名: McpXAdapters.cs
    // 描述:   McpX 三菱 PLC 适配器
    //
    // 功能:
    //   - 基于 McpX 提供三菱 MC Binary/ASCII 适配
    //   - 对接 IPlcDevice 统一接口
    //   - 兼容当前业务层使用的 D 区 Int16 读写模型
    // ============================================================================

    public abstract class McpXMitsubishiAdapterBase : IPlcDevice
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly bool _isAscii;
        private McpX? _plc;
        private bool _isConnected;

        protected McpXMitsubishiAdapterBase(string ip, int port, bool isAscii)
        {
            _ip = ip;
            _port = port;
            _isAscii = isAscii;
        }

        public string LastError { get; private set; } = string.Empty;

        public bool IsConnected => _isConnected;

        public abstract string ProtocolName { get; }

        public Task<bool> ConnectAsync()
        {
            try
            {
                _plc?.Dispose();
                _plc = new McpX(_ip, _port, isAscii: _isAscii, requestFrame: RequestFrame.E3);
                _isConnected = true;
                LastError = string.Empty;
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                _plc = null;
                return Task.FromResult(false);
            }
        }

        public void Disconnect()
        {
            try
            {
                _plc?.Dispose();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                _plc = null;
                _isConnected = false;
            }
        }

        public async Task<(bool Success, short Value)> ReadInt16Async(string address)
        {
            try
            {
                if (_plc == null)
                {
                    LastError = "PLC 未连接";
                    return (false, 0);
                }

                var (prefix, numericAddress) = ParseAddress(address);
                short value = await _plc.ReadInt16Async(prefix, numericAddress);
                return (true, value);
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
                if (_plc == null)
                {
                    LastError = "PLC 未连接";
                    return false;
                }

                var (prefix, numericAddress) = ParseAddress(address);
                await _plc.WriteInt16Async(prefix, numericAddress, value);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private static (Prefix Prefix, string Address) ParseAddress(string address)
        {
            string normalized = (address ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new ArgumentException("PLC 地址不能为空", nameof(address));
            }

            if (char.IsDigit(normalized[0]))
            {
                return (Prefix.D, normalized);
            }

            string upper = normalized.ToUpperInvariant();
            if (upper.StartsWith("D", StringComparison.Ordinal))
            {
                string numericAddress = upper.Substring(1).Trim();
                if (string.IsNullOrWhiteSpace(numericAddress))
                {
                    throw new ArgumentException($"PLC 地址格式无效: {address}", nameof(address));
                }

                return (Prefix.D, numericAddress);
            }

            throw new NotSupportedException($"McpX 当前仅支持 D 区地址，收到: {address}");
        }
    }

    public sealed class McpXMitsubishiMcBinaryAdapter : McpXMitsubishiAdapterBase
    {
        public McpXMitsubishiMcBinaryAdapter(string ip, int port)
            : base(ip, port, false)
        {
        }

        public override string ProtocolName => "三菱MC Binary (McpX)";
    }

    public sealed class McpXMitsubishiMcAsciiAdapter : McpXMitsubishiAdapterBase
    {
        public McpXMitsubishiMcAsciiAdapter(string ip, int port)
            : base(ip, port, true)
        {
        }

        public override string ProtocolName => "三菱MC ASCII (McpX)";
    }
}
