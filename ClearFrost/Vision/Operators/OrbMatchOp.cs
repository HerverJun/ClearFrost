// ============================================================================
// 
// 
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;
using ClearFrost.Vision;

namespace ClearFrost.Vision
{
    /// <summary>
    /// 
    /// </summary>
    public class OrbMatchOp : FeatureMatchOpBase<RobustOrbExtractor>
    {
        public override string Name => "Feature Match (ORB)";
        public override string TypeId => "orb_match";

        // Robust ORB parameters
        private int _nLevels = 8;
        private float _scaleFactor = 1.2f;
        private int _iniThFast = 20;
        private int _minThFast = 7;

        public OrbMatchOp() { }

        protected override void EnsureDetector()
        {
            if (_detector == null)
            {
                _detector = new RobustOrbExtractor(_nLevels, _scaleFactor, _iniThFast, _minThFast);
            }
        }

        public override Dictionary<string, object> Parameters
        {
            get
            {
                var dict = base.Parameters;
                dict["nLevels"] = _nLevels;
                dict["scaleFactor"] = _scaleFactor;
                dict["iniThFast"] = _iniThFast;
                dict["minThFast"] = _minThFast;
                return dict;
            }
        }

        protected override bool SetParameterInternal(string name, object value)
        {
            if (base.SetParameterInternal(name, value)) return true;

            bool needReset = false;
            switch (name)
            {
                case "nlevels": _nLevels = Convert.ToInt32(value); needReset = true; break;
                case "scalefactor": _scaleFactor = (float)Convert.ToDouble(value); needReset = true; break;
                case "inithfast": _iniThFast = Convert.ToInt32(value); needReset = true; break;
                case "minthfast": _minThFast = Convert.ToInt32(value); needReset = true; break;
            }

            if (needReset)
            {
                _detector = null;
                InvalidateTemplateCache();
                return true;
            }
            return false;
        }

        protected override (KeyPoint[], Mat) DetectAndCompute(RobustOrbExtractor detector, Mat image, int maxFeatures)
        {
            return detector.DetectAndCompute(image, maxFeatures);
        }

        public override List<OperatorParameterInfo> GetParameterInfo() => new()
        {
            new OperatorParameterInfo { Name = "featureCount", DisplayName = "特征点上限", Type = "slider", Min = 100, Max = 2000, Step = 100, DefaultValue = 500, CurrentValue = _featureCount },
            new OperatorParameterInfo { Name = "scoreThreshold", DisplayName = "最小匹配对数", Type = "slider", Min = 4, Max = 100, Step = 1, DefaultValue = 10, CurrentValue = _scoreThreshold },
            new OperatorParameterInfo { Name = "nLevels", DisplayName = "金字塔层数", Type = "slider", Min = 1, Max = 12, Step = 1, DefaultValue = 8, CurrentValue = _nLevels },
            new OperatorParameterInfo { Name = "scaleFactor", DisplayName = "尺度因子", Type = "slider", Min = 1.0, Max = 2.0, Step = 0.1, DefaultValue = 1.2, CurrentValue = _scaleFactor },
            new OperatorParameterInfo { Name = "iniThFast", DisplayName = "FAST初始阈值", Type = "slider", Min = 5, Max = 50, Step = 1, DefaultValue = 20, CurrentValue = _iniThFast },
            new OperatorParameterInfo { Name = "minThFast", DisplayName = "FAST最小阈值", Type = "slider", Min = 2, Max = 20, Step = 1, DefaultValue = 7, CurrentValue = _minThFast }
        };
    }
}
