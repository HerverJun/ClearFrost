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

            if (!entry.IsPackage || entry.Manifest == null || string.IsNullOrWhiteSpace(entry.ManifestPath))
            {
                return Fail("只有带 manifest 的模型包可以绑定 Replay 批准证据。", 0);
            }

            if (!string.Equals(entry.ModelId, evidence.CandidateModel.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.Version, evidence.CandidateModel.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(entry.ModelHash, evidence.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail("Replay 批准证据与候选模型身份不匹配。", 0);
            }

            if (evidence.Metrics.CandidateNewMissedDetectionCount > 0 ||
                evidence.Metrics.CandidateNewFalseRejectCount > 0)
            {
                return Fail("Replay 结果包含新增漏检或新增误检，不能批准。", 0);
            }

            ModelPackageManifest manifest = entry.Manifest;
            manifest.AcceptanceDataset = evidence.DatasetPath;
            manifest.AcceptanceMetrics["totalSamples"] = evidence.Metrics.SampleCount;
            manifest.AcceptanceMetrics["candidateCorrectSamples"] = evidence.Metrics.CandidateCorrectCount;
            manifest.AcceptanceMetrics["candidateNewMissedDetectionCount"] = evidence.Metrics.CandidateNewMissedDetectionCount;
            manifest.AcceptanceMetrics["candidateNewFalseRejectCount"] = evidence.Metrics.CandidateNewFalseRejectCount;
            manifest.Approval = new ModelApprovalMetadata
            {
                Status = ModelApprovalStatuses.Approved,
                ApprovedAt = evidence.CreatedAt,
                ApprovedBy = evidence.ApprovedBy,
                Summary = $"Replay evidence {evidence.EvidenceId}",
                GoldenDatasetPath = evidence.DatasetPath,
                ActualPassRate = evidence.Metrics.SampleCount == 0
                    ? 0
                    : evidence.Metrics.CandidateCorrectCount / (double)evidence.Metrics.SampleCount,
                ReplayEvidenceId = evidence.EvidenceId,
                ReplayEvidenceHash = evidence.EvidenceHash,
                ReplayDatasetHash = evidence.DatasetHash,
                ReplayRunId = evidence.ReplayRunId
            };

            AtomicFileWriter.WriteAllText(entry.ManifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
            return new ModelAcceptanceResult
            {
                Succeeded = true,
                Message = "模型 Replay 批准证据已绑定。",
                PassRate = manifest.Approval.ActualPassRate
            };
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
            return new ModelAcceptanceResult
            {
                Succeeded = false,
                Message = message,
                PassRate = passRate
            };
        }
    }
}
