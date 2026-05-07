using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace ClearFrost.Hardware
{
    // ============================================================================
    // 文件名: HaoCommunicationAdapters.cs
    // 描述:   信息部特调版 HaoCommunication PLC 适配器
    //
    // 功能:
    //   - 在独立 AssemblyLoadContext 中加载同身份 HslCommunication 改版 DLL
    //   - 避免与 NuGet 稳定版 HslCommunication 冲突
    //   - 对接 IPlcDevice 统一接口
    // ============================================================================

    internal sealed class HaoCommunicationLoadContext : AssemblyLoadContext
    {
        private readonly string _haoAssemblyPath;

        public HaoCommunicationLoadContext(string haoAssemblyPath)
            : base("HaoCommunicationContext", isCollectible: false)
        {
            _haoAssemblyPath = haoAssemblyPath;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "HslCommunication", StringComparison.OrdinalIgnoreCase))
            {
                return LoadFromAssemblyPath(_haoAssemblyPath);
            }

            return null;
        }
    }

    internal static class HaoCommunicationRuntime
    {
        private const string AssemblyFileName = "HaoCommunication.dll";

        private static readonly Lazy<Assembly> HaoAssembly = new(LoadHaoAssembly);

        public static Assembly Assembly => HaoAssembly.Value;

        private static Assembly LoadHaoAssembly()
        {
            string assemblyPath = ResolveAssemblyPath();
            var loadContext = new HaoCommunicationLoadContext(assemblyPath);
            return loadContext.LoadFromAssemblyPath(assemblyPath);
        }

        private static string ResolveAssemblyPath()
        {
            string baseDirectory = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDirectory, AssemblyFileName),
                Path.Combine(baseDirectory, "DLL", AssemblyFileName),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", AssemblyFileName))
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"未找到信息部特调版通讯库 {AssemblyFileName}", AssemblyFileName);
        }
    }

    public abstract class HaoCommunicationAdapterBase : IPlcDevice
    {
        private object? _plc;
        private bool _isConnected;

        protected HaoCommunicationAdapterBase(string ip, int port)
        {
            Ip = ip;
            Port = port;
        }

        public string LastError { get; private set; } = string.Empty;

        public bool IsConnected => _isConnected;

        public abstract string ProtocolName { get; }

        protected string Ip { get; }

        protected int Port { get; }

        protected abstract string PlcTypeName { get; }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    CloseCurrentConnection();
                    _plc = CreatePlcInstance(HaoCommunicationRuntime.Assembly);
                    ConfigurePlc(_plc);

                    object result = InvokeMethod(_plc, "ConnectServer", Type.EmptyTypes, Array.Empty<object?>());
                    bool success = ReadBoolProperty(result, "IsSuccess");
                    _isConnected = success;
                    LastError = success ? string.Empty : ReadStringProperty(result, "Message");
                });

                return _isConnected;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                _plc = null;
                return false;
            }
        }

        public void Disconnect()
        {
            try
            {
                CloseCurrentConnection();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.WriteLine($"[{ProtocolName}] Disconnect: {ex.Message}");
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
                object plc = GetConnectedPlc();
                object result = await Task.Run(() => InvokeMethod(
                    plc,
                    "ReadInt16",
                    new[] { typeof(string) },
                    new object?[] { address }));

                if (!ReadBoolProperty(result, "IsSuccess"))
                {
                    LastError = ReadStringProperty(result, "Message");
                    _isConnected = false;
                    return (false, 0);
                }

                short value = Convert.ToInt16(ReadProperty(result, "Content"));
                return (true, value);
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
                object plc = GetConnectedPlc();
                object result = await Task.Run(() => InvokeMethod(
                    plc,
                    "Read",
                    new[] { typeof(string), typeof(ushort) },
                    new object?[] { address, length }));

                if (!ReadBoolProperty(result, "IsSuccess"))
                {
                    LastError = ReadStringProperty(result, "Message");
                    _isConnected = false;
                    return (false, Array.Empty<byte>());
                }

                byte[] value = (byte[])(ReadProperty(result, "Content") ?? Array.Empty<byte>());
                return (true, value);
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
                object plc = GetConnectedPlc();
                object result = await Task.Run(() => InvokeMethod(
                    plc,
                    "Write",
                    new[] { typeof(string), typeof(short) },
                    new object?[] { address, value }));

                bool success = ReadBoolProperty(result, "IsSuccess");
                if (!success)
                {
                    LastError = ReadStringProperty(result, "Message");
                    _isConnected = false;
                }

                return success;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _isConnected = false;
                return false;
            }
        }

        protected virtual object CreatePlcInstance(Assembly assembly)
        {
            Type plcType = GetRequiredType(assembly, PlcTypeName);
            return Activator.CreateInstance(plcType, Ip, Port)
                   ?? throw new InvalidOperationException($"创建 {PlcTypeName} 失败");
        }

        protected virtual void ConfigurePlc(object plc)
        {
        }

        protected static void SetProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName)
                                    ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
            object convertedValue = Convert.ChangeType(value, property.PropertyType);
            property.SetValue(target, convertedValue);
        }

        protected static Type GetRequiredType(Assembly assembly, string typeName)
        {
            return assembly.GetType(typeName, throwOnError: true)
                   ?? throw new TypeLoadException($"未找到类型: {typeName}");
        }

        private object GetConnectedPlc()
        {
            if (_plc == null)
            {
                LastError = "PLC 未连接";
                _isConnected = false;
                throw new InvalidOperationException(LastError);
            }

            return _plc;
        }

        private void CloseCurrentConnection()
        {
            if (_plc == null) return;

            InvokeMethod(_plc, "ConnectClose", Type.EmptyTypes, Array.Empty<object?>());
            _isConnected = false;
        }

        private static object InvokeMethod(object target, string methodName, Type[] parameterTypes, object?[] args)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, parameterTypes)
                                ?? throw new MissingMethodException(target.GetType().FullName, methodName);

            try
            {
                return method.Invoke(target, args)
                       ?? throw new InvalidOperationException($"{methodName} 返回空结果");
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static object? ReadProperty(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName)
                                    ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
            return property.GetValue(target);
        }

        private static bool ReadBoolProperty(object target, string propertyName)
        {
            return Convert.ToBoolean(ReadProperty(target, propertyName));
        }

        private static string ReadStringProperty(object target, string propertyName)
        {
            return Convert.ToString(ReadProperty(target, propertyName)) ?? string.Empty;
        }
    }

    public sealed class HaoMitsubishiMcAsciiAdapter : HaoCommunicationAdapterBase
    {
        public HaoMitsubishiMcAsciiAdapter(string ip, int port)
            : base(ip, port)
        {
        }

        public override string ProtocolName => "三菱MC ASCII (信息部特调版)";

        protected override string PlcTypeName => "HslCommunication.Profinet.Melsec.MelsecMcAsciiNet";
    }

    public sealed class HaoMitsubishiMcBinaryAdapter : HaoCommunicationAdapterBase
    {
        public HaoMitsubishiMcBinaryAdapter(string ip, int port)
            : base(ip, port)
        {
        }

        public override string ProtocolName => "三菱MC Binary (信息部特调版)";

        protected override string PlcTypeName => "HslCommunication.Profinet.Melsec.MelsecMcNet";
    }

    public sealed class HaoModbusTcpAdapter : HaoCommunicationAdapterBase
    {
        public HaoModbusTcpAdapter(string ip, int port)
            : base(ip, port)
        {
        }

        public override string ProtocolName => "Modbus TCP (信息部特调版)";

        protected override string PlcTypeName => "HslCommunication.ModBus.ModbusTcpNet";

        protected override object CreatePlcInstance(Assembly assembly)
        {
            Type plcType = GetRequiredType(assembly, PlcTypeName);
            try
            {
                return Activator.CreateInstance(plcType, Ip, Port)
                       ?? throw new InvalidOperationException($"创建 {PlcTypeName} 失败");
            }
            catch (MissingMethodException)
            {
                return Activator.CreateInstance(plcType, Ip, Port, (byte)1)
                       ?? throw new InvalidOperationException($"创建 {PlcTypeName} 失败");
            }
        }

        protected override void ConfigurePlc(object plc)
        {
            SetProperty(plc, "Station", 1);
        }
    }

    public sealed class HaoSiemensS7Adapter : HaoCommunicationAdapterBase
    {
        private readonly string _cpuModel;
        private readonly int _rack;
        private readonly int _slot;

        public HaoSiemensS7Adapter(string ip, int port, string cpuModel, int rack, int slot)
            : base(ip, port)
        {
            _cpuModel = cpuModel;
            _rack = rack;
            _slot = slot;
        }

        public override string ProtocolName => "西门子S7 (信息部特调版)";

        protected override string PlcTypeName => "HslCommunication.Profinet.Siemens.SiemensS7Net";

        protected override object CreatePlcInstance(Assembly assembly)
        {
            Type plcType = GetRequiredType(assembly, PlcTypeName);
            Type cpuEnumType = GetRequiredType(assembly, "HslCommunication.Profinet.Siemens.SiemensPLCS");
            object cpuType = Enum.Parse(cpuEnumType, NormalizeCpuModel(_cpuModel), ignoreCase: true);

            object plc = Activator.CreateInstance(plcType, cpuType, Ip)
                         ?? throw new InvalidOperationException($"创建 {PlcTypeName} 失败");
            if (Port != 102)
            {
                SetProperty(plc, "Port", Port);
            }

            string normalizedCpuModel = NormalizeCpuModel(_cpuModel);
            if (normalizedCpuModel is "S300" or "S400")
            {
                SetProperty(plc, "Rack", (byte)Math.Clamp(_rack, 0, byte.MaxValue));
                SetProperty(plc, "Slot", (byte)Math.Clamp(_slot, 0, byte.MaxValue));
            }

            return plc;
        }

        private static string NormalizeCpuModel(string? cpuModel)
        {
            return cpuModel?.Trim().ToUpperInvariant() switch
            {
                "S1200" => "S1200",
                "S1500" => "S1500",
                "S300" => "S300",
                "S400" => "S400",
                _ => "S1200"
            };
        }
    }

    public sealed class HaoOmronFinsAdapter : HaoCommunicationAdapterBase
    {
        public HaoOmronFinsAdapter(string ip, int port)
            : base(ip, port)
        {
        }

        public override string ProtocolName => "欧姆龙Fins (信息部特调版)";

        protected override string PlcTypeName => "HslCommunication.Profinet.Omron.OmronFinsNet";

        protected override void ConfigurePlc(object plc)
        {
            SetProperty(plc, "SA1", 0x00);
            SetProperty(plc, "DA1", 0x00);
        }
    }
}
