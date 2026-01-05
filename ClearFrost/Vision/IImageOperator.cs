using OpenCvSharp;

namespace YOLO.Vision
{
    /// <summary>
    /// 图像处理算子基类接口
    /// 所有图像处理步骤都实现此接口
    /// </summary>
    public interface IImageOperator
    {
        /// <summary>
        /// 算子名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 算子类型标识
        /// </summary>
        string TypeId { get; }

        /// <summary>
        /// 算子参数（用于序列化到前端）
        /// </summary>
        Dictionary<string, object> Parameters { get; }

        /// <summary>
        /// 执行图像处理
        /// </summary>
        /// <param name="input">输入图像</param>
        /// <returns>处理后的图像</returns>
        Mat Execute(Mat input);

        /// <summary>
        /// 更新参数
        /// </summary>
        /// <param name="paramName">参数名</param>
        /// <param name="value">参数值</param>
        void SetParameter(string paramName, object value);

        /// <summary>
        /// 获取参数配置（用于前端显示）
        /// </summary>
        /// <returns>参数配置列表</returns>
        List<OperatorParameterInfo> GetParameterInfo();
    }

    /// <summary>
    /// 算子参数信息（用于前端动态生成 UI）
    /// </summary>
    public class OperatorParameterInfo
    {
        /// <summary>参数名</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>显示名称</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>参数类型: slider, number, checkbox, file</summary>
        public string Type { get; set; } = "number";

        /// <summary>最小值（slider 类型）</summary>
        public double Min { get; set; }

        /// <summary>最大值（slider 类型）</summary>
        public double Max { get; set; }

        /// <summary>步进值</summary>
        public double Step { get; set; } = 1;

        /// <summary>默认值</summary>
        public object DefaultValue { get; set; } = 0;

        /// <summary>当前值</summary>
        public object CurrentValue { get; set; } = 0;
    }

    /// <summary>
    /// 算子配置（用于序列化）
    /// </summary>
    public class OperatorConfig
    {
        /// <summary>算子类型标识</summary>
        public string TypeId { get; set; } = string.Empty;

        /// <summary>算子实例ID（用于前端唯一标识）</summary>
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>算子参数</summary>
        public Dictionary<string, object> Parameters { get; set; } = new();
    }
}
