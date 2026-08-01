namespace ClearFrost.Hardware
{
    /// <summary>
    /// Historical compatibility name used by the provider factory for Huaray cameras.
    /// </summary>
    public class MindVisionCamera : HuaraySdkCamera
    {
        public MindVisionCamera(string? targetSerialNumber = null)
            : base(targetSerialNumber)
        {
        }
    }
}
