using System;
using ClearFrost.Config;
using System.Text.Json.Serialization;

namespace ClearFrost.Config
{
    /// <summary>
    /// 
    /// </summary>
    public class CameraConfig
    {
        /// <summary>
        /// 
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// 
        /// </summary>
        public string SerialNumber { get; set; } = "";

        /// <summary>
        /// 
        /// </summary>
        public string DisplayName { get; set; } = "Camera";

        /// <summary>
        /// 
        /// </summary>
        public double ExposureTime { get; set; } = 50000.0;

        /// <summary>
        /// 
        /// </summary>
        public double Gain { get; set; } = 1.1;

        /// <summary>
        /// 
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 
        /// 
        /// </summary>
        public string Manufacturer { get; set; } = "Huaray";

        /// <summary>
        /// 
        /// </summary>
        [JsonIgnore]
        public string ModelName { get; set; } = "";

        /// <summary>
        /// 
        /// </summary>
        public CameraConfig Clone()
        {
            return new CameraConfig
            {
                Id = this.Id,
                SerialNumber = this.SerialNumber,
                DisplayName = this.DisplayName,
                ExposureTime = this.ExposureTime,
                Gain = this.Gain,
                IsEnabled = this.IsEnabled,
                Manufacturer = this.Manufacturer,
                ModelName = this.ModelName
            };
        }
    }
}


