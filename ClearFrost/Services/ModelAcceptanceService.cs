using System;
using System.IO;
using System.Text.Json;
using ClearFrost.Core.Models;
using ClearFrost.Helpers;

namespace ClearFrost.Services
{
    public sealed class ModelAcceptanceRequest
    {
        public string OperatorId { get; init; } = string.Empty;
        public string GoldenDatasetPath { get; init; } = string.Empty;
        public int TotalSamples { get; init; }
        public int PassedSamples { get; init; }
        public double MinimumPassRate { get; init; } = 0.98;
        public string Summary { get; init; } = string.Empty;
    }

    public sealed class ModelAcceptanceResult
    {
        public bool Succeeded { get; init; }
        public string Message { get; init; } = string.Empty;
        public double PassRate { get; init; }
    }

    public sealed class ModelProductionState
    {
        public string CurrentModelId { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string CurrentModelPath { get; set; } = string.Empty;
        public string PreviousModelId { get; set; } = string.Empty;
        public string PreviousVersion { get; set; } = string.Empty;
        public string PreviousModelPath { get; set; } = string.Empty;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    }

    public sealed class ModelAcceptanceService
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly string _productionStatePath;

        public ModelAcceptanceService(string? productionStatePath = null)
        {
            _productionStatePath = string.IsNullOrWhiteSpace(productionStatePath)
                ? Path.Combine(RuntimePaths.DataDirectory, "Models", "model-production-state.json")
                : productionStatePath;
        }

        public ModelAcceptanceResult ApprovePackage(ModelRegistryEntry entry, ModelAcceptanceRequest request)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (request == null) throw new ArgumentNullException(nameof(request));

            if (!entry.IsPackage || entry.Manifest == null || string.IsNullOrWhiteSpace(entry.ManifestPath))
            {
                return Fail("只有带 manifest 的模型包可以执行上线验收。", 0);
            }

            if (entry.Labels.Count == 0)
            {
                return Fail("类别列表为空，模型不能上线。", 0);
            }

            if (entry.InputWidth <= 0 || entry.InputHeight <= 0)
            {
                return Fail("输入尺寸元数据缺失，模型不能上线。", 0);
            }

            if (string.IsNullOrWhiteSpace(entry.TaskType))
            {
                return Fail("任务类型元数据缺失，模型不能上线。", 0);
            }

            if (request.TotalSamples <= 0 || request.PassedSamples < 0 || request.PassedSamples > request.TotalSamples)
            {
                return Fail("验收样本统计无效。", 0);
            }

            if (string.IsNullOrWhiteSpace(request.GoldenDatasetPath))
            {
                return Fail("Golden dataset 路径不能为空。", 0);
            }

            double passRate = request.PassedSamples / (double)request.TotalSamples;
            double minimumPassRate = Math.Clamp(request.MinimumPassRate, 0, 1);
            if (passRate < minimumPassRate)
            {
                return Fail($"验收未通过: {passRate:P2} < {minimumPassRate:P2}", passRate);
            }

            ModelPackageManifest manifest = entry.Manifest;
            manifest.AcceptanceDataset = request.GoldenDatasetPath.Trim();
            manifest.AcceptanceMetrics["totalSamples"] = request.TotalSamples;
            manifest.AcceptanceMetrics["passedSamples"] = request.PassedSamples;
            manifest.AcceptanceMetrics["passRate"] = passRate;
            manifest.Approval = new ModelApprovalMetadata
            {
                Status = ModelApprovalStatuses.Approved,
                ApprovedAt = DateTimeOffset.Now,
                ApprovedBy = request.OperatorId?.Trim() ?? string.Empty,
                Summary = request.Summary ?? string.Empty,
                GoldenDatasetPath = request.GoldenDatasetPath.Trim(),
                MinimumPassRate = minimumPassRate,
                ActualPassRate = passRate
            };

            AtomicFileWriter.WriteAllText(entry.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            return new ModelAcceptanceResult
            {
                Succeeded = true,
                Message = "模型上线验收通过。",
                PassRate = passRate
            };
        }

        public ModelAcceptanceResult EnableApprovedModel(ModelRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!entry.ApprovedForProduction)
            {
                return Fail("模型未批准，不能启用到生产链路。", 0);
            }

            ModelProductionState state = LoadState();
            if (!string.Equals(state.CurrentModelPath, entry.ModelPath, StringComparison.OrdinalIgnoreCase))
            {
                state.PreviousModelId = state.CurrentModelId;
                state.PreviousVersion = state.CurrentVersion;
                state.PreviousModelPath = state.CurrentModelPath;
            }

            state.CurrentModelId = entry.ModelId;
            state.CurrentVersion = entry.Version;
            state.CurrentModelPath = entry.ModelPath;
            state.UpdatedAt = DateTimeOffset.Now;
            SaveState(state);

            return new ModelAcceptanceResult
            {
                Succeeded = true,
                Message = "模型已启用到生产链路。"
            };
        }

        public ModelProductionState RollbackToPrevious()
        {
            ModelProductionState state = LoadState();
            if (string.IsNullOrWhiteSpace(state.PreviousModelPath))
            {
                throw new InvalidOperationException("没有可回滚的上一批准模型。");
            }

            string currentId = state.CurrentModelId;
            string currentVersion = state.CurrentVersion;
            string currentPath = state.CurrentModelPath;

            state.CurrentModelId = state.PreviousModelId;
            state.CurrentVersion = state.PreviousVersion;
            state.CurrentModelPath = state.PreviousModelPath;
            state.PreviousModelId = currentId;
            state.PreviousVersion = currentVersion;
            state.PreviousModelPath = currentPath;
            state.UpdatedAt = DateTimeOffset.Now;
            SaveState(state);
            return state;
        }

        public ModelProductionState LoadState()
        {
            try
            {
                if (!File.Exists(_productionStatePath))
                {
                    return new ModelProductionState();
                }

                string json = File.ReadAllText(_productionStatePath);
                return JsonSerializer.Deserialize<ModelProductionState>(json, JsonOptions) ?? new ModelProductionState();
            }
            catch
            {
                return new ModelProductionState();
            }
        }

        private void SaveState(ModelProductionState state)
        {
            AtomicFileWriter.WriteAllText(_productionStatePath, JsonSerializer.Serialize(state, JsonOptions));
        }

        private static ModelAcceptanceResult Fail(string message, double passRate)
        {
            return new ModelAcceptanceResult
            {
                Succeeded = false,
                Message = message,
                PassRate = passRate
            };
        }
    }
}
