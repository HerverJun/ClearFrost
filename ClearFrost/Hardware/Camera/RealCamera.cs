namespace ClearFrost.Hardware
{
    /// <summary>
    /// Compatibility name for the existing Huaray camera entry point.
    /// </summary>
    public class RealCamera : HuaraySdkCamera
    {
        public RealCamera(string? targetSerialNumber = null)
            : base(targetSerialNumber)
        {
        }

        public static int EnumDevicesStatic(ref CameraDeviceList deviceList, uint interfaceType)
        {
            return EnumerateDevicesStatic(ref deviceList, interfaceType);
        }
    }
}
