using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClearFrost.Helpers;
using ClearFrost.Interfaces;
using Microsoft.Data.Sqlite;

namespace ClearFrost.Services.Replay
{
    public sealed class FileReplayDatasetStore : IReplayDatasetStore
    {
        private readonly IDatabaseService _databaseService;
        private readonly string _rootDirectory;

        public FileReplayDatasetStore(IDatabaseService databaseService, string rootDirectory)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? throw new ArgumentException("Dataset root is required.", nameof(rootDirectory))
                : rootDirectory;
        }

        public async Task<ReplayDatasetSnapshot> CreateSnapshotAsync(
            ReplayDatasetCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string datasetId = string.IsNullOrWhiteSpace(request.DatasetId)
                ? $"dataset-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"
                : SanitizeName(request.DatasetId);
            string finalDirectory = Path.Combine(_rootDirectory, datasetId);
            if (Directory.Exists(finalDirectory))
            {
                throw new IOException($"Replay dataset already exists: {finalDirectory}");
            }

            string stagingDirectory = Path.Combine(_rootDirectory, $".{datasetId}.staging-{Guid.NewGuid():N}");
            string imageDirectory = Path.Combine(stagingDirectory, "images");
            Directory.CreateDirectory(imageDirectory);

            try
            {
                List<DetectionRecord> records = await _databaseService.GetReplayRecordsAsync(request.Query)
                    .ConfigureAwait(false);
                if (records.Count == 0)
                {
                    throw new InvalidOperationException("Replay dataset query returned no detection records.");
                }

                var samples = new List<ReplayDatasetSample>(records.Count);
                int ordinal = 1;
                foreach (DetectionRecord record in records.OrderBy(record => record.Timestamp).ThenBy(record => record.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string sourcePath = ResolveSourceImage(record);
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException($"Replay source image missing for {record.InspectionId}.", sourcePath);
                    }

                    request.ManualReviewsByInspectionId.TryGetValue(
                        record.InspectionId ?? string.Empty,
                        out ReplayManualReviewRecord? review);

                    string sampleId = !string.IsNullOrWhiteSpace(review?.SampleId)
                        ? review!.SampleId.Trim()
                        : $"S{ordinal}";
                    string extension = Path.GetExtension(sourcePath);
                    if (string.IsNullOrWhiteSpace(extension))
                    {
                        extension = ".png";
                    }

                    string frozenFileName = $"{sampleId}{extension}";
                    string frozenPath = Path.Combine(imageDirectory, frozenFileName);
                    File.Copy(sourcePath, frozenPath, overwrite: false);

                    samples.Add(new ReplayDatasetSample
                    {
                        SampleId = sampleId,
                        DetectionRecordId = record.Id,
                        InspectionId = record.InspectionId ?? string.Empty,
                        SourceImagePath = Path.GetFullPath(sourcePath),
                        ImagePath = Path.GetFullPath(frozenPath),
                        ImageHash = ComputeSha256(frozenPath),
                        GroundTruth = ReplayMetrics.Normalize(review?.GroundTruth ?? (record.IsQualified ? ReplayDecisions.OK : ReplayDecisions.NG)),
                        SystemDecision = record.IsQualified ? ReplayDecisions.OK : ReplayDecisions.NG,
                        RecipeId = request.Recipe.RecipeId,
                        RecipeVersion = request.Recipe.RecipeVersion,
                        ReviewRevision = review?.Revision ?? 0,
                        ManualReview = review,
                        Record = record
                    });
                    ordinal++;
                }

                var snapshot = new ReplayDatasetSnapshot
                {
                    DatasetId = datasetId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    RootDirectory = Path.GetFullPath(stagingDirectory),
                    Recipe = request.Recipe,
                    BaselineModel = request.BaselineModel,
                    CandidateModel = request.CandidateModel,
                    Samples = samples.OrderBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase).ToList()
                };
                snapshot.DatasetHash = ComputeDatasetHash(snapshot, useStoredImageHash: false);

                string manifestPath = Path.Combine(stagingDirectory, "manifest.json");
                AtomicFileWriter.WriteAllText(manifestPath, JsonSerializer.Serialize(snapshot, ReplayJson.Options));

                Directory.CreateDirectory(_rootDirectory);
                Directory.Move(stagingDirectory, finalDirectory);

                snapshot.RootDirectory = Path.GetFullPath(finalDirectory);
                foreach (ReplayDatasetSample sample in snapshot.Samples)
                {
                    sample.ImagePath = Path.GetFullPath(Path.Combine(finalDirectory, "images", Path.GetFileName(sample.ImagePath)));
                }

                AtomicFileWriter.WriteAllText(
                    Path.Combine(finalDirectory, "manifest.json"),
                    JsonSerializer.Serialize(snapshot, ReplayJson.Options));
                return snapshot;
            }
            catch
            {
                if (Directory.Exists(stagingDirectory))
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }

