using System.Text.Json.Serialization;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 视觉检测配置
    /// 用于 C# <-> JS 通信，序列化为 JSON 发送给前端
    /// </summary>
    public class VisionConfig
    {
        /// <summary>检测模式</summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public VisionMode Mode { get; set; } = VisionMode.YOLO;

        /// <summary>算子管线配置</summary>
        public List<OperatorConfig> Operators { get; set; } = new();

        /// <summary>模板匹配阈值</summary>
        public double TemplateThreshold { get; set; } = 0.8;

        /// <summary>模板图像路径</summary>
        public string TemplatePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// 视觉配置响应（发送给前端）
    /// </summary>
    public class VisionConfigResponse
    {
        /// <summary>当前配置</summary>
        public VisionConfig Config { get; set; } = new();

        /// <summary>可用算子列表</summary>
        public List<OperatorInfo> AvailableOperators { get; set; } = new();

        /// <summary>各算子的参数信息</summary>
        public Dictionary<string, List<OperatorParameterInfo>> OperatorParameters { get; set; } = new();
    }

    /// <summary>
    /// 前端发送的管线更新请求
    /// </summary>
    public class PipelineUpdateRequest
    {
        /// <summary>操作类型: add, remove, update, move, clear</summary>
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        /// <summary>算子类型ID（add 时使用）</summary>
        [JsonPropertyName("typeId")]
        public string? TypeId { get; set; }

        /// <summary>算子实例ID（remove/update/move 时使用）</summary>
        [JsonPropertyName("instanceId")]
        public string? InstanceId { get; set; }

        /// <summary>参数名（update 时使用）</summary>
        [JsonPropertyName("paramName")]
        public string? ParamName { get; set; }

        /// <summary>参数值（update 时使用）</summary>
        [JsonPropertyName("paramValue")]
        public object? ParamValue { get; set; }

        /// <summary>新位置（move 时使用）</summary>
        [JsonPropertyName("newIndex")]
        public int? NewIndex { get; set; }
    }

    /// <summary>
    /// 预览请求
    /// </summary>
    public class PreviewRequest
    {
        /// <summary>是否只预览到指定算子</summary>
        public bool StepPreview { get; set; } = false;

        /// <summary>预览到的步骤索引</summary>
        public int StepIndex { get; set; } = -1;
    }

    /// <summary>
    /// 预览响应
    /// </summary>
    public class PreviewResponse
    {
        /// <summary>Base64 编码的图像</summary>
        public string ImageBase64 { get; set; } = string.Empty;

        /// <summary>图像宽</summary>
        public int Width { get; set; }

        /// <summary>图像高度</summary>
        public int Height { get; set; }

        /// <summary>处理时间（毫秒）</summary>
        public double ProcessingTimeMs { get; set; }
    }

    /// <summary>
    /// 检测响应
    /// </summary>
    public class DetectionResponse
    {
        /// <summary>是否通过</summary>
        public bool IsPass { get; set; }

        /// <summary>得分</summary>
        public double Score { get; set; }

        /// <summary>处理时间（毫秒）</summary>
        public double ProcessingTimeMs { get; set; }

        /// <summary>结果描述</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>检测到的对象</summary>
        public List<DetectedObjectDto> Objects { get; set; } = new();

        /// <summary>结果图像 Base64</summary>
        public string? ResultImageBase64 { get; set; }
    }

    /// <summary>
    /// 检测对象 DTO
    /// </summary>
    public class DetectedObjectDto
    {
        public string Label { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// 算子训练请求
    /// </summary>
    public class TrainOperatorRequest
    {
        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = string.Empty;

        [JsonPropertyName("imageBase64")]
        public string ImageBase64 { get; set; } = string.Empty;
    }
}
