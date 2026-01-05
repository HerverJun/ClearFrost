using System;
using System.Text.Json.Serialization;

namespace YOLO
{
    /// <summary>
    /// 单个相机的配置信息
    /// </summary>
    public class CameraConfig
    {
        /// <summary>
        /// 相机唯一标识符
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

        /// <summary>
        /// 相机序列号 (用于SDK连接)
        /// </summary>
        public string SerialNumber { get; set; } = "";

        /// <summary>
        /// 相机显示名称
        /// </summary>
        public string DisplayName { get; set; } = "Camera";

        /// <summary>
        /// 曝光时间 (微秒)
        /// </summary>
        public double ExposureTime { get; set; } = 50000.0;

        /// <summary>
        /// 增益
        /// </summary>
        public double Gain { get; set; } = 1.1;

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// 相机型号 (可选，枚举时填充)
        /// </summary>
        [JsonIgnore]
        public string ModelName { get; set; } = "";

        /// <summary>
        /// 创建配置副本
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
                ModelName = this.ModelName
            };
        }
    }
}
