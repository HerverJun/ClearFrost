using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClearFrost.Core.Models;
using ClearFrost.Helpers;
using ClearFrost.Services.Replay;

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
        public string ErrorCode { get; init; } = string.Empty;
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
            return Fail(
                "ReplayEvidenceRequired",
                "模型批准必须通过 ReplayApprovalApplicationService.ApproveCandidateAsync 绑定 Replay Evidence。",
                0);
        }

        public ModelAcceptanceResult EnableApprovedModel(ModelRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            ModelAcceptanceResult validation = ValidateProductionStateEntry(entry);
            if (!validation.Succeeded)
            {
                return validation;
            }

            return new ModelAcceptanceResult
            {
                Succeeded = true,
                Message = "模型已通过生产启用校验；生产选择以 AppConfig 模型引用为唯一权威。"
            };
        }

        public ModelAcceptanceResult ApprovePackageWithReplayEvidence(
            ModelRegistryEntry entry,
            ModelApprovalEvidence evidence)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));
            return Fail(
                "ReplayEvidenceRequired",
                "模型批准必须通过 ReplayApprovalApplicationService.ApproveCandidateAsync 串行写入批准清单。",
                0);
        }

        public ModelProductionState RollbackToPrevious()
        {
            throw new InvalidOperationException("模型生产选择以 AppConfig 为唯一权威，ModelAcceptanceService 不再执行生产回滚。");
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

        private static ModelAcceptanceResult ValidateProductionStateEntry(ModelRegistryEntry entry)
        {
            if (!entry.IsPackage || entry.Manifest == null || string.IsNullOrWhiteSpace(entry.ManifestPath))
            {
                return Fail("模型缺少有效 manifest，不能记录为生产状态。", 0);
            }

            if (!entry.ApprovedForProduction)
            {
                return Fail("模型未批准，不能记录为生产状态。", 0);
            }

            if (entry.Labels.Count == 0 || entry.Labels.All(string.IsNullOrWhiteSpace))
            {
                return Fail("类别列表为空，模型不能记录为生产状态。", 0);
            }

            if (entry.InputWidth <= 0 || entry.InputHeight <= 0)
            {
                return Fail("输入尺寸元数据缺失，模型不能记录为生产状态。", 0);
            }

            if (string.IsNullOrWhiteSpace(entry.TaskType))
            {
                return Fail("任务类型元数据缺失，模型不能记录为生产状态。", 0);
            }

            if (!File.Exists(entry.ModelPath))
            {
                return Fail("模型文件不存在，不能记录为生产状态。", 0);
            }

            return new ModelAcceptanceResult
            {
                Succeeded = true,
                Message = "模型生产状态校验通过。"
            };
        }

        private static ModelAcceptanceResult Fail(string message, double passRate)
        {
            return Fail(string.Empty, message, passRate);
        }

        private static ModelAcceptanceResult Fail(string errorCode, string message, double passRate)
        {
            return new ModelAcceptanceResult
            {
                Succeeded = false,
                ErrorCode = errorCode ?? string.Empty,
                Message = message,
                PassRate = passRate
            };
        }
    }
}