                throw;
            }
        }

        public Task<ReplayDatasetSnapshot> LoadSnapshotAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            string manifestPath = ResolveManifestPath(datasetId);
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"Replay dataset manifest not found: {manifestPath}", manifestPath);
            }

            ReplayDatasetSnapshot snapshot = JsonSerializer.Deserialize<ReplayDatasetSnapshot>(
                File.ReadAllText(manifestPath),
                ReplayJson.Options) ?? throw new InvalidOperationException("Replay dataset manifest is invalid.");

            snapshot.RootDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath) ?? _rootDirectory);
            return Task.FromResult(snapshot);
        }

        public async Task<string> ComputeSnapshotHashAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            ReplayDatasetSnapshot snapshot = await LoadSnapshotAsync(datasetId, cancellationToken).ConfigureAwait(false);
            return ComputeDatasetHash(snapshot, useStoredImageHash: false);
        }

        internal static string ComputeDatasetHash(ReplayDatasetSnapshot snapshot, bool useStoredImageHash)
        {
            var canonical = new
            {
                snapshot.DatasetId,
                Recipe = new
                {
                    snapshot.Recipe.RecipeId,
                    snapshot.Recipe.RecipeVersion,
                    snapshot.Recipe.Confidence,
                    snapshot.Recipe.IouThreshold,
                    Roi = snapshot.Recipe.Roi ?? Array.Empty<float>(),
                    snapshot.Recipe.RuleSetJson
                },
                Samples = snapshot.Samples
                    .OrderBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
                    .Select(sample => new
                    {
                        sample.SampleId,
                        sample.InspectionId,
                        sample.GroundTruth,
                        sample.SystemDecision,
                        sample.RecipeId,
                        sample.RecipeVersion,
                        sample.ReviewRevision,
                        ImageHash = useStoredImageHash ? sample.ImageHash : ComputeSha256(sample.ImagePath)
                    })
                    .ToList()
            };

            return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }

        private string ResolveManifestPath(string datasetId)
        {
            string trimmed = datasetId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new ArgumentException("Dataset id is required.", nameof(datasetId));
            }

            if (File.Exists(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            string directory = Path.IsPathRooted(trimmed)
                ? trimmed
                : Path.Combine(_rootDirectory, SanitizeName(trimmed));
            return Path.Combine(directory, "manifest.json");
        }

        private static string ResolveSourceImage(DetectionRecord record)
        {
            if (!string.IsNullOrWhiteSpace(record.ImagePath))
            {
                return record.ImagePath;
            }

            if (!string.IsNullOrWhiteSpace(record.TraceImagePath))
            {
                return record.TraceImagePath;
            }

            return record.RenderedImagePath ?? string.Empty;
        }

        private static string SanitizeName(string value)
        {
            string sanitized = string.Join("_", value.Trim().Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
        }

        internal static string ComputeSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        internal static string ComputeSha256(byte[] bytes)
        {
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
        }
    }

    public sealed class SqliteReplayRunStore : IReplayRunStore
    {
        private const int BusyTimeoutMs = 5000;
        private readonly string _dbPath;
        private readonly string _reportRoot;

        public SqliteReplayRunStore(string dbPath, string reportRoot)
        {
            _dbPath = string.IsNullOrWhiteSpace(dbPath)
                ? throw new ArgumentException("Replay run database path is required.", nameof(dbPath))
                : dbPath;
            _reportRoot = string.IsNullOrWhiteSpace(reportRoot)
                ? throw new ArgumentException("Replay report root is required.", nameof(reportRoot))
                : reportRoot;
        }

        public async Task RecordRunStartedAsync(
            ReplayRunReport report,
            CancellationToken cancellationToken = default)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO ReplayRuns
                    (RunId, DatasetId, DatasetHash, BaselineModelId, CandidateModelId, Status, StartedAt, CompletedAt, Message, ReportJsonPath, ReportCsvPath)
                VALUES
                    ($runId, $datasetId, $datasetHash, $baselineModelId, $candidateModelId, $status, $startedAt, NULL, '', '', '');
            ";
            command.Parameters.AddWithValue("$runId", report.RunId);
            command.Parameters.AddWithValue("$datasetId", report.DatasetId);
            command.Parameters.AddWithValue("$datasetHash", report.DatasetHash);
            command.Parameters.AddWithValue("$baselineModelId", report.BaselineModel.ModelId);
            command.Parameters.AddWithValue("$candidateModelId", report.CandidateModel.ModelId);
            command.Parameters.AddWithValue("$status", report.Status);
            command.Parameters.AddWithValue("$startedAt", report.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task RecordRunProgressAsync(
            ReplayRunProgress progress,
            CancellationToken cancellationToken = default)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ReplayRunProgress
                    (RunId, Timestamp, Status, Phase, CompletedSamples, TotalSamples, Message)
                VALUES
                    ($runId, $timestamp, $status, $phase, $completedSamples, $totalSamples, $message);
            ";
            command.Parameters.AddWithValue("$runId", progress.RunId);
            command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$status", progress.Status);
            command.Parameters.AddWithValue("$phase", progress.Phase);
            command.Parameters.AddWithValue("$completedSamples", progress.CompletedSamples);
            command.Parameters.AddWithValue("$totalSamples", progress.TotalSamples);
            command.Parameters.AddWithValue("$message", progress.Message);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<ReplayRunReport> SaveReportAsync(
            ReplayRunReport report,
            CancellationToken cancellationToken = default)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

            string runDirectory = Path.Combine(_reportRoot, report.RunId);
            Directory.CreateDirectory(runDirectory);
            report.ReportJsonPath = Path.GetFullPath(Path.Combine(runDirectory, "report.json"));
            report.ReportCsvPath = Path.GetFullPath(Path.Combine(runDirectory, "report.csv"));

            AtomicFileWriter.WriteAllText(report.ReportJsonPath, JsonSerializer.Serialize(report, ReplayJson.Options));
            AtomicFileWriter.WriteAllText(report.ReportCsvPath, BuildCsv(report));

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ReplayRuns
                SET Status = $status,
                    CompletedAt = $completedAt,
                    Message = $message,
                    ReportJsonPath = $reportJsonPath,
                    ReportCsvPath = $reportCsvPath
                WHERE RunId = $runId;
            ";
            command.Parameters.AddWithValue("$runId", report.RunId);
            command.Parameters.AddWithValue("$status", report.Status);
            command.Parameters.AddWithValue("$completedAt", report.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$message", string.Join("; ", report.Errors));
            command.Parameters.AddWithValue("$reportJsonPath", report.ReportJsonPath);
            command.Parameters.AddWithValue("$reportCsvPath", report.ReportCsvPath);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return report;
        }

        public Task RecordRunFailedAsync(
            string runId,
            string message,
            CancellationToken cancellationToken = default)
        {
            return UpdateTerminalStateAsync(runId, ReplayRunStatuses.Failed, message, cancellationToken);
        }

        public Task RecordRunCanceledAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            return UpdateTerminalStateAsync(runId, ReplayRunStatuses.Canceled, "Replay canceled.", cancellationToken);
        }

        private async Task UpdateTerminalStateAsync(
            string runId,
            string status,
            string message,
            CancellationToken cancellationToken)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ReplayRuns
                SET Status = $status,
                    CompletedAt = $completedAt,
                    Message = $message
                WHERE RunId = $runId;
            ";
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$message", message ?? string.Empty);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            string directory = Path.GetDirectoryName(_dbPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Directory.CreateDirectory(_reportRoot);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ReplayRuns (
                    RunId TEXT PRIMARY KEY,
                    DatasetId TEXT NOT NULL,
                    DatasetHash TEXT NOT NULL,
                    BaselineModelId TEXT,
                    CandidateModelId TEXT,
                    Status TEXT NOT NULL,
                    StartedAt TEXT NOT NULL,
                    CompletedAt TEXT,
                    Message TEXT,
                    ReportJsonPath TEXT,
                    ReportCsvPath TEXT
                );
                CREATE TABLE IF NOT EXISTS ReplayRunProgress (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RunId TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    Phase TEXT,
                    CompletedSamples INTEGER,
                    TotalSamples INTEGER,
                    Message TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_replay_progress_run ON ReplayRunProgress(RunId, Id);
            ";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
        {
            var connection = new SqliteConnection($"Data Source={_dbPath};Cache=Shared;Pooling=True;Default Timeout={BusyTimeoutMs / 1000}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $@"
                PRAGMA busy_timeout = {BusyTimeoutMs};
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        private static string BuildCsv(ReplayRunReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("SampleId,InspectionId,GroundTruth,BaselineDecision,CandidateDecision,Classification,DecisionChanged");
            foreach (ReplaySampleComparison sample in report.Samples)
            {
                builder.AppendLine(string.Join(
                    ",",
                    Csv(sample.SampleId),
                    Csv(sample.InspectionId),
                    Csv(sample.GroundTruth),
                    Csv(sample.BaselineDecision),
                    Csv(sample.CandidateDecision),
                    Csv(sample.Classification),
                    sample.DecisionChanged ? "true" : "false"));
            }

            return builder.ToString();
        }

        private static string Csv(string value)
        {
            string safe = value ?? string.Empty;
            return safe.Contains(',') || safe.Contains('"') || safe.Contains('\r') || safe.Contains('\n')
                ? "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
                : safe;
        }
    }

    public sealed class FileModelApprovalEvidenceStore : IModelApprovalEvidenceStore
    {
        private readonly string _rootDirectory;
        private readonly ReplayAcceptancePolicy _policy;

        public FileModelApprovalEvidenceStore(string rootDirectory, ReplayAcceptancePolicy? policy = null)
        {
            _rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
                ? throw new ArgumentException("Evidence root is required.", nameof(rootDirectory))
                : rootDirectory;
            _policy = policy ?? new ReplayAcceptancePolicy();
        }

        public ModelApprovalEvidence SaveEvidence(
            ReplayRunReport report,
            string approvedBy,
            string datasetPath)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            ReplayApprovalDecision decision = _policy.Evaluate(report);
            if (!decision.Approved)
            {
                throw new InvalidOperationException(string.Join("; ", decision.Reasons));
            }

            Directory.CreateDirectory(_rootDirectory);
            string evidenceId = $"evidence-{report.CandidateModel.ModelId}-{report.CandidateModel.Version}-{report.RunId}";
            evidenceId = string.Join("_", evidenceId.Split(Path.GetInvalidFileNameChars()));
            var evidence = new ModelApprovalEvidence
            {
                EvidenceId = evidenceId,
                CreatedAt = DateTimeOffset.UtcNow,
                ApprovedBy = approvedBy ?? string.Empty,
                DatasetId = report.DatasetId,
                DatasetHash = report.DatasetHash,
                DatasetPath = datasetPath ?? string.Empty,
                ReplayRunId = report.RunId,
                ReplayReportHash = File.Exists(report.ReportJsonPath)
                    ? FileReplayDatasetStore.ComputeSha256(report.ReportJsonPath)
                    : FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(report, ReplayJson.Options)),
                BaselineModel = report.BaselineModel,
                CandidateModel = report.CandidateModel,
                Metrics = report.Metrics,
                PolicyReasons = decision.Reasons
            };
            evidence.EvidenceHash = ComputeEvidenceHash(evidence);

            AtomicFileWriter.WriteAllText(
                ResolvePath(evidenceId),
                JsonSerializer.Serialize(evidence, ReplayJson.Options));
            return evidence;
        }

        public ModelApprovalEvidenceValidationResult ValidateEvidence(
            ReplayModelIdentity candidate,
            string evidenceId,
            string expectedEvidenceHash,
            IReplayDatasetStore datasetStore)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (datasetStore == null) throw new ArgumentNullException(nameof(datasetStore));

            if (string.IsNullOrWhiteSpace(evidenceId))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceMissing", "Replay approval evidence id is missing.");
            }

            string path = ResolvePath(evidenceId);
            if (!File.Exists(path))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceFileMissing", $"Replay approval evidence file is missing: {path}");
            }

            ModelApprovalEvidence evidence;
            try
            {
                evidence = JsonSerializer.Deserialize<ModelApprovalEvidence>(
                    File.ReadAllText(path),
                    ReplayJson.Options) ?? new ModelApprovalEvidence();
            }
            catch (Exception ex)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceParseFailed", ex.Message);
            }

            string actualEvidenceHash = ComputeEvidenceHash(evidence);
            if (!string.Equals(actualEvidenceHash, evidence.EvidenceHash, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(expectedEvidenceHash) &&
                 !string.Equals(actualEvidenceHash, expectedEvidenceHash, StringComparison.OrdinalIgnoreCase)))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceHashMismatch", "Replay approval evidence hash does not match.");
            }

            if (!string.Equals(candidate.ModelId, evidence.CandidateModel.ModelId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Version, evidence.CandidateModel.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(candidate.Sha256, evidence.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceModelMismatch", "Replay approval evidence is bound to a different model identity.");
            }

            string actualDatasetHash;
            try
            {
                actualDatasetHash = datasetStore.ComputeSnapshotHashAsync(evidence.DatasetId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceDatasetUnavailable", ex.Message);
            }

            if (!string.Equals(actualDatasetHash, evidence.DatasetHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceDatasetHashMismatch", "Frozen dataset hash no longer matches approval evidence.");
            }

            var report = new ReplayRunReport
            {
                Status = ReplayRunStatuses.Completed,
                Metrics = evidence.Metrics
            };
            ReplayApprovalDecision policy = _policy.Evaluate(report);
            if (!policy.Approved)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidencePolicyRejected", string.Join("; ", policy.Reasons));
            }

            return ModelApprovalEvidenceValidationResult.Ok();
        }

        internal string ResolvePath(string evidenceId)
        {
            string fileName = evidenceId.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? evidenceId
                : $"{evidenceId}.json";
            return Path.Combine(_rootDirectory, fileName);
        }

        internal static string ComputeEvidenceHash(ModelApprovalEvidence evidence)
        {
            var canonical = new ModelApprovalEvidence
            {
                EvidenceId = evidence.EvidenceId,
                EvidenceHash = string.Empty,
                CreatedAt = evidence.CreatedAt,
                ApprovedBy = evidence.ApprovedBy,
                DatasetId = evidence.DatasetId,
                DatasetHash = evidence.DatasetHash,
                DatasetPath = evidence.DatasetPath,
                ReplayRunId = evidence.ReplayRunId,
                ReplayReportHash = evidence.ReplayReportHash,
                BaselineModel = evidence.BaselineModel,
                CandidateModel = evidence.CandidateModel,
                Metrics = evidence.Metrics,
                PolicyReasons = evidence.PolicyReasons
            };
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }
    }
}
