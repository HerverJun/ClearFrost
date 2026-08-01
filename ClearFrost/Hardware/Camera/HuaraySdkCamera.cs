using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// Huaray camera adapter. The private MVSDK assembly is loaded only at runtime from an
    /// explicit external input, so tracked-only builds do not acquire a fake SDK contract.
    /// </summary>
    public class HuaraySdkCamera : ICamera, ICameraProvider, ICameraFeatureInspector, ICameraFramePixelConverter
    {
        private const int SdkUnavailable = -1001;
        private const int SdkInvocationFailed = -1002;

        private readonly string _targetSerialNumber;
        private readonly HuaraySdkBridge? _bridge;
        private readonly string _bridgeError;
        private object? _nativeCamera;
        private object? _nativeFrame;
        private bool _handleCreated;
        private bool _isConnected;
        private bool _isGrabbing;
        private bool _disposed;
        private CameraDeviceInfo? _currentDevice;
        private byte[]? _convertedFrameBuffer;
        private GCHandle _convertedFrameHandle;

        public HuaraySdkCamera(string? targetSerialNumber = null)
        {
            _targetSerialNumber = targetSerialNumber?.Trim() ?? string.Empty;
            if (HuaraySdkBridge.TryCreate(out HuaraySdkBridge? bridge, out string error))
            {
                _bridge = bridge;
                _bridgeError = string.Empty;
            }
            else
            {
                _bridgeError = error;
            }
        }

        public string ProviderName => "Huaray";

        public bool IsConnected => _isConnected;

        public bool IsGrabbing => _isGrabbing && _nativeCamera != null && _bridge?.IsGrabbing(_nativeCamera) == true;

        public CameraDeviceInfo? CurrentDevice => _currentDevice;

        public List<CameraDeviceInfo> EnumerateDevices()
        {
            if (_bridge == null)
            {
                LogSdkUnavailable();
                return new List<CameraDeviceInfo>();
            }

            int result = _bridge.EnumerateDevices(out List<CameraDeviceInfo> devices);
            if (result != CameraSdk.Ok)
            {
                Debug.WriteLine($"[HuaraySdkCamera] EnumerateDevices failed: {result}");
                return new List<CameraDeviceInfo>();
            }

            return devices;
        }

        public static int EnumerateDevicesStatic(ref CameraDeviceList deviceList, uint interfaceType)
        {
            using var camera = new HuaraySdkCamera();
            return camera.IMV_EnumDevices(ref deviceList, interfaceType);
        }

        public int IMV_EnumDevices(ref CameraDeviceList deviceList, uint interfaceType)
        {
            deviceList ??= new CameraDeviceList();
            List<CameraDeviceInfo> devices = EnumerateDevices();
            deviceList.Devices.Clear();
            deviceList.Devices.AddRange(devices);
            return _bridge == null ? SdkUnavailable : CameraSdk.Ok;
        }

        public int IMV_CreateHandle(CameraCreateHandleMode mode, int index)
        {
            if (_bridge == null)
            {
                LogSdkUnavailable();
                return SdkUnavailable;
            }

            _nativeCamera ??= _bridge.CreateCamera();
            if (_nativeCamera == null)
            {
                return SdkInvocationFailed;
            }

            int result = _bridge.CreateHandle(_nativeCamera, mode, index);
            _handleCreated = result == CameraSdk.Ok;
            return result;
        }

        public int IMV_Open()
        {
            if (_bridge == null)
            {
                LogSdkUnavailable();
                return SdkUnavailable;
            }

            if (!_handleCreated)
            {
                if (string.IsNullOrWhiteSpace(_targetSerialNumber))
                {
                    return -1;
                }

                List<CameraDeviceInfo> devices = EnumerateDevices();
                int index = devices.FindIndex(device => string.Equals(
                    device.SerialNumber,
                    _targetSerialNumber,
                    StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return -1;
                }

                int createResult = IMV_CreateHandle(CameraCreateHandleMode.ByIndex, index);
                if (createResult != CameraSdk.Ok)
                {
                    return createResult;
                }

                _currentDevice = devices[index];
            }

            if (_nativeCamera == null)
            {
                return SdkInvocationFailed;
            }

            int result = _bridge.Open(_nativeCamera);
            _isConnected = result == CameraSdk.Ok;
            return result;
        }

        public bool Open(string serialNumber)
        {
            if (_bridge == null)
            {
                LogSdkUnavailable();
                return false;
            }

            _currentDevice = EnumerateDevices().FirstOrDefault(device => string.Equals(
                device.SerialNumber,
                serialNumber?.Trim(),
                StringComparison.OrdinalIgnoreCase));
            if (_currentDevice == null)
            {
                return false;
            }

            int createResult = IMV_CreateHandle(CameraCreateHandleMode.ByIndex, EnumerateDevices().FindIndex(
                device => string.Equals(device.SerialNumber, _currentDevice.SerialNumber, StringComparison.OrdinalIgnoreCase)));
            return createResult == CameraSdk.Ok && IMV_Open() == CameraSdk.Ok;
        }

        public int IMV_SetEnumFeatureSymbol(string name, string value)
        {
            return _nativeCamera == null || _bridge == null
                ? SdkUnavailable
                : _bridge.SetEnumFeatureSymbol(_nativeCamera, name, value);
        }

        public int IMV_SetDoubleFeatureValue(string name, double value)
        {
            return _nativeCamera == null || _bridge == null
                ? SdkUnavailable
                : _bridge.SetDoubleFeatureValue(_nativeCamera, name, value);
        }

        public int IMV_SetBufferCount(int count)
        {
            return _nativeCamera == null || _bridge == null
                ? SdkUnavailable
                : _bridge.SetBufferCount(_nativeCamera, count);
        }

        public int IMV_StartGrabbing()
        {
            if (_nativeCamera == null || _bridge == null)
            {
                return SdkUnavailable;
            }

            int result = _bridge.StartGrabbing(_nativeCamera);
            _isGrabbing = result == CameraSdk.Ok;
            return result;
        }

        public int IMV_StopGrabbing()
        {
            if (_nativeCamera == null || _bridge == null)
            {
                return SdkUnavailable;
            }

            int result = _bridge.StopGrabbing(_nativeCamera);
            _isGrabbing = false;
            return result;
        }

        public int IMV_Close()
        {
            _isConnected = false;
            _isGrabbing = false;
            if (_nativeCamera == null || _bridge == null || !_handleCreated)
            {
                return CameraSdk.Ok;
            }

            return _bridge.Close(_nativeCamera);
        }

        public int IMV_DestroyHandle()
        {
            _isConnected = false;
            _isGrabbing = false;
            if (_nativeCamera == null || _bridge == null || !_handleCreated)
            {
                return CameraSdk.Ok;
            }

            int result = _bridge.DestroyHandle(_nativeCamera);
            _handleCreated = false;
            return result;
        }

        public int IMV_ExecuteCommandFeature(string name)
        {
            return _nativeCamera == null || _bridge == null
                ? SdkUnavailable
                : _bridge.ExecuteCommandFeature(_nativeCamera, name);
        }

        public int IMV_ClearFrameBuffer()
        {
            return _nativeCamera == null || _bridge == null
                ? SdkUnavailable
                : _bridge.ClearFrameBuffer(_nativeCamera);
        }

        public bool IMV_IsGrabbing() => IsGrabbing;

        public int IMV_GetFrame(ref CameraFrame frame, int timeout)
        {
            if (_nativeCamera == null || _bridge == null)
            {
                return SdkUnavailable;
            }

            ReleaseNativeFrame();
            int result = _bridge.GetFrame(_nativeCamera, timeout, out object? nativeFrame, out CameraFrame? managedFrame);
            if (result != CameraSdk.Ok || nativeFrame == null || managedFrame == null)
            {
                return result;
            }

            _nativeFrame = nativeFrame;
            frame = managedFrame;
            return CameraSdk.Ok;
        }

        public int IMV_ReleaseFrame(ref CameraFrame frame)
        {
            int result = ReleaseNativeFrame();
            frame = new CameraFrame();
            return result;
        }

        public CameraFrame? GetFrame(int timeoutMs = 1000)
        {
            CameraFrame frame = new();
            int result = IMV_GetFrame(ref frame, timeoutMs);
            if (result != CameraSdk.Ok)
            {
                return null;
            }

            CameraFrame capturedFrame = frame;
            capturedFrame.ReleaseCallback = _ =>
            {
                CameraFrame releaseFrame = capturedFrame;
                IMV_ReleaseFrame(ref releaseFrame);
            };
            return capturedFrame;
        }

        public bool Close() => IMV_Close() == CameraSdk.Ok;

        public bool StartGrabbing() => IMV_StartGrabbing() == CameraSdk.Ok;

        public bool StopGrabbing() => IMV_StopGrabbing() == CameraSdk.Ok;

        public bool SetExposure(double microseconds) => IMV_SetDoubleFeatureValue("ExposureTime", microseconds) == CameraSdk.Ok;

        public bool SetGain(double value) => IMV_SetDoubleFeatureValue("GainRaw", value) == CameraSdk.Ok;

        public bool SetPixelFormat(string pixelFormat) => IMV_SetEnumFeatureSymbol("PixelFormat", pixelFormat) == CameraSdk.Ok;

        public bool SetTriggerMode(bool softwareTrigger)
        {
            if (!_isConnected)
            {
                return false;
            }

            if (IMV_SetEnumFeatureSymbol("TriggerMode", softwareTrigger ? "On" : "Off") != CameraSdk.Ok)
            {
                return false;
            }

            return !softwareTrigger || IMV_SetEnumFeatureSymbol("TriggerSource", "Software") == CameraSdk.Ok;
        }

        public bool ExecuteSoftwareTrigger() => IMV_ExecuteCommandFeature("TriggerSoftware") == CameraSdk.Ok;

        public bool TryGetEnumFeatureSymbol(string name, out string value)
        {
            value = string.Empty;
            if (_nativeCamera == null || _bridge == null || !_isConnected)
            {
                return false;
            }

            return _bridge.TryGetEnumFeatureSymbol(_nativeCamera, name, out value);
        }

        public IReadOnlyList<string> GetEnumFeatureEntries(string name)
        {
            if (_nativeCamera == null || _bridge == null || !_isConnected)
            {
                return Array.Empty<string>();
            }

            return _bridge.GetEnumFeatureEntries(_nativeCamera, name);
        }

        public bool TryConvertFrameToBgr8(CameraFrame frame, out CameraFrame convertedFrame)
        {
            convertedFrame = null!;
            if (_nativeCamera == null || _bridge == null || !_isConnected ||
                frame.DataPtr == IntPtr.Zero || frame.Width <= 0 || frame.Height <= 0 ||
                CameraSdk.ToPixelFormat(frame.RawPixelFormat) == CameraPixelFormat.Unknown)
            {
                return false;
            }

            try
            {
                int destinationSize = checked(frame.Width * frame.Height * 3);
                EnsureConvertedFrameBuffer(destinationSize);
                int result = _bridge.PixelConvert(
                    _nativeCamera,
                    frame,
                    _convertedFrameHandle.AddrOfPinnedObject(),
                    (uint)destinationSize,
                    out uint convertedSize);
                if (result != CameraSdk.Ok || convertedSize == 0)
                {
                    return false;
                }

                convertedFrame = new CameraFrame
                {
                    DataPtr = _convertedFrameHandle.AddrOfPinnedObject(),
                    Width = frame.Width,
                    Height = frame.Height,
                    Size = checked((int)convertedSize),
                    PixelFormat = CameraPixelFormat.BGR8,
                    RawPixelFormat = CameraSdk.GvspPixelBgr8,
                    FrameNumber = frame.FrameNumber,
                    Timestamp = frame.Timestamp
                };
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HuaraySdkCamera] PixelConvert failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                ReleaseNativeFrame();
                if (_nativeCamera != null && _bridge != null && _bridge.IsGrabbing(_nativeCamera))
                {
                    _bridge.StopGrabbing(_nativeCamera);
                }

                IMV_Close();
                IMV_DestroyHandle();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HuaraySdkCamera] Dispose failed: {ex.Message}");
            }
            finally
            {
                ReleaseConvertedFrameBuffer();
                _nativeCamera = null;
                _disposed = true;
            }
        }

        private int ReleaseNativeFrame()
        {
            if (_nativeFrame == null || _nativeCamera == null || _bridge == null)
            {
                _nativeFrame = null;
                return CameraSdk.Ok;
            }

            int result = _bridge.ReleaseFrame(_nativeCamera, _nativeFrame);
            _nativeFrame = null;
            return result;
        }

        private void EnsureConvertedFrameBuffer(int size)
        {
            if (_convertedFrameBuffer != null && _convertedFrameBuffer.Length >= size && _convertedFrameHandle.IsAllocated)
            {
                return;
            }

            ReleaseConvertedFrameBuffer();
            _convertedFrameBuffer = new byte[size];
            _convertedFrameHandle = GCHandle.Alloc(_convertedFrameBuffer, GCHandleType.Pinned);
        }

        private void ReleaseConvertedFrameBuffer()
        {
            if (_convertedFrameHandle.IsAllocated)
            {
                _convertedFrameHandle.Free();
            }

            _convertedFrameBuffer = null;
        }

        private void LogSdkUnavailable()
        {
            if (!string.IsNullOrWhiteSpace(_bridgeError))
            {
                Debug.WriteLine($"[HuaraySdkCamera] {_bridgeError}");
            }
        }
    }

    internal sealed class HuaraySdkBridge
    {
        private const int SdkInvocationFailed = -1002;

        private readonly Type _cameraType;
        private readonly Type _deviceListType;
        private readonly Type _deviceInfoType;
        private readonly Type _frameType;
        private readonly Type _frameInfoType;
        private readonly Type _pixelConvertParamType;
        private readonly Type _stringType;
        private readonly Type _enumEntryInfoType;
        private readonly Type _enumEntryListType;
        private readonly Type _pixelType;
        private readonly Type _createHandleModeType;
        private readonly Type _bayerDemosaicType;

        private HuaraySdkBridge(Assembly assembly)
        {
            _cameraType = GetRequiredType(assembly, "MVSDK_Net.MyCamera");
            Type defineType = GetRequiredType(assembly, "MVSDK_Net.IMVDefine");
            _deviceListType = GetNestedType(defineType, "IMV_DeviceList");
            _deviceInfoType = GetNestedType(defineType, "IMV_DeviceInfo");
            _frameType = GetNestedType(defineType, "IMV_Frame");
            _frameInfoType = GetNestedType(defineType, "IMV_FrameInfo");
            _pixelConvertParamType = GetNestedType(defineType, "IMV_PixelConvertParam");
            _stringType = GetNestedType(defineType, "IMV_String");
            _enumEntryInfoType = GetNestedType(defineType, "IMV_EnumEntryInfo");
            _enumEntryListType = GetNestedType(defineType, "IMV_EnumEntryList");
            _pixelType = GetNestedType(defineType, "IMV_EPixelType");
            _createHandleModeType = GetNestedType(defineType, "IMV_ECreateHandleMode");
            _bayerDemosaicType = GetNestedType(defineType, "IMV_EBayerDemosaic");
        }

        public static bool TryCreate(out HuaraySdkBridge? bridge, out string error)
        {
            string? sdkPath = ResolveSdkPath();
            if (string.IsNullOrWhiteSpace(sdkPath))
            {
                bridge = null;
                error = "MVSDK_Net.dll was not supplied through CLEARFROST_HUARAY_SDK_PATH or the application directory.";
                return false;
            }

            try
            {
                bridge = new HuaraySdkBridge(Assembly.LoadFrom(sdkPath));
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                bridge = null;
                error = $"Unable to load Huaray SDK '{sdkPath}': {ex.Message}";
                return false;
            }
        }

        public object? CreateCamera()
        {
            try
            {
                return Activator.CreateInstance(_cameraType);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HuaraySdkBridge] Create camera failed: {ex.Message}");
                return null;
            }
        }

        public int EnumerateDevices(out List<CameraDeviceInfo> devices)
        {
            devices = new List<CameraDeviceInfo>();
            object deviceList = Activator.CreateInstance(_deviceListType)!;
            object?[] args = { deviceList, 0xFFFFFFFFu };
            int result = Invoke("IMV_EnumDevices", args);
            if (result != CameraSdk.Ok)
            {
                return result;
            }

            deviceList = args[0]!;
            uint count = Convert.ToUInt32(GetMember(deviceList, "nDevNum") ?? 0u);
            IntPtr pointer = (IntPtr)(GetMember(deviceList, "pDevInfo") ?? IntPtr.Zero);
            if (count == 0 || pointer == IntPtr.Zero)
            {
                return CameraSdk.Ok;
            }

            int entrySize = Marshal.SizeOf(_deviceInfoType);
            for (int index = 0; index < count; index++)
            {
                object info = Marshal.PtrToStructure(IntPtr.Add(pointer, index * entrySize), _deviceInfoType)!;
                devices.Add(new CameraDeviceInfo
                {
                    SerialNumber = ReadString(info, "serialNumber"),
                    Manufacturer = ReadString(info, "vendorName"),
                    Model = ReadString(info, "modelName"),
                    UserDefinedName = ReadString(info, "cameraName"),
                    InterfaceType = (GetMember(info, "nInterfaceType")?.ToString() ?? string.Empty).Trim()
                });
            }

            return CameraSdk.Ok;
        }

        public int CreateHandle(object camera, CameraCreateHandleMode mode, int index)
        {
            object sdkMode = Enum.ToObject(_createHandleModeType, (int)mode);
            return Invoke(camera, "IMV_CreateHandle", sdkMode, index, string.Empty);
        }

        public int Open(object camera) => Invoke(camera, "IMV_Open");

        public int Close(object camera) => Invoke(camera, "IMV_Close");

        public int DestroyHandle(object camera) => Invoke(camera, "IMV_DestroyHandle");

        public int SetEnumFeatureSymbol(object camera, string name, string value) => Invoke(camera, "IMV_SetEnumFeatureSymbol", name, value);

        public int SetDoubleFeatureValue(object camera, string name, double value) => Invoke(camera, "IMV_SetDoubleFeatureValue", name, value);

        public int SetBufferCount(object camera, int count) => Invoke(camera, "IMV_SetBufferCount", (uint)Math.Max(0, count));

        public int StartGrabbing(object camera) => Invoke(camera, "IMV_StartGrabbing");

        public int StopGrabbing(object camera) => Invoke(camera, "IMV_StopGrabbing");

        public int ClearFrameBuffer(object camera) => Invoke(camera, "IMV_ClearFrameBuffer");

        public int ExecuteCommandFeature(object camera, string name) => Invoke(camera, "IMV_ExecuteCommandFeature", name);

        public bool IsGrabbing(object camera)
        {
            try
            {
                return Convert.ToBoolean(InvokeRaw(camera, "IMV_IsGrabbing"));
            }
            catch
            {
                return false;
            }
        }

        public int GetFrame(object camera, int timeout, out object? nativeFrame, out CameraFrame? frame)
        {
            nativeFrame = Activator.CreateInstance(_frameType);
            frame = null;
            object?[] args = { nativeFrame, (uint)Math.Max(0, timeout) };
            int result = Invoke(camera, "IMV_GetFrame", args);
            if (result != CameraSdk.Ok)
            {
                return result;
            }

            nativeFrame = args[0];
            if (nativeFrame == null)
            {
                return -1;
            }

            object frameInfo = GetMember(nativeFrame, "frameInfo")!;
            uint rawPixelFormat = Convert.ToUInt32(GetMember(frameInfo, "pixelFormat") ?? 0);
            frame = new CameraFrame
            {
                DataPtr = (IntPtr)(GetMember(nativeFrame, "pData") ?? IntPtr.Zero),
                Width = checked((int)Convert.ToUInt32(GetMember(frameInfo, "width") ?? 0)),
                Height = checked((int)Convert.ToUInt32(GetMember(frameInfo, "height") ?? 0)),
                Size = checked((int)Convert.ToUInt32(GetMember(frameInfo, "size") ?? 0)),
                PixelFormat = CameraSdk.ToPixelFormat(rawPixelFormat),
                RawPixelFormat = rawPixelFormat,
                Status = Convert.ToUInt32(GetMember(frameInfo, "status") ?? 0),
                PaddingX = Convert.ToUInt32(GetMember(frameInfo, "paddingX") ?? 0),
                PaddingY = Convert.ToUInt32(GetMember(frameInfo, "paddingY") ?? 0),
                FrameNumber = Convert.ToUInt64(GetMember(frameInfo, "blockId") ?? 0UL),
                Timestamp = Convert.ToUInt64(GetMember(frameInfo, "timeStamp") ?? 0UL)
            };
            return CameraSdk.Ok;
        }

        public int ReleaseFrame(object camera, object nativeFrame)
        {
            object?[] args = { nativeFrame };
            return Invoke(camera, "IMV_ReleaseFrame", args);
        }

        public bool TryGetEnumFeatureSymbol(object camera, string name, out string value)
        {
            value = string.Empty;
            if (!Convert.ToBoolean(InvokeRaw(camera, "IMV_FeatureIsReadable", name)))
            {
                return false;
            }

            object symbol = Activator.CreateInstance(_stringType)!;
            object?[] args = { name, symbol };
            int result = Invoke(camera, "IMV_GetEnumFeatureSymbol", args);
            if (result != CameraSdk.Ok)
            {
                return false;
            }

            value = ReadString(args[1]!, "str");
            return !string.IsNullOrWhiteSpace(value);
        }

        public IReadOnlyList<string> GetEnumFeatureEntries(object camera, string name)
        {
            if (!Convert.ToBoolean(InvokeRaw(camera, "IMV_FeatureIsReadable", name)))
            {
                return Array.Empty<string>();
            }

            object?[] countArgs = { name, 0u };
            int countResult = Invoke(camera, "IMV_GetEnumFeatureEntryNum", countArgs);
            if (countResult != CameraSdk.Ok)
            {
                return Array.Empty<string>();
            }

            uint entryCount = Convert.ToUInt32(countArgs[1] ?? 0u);
            if (entryCount == 0)
            {
                return Array.Empty<string>();
            }

            int entrySize = Marshal.SizeOf(_enumEntryInfoType);
            IntPtr buffer = Marshal.AllocHGlobal(checked(entrySize * (int)entryCount));
            try
            {
                object entryList = Activator.CreateInstance(_enumEntryListType)!;
                SetMember(entryList, "nEnumEntryBufferSize", checked((uint)(entrySize * (int)entryCount)));
                SetMember(entryList, "pEnumEntryInfo", buffer);
                object?[] listArgs = { name, entryList };
                if (Invoke(camera, "IMV_GetEnumFeatureEntrys", listArgs) != CameraSdk.Ok)
                {
                    return Array.Empty<string>();
                }

                var entries = new List<string>((int)entryCount);
                for (int index = 0; index < entryCount; index++)
                {
                    object info = Marshal.PtrToStructure(IntPtr.Add(buffer, index * entrySize), _enumEntryInfoType)!;
                    string entryName = ReadString(info, "name");
                    if (!string.IsNullOrWhiteSpace(entryName))
                    {
                        entries.Add(entryName);
                    }
                }

                return entries;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public int PixelConvert(object camera, CameraFrame frame, IntPtr destination, uint destinationSize, out uint convertedSize)
        {
            convertedSize = 0;
            object parameters = Activator.CreateInstance(_pixelConvertParamType)!;
            SetMember(parameters, "nWidth", (uint)frame.Width);
            SetMember(parameters, "nHeight", (uint)frame.Height);
            SetMember(parameters, "ePixelFormat", Enum.ToObject(_pixelType, frame.RawPixelFormat));
            SetMember(parameters, "pSrcData", frame.DataPtr);
            SetMember(parameters, "nSrcDataLen", (uint)frame.Size);
            SetMember(parameters, "nPaddingX", frame.PaddingX);
            SetMember(parameters, "nPaddingY", frame.PaddingY);
            SetMember(parameters, "eBayerDemosaic", Enum.ToObject(_bayerDemosaicType, 2));
            SetMember(parameters, "eDstPixelFormat", Enum.ToObject(_pixelType, CameraSdk.GvspPixelBgr8));
            SetMember(parameters, "pDstBuf", destination);
            SetMember(parameters, "nDstBufSize", destinationSize);

            object?[] args = { parameters };
            int result = Invoke(camera, "IMV_PixelConvert", args);
            if (result == CameraSdk.Ok)
            {
                convertedSize = Convert.ToUInt32(GetMember(args[0]!, "nDstDataLen") ?? 0u);
            }

            return result;
        }

        private int Invoke(object target, string methodName, params object?[] args)
        {
            try
            {
                return Convert.ToInt32(InvokeRaw(target, methodName, args));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HuaraySdkBridge] {methodName} failed: {Unwrap(ex).Message}");
                return SdkInvocationFailed;
            }
        }

        private int Invoke(string methodName, params object?[] args)
        {
            try
            {
                return Convert.ToInt32(InvokeRaw(null, methodName, args));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HuaraySdkBridge] {methodName} failed: {Unwrap(ex).Message}");
                return SdkInvocationFailed;
            }
        }

        private object? InvokeRaw(object? target, string methodName, params object?[] args)
        {
            Type type = target?.GetType() ?? _cameraType;
            MethodInfo method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Where(candidate => candidate.Name == methodName)
                .Where(candidate => candidate.GetParameters().Length == args.Length)
                .Single();
            return method.Invoke(target, args);
        }

        private static object? GetMember(object target, string name)
        {
            Type type = target.GetType();
            return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target) ??
                   type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }

        private static void SetMember(object target, string name, object value)
        {
            Type type = target.GetType();
            FieldInfo? field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(target, value);
                return;
            }

            PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            property?.SetValue(target, value);
        }

        private static string ReadString(object target, string name)
        {
            return GetMember(target, name)?.ToString()?.Trim() ?? string.Empty;
        }

        private static Type GetRequiredType(Assembly assembly, string name) =>
            assembly.GetType(name, throwOnError: true)!;

        private static Type GetNestedType(Type type, string name) =>
            type.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(type.FullName, name);

        private static Exception Unwrap(Exception exception)
        {
            return exception is TargetInvocationException invocation && invocation.InnerException is Exception inner
                ? Unwrap(inner)
                : exception;
        }

        private static string? ResolveSdkPath()
        {
            string? explicitPath = Environment.GetEnvironmentVariable("CLEARFROST_HUARAY_SDK_PATH");
            if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            {
                return Path.GetFullPath(explicitPath);
            }

            string[] applicationCandidates =
            {
                Path.Combine(AppContext.BaseDirectory, "MVSDK_Net.dll"),
                Path.Combine(AppContext.BaseDirectory, "DLL", "MVSDK_Net.dll")
            };

            return applicationCandidates.FirstOrDefault(File.Exists);
        }
    }
}
