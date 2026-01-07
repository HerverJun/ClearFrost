// ============================================================================
// 文件名: FeatureMatchOp.cs
// 描述:   AKAZE 特征匹配算子 - 工业增强版，支持双向验证
// ============================================================================
using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace YOLO.Vision
{
    /// <summary>
    /// AKAZE 特征匹配算子 (工业增强版)
    /// </summary>
    public class FeatureMatchOp : FeatureMatchOpBase<AKAZE>
    {
        public override string Name => "Feature Match (AKAZE)";
        public override string TypeId => "feature_match";

        private float _akazeThreshold = 0.001f;
        private bool _useSymmetryTest = true;

        public FeatureMatchOp() { }

        public override Dictionary<string, object> Parameters
        {
            get
            {
                var dict = base.Parameters;
                dict["akazeThreshold"] = _akazeThreshold;
                dict["useSymmetryTest"] = _useSymmetryTest;
                return dict;
            }
        }

        protected override void EnsureDetector()
        {
            if (_detector == null)
            {
                _detector = AKAZE.Create(threshold: _akazeThreshold);
            }
        }

        protected override bool SetParameterInternal(string name, object value)
        {
            if (base.SetParameterInternal(name, value)) return true;

            switch (name)
            {
                case "akazethreshold":
                    float newThres = Convert.ToSingle(value);
                    if (Math.Abs(_akazeThreshold - newThres) > 0.00001f)
                    {
                        _akazeThreshold = Math.Clamp(newThres, 0.0001f, 0.1f);
                        _detector?.Dispose();
                        _detector = null;
                        InvalidateTemplateCache();
                    }
                    return true;
                case "usesymmetrytest": _useSymmetryTest = Convert.ToBoolean(value); return true;
            }
            return false;
        }

        // Implementation of Detection
        protected override (KeyPoint[], Mat) DetectAndCompute(AKAZE detector, Mat image, int maxFeatures)
        {
            KeyPoint[] allKeyPoints;
            using Mat allDescriptors = new Mat();

            detector.DetectAndCompute(image, null, out allKeyPoints, allDescriptors);

            // Filter to maxFeatures
            if (allKeyPoints.Length > maxFeatures)
            {
                FilterKeyPointsAndDescriptors(allKeyPoints, allDescriptors, maxFeatures, out var finalKpts, out var finalDescs);
                return (finalKpts, finalDescs);
            }
            else
            {
                return (allKeyPoints, allDescriptors.Clone());
            }
        }

        // Override Matching to support Symmetry Test
        protected override List<DMatch> MatchDescriptors(Mat templateDesc, Mat sceneDesc)
        {
            if (_useSymmetryTest)
            {
                using var matcher = new BFMatcher(NormTypes.Hamming, crossCheck: false);
                var forwardMatches = matcher.KnnMatch(templateDesc, sceneDesc, k: 2);
                var backwardMatches = matcher.KnnMatch(sceneDesc, templateDesc, k: 2);

                var backwardBest = new Dictionary<int, int>();
                foreach (var m in backwardMatches)
                {
                    if (m.Length >= 2)
                    {
                        if (m[0].Distance < 0.75 * m[1].Distance) backwardBest[m[0].QueryIdx] = m[0].TrainIdx;
                    }
                    else if (m.Length == 1)
                    {
                        backwardBest[m[0].QueryIdx] = m[0].TrainIdx;
                    }
                }

                var goodMatches = new List<DMatch>();
                foreach (var m in forwardMatches)
                {
                    if (m.Length < 2) continue;
                    if (m[0].Distance >= 0.75 * m[1].Distance) continue;

                    if (backwardBest.TryGetValue(m[0].TrainIdx, out int reverseTemplateIdx) && reverseTemplateIdx == m[0].QueryIdx)
                    {
                        goodMatches.Add(m[0]);
                    }
                }
                return goodMatches;
            }
            else
            {
                return base.MatchDescriptors(templateDesc, sceneDesc);
            }
        }

        private void FilterKeyPointsAndDescriptors(KeyPoint[] kpts, Mat descs, int count, out KeyPoint[] outKpts, out Mat outDescs)
        {
            int[] indices = new int[kpts.Length];
            for (int i = 0; i < kpts.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) => kpts[b].Response.CompareTo(kpts[a].Response));

            int safeCount = Math.Min(count, kpts.Length);
            outKpts = new KeyPoint[safeCount];
            outDescs = new Mat(safeCount, descs.Cols, descs.Type());

            for (int i = 0; i < safeCount; i++)
            {
                int originalIdx = indices[i];
                outKpts[i] = kpts[originalIdx];
                using var srcRow = descs.Row(originalIdx);
                using var dstRow = outDescs.Row(i);
                srcRow.CopyTo(dstRow);
            }
        }

        public override List<OperatorParameterInfo> GetParameterInfo() => new()
        {
            new OperatorParameterInfo { Name = "featureCount", DisplayName = "特征点上限", Type = "slider", Min = 100, Max = 2000, Step = 100, DefaultValue = 500, CurrentValue = _featureCount },
            new OperatorParameterInfo { Name = "scoreThreshold", DisplayName = "评分阈值", Type = "slider", Min = 4, Max = 20, Step = 1, DefaultValue = 10, CurrentValue = _scoreThreshold },
            new OperatorParameterInfo { Name = "akazeThreshold", DisplayName = "检测灵敏度", Type = "slider", Min = 0.0001, Max = 0.01, Step = 0.0001, DefaultValue = 0.001, CurrentValue = _akazeThreshold },
            new OperatorParameterInfo { Name = "useSymmetryTest", DisplayName = "启用双向验证", Type = "checkbox", DefaultValue = true, CurrentValue = _useSymmetryTest },
            new OperatorParameterInfo { Name = "useRansac", DisplayName = "启用RANSAC", Type = "checkbox", DefaultValue = true, CurrentValue = _useRansac }
        };
    }
}
