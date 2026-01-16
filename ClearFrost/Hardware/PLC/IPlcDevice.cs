using System.Threading.Tasks;

namespace ClearFrost.Hardware
{
    /// <summary>
    /// 
    /// </summary>
    public interface IPlcDevice
    {
        /// <summary>
        /// 
        /// </summary>
        /// 
        Task<bool> ConnectAsync();

        /// <summary>
        /// 
        /// </summary>
        void Disconnect();

        /// <summary>
        /// 
        /// </summary>
        /// 
        /// 
        Task<(bool Success, short Value)> ReadInt16Async(string address);

        /// <summary>
        /// 
        /// </summary>
        /// 
        /// 
        /// 
        Task<bool> WriteInt16Async(string address, short value);

        /// <summary>
        /// 
        /// </summary>
        string LastError { get; }

        /// <summary>
        /// 
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 
        /// </summary>
        string ProtocolName { get; }
    }
}


