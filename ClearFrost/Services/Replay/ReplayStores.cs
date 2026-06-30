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

                EnsureSingleRecipe(records, request.Recipe);

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
                    if (review == null)
                    {
                        throw new InvalidOperationException($"Manual review is required before freezing replay dataset: {record.InspectionId}.");
                    }

                    string systemDecision = record.IsQualified ? ReplayDecisions.OK : ReplayDecisions.NG;
                    if (!TryResolveGroundTruth(record, review, systemDecision, out string groundTruth, out string reviewError))
                    {
                        throw new InvalidOperationException(reviewError);
                    }

                    string sampleId = !string.IsNullOrWhiteSpace(review.SampleId)
                        ? review.SampleId.Trim()
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
                        GroundTruth = groundTruth,
                        SystemDecision = systemDecision,
                        RecipeId = request.Recipe.RecipeId,
                        RecipeVersion = request.Recipe.RecipeVersion,
                        ReviewRevision = review.Revision,
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
                    snapshot.Recipe.RuleSetJson,
                    RecipeHash = ComputeRecipeHash(snapshot.Recipe),
                    RuleSetHash = ComputeRuleSetHash(snapshot.Recipe.RuleSetJson)
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
                        Disposition = sample.ManualReview?.Disposition ?? string.Empty,
                        ReviewerId = sample.ManualReview?.ReviewerId ?? string.Empty,
                        ReviewerRole = sample.ManualReview?.ReviewerRole ?? string.Empty,
                        SourceModel = new
                        {
                            sample.Record.ModelId,
                            sample.Record.ModelVersion,
                            sample.Record.ModelHash,
                            sample.Record.ModelName
                        },
                        ImageHash = useStoredImageHash ? sample.ImageHash : ComputeSha256(sample.ImagePath)
                    })
                    .ToList()
            };

            return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }

        internal static string ComputeRecipeHash(ReplayRecipeSnapshot recipe)
        {
            recipe ??= new ReplayRecipeSnapshot();
            var canonical = new
            {
                recipe.RecipeId,
                recipe.RecipeVersion,
                recipe.Confidence,
                recipe.IouThreshold,
                Roi = recipe.Roi ?? Array.Empty<float>(),
                recipe.RuleSetJson
            };
            return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }

        internal static string ComputeRuleSetHash(string ruleSetJson)
        {
            return ComputeSha256(Encoding.UTF8.GetBytes(ruleSetJson ?? string.Empty));
        }

        private static void EnsureSingleRecipe(IReadOnlyList<DetectionRecord> records, ReplayRecipeSnapshot requestRecipe)
        {
            string recipeId = requestRecipe.RecipeId?.Trim() ?? string.Empty;
            string recipeVersion = requestRecipe.RecipeVersion?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(recipeId) || string.IsNullOrWhiteSpace(recipeVersion))
            {
                throw new InvalidOperationException("Replay dataset recipe id/version are required.");
            }

            var distinct = records
                .Select(record => new
                {
                    RecipeId = record.RecipeId?.Trim() ?? string.Empty,
                    RecipeVersion = record.RecipeVersion?.Trim() ?? string.Empty
                })
                .Distinct()
                .ToList();
            if (distinct.Count != 1 ||
                !string.Equals(distinct[0].RecipeId, recipeId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(distinct[0].RecipeVersion, recipeVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Replay dataset records must belong to the same requested recipe id/version.");
            }
        }

        private static bool TryResolveGroundTruth(
            DetectionRecord record,
            ReplayManualReviewRecord review,
            string systemDecision,
            out string groundTruth,
            out string error)
        {
            groundTruth = string.Empty;
            error = string.Empty;
            if (!ReplayReviewDispositions.IsDatasetEligible(review.Disposition))
            {
                error = $"Manual review disposition is not eligible for replay dataset: {record.InspectionId}/{review.Disposition}.";
                return false;
            }

            if (!ReplayMetrics.TryNormalizeDecision(review.GroundTruth, out string normalizedTruth))
            {
                error = $"Manual review ground truth is invalid: {record.InspectionId}/{review.GroundTruth}.";
                return false;
            }

            string expected = NormalizeDispositionTruth(review.Disposition, systemDecision);
            if (!string.Equals(expected, normalizedTruth, StringComparison.Ordinal))
            {
                error = $"Manual review disposition does not match ground truth for {record.InspectionId}. Disposition={review.Disposition}; System={systemDecision}; Truth={normalizedTruth}.";
                return false;
            }

            groundTruth = normalizedTruth;
            return true;
        }

        private static string NormalizeDispositionTruth(string disposition, string systemDecision)
        {
            if (string.Equals(disposition, ReplayReviewDispositions.Confirmed, StringComparison.OrdinalIgnoreCase))
            {
                return systemDecision;
            }

            if (string.Equals(disposition, ReplayReviewDispositions.FalseReject, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayDecisions.OK;
            }

            if (string.Equals(disposition, ReplayReviewDispositions.MissedDetection, StringComparison.OrdinalIgnoreCase))
            {
                return ReplayDecisions.NG;
            }

            throw new InvalidOperationException($"Unknown review disposition: {disposition}");
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
            report.ReportHash = string.Empty;

            AtomicFileWriter.WriteAllText(report.ReportJsonPath, JsonSerializer.Serialize(report, ReplayJson.Options));
            report.ReportHash = ComputeReportHash(report);
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

        public async Task<ReplayRunReport> LoadReportAsync(
            string runId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(runId))
            {
                throw new ArgumentException("Replay run id is required.", nameof(runId));
            }

            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ReportJsonPath
                FROM ReplayRuns
                WHERE RunId = $runId
                LIMIT 1;
            ";
            command.Parameters.AddWithValue("$runId", runId.Trim());

            object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            string reportPath = value as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath))
            {
                throw new FileNotFoundException($"Replay report not found for run: {runId}", reportPath);
            }

            ReplayRunReport report = JsonSerializer.Deserialize<ReplayRunReport>(
                await File.ReadAllTextAsync(reportPath, cancellationToken).ConfigureAwait(false),
                ReplayJson.Options) ?? new ReplayRunReport();
            if (!string.Equals(report.RunId, runId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Replay report run id mismatch: {report.RunId}");
            }

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

        public async Task MarkNonTerminalRunsInterruptedAsync(
            string stationId,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ReplayRuns
                SET Status = $status,
                    CompletedAt = $completedAt,
                    Message = $message
                WHERE Status NOT IN ($completed, $failed, $canceled, $interrupted);
            ";
            command.Parameters.AddWithValue("$status", ReplayRunStatuses.Interrupted);
            command.Parameters.AddWithValue("$completedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$message", "Replay run was interrupted by process startup.");
            command.Parameters.AddWithValue("$completed", ReplayRunStatuses.Completed);
            command.Parameters.AddWithValue("$failed", ReplayRunStatuses.Failed);
            command.Parameters.AddWithValue("$canceled", ReplayRunStatuses.Canceled);
            command.Parameters.AddWithValue("$interrupted", ReplayRunStatuses.Interrupted);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            builder.AppendLine("Metric,Value");
            builder.AppendLine($"SampleCount,{report.Metrics.SampleCount}");
            builder.AppendLine($"BaselineAccuracy,{report.Metrics.BaselineAccuracy.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CandidateAccuracy,{report.Metrics.CandidateAccuracy.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CandidateNewMissedDetectionCount,{report.Metrics.CandidateNewMissedDetectionCount}");
            builder.AppendLine($"CandidateNewFalseRejectCount,{report.Metrics.CandidateNewFalseRejectCount}");
            builder.AppendLine($"BaselineP95ElapsedMs,{report.Metrics.BaselineP95ElapsedMs}");
            builder.AppendLine($"CandidateP95ElapsedMs,{report.Metrics.CandidateP95ElapsedMs}");
            builder.AppendLine();
            builder.AppendLine("SampleId,InspectionId,GroundTruth,BaselineDecision,CandidateDecision,Classification,DecisionChanged,BaselineElapsedMs,CandidateElapsedMs");
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
                    sample.DecisionChanged ? "true" : "false",
                    sample.BaselineElapsedMs.ToString(CultureInfo.InvariantCulture),
                    sample.CandidateElapsedMs.ToString(CultureInfo.InvariantCulture)));
            }

            return builder.ToString();
        }

        internal static string ComputeReportHash(ReplayRunReport report)
        {
            var canonical = new ReplayRunReport
            {
                RunId = report.RunId,
                Status = report.Status,
                DatasetId = report.DatasetId,
                DatasetHash = report.DatasetHash,
                BaselineModel = report.BaselineModel,
                CandidateModel = report.CandidateModel,
                StartedAt = report.StartedAt,
                CompletedAt = report.CompletedAt,
                Metrics = report.Metrics,
                Samples = report.Samples,
                Errors = report.Errors,
                ReportJsonPath = string.Empty,
                ReportCsvPath = string.Empty,
                ReportHash = string.Empty,
                RecipeHash = report.RecipeHash,
                RuleSetHash = report.RuleSetHash,
                PolicyHash = report.PolicyHash,
                BaselineModelHash = report.BaselineModelHash,
                CandidateModelHash = report.CandidateModelHash
            };
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
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
            string datasetPath,
            string policyHash)
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
                ReplayReportHash = string.IsNullOrWhiteSpace(report.ReportHash)
                    ? SqliteReplayRunStore.ComputeReportHash(report)
                    : report.ReportHash,
                BaselineModel = report.BaselineModel,
                CandidateModel = report.CandidateModel,
                Metrics = report.Metrics,
                PolicyReasons = decision.Reasons,
                ReplayReportPath = report.ReportJsonPath,
                PolicyHash = string.IsNullOrWhiteSpace(policyHash) ? _policy.PolicyHash : policyHash,
                RecipeHash = report.RecipeHash,
                RuleSetHash = report.RuleSetHash,
                BaselineModelHash = report.BaselineModelHash,
                CandidateModelHash = report.CandidateModelHash
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

            if (!string.Equals(evidence.PolicyHash, _policy.PolicyHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidencePolicyHashMismatch", "Replay approval evidence policy hash does not match current policy.");
            }

            if (!string.IsNullOrWhiteSpace(candidate.ModelPath))
            {
                try
                {
                    string actualModelHash = FileReplayDatasetStore.ComputeSha256(candidate.ModelPath);
                    if (!string.Equals(actualModelHash, evidence.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceCurrentModelHashMismatch", "Current candidate model file no longer matches approval evidence.");
                    }
                }
                catch (Exception ex)
                {
                    return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceCurrentModelUnavailable", ex.Message);
                }
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

            if (string.IsNullOrWhiteSpace(evidence.ReplayReportPath) || !File.Exists(evidence.ReplayReportPath))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportMissing", "Replay report referenced by evidence is missing.");
            }

            ReplayRunReport report;
            try
            {
                report = JsonSerializer.Deserialize<ReplayRunReport>(
                    File.ReadAllText(evidence.ReplayReportPath),
                    ReplayJson.Options) ?? new ReplayRunReport();
            }
            catch (Exception ex)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportParseFailed", ex.Message);
            }

            string reportHash = SqliteReplayRunStore.ComputeReportHash(report);
            if (!string.Equals(reportHash, report.ReportHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(reportHash, evidence.ReplayReportHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportHashMismatch", "Replay report hash no longer matches approval evidence.");
            }

            if (!string.Equals(report.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal) ||
                !string.Equals(report.DatasetHash, evidence.DatasetHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.CandidateModel.Sha256, evidence.CandidateModel.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.BaselineModel.Sha256, evidence.BaselineModel.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.RecipeHash, evidence.RecipeHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.RuleSetHash, evidence.RuleSetHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportBindingMismatch", "Replay report bindings do not match approval evidence.");
            }

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

        internal bool TryDeleteUnpublishedEvidence(string evidenceId, out string error)
        {
            error = string.Empty;
            try
            {
                string path = ResolvePath(evidenceId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
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
                PolicyReasons = evidence.PolicyReasons,
                ReplayReportPath = evidence.ReplayReportPath,
                PolicyHash = evidence.PolicyHash,
                RecipeHash = evidence.RecipeHash,
                RuleSetHash = evidence.RuleSetHash,
                BaselineModelHash = evidence.BaselineModelHash,
                CandidateModelHash = evidence.CandidateModelHash
            };
            return FileReplayDatasetStore.ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }
    }
}
