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

        internal string RootDirectory => Path.GetFullPath(_rootDirectory);

        internal bool HasStagingDirectory(string datasetId)
        {
            if (string.IsNullOrWhiteSpace(datasetId) || !Directory.Exists(_rootDirectory))
            {
                return false;
            }

            string rootDirectory = RootDirectory;
            if (HasReparsePoint(new DirectoryInfo(rootDirectory)))
            {
                throw new InvalidOperationException($"Replay dataset store root traverses a reparse point: {rootDirectory}");
            }

            string prefix = $".{SanitizeName(datasetId)}.staging-";
            return Directory.EnumerateDirectories(rootDirectory)
                .Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<ReplayDatasetSnapshot> CreateSnapshotAsync(
            ReplayDatasetCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            string datasetId = string.IsNullOrWhiteSpace(request.DatasetId)
                ? $"dataset-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}"
                : SanitizeName(request.DatasetId);
            string rootDirectory = EnsureDatasetRootSafeForWrite();
            string finalDirectory = Path.Combine(rootDirectory, datasetId);
            if (Directory.Exists(finalDirectory))
            {
                throw new IOException($"Replay dataset already exists: {finalDirectory}");
            }

            string stagingDirectory = Path.Combine(rootDirectory, $".{datasetId}.staging-{Guid.NewGuid():N}");
            string imageDirectory = Path.Combine(stagingDirectory, "images");
            Directory.CreateDirectory(imageDirectory);
            EnsureDatasetDirectorySafeForWrite(stagingDirectory, rootDirectory);

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

                    request.ManualReviewsByDetectionRecordId.TryGetValue(record.Id, out ReplayManualReviewRecord? review);
                    if (review == null)
                    {
                        throw new InvalidOperationException($"Manual review is required before freezing replay dataset: {record.InspectionId}.");
                    }

                    if (!string.Equals(review.InspectionId, record.InspectionId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"Manual review binding mismatch for DetectionRecordId={record.Id}. Record InspectionId={record.InspectionId}; Review InspectionId={review.InspectionId}.");
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

                    string imageHash = ComputeSha256(frozenPath);
                    string relativeImagePath = NormalizeRelativePath(Path.Combine("images", frozenFileName));
                    samples.Add(new ReplayDatasetSample
                    {
                        SampleId = sampleId,
                        DetectionRecordId = record.Id,
                        InspectionId = record.InspectionId ?? string.Empty,
                        SourceImagePath = $"record:{record.Id.ToString(CultureInfo.InvariantCulture)}",
                        SourceRecordHash = ComputeSourceRecordHash(record, imageHash),
                        ImagePath = relativeImagePath,
                        ImageHash = imageHash,
                        GroundTruth = groundTruth,
                        SystemDecision = systemDecision,
                        RecipeId = request.Recipe.RecipeId,
                        RecipeVersion = request.Recipe.RecipeVersion,
                        ReviewRevision = review.Revision,
                        ManualReview = review,
                        Record = SanitizeRecordForManifest(record)
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
                AtomicFileWriter.WriteAllText(manifestPath, JsonSerializer.Serialize(CreateManifestSnapshot(snapshot), ReplayJson.Options));
                ReplayDatasetSnapshot stagedReload = LoadSnapshotFromManifest(manifestPath, rootDirectory);
                ValidateSnapshotIntegrity(stagedReload);
                string stagedHash = ComputeDatasetHash(stagedReload, useStoredImageHash: false);
                if (!string.Equals(stagedHash, snapshot.DatasetHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Replay dataset staging integrity verification failed.");
                }

                Directory.CreateDirectory(_rootDirectory);
                EnsureDatasetDirectorySafeForWrite(stagingDirectory, rootDirectory);
                Directory.Move(stagingDirectory, finalDirectory);
                return ResolveSnapshotPaths(snapshot, finalDirectory);
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
            string manifestPath = ResolveManifestPath(datasetId, allowExternalDirectory: true);
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"Replay dataset manifest not found: {manifestPath}", manifestPath);
            }

            string manifestReadRoot = EnsureDatasetManifestSafeForRead(manifestPath);
            ReplayDatasetSnapshot snapshot = LoadSnapshotFromManifest(manifestPath, manifestReadRoot);
            ValidateSnapshotIntegrity(snapshot);
            return Task.FromResult(snapshot);
        }

        public async Task<string> ComputeSnapshotHashAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            ReplayDatasetSnapshot snapshot = await LoadSnapshotAsync(datasetId, cancellationToken).ConfigureAwait(false);
            return ComputeDatasetHash(snapshot, useStoredImageHash: false);
        }

        public Task<IReadOnlyList<ReplayDatasetSummary>> ListSnapshotsAsync(
            CancellationToken cancellationToken = default)
        {
            var summaries = new List<ReplayDatasetSummary>();
            string rootDirectory = RootDirectory;
            if (!Directory.Exists(rootDirectory))
            {
                return Task.FromResult<IReadOnlyList<ReplayDatasetSummary>>(summaries);
            }

            if (HasReparsePoint(new DirectoryInfo(rootDirectory)))
            {
                throw new InvalidOperationException($"Replay dataset store root traverses a reparse point: {rootDirectory}");
            }

            foreach (string directory in Directory.EnumerateDirectories(rootDirectory)
                         .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal) &&
                                        !string.Equals(Path.GetFileName(path), "_archive", StringComparison.OrdinalIgnoreCase)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    EnsureDatasetFileSafeForRead(manifestPath, rootDirectory);
                    ReplayDatasetSnapshot snapshot = LoadSnapshotFromManifest(manifestPath, rootDirectory);
                    summaries.Add(new ReplayDatasetSummary
                    {
                        DatasetId = snapshot.DatasetId,
                        DatasetHash = snapshot.DatasetHash,
                        CreatedAt = snapshot.CreatedAt,
                        SampleCount = snapshot.Samples.Count,
                        RootDirectory = snapshot.RootDirectory,
                        Status = "Frozen"
                    });
                }
                catch
                {
                    summaries.Add(new ReplayDatasetSummary
                    {
                        DatasetId = Path.GetFileName(directory),
                        RootDirectory = Path.GetFullPath(directory),
                        Status = "Invalid"
                    });
                }
            }

            return Task.FromResult<IReadOnlyList<ReplayDatasetSummary>>(
                summaries.OrderByDescending(item => item.CreatedAt).ToList());
        }

        public Task<ReplayDatasetArchiveResult> ArchiveSnapshotAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            string manifestPath;
            try
            {
                manifestPath = ResolveManifestPath(datasetId, allowExternalDirectory: false);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult(ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetPathOutsideRoot",
                    ex.Message));
            }

            string? datasetDirectory = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrWhiteSpace(datasetDirectory) || !Directory.Exists(datasetDirectory))
            {
                return Task.FromResult(ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetMissing",
                    $"Replay dataset not found: {datasetId}"));
            }

            try
            {
                string rootDirectory = EnsureDatasetRootSafeForWrite();
                EnsureDatasetDirectorySafeForWrite(datasetDirectory, rootDirectory);
                string archiveRoot = Path.Combine(rootDirectory, "_archive");
                Directory.CreateDirectory(archiveRoot);
                EnsureDatasetDirectorySafeForWrite(archiveRoot, rootDirectory);
                string archiveName = $"{Path.GetFileName(datasetDirectory)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                string archivePath = Path.Combine(archiveRoot, archiveName);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(datasetDirectory, archivePath);
                return Task.FromResult(ReplayDatasetArchiveResult.Ok(Path.GetFullPath(archivePath)));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult(ReplayDatasetArchiveResult.Fail(
                    "ReplayDatasetReparsePoint",
                    ex.Message));
            }
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
                        sample.DetectionRecordId,
                        sample.InspectionId,
                        sample.SourceRecordHash,
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
                        ImageHash = useStoredImageHash
                            ? sample.ImageHash
                            : ComputeSnapshotImageHash(snapshot, sample.ImagePath)
                    })
                    .ToList()
            };

            return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }

        private static ReplayDatasetSnapshot LoadSnapshotFromManifest(string manifestPath, string rootDirectory)
        {
            using FileStream stream = OpenDatasetFileForRead(manifestPath, rootDirectory);
            ReplayDatasetSnapshot manifest = JsonSerializer.Deserialize<ReplayDatasetSnapshot>(
                stream,
                ReplayJson.Options) ?? throw new InvalidOperationException("Replay dataset manifest is invalid.");
            EnsureDatasetFileSafeForRead(manifestPath, rootDirectory);

            string snapshotRootDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath) ?? string.Empty);
            return ResolveSnapshotPaths(manifest, snapshotRootDirectory);
        }

        private static ReplayDatasetSnapshot CreateManifestSnapshot(ReplayDatasetSnapshot snapshot)
        {
            return new ReplayDatasetSnapshot
            {
                DatasetId = snapshot.DatasetId,
                DatasetHash = snapshot.DatasetHash,
                CreatedAt = snapshot.CreatedAt,
                RootDirectory = string.Empty,
                Recipe = snapshot.Recipe,
                BaselineModel = SanitizeModelForManifest(snapshot.BaselineModel),
                CandidateModel = SanitizeModelForManifest(snapshot.CandidateModel),
                Samples = snapshot.Samples.Select(sample => new ReplayDatasetSample
                {
                    SampleId = sample.SampleId,
                    DetectionRecordId = sample.DetectionRecordId,
                    InspectionId = sample.InspectionId,
                    SourceImagePath = sample.SourceImagePath,
                    SourceRecordHash = sample.SourceRecordHash,
                    ImagePath = NormalizeRelativePath(sample.ImagePath),
                    ImageHash = sample.ImageHash,
                    GroundTruth = sample.GroundTruth,
                    SystemDecision = sample.SystemDecision,
                    RecipeId = sample.RecipeId,
                    RecipeVersion = sample.RecipeVersion,
                    ReviewRevision = sample.ReviewRevision,
                    ManualReview = sample.ManualReview,
                    Record = SanitizeRecordForManifest(sample.Record)
                }).ToList()
            };
        }

        private static ReplayModelIdentity SanitizeModelForManifest(ReplayModelIdentity model)
        {
            model ??= new ReplayModelIdentity();
            return new ReplayModelIdentity
            {
                ModelId = model.ModelId,
                Version = model.Version,
                Sha256 = model.Sha256,
                Labels = model.Labels,
                TaskType = model.TaskType,
                PostprocessorKey = model.PostprocessorKey,
                ScoreNormalization = model.ScoreNormalization,
                PostprocessOptions = ReplayModelIdentity.CopyPostprocessOptions(model.PostprocessOptions),
                InputWidth = model.InputWidth,
                InputHeight = model.InputHeight,
                ApprovalStatus = model.ApprovalStatus,
                IsPackage = model.IsPackage
            };
        }

        private static ReplayDatasetSnapshot ResolveSnapshotPaths(ReplayDatasetSnapshot snapshot, string rootDirectory)
        {
            string fullRoot = Path.GetFullPath(rootDirectory);
            foreach (ReplayDatasetSample sample in snapshot.Samples)
            {
                sample.ImagePath = ResolvePathForLoad(fullRoot, sample.ImagePath);
                if (!string.IsNullOrWhiteSpace(sample.SourceImagePath) &&
                    !sample.SourceImagePath.StartsWith("record:", StringComparison.OrdinalIgnoreCase))
                {
                    sample.SourceImagePath = ResolvePathForLoad(fullRoot, sample.SourceImagePath);
                }
            }

            snapshot.RootDirectory = fullRoot;
            return snapshot;
        }

        private static string ResolvePathForLoad(string rootDirectory, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Replace('/', Path.DirectorySeparatorChar);
            return Path.IsPathRooted(normalized)
                ? Path.GetFullPath(normalized)
                : Path.GetFullPath(Path.Combine(rootDirectory, normalized));
        }

        private static string ResolveSnapshotFilePath(ReplayDatasetSnapshot snapshot, string path)
        {
            if (Path.IsPathRooted(path))
            {
                return Path.GetFullPath(path);
            }

            return Path.GetFullPath(Path.Combine(snapshot.RootDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static void ValidateSnapshotIntegrity(ReplayDatasetSnapshot snapshot)
        {
            foreach (ReplayDatasetSample sample in snapshot.Samples)
            {
                string imagePath = ResolveSnapshotFilePath(snapshot, sample.ImagePath);
                EnsureDatasetFileSafeForRead(imagePath, snapshot.RootDirectory);
                if (!File.Exists(imagePath))
                {
                    throw new FileNotFoundException($"Replay dataset image missing: {imagePath}", imagePath);
                }

                string actualHash = ComputeSnapshotImageHash(snapshot, sample.ImagePath);
                if (!string.Equals(actualHash, sample.ImageHash, StringComparison.OrdinalIgnoreCase))
                {
                throw new InvalidOperationException($"Replay dataset image hash mismatch: {sample.SampleId}");
                }

                if (!string.IsNullOrWhiteSpace(sample.SourceImagePath) &&
                    !sample.SourceImagePath.StartsWith("record:", StringComparison.OrdinalIgnoreCase))
                {
                    string sourceImagePath = ResolveSnapshotFilePath(snapshot, sample.SourceImagePath);
                    EnsureDatasetFileSafeForRead(sourceImagePath, snapshot.RootDirectory);
                }
            }
        }

        private static string NormalizeRelativePath(string value)
        {
            string normalized = value.Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                return Path.GetFileName(normalized);
            }

            return normalized.TrimStart('/');
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
                if (!string.Equals(systemDecision, ReplayDecisions.NG, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("FalseReject requires system NG.");
                }

                return ReplayDecisions.OK;
            }

            if (string.Equals(disposition, ReplayReviewDispositions.MissedDetection, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(systemDecision, ReplayDecisions.OK, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("MissedDetection requires system OK.");
                }

                return ReplayDecisions.NG;
            }

            throw new InvalidOperationException($"Unknown review disposition: {disposition}");
        }

        private string ResolveManifestPath(string datasetId, bool allowExternalDirectory)
        {
            string trimmed = datasetId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new ArgumentException("Dataset id is required.", nameof(datasetId));
            }

            if (File.Exists(trimmed))
            {
                throw new ArgumentException("Replay dataset must be referenced by dataset id or dataset directory, not a manifest file path.", nameof(datasetId));
            }

            string directory = Path.IsPathRooted(trimmed)
                ? ResolveAbsoluteDatasetDirectory(trimmed, allowExternalDirectory)
                : Path.Combine(_rootDirectory, SanitizeName(trimmed));
            return Path.Combine(directory, "manifest.json");
        }

        private string ResolveAbsoluteDatasetDirectory(string directory, bool allowExternalDirectory)
        {
            string fullDirectory = Path.GetFullPath(directory);
            if (!allowExternalDirectory && !IsSameOrChildPath(fullDirectory, _rootDirectory))
            {
                throw new ArgumentException($"Replay dataset path is outside the dataset store root: {fullDirectory}");
            }

            return fullDirectory;
        }

        private string EnsureDatasetRootSafeForWrite()
        {
            string rootDirectory = RootDirectory;
            Directory.CreateDirectory(rootDirectory);
            if (HasReparsePoint(new DirectoryInfo(rootDirectory)))
            {
                throw new InvalidOperationException($"Replay dataset store root traverses a reparse point: {rootDirectory}");
            }

            return rootDirectory;
        }

        private string EnsureDatasetManifestSafeForRead(string manifestPath)
        {
            string rootDirectory = IsSameOrChildPath(manifestPath, _rootDirectory)
                ? RootDirectory
                : Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty;
            EnsureDatasetFileSafeForRead(manifestPath, rootDirectory);
            return rootDirectory;
        }

        private static bool IsSameOrChildPath(string candidatePath, string rootPath)
        {
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureDatasetDirectorySafeForWrite(string directory, string rootDirectory)
        {
            if (!IsSameOrChildPath(directory, rootDirectory) ||
                HasReparsePointInPath(directory, rootDirectory))
            {
                throw new InvalidOperationException($"Replay dataset directory traverses a reparse point: {directory}");
            }
        }

        private static void EnsureDatasetFileSafeForRead(string path, string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) ||
                !IsSameOrChildPath(path, rootDirectory) ||
                HasReparsePointInPath(path, rootDirectory))
            {
                throw new InvalidOperationException($"Replay dataset file traverses a reparse point or leaves the dataset root: {path}");
            }
        }

        private static string ComputeSnapshotImageHash(ReplayDatasetSnapshot snapshot, string imagePath)
        {
            string resolvedPath = ResolveSnapshotFilePath(snapshot, imagePath);
            using FileStream stream = OpenDatasetFileForRead(resolvedPath, snapshot.RootDirectory);
            return ComputeSha256(stream);
        }

        private static FileStream OpenDatasetFileForRead(string path, string rootDirectory)
        {
            EnsureDatasetFileSafeForRead(path, rootDirectory);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                EnsureDatasetFileSafeForRead(path, rootDirectory);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private static bool HasReparsePointInPath(string path, string rootPath)
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath)))
            {
                return true;
            }

            DirectoryInfo? directory = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath).Directory;
            while (directory != null)
            {
                string directoryPath = Path.GetFullPath(directory.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(directoryPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return HasReparsePoint(directory);
                }

                if (!IsSameOrChildPath(directoryPath, root) || HasReparsePoint(directory))
                {
                    return true;
                }

                directory = directory.Parent;
            }

            return true;
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
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

        private static string ComputeSourceRecordHash(DetectionRecord record, string imageHash)
        {
            var canonical = new
            {
                record.Id,
                record.InspectionId,
                record.Timestamp,
                record.IsQualified,
                record.RecipeId,
                record.RecipeVersion,
                record.ModelId,
                record.ModelVersion,
                record.ModelHash,
                record.ModelName,
                record.RuleSetJson,
                ImageHash = imageHash ?? string.Empty
            };
            return ComputeSha256(JsonSerializer.SerializeToUtf8Bytes(canonical, ReplayJson.Options));
        }

        private static DetectionRecord SanitizeRecordForManifest(DetectionRecord record)
        {
            return new DetectionRecord
            {
                Id = record.Id,
                Timestamp = record.Timestamp,
                IsQualified = record.IsQualified,
                InspectionId = record.InspectionId ?? string.Empty,
                TriggerSource = record.TriggerSource ?? string.Empty,
                TriggerSeq = record.TriggerSeq,
                PlcTriggerSeq = record.PlcTriggerSeq,
                ResultSeq = record.ResultSeq,
                TerminalHandshakeAttempted = record.TerminalHandshakeAttempted,
                TerminalHandshakeSucceeded = record.TerminalHandshakeSucceeded,
                TerminalHandshakeErrorCode = record.TerminalHandshakeErrorCode ?? string.Empty,
                TerminalHandshakeSignalName = record.TerminalHandshakeSignalName ?? string.Empty,
                TerminalHandshakeAddress = record.TerminalHandshakeAddress ?? string.Empty,
                TerminalHandshakeMessage = record.TerminalHandshakeMessage ?? string.Empty,
                CycleSucceeded = record.CycleSucceeded,
                ProductBarcode = record.ProductBarcode ?? string.Empty,
                Barcode = record.Barcode ?? string.Empty,
                BarcodeReadSucceeded = record.BarcodeReadSucceeded,
                BarcodeError = record.BarcodeError ?? string.Empty,
                TraceStatus = record.TraceStatus,
                QueueStatus = record.QueueStatus ?? string.Empty,
                ErrorStage = record.ErrorStage ?? string.Empty,
                ErrorCode = record.ErrorCode ?? string.Empty,
                ErrorMessage = record.ErrorMessage ?? string.Empty,
                TotalMs = record.TotalMs,
                CaptureMs = record.CaptureMs,
                RoiMs = record.RoiMs,
                PlcWriteMs = record.PlcWriteMs,
                SaveImageMs = record.SaveImageMs,
                SaveRecordMs = record.SaveRecordMs,
                RecipeId = record.RecipeId ?? string.Empty,
                RecipeVersion = record.RecipeVersion ?? string.Empty,
                ModelId = record.ModelId ?? string.Empty,
                ModelVersion = record.ModelVersion ?? string.Empty,
                ModelHash = record.ModelHash ?? string.Empty,
                WasFallback = record.WasFallback,
                UsedModelName = record.UsedModelName ?? string.Empty,
                TargetLabel = record.TargetLabel ?? string.Empty,
                ExpectedCount = record.ExpectedCount,
                ActualCount = record.ActualCount,
                InferenceMs = record.InferenceMs,
                ModelName = record.ModelName ?? string.Empty,
                CameraId = record.CameraId ?? string.Empty,
                RuleSummary = record.RuleSummary ?? string.Empty,
                RuleResultJson = record.RuleResultJson ?? string.Empty,
                RuleSetJson = record.RuleSetJson ?? string.Empty,
                ResultJson = record.ResultJson ?? string.Empty
            };
        }

        private static string SanitizeName(string value)
        {
            string sanitized = string.Join("_", value.Trim().Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
        }

        internal static string ComputeSha256(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ComputeSha256(stream);
        }

        private static string ComputeSha256(Stream stream)
        {
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

        internal string DbPath => Path.GetFullPath(_dbPath);

        internal string ReportRoot => Path.GetFullPath(_reportRoot);

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

            string runDirectory = ResolveRunReportDirectory(report.RunId);
            EnsureReportDirectorySafeForWrite(runDirectory);
            Directory.CreateDirectory(runDirectory);
            EnsureReportDirectorySafeForWrite(runDirectory);
            report.ReportJsonPath = Path.GetFullPath(Path.Combine(runDirectory, "report.json"));
            report.ReportCsvPath = Path.GetFullPath(Path.Combine(runDirectory, "report.csv"));
            EnsureReportFileSafeForWrite(report.ReportJsonPath);
            EnsureReportFileSafeForWrite(report.ReportCsvPath);
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

            await using FileStream stream = OpenReportFileForRead(reportPath);
            ReplayRunReport report = await JsonSerializer.DeserializeAsync<ReplayRunReport>(
                stream,
                ReplayJson.Options,
                cancellationToken).ConfigureAwait(false) ?? new ReplayRunReport();
            EnsureReportPathSafeForRead(reportPath);
            if (!string.Equals(report.RunId, runId.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Replay report run id mismatch: {report.RunId}");
            }

            return report;
        }

        public async Task<ReplayRunRecord?> LoadRunRecordAsync(
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
                SELECT RunId, DatasetId, DatasetHash, BaselineModelId, CandidateModelId, Status, StartedAt, CompletedAt, Message, ReportJsonPath, ReportCsvPath
                FROM ReplayRuns
                WHERE RunId = $runId
                LIMIT 1;
            ";
            command.Parameters.AddWithValue("$runId", runId.Trim());
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadRunRecord(reader)
                : null;
        }

        public async Task<IReadOnlyList<ReplayRunRecord>> ListRunRecordsAsync(
            int limit = 100,
            CancellationToken cancellationToken = default)
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            int safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
            var records = new List<ReplayRunRecord>(safeLimit);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                SELECT RunId, DatasetId, DatasetHash, BaselineModelId, CandidateModelId, Status, StartedAt, CompletedAt, Message, ReportJsonPath, ReportCsvPath
                FROM ReplayRuns
                ORDER BY StartedAt DESC
                LIMIT $limit;
            ";
            command.Parameters.AddWithValue("$limit", safeLimit);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                records.Add(ReadRunRecord(reader));
            }

            return records;
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

        public async Task RecordRunCancelRequestedAsync(
            ReplayRunProgress progress,
            CancellationToken cancellationToken = default)
        {
            if (progress == null) throw new ArgumentNullException(nameof(progress));
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await RecordRunProgressAsync(progress, cancellationToken).ConfigureAwait(false);
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE ReplayRuns
                SET Status = $status,
                    CompletedAt = NULL,
                    Message = $message
                WHERE RunId = $runId
                  AND Status NOT IN ($completed, $failed, $canceled, $interrupted);
            ";
            command.Parameters.AddWithValue("$runId", progress.RunId);
            command.Parameters.AddWithValue("$status", ReplayRunStatuses.CancelRequested);
            command.Parameters.AddWithValue("$message", progress.Message);
            command.Parameters.AddWithValue("$completed", ReplayRunStatuses.Completed);
            command.Parameters.AddWithValue("$failed", ReplayRunStatuses.Failed);
            command.Parameters.AddWithValue("$canceled", ReplayRunStatuses.Canceled);
            command.Parameters.AddWithValue("$interrupted", ReplayRunStatuses.Interrupted);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

        private string ResolveRunReportDirectory(string runId)
        {
            string trimmed = runId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new ArgumentException("Replay run id is required.", nameof(runId));
            }

            if (Path.IsPathRooted(trimmed) ||
                trimmed.Contains(Path.DirectorySeparatorChar) ||
                trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new ArgumentException("Replay run id must be a directory name, not a path.", nameof(runId));
            }

            string fullDirectory = Path.GetFullPath(Path.Combine(_reportRoot, trimmed));
            if (!IsSameOrChildPath(fullDirectory, _reportRoot))
            {
                throw new ArgumentException("Replay run report path is outside the report root.", nameof(runId));
            }

            return fullDirectory;
        }

        private void EnsureReportDirectorySafeForWrite(string directory)
        {
            EnsureReportPathUnderRoot(directory);
            if (HasReparsePointInPath(directory, _reportRoot))
            {
                throw new InvalidOperationException($"Replay run report directory traverses a reparse point: {directory}");
            }
        }

        private void EnsureReportFileSafeForWrite(string reportPath)
        {
            EnsureReportPathUnderRoot(reportPath);
            string directory = Path.GetDirectoryName(reportPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(directory) ||
                HasReparsePointInPath(directory, _reportRoot) ||
                (File.Exists(reportPath) && HasReparsePoint(new FileInfo(reportPath))))
            {
                throw new InvalidOperationException($"Replay report file traverses a reparse point: {reportPath}");
            }
        }

        private void EnsureReportPathSafeForRead(string reportPath)
        {
            EnsureReportPathUnderRoot(reportPath);
            if (HasReparsePointInPath(reportPath, _reportRoot))
            {
                throw new InvalidOperationException($"Replay report path traverses a reparse point: {reportPath}");
            }
        }

        private FileStream OpenReportFileForRead(string reportPath)
        {
            EnsureReportPathSafeForRead(reportPath);
            var stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                EnsureReportPathSafeForRead(reportPath);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private void EnsureReportPathUnderRoot(string reportPath)
        {
            if (!IsSameOrChildPath(reportPath, _reportRoot))
            {
                throw new InvalidOperationException($"Replay report path is outside the report root: {reportPath}");
            }
        }

        private static bool IsSameOrChildPath(string candidatePath, string rootPath)
        {
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasReparsePointInPath(string path, string rootPath)
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath)))
            {
                return true;
            }

            DirectoryInfo? directory = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath).Directory;
            while (directory != null)
            {
                string directoryPath = Path.GetFullPath(directory.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(directoryPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return HasReparsePoint(directory);
                }

                if (!IsSameOrChildPath(directoryPath, root) || HasReparsePoint(directory))
                {
                    return true;
                }

                directory = directory.Parent;
            }

            return true;
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static string BuildCsv(ReplayRunReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Metric,Value");
            builder.AppendLine($"SampleCount,{report.Metrics.SampleCount}");
            builder.AppendLine($"TotalSampleCount,{report.Metrics.TotalSampleCount}");
            builder.AppendLine($"ValidSampleCount,{report.Metrics.ValidSampleCount}");
            builder.AppendLine($"InvalidSampleCount,{report.Metrics.InvalidSampleCount}");
            builder.AppendLine($"BaselineAccuracy,{report.Metrics.BaselineAccuracy.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CandidateAccuracy,{report.Metrics.CandidateAccuracy.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CandidateNewMissedDetectionCount,{report.Metrics.CandidateNewMissedDetectionCount}");
            builder.AppendLine($"CandidateFixedMissedDetectionCount,{report.Metrics.CandidateFixedMissedDetectionCount}");
            builder.AppendLine($"CandidateNewFalseRejectCount,{report.Metrics.CandidateNewFalseRejectCount}");
            builder.AppendLine($"CandidateFixedFalseRejectCount,{report.Metrics.CandidateFixedFalseRejectCount}");
            builder.AppendLine($"BaselineMissedDetectionCount,{report.Metrics.BaselineMissedDetectionCount}");
            builder.AppendLine($"BaselineMissedDetectionRate,{report.Metrics.BaselineMissedDetectionRate.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CandidateMissedDetectionCount,{report.Metrics.CandidateMissedDetectionCount}");
            builder.AppendLine($"CandidateMissedDetectionRate,{report.Metrics.CandidateMissedDetectionRate.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"BaselineFalseRejectCount,{report.Metrics.BaselineFalseRejectCount}");
            builder.AppendLine($"BaselineFalseRejectRate,{report.Metrics.BaselineFalseRejectRate.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"CandidateFalseRejectCount,{report.Metrics.CandidateFalseRejectCount}");
            builder.AppendLine($"CandidateFalseRejectRate,{report.Metrics.CandidateFalseRejectRate.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"FalseRejectRateIncrease,{report.Metrics.FalseRejectRateIncrease.ToString(CultureInfo.InvariantCulture)}");
            builder.AppendLine($"BaselineP95ElapsedMs,{report.Metrics.BaselineP95ElapsedMs}");
            builder.AppendLine($"CandidateP95ElapsedMs,{report.Metrics.CandidateP95ElapsedMs}");
            builder.AppendLine();
            builder.AppendLine("SampleId,InspectionId,GroundTruth,BaselineDecision,CandidateDecision,Classification,DecisionChanged,BaselineElapsedMs,CandidateElapsedMs,IsValid,InvalidReason");
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
                    sample.CandidateElapsedMs.ToString(CultureInfo.InvariantCulture),
                    sample.IsValid ? "true" : "false",
                    Csv(sample.InvalidReason)));
            }

            return builder.ToString();
        }

        internal static string ComputeReportHash(ReplayRunReport report)
        {
            return ReplayArtifactHashing.ComputeReportHash(report);
        }

        private static ReplayRunRecord ReadRunRecord(SqliteDataReader reader)
        {
            static string ReadString(SqliteDataReader reader, int index)
            {
                return reader.IsDBNull(index) ? string.Empty : reader.GetString(index);
            }

            static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int index)
            {
                return DateTimeOffset.TryParse(
                    ReadString(reader, index),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset value)
                    ? value
                    : DateTimeOffset.MinValue;
            }

            DateTimeOffset? completedAt = null;
            string completedText = ReadString(reader, 7);
            if (DateTimeOffset.TryParse(
                    completedText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset parsedCompleted))
            {
                completedAt = parsedCompleted;
            }

            return new ReplayRunRecord
            {
                RunId = ReadString(reader, 0),
                DatasetId = ReadString(reader, 1),
                DatasetHash = ReadString(reader, 2),
                BaselineModelId = ReadString(reader, 3),
                CandidateModelId = ReadString(reader, 4),
                Status = ReadString(reader, 5),
                StartedAt = ReadDateTimeOffset(reader, 6),
                CompletedAt = completedAt,
                Message = ReadString(reader, 8),
                ReportJsonPath = ReadString(reader, 9),
                ReportCsvPath = ReadString(reader, 10)
            };
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

        internal string RootDirectory => Path.GetFullPath(_rootDirectory);

        public ModelApprovalEvidence SaveEvidence(
            ReplayRunReport report,
            string approvedBy,
            string datasetPath,
            string policyHash)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            if (string.IsNullOrWhiteSpace(report.ReportHash))
            {
                throw new InvalidOperationException("Replay report hash is required before saving approval evidence.");
            }

            if (report.PolicyVersion <= 0 ||
                report.PolicySnapshot == null ||
                report.PolicySnapshot.Version != report.PolicyVersion)
            {
                throw new InvalidOperationException("Replay report policy snapshot is required before saving approval evidence.");
            }

            if (!ReplayArtifactHashing.TryComputePolicyHash(report.PolicySnapshot, out string reportPolicyHash, out string policyHashError) ||
                string.IsNullOrWhiteSpace(report.PolicyHash) ||
                !string.Equals(report.PolicyHash, reportPolicyHash, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(policyHash) &&
                 !string.Equals(policyHash, reportPolicyHash, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(policyHashError)
                    ? "Replay report policy hash is invalid."
                    : policyHashError);
            }

            ReplayApprovalDecision decision = _policy.Evaluate(report, report.PolicySnapshot);
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
                ReplayReportHash = report.ReportHash,
                BaselineModel = report.BaselineModel,
                CandidateModel = report.CandidateModel,
                Metrics = report.Metrics,
                PolicyReasons = decision.Reasons,
                ReplayReportPath = report.ReportJsonPath,
                PolicyVersion = report.PolicyVersion,
                PolicyHash = reportPolicyHash,
                PolicySnapshot = report.PolicySnapshot.Clone(),
                RecipeHash = report.RecipeHash,
                RuleSetHash = report.RuleSetHash,
                BaselineModelHash = report.BaselineModelHash,
                CandidateModelHash = report.CandidateModelHash
            };
            evidence.EvidenceHash = ComputeEvidenceHash(evidence);

            string evidencePath = ResolvePath(evidenceId);
            EnsureEvidenceFileSafeForWrite(evidencePath);
            AtomicFileWriter.WriteAllText(
                evidencePath,
                JsonSerializer.Serialize(evidence, ReplayJson.Options));
            return evidence;
        }

        public ModelApprovalEvidenceValidationResult ValidateEvidence(
            ReplayModelIdentity candidate,
            string evidenceId,
            string expectedEvidenceHash,
            IReplayDatasetStore datasetStore,
            IReplayRunStore runStore)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            if (datasetStore == null) throw new ArgumentNullException(nameof(datasetStore));
            if (runStore == null) throw new ArgumentNullException(nameof(runStore));

            if (string.IsNullOrWhiteSpace(evidenceId))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceMissing", "Replay approval evidence id is missing.");
            }

            ModelApprovalEvidence? evidence;
            try
            {
                evidence = LoadEvidence(evidenceId);
            }
            catch (Exception ex)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceParseFailed", ex.Message);
            }

            if (evidence == null)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceFileMissing", $"Replay approval evidence file is missing: {ResolvePath(evidenceId)}");
            }

            if (!ReplayArtifactHashing.TryComputeEvidenceHash(evidence, out string actualEvidenceHash, out string evidenceHashError))
            {
                return ModelApprovalEvidenceValidationResult.Fail(
                    "ReplayEvidenceHashVersionInvalid",
                    evidenceHashError);
            }

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

            if (!ReplayAcceptancePolicyOptions.IsSupportedVersion(evidence.PolicyVersion))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidencePolicyVersionUnsupported", "Replay approval evidence policy version is not supported.");
            }

            if (evidence.PolicySnapshot == null ||
                evidence.PolicySnapshot.Version != evidence.PolicyVersion ||
                !ReplayAcceptancePolicyOptions.IsSupportedVersion(evidence.PolicySnapshot.Version))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidencePolicySnapshotInvalid", "Replay approval evidence policy snapshot is invalid or unsupported.");
            }

            if (!ReplayArtifactHashing.TryComputePolicyHash(evidence.PolicySnapshot, out string evidencePolicyHash, out string evidencePolicyHashError) ||
                !string.Equals(evidencePolicyHash, evidence.PolicyHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail(
                    "ReplayEvidencePolicyHashMismatch",
                    string.IsNullOrWhiteSpace(evidencePolicyHashError)
                        ? "Replay approval evidence policy hash does not match policy snapshot."
                        : evidencePolicyHashError);
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
                if (IsDatasetIntegrityFailure(ex))
                {
                    return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceDatasetHashMismatch", ex.Message);
                }

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

            ReplayRunRecord? runRecord;
            try
            {
                runRecord = runStore.LoadRunRecordAsync(evidence.ReplayRunId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayRunDbUnavailable", ex.Message);
            }

            if (runRecord == null)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayRunDbMissing", "Replay run referenced by evidence is missing from DB.");
            }

            if (!string.Equals(runRecord.Status, ReplayRunStatuses.Completed, StringComparison.Ordinal))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayRunDbNotCompleted", $"Replay run DB status is {runRecord.Status}.");
            }

            if (!SamePath(runRecord.ReportJsonPath, evidence.ReplayReportPath))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayRunDbReportPathMismatch", "Replay run DB report path does not match evidence.");
            }

            ReplayRunReport report;
            try
            {
                report = runStore.LoadReportAsync(evidence.ReplayRunId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportParseFailed", ex.Message);
            }

            if (!ReplayArtifactHashing.TryComputeReportHash(report, out string reportHash, out string reportHashError))
            {
                return ModelApprovalEvidenceValidationResult.Fail(
                    "ReplayEvidenceReportHashVersionInvalid",
                    reportHashError);
            }

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
                !string.Equals(report.RuleSetHash, evidence.RuleSetHash, StringComparison.OrdinalIgnoreCase) ||
                report.PolicyVersion != evidence.PolicyVersion ||
                report.PolicySnapshot == null ||
                report.PolicySnapshot.Version != evidence.PolicyVersion)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportBindingMismatch", "Replay report bindings do not match approval evidence.");
            }

            if (!SamePath(report.ReportJsonPath, runRecord.ReportJsonPath))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceReportPathMismatch", "Replay report JSON path does not match DB.");
            }

            if (!string.Equals(report.PolicyHash, evidence.PolicyHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.BaselineModelHash, evidence.BaselineModelHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(report.CandidateModelHash, evidence.CandidateModelHash, StringComparison.OrdinalIgnoreCase))
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidenceAuthorityHashMismatch", "Replay report authority hashes do not match approval evidence.");
            }

            ReplayApprovalDecision policy = _policy.Evaluate(report, evidence.PolicySnapshot);
            if (!policy.Approved)
            {
                return ModelApprovalEvidenceValidationResult.Fail("ReplayEvidencePolicyRejected", string.Join("; ", policy.Reasons));
            }

            return ModelApprovalEvidenceValidationResult.Ok();
        }

        public ModelApprovalEvidence? LoadEvidence(string evidenceId)
        {
            string path = ResolvePath(evidenceId);
            if (!File.Exists(path))
            {
                return null;
            }

            EnsureEvidenceFileSafeForRead(path);
            using FileStream stream = OpenEvidenceFileForRead(path);
            ModelApprovalEvidence? evidence = JsonSerializer.Deserialize<ModelApprovalEvidence>(
                stream,
                ReplayJson.Options);
            EnsureEvidenceFileSafeForRead(path);
            return evidence;
        }

        public IReadOnlyList<ModelApprovalEvidence> ListEvidence()
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return Array.Empty<ModelApprovalEvidence>();
            }

            if (HasReparsePoint(new DirectoryInfo(_rootDirectory)))
            {
                return Array.Empty<ModelApprovalEvidence>();
            }

            var evidence = new List<ModelApprovalEvidence>();
            foreach (string path in Directory.EnumerateFiles(_rootDirectory, "*.json"))
            {
                try
                {
                    if (!IsEvidenceFileSafeForRead(path))
                    {
                        continue;
                    }

                    using FileStream stream = OpenEvidenceFileForRead(path);
                    ModelApprovalEvidence? item = JsonSerializer.Deserialize<ModelApprovalEvidence>(
                        stream,
                        ReplayJson.Options);
                    EnsureEvidenceFileSafeForRead(path);
                    if (item != null)
                    {
                        evidence.Add(item);
                    }
                }
                catch
                {
                    // Integrity scanner reports malformed files through validation paths.
                }
            }

            return evidence;
        }

        internal string ResolvePath(string evidenceId)
        {
            string trimmed = evidenceId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                throw new ArgumentException("Replay approval evidence id is required.", nameof(evidenceId));
            }

            if (Path.IsPathRooted(trimmed) ||
                trimmed.Contains(Path.DirectorySeparatorChar) ||
                trimmed.Contains(Path.AltDirectorySeparatorChar))
            {
                throw new ArgumentException("Replay approval evidence id must be a file name, not a path.", nameof(evidenceId));
            }

            string fileName = trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : $"{trimmed}.json";
            string fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, fileName));
            if (!IsSameOrChildPath(fullPath, _rootDirectory))
            {
                throw new ArgumentException("Replay approval evidence path is outside the evidence store root.", nameof(evidenceId));
            }

            return fullPath;
        }

        private void EnsureEvidenceFileSafeForWrite(string path)
        {
            if (!IsSameOrChildPath(path, _rootDirectory))
            {
                throw new ArgumentException("Replay approval evidence path is outside the evidence store root.");
            }

            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(directory) ||
                HasReparsePointInPath(directory, _rootDirectory) ||
                (File.Exists(path) && HasReparsePoint(new FileInfo(path))))
            {
                throw new InvalidOperationException($"Replay approval evidence file traverses a reparse point: {path}");
            }
        }

        private void EnsureEvidenceFileSafeForRead(string path)
        {
            if (!IsEvidenceFileSafeForRead(path))
            {
                throw new InvalidOperationException($"Replay approval evidence file traverses a reparse point: {path}");
            }
        }

        private FileStream OpenEvidenceFileForRead(string path)
        {
            EnsureEvidenceFileSafeForRead(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                EnsureEvidenceFileSafeForRead(path);
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private bool IsEvidenceFileSafeForRead(string path)
        {
            return IsSameOrChildPath(path, _rootDirectory) &&
                   !HasReparsePointInPath(path, _rootDirectory);
        }

        internal bool TryDeleteUnpublishedEvidence(string evidenceId, out string error)
        {
            error = string.Empty;
            try
            {
                string path = ResolvePath(evidenceId);
                if (File.Exists(path))
                {
                    EnsureEvidenceFileSafeForRead(path);
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
            return ReplayArtifactHashing.ComputeEvidenceHash(evidence);
        }

        private static bool IsSameOrChildPath(string candidatePath, string rootPath)
        {
            string candidate = Path.GetFullPath(candidatePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string rootWithSeparator = root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasReparsePointInPath(string path, string rootPath)
        {
            string root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath) && HasReparsePoint(new FileInfo(fullPath)))
            {
                return true;
            }

            DirectoryInfo? directory = Directory.Exists(fullPath)
                ? new DirectoryInfo(fullPath)
                : new FileInfo(fullPath).Directory;
            while (directory != null)
            {
                string directoryPath = Path.GetFullPath(directory.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(directoryPath, root, StringComparison.OrdinalIgnoreCase))
                {
                    return HasReparsePoint(directory);
                }

                if (!IsSameOrChildPath(directoryPath, root) || HasReparsePoint(directory))
                {
                    return true;
                }

                directory = directory.Parent;
            }

            return true;
        }

        private static bool HasReparsePoint(FileSystemInfo info)
        {
            try
            {
                return (info.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDatasetIntegrityFailure(Exception ex)
        {
            if (ex is InvalidOperationException &&
                ex.Message.Contains("Replay dataset image hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (ex is FileNotFoundException fileNotFound)
            {
                string? fileName = fileNotFound.FileName;
                return string.IsNullOrWhiteSpace(fileName) ||
                    !string.Equals(Path.GetFileName(fileName), "manifest.json", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
