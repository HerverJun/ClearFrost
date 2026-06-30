using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearFrost.Config;
using ClearFrost.Helpers;

namespace ClearFrost.Core.Recipes
{
    /// <summary>
    /// Manages the default production recipe snapshot without replacing AppConfig.
    /// </summary>
    public sealed class RecipeManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private readonly string _recipePath;
        private readonly string _backupPath;
        private readonly string _historyPath;
        private readonly string _versionsDirectory;
        private readonly object _saveLock = new object();
        private readonly Action<string, string> _writeAllText;
        private readonly Action<string, byte[]> _restoreAllBytes;
        private readonly Action<string> _deleteFile;

        public RecipeManager(string? recipePath = null)
            : this(recipePath, AtomicFileWriter.WriteAllText)
        {
        }

        internal RecipeManager(
            string? recipePath,
            Action<string, string> writeAllText,
            Action<string, byte[]>? restoreAllBytes = null,
            Action<string>? deleteFile = null)
        {
            _recipePath = string.IsNullOrWhiteSpace(recipePath)
                ? Path.Combine(RuntimePaths.DataDirectory, "Recipes", "default_recipe.json")
                : recipePath;
            _backupPath = _recipePath + ".bak";
            string recipeDirectory = Path.GetDirectoryName(_recipePath) ?? RuntimePaths.DataDirectory;
            _historyPath = Path.Combine(recipeDirectory, "recipe_versions.json");
            _versionsDirectory = Path.Combine(recipeDirectory, "Versions");
            _writeAllText = writeAllText ?? throw new ArgumentNullException(nameof(writeAllText));
            _restoreAllBytes = restoreAllBytes ?? AtomicFileWriter.RestoreAllBytes;
            _deleteFile = deleteFile ?? File.Delete;
        }

        public string RecipePath => _recipePath;

        public string BackupPath => _backupPath;

        public string HistoryPath => _historyPath;

        internal string VersionsDirectory => _versionsDirectory;

        public Recipe CurrentRecipe { get; private set; } = new Recipe();

        public Recipe LoadOrCreateDefault(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            if (File.Exists(_recipePath))
            {
                try
                {
                    string json = File.ReadAllText(_recipePath);
                    Recipe loadedRecipe = JsonSerializer.Deserialize<Recipe>(json, JsonOptions) ?? Recipe.FromAppConfig(config);
                    Recipe candidate = EnsureProductionSnapshot(loadedRecipe, config, out bool migrated);
                    if (migrated)
                    {
                        Save(candidate);
                    }
                    else
                    {
                        EnsureVersionInfo(candidate);
                        CurrentRecipe = candidate;
                    }

                    return CurrentRecipe;
                }
                catch
                {
                    // Fall back to a fresh AppConfig snapshot and overwrite the damaged recipe atomically.
                }
            }

            Recipe recipe = Recipe.FromAppConfig(config);
            Save(recipe);
            return CurrentRecipe;
        }

        public Recipe GenerateDefault(
            AppConfig config,
            float[]? roi = null,
            string? operatorId = null,
            string? operatorRole = null,
            string? changeSummary = null)
        {
            return Recipe.FromAppConfig(
                config ?? throw new ArgumentNullException(nameof(config)),
                roi,
                operatorId,
                operatorRole,
                changeSummary);
        }

        public Recipe SaveNewVersion(
            AppConfig config,
            float[]? roi,
            string? operatorId,
            string? operatorRole,
            string? changeSummary)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            lock (_saveLock)
            {
                string recipeId = string.IsNullOrWhiteSpace(CurrentRecipe?.RecipeId)
                    ? "default"
                    : CurrentRecipe.RecipeId;
                Recipe recipe = GenerateDefault(config, roi, operatorId, operatorRole, changeSummary);
                recipe.RecipeId = recipeId;
                SaveInternal(recipe, ensureUniqueVersion: true);
                return recipe;
            }
        }

        internal Recipe SaveNewVersionForActivationTransaction(
            AppConfig config,
            float[]? roi,
            string? operatorId,
            string? operatorRole,
            string? changeSummary,
            RecipeTransactionSnapshot transactionSnapshot)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (transactionSnapshot == null) throw new ArgumentNullException(nameof(transactionSnapshot));

            lock (_saveLock)
            {
                string recipeId = string.IsNullOrWhiteSpace(CurrentRecipe?.RecipeId)
                    ? "default"
                    : CurrentRecipe.RecipeId;
                Recipe recipe = GenerateDefault(config, roi, operatorId, operatorRole, changeSummary);
                recipe.RecipeId = recipeId;
                SaveInternal(recipe, ensureUniqueVersion: true, transactionSnapshot);
                return recipe;
            }
        }

        internal Recipe CaptureCurrentSnapshot()
        {
            lock (_saveLock)
            {
                return CloneRecipe(CurrentRecipe);
            }
        }

        internal RecipeTransactionSnapshot CaptureTransactionSnapshot()
        {
            lock (_saveLock)
            {
                return new RecipeTransactionSnapshot(
                    _recipePath,
                    _backupPath,
                    _historyPath,
                    _historyPath + ".bak",
                    _versionsDirectory,
                    CloneRecipe(CurrentRecipe),
                    CaptureFile(_recipePath),
                    CaptureFile(_backupPath),
                    CaptureFile(_historyPath),
                    CaptureFile(_historyPath + ".bak"),
                    Directory.Exists(_versionsDirectory),
                    CaptureVersionFiles(),
                    CaptureRecoveryArtifacts());
            }
        }

        internal IReadOnlyList<string> RestoreTransactionSnapshot(RecipeTransactionSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            lock (_saveLock)
            {
                var failures = new List<string>();

                RestoreFile(snapshot.RecipeFile, failures);
                RestoreFile(snapshot.RecipeBackupFile, failures);
                RestoreFile(snapshot.HistoryFile, failures);
                RestoreFile(snapshot.HistoryBackupFile, failures);
                DeleteTransactionVersionFiles(snapshot, failures);
                RestoreVersionsDirectoryState(snapshot, failures);

                try
                {
                    failures.AddRange(VerifyTransactionDiskSnapshot(snapshot));
                }
                catch (Exception ex)
                {
                    failures.Add($"Verify transaction disk snapshot failed: {ex.Message}");
                }

                if (failures.Count > 0)
                {
                    return failures;
                }

                try
                {
                    CurrentRecipe = CloneRecipe(snapshot.CurrentRecipe);
                    failures.AddRange(VerifyTransactionMemorySnapshot(snapshot));
                }
                catch (Exception ex)
                {
                    failures.Add($"Restore CurrentRecipe failed: {ex.Message}");
                }

                return failures;
            }
        }

        internal IReadOnlyList<string> VerifyTransactionSnapshot(RecipeTransactionSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            lock (_saveLock)
            {
                var failures = new List<string>();
                try
                {
                    failures.AddRange(VerifyTransactionDiskSnapshot(snapshot));
                }
                catch (Exception ex)
                {
                    failures.Add($"Verify transaction disk snapshot failed: {ex.Message}");
                }

                try
                {
                    failures.AddRange(VerifyTransactionMemorySnapshot(snapshot));
                }
                catch (Exception ex)
                {
                    failures.Add($"Verify transaction memory snapshot failed: {ex.Message}");
                }

                return failures;
            }
        }

        internal void RestoreSnapshot(Recipe snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            lock (_saveLock)
            {
                SaveInternal(CloneRecipe(snapshot), ensureUniqueVersion: false);
            }
        }

        public void Save(Recipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));

            lock (_saveLock)
            {
                SaveInternal(recipe, ensureUniqueVersion: false);
            }
        }

        public bool RollbackLastVersion()
        {
            if (!File.Exists(_backupPath))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(_recipePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(_backupPath, _recipePath, overwrite: true);
            string json = File.ReadAllText(_recipePath);
            CurrentRecipe = JsonSerializer.Deserialize<Recipe>(json, JsonOptions) ?? new Recipe();
            NormalizeNestedSnapshots(CurrentRecipe);
            EnsureVersionInfo(CurrentRecipe);
            return true;
        }

        public RecipeVersionInfo GetCurrentVersionInfo()
        {
            NormalizeRecipeMetadata(CurrentRecipe);
            return ToVersionInfo(CurrentRecipe, BuildSnapshotPath(CurrentRecipe));
        }

        public IReadOnlyList<RecipeVersionInfo> GetVersionHistory(int limit = 100)
        {
            int safeLimit = Math.Clamp(limit <= 0 ? 100 : limit, 1, 1000);
            return LoadVersionHistory()
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Take(safeLimit)
                .ToList();
        }

        private static Recipe EnsureProductionSnapshot(Recipe recipe, AppConfig config, out bool migrated)
        {
            NormalizeNestedSnapshots(recipe);

            bool needsMigration =
                (recipe.Cameras.Count == 0 && (config.Cameras?.Count ?? 0) > 0) ||
                (string.IsNullOrWhiteSpace(recipe.ActiveCameraId) && !string.IsNullOrWhiteSpace(config.ActiveCameraId)) ||
                string.IsNullOrWhiteSpace(recipe.Plc.Protocol) ||
                string.IsNullOrWhiteSpace(recipe.Plc.TriggerAddress) ||
                string.IsNullOrWhiteSpace(recipe.Barcode.Encoding) ||
                string.IsNullOrWhiteSpace(recipe.Trigger.Source);

            if (!needsMigration)
            {
                migrated = false;
                return recipe;
            }

            Recipe migratedRecipe = Recipe.FromAppConfig(config, recipe.GetRoiSnapshot());
            migratedRecipe.RecipeId = string.IsNullOrWhiteSpace(recipe.RecipeId) ? migratedRecipe.RecipeId : recipe.RecipeId;
            migratedRecipe.Version = string.IsNullOrWhiteSpace(recipe.Version) ? migratedRecipe.Version : recipe.Version;
            migratedRecipe.CreatedAt = recipe.CreatedAt == default ? migratedRecipe.CreatedAt : recipe.CreatedAt;
            migratedRecipe.OperatorId = recipe.OperatorId ?? string.Empty;
            migratedRecipe.OperatorRole = recipe.OperatorRole ?? string.Empty;
            migratedRecipe.ChangeSummary = recipe.ChangeSummary ?? string.Empty;
            migrated = true;
            return migratedRecipe;
        }

        private static void NormalizeNestedSnapshots(Recipe recipe)
        {
            recipe.CurrentModelReference ??= ClearFrost.Core.Models.ProductionModelReference.Empty();
            recipe.Auxiliary1ModelReference ??= ClearFrost.Core.Models.ProductionModelReference.Empty();
            recipe.Auxiliary2ModelReference ??= ClearFrost.Core.Models.ProductionModelReference.Empty();
            recipe.Cameras ??= new();
            recipe.Plc ??= new RecipePlcSnapshot();
            recipe.Barcode ??= new RecipeBarcodeSnapshot();
            recipe.Trigger ??= new RecipeTriggerSnapshot();
            recipe.Roi = recipe.GetRoiSnapshot();
        }

        private void EnsureVersionInfo(Recipe recipe)
        {
            NormalizeRecipeMetadata(recipe);
            Directory.CreateDirectory(_versionsDirectory);
            string snapshotPath = BuildSnapshotPath(recipe);
            string snapshotJson = JsonSerializer.Serialize(recipe, JsonOptions);
            _writeAllText(snapshotPath, snapshotJson);

            List<RecipeVersionInfo> history = LoadVersionHistory()
                .Where(item =>
                    !string.Equals(item.RecipeId, recipe.RecipeId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.Version, recipe.Version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            history.Add(ToVersionInfo(recipe, snapshotPath));
            SaveVersionHistory(history);
        }

        private void SaveInternal(
            Recipe recipe,
            bool ensureUniqueVersion,
            RecipeTransactionSnapshot? transactionSnapshot = null)
        {
            Recipe oldRecipe = CurrentRecipe;
            List<RecipeVersionInfo> oldHistory = LoadVersionHistory();

            NormalizeRecipeMetadata(recipe);
            if (ensureUniqueVersion)
            {
                EnsureUniqueVersion(recipe, oldHistory);
            }

            Directory.CreateDirectory(_versionsDirectory);
            string recipeJson = JsonSerializer.Serialize(recipe, JsonOptions);
            string snapshotPath = BuildSnapshotPath(recipe);
            string snapshotJson = JsonSerializer.Serialize(recipe, JsonOptions);
            transactionSnapshot?.TrackTransactionVersionFile(snapshotPath, snapshotJson);
            List<RecipeVersionInfo> newHistory = oldHistory
                .Where(item =>
                    !string.Equals(item.RecipeId, recipe.RecipeId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(item.Version, recipe.Version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            newHistory.Add(ToVersionInfo(recipe, snapshotPath));

            bool currentWritten = false;
            try
            {
                _writeAllText(snapshotPath, snapshotJson);
                _writeAllText(_recipePath, recipeJson);
                currentWritten = true;
                SaveVersionHistory(newHistory);
                CurrentRecipe = recipe;
            }
            catch
            {
                if (currentWritten)
                {
                    TryRestoreRecipeFile(oldRecipe);
                }

                TryRestoreVersionHistory(oldHistory);
                throw;
            }
        }

        private FileContentSnapshot CaptureFile(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath)
                ? new FileContentSnapshot(fullPath, true, File.ReadAllBytes(fullPath))
                : new FileContentSnapshot(fullPath, false, Array.Empty<byte>());
        }

        private IReadOnlyDictionary<string, VersionFileSnapshot> CaptureVersionFiles()
        {
            var files = new Dictionary<string, VersionFileSnapshot>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(_versionsDirectory))
            {
                return files;
            }

            foreach (string path in Directory.EnumerateFiles(_versionsDirectory, "*", SearchOption.AllDirectories))
            {
                string relativePath = GetVersionRelativePath(path);
                files[relativePath] = new VersionFileSnapshot(relativePath, ComputeFileSha256(path));
            }

            return files;
        }

        private IReadOnlySet<string> CaptureRecoveryArtifacts()
        {
            var artifacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string recipeDirectory = Path.GetDirectoryName(_recipePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(recipeDirectory) && Directory.Exists(recipeDirectory))
            {
                foreach (string path in Directory.EnumerateFiles(recipeDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsRecoveryArtifact(path))
                    {
                        artifacts.Add(GetRecipeRelativePath(path));
                    }
                }
            }

            if (Directory.Exists(_versionsDirectory))
            {
                foreach (string path in Directory.EnumerateFiles(_versionsDirectory, "*", SearchOption.AllDirectories))
                {
                    if (IsRecoveryArtifact(path))
                    {
                        artifacts.Add(GetRecipeRelativePath(path));
                    }
                }
            }

            return artifacts;
        }

        private void RestoreFile(FileContentSnapshot snapshot, List<string> failures)
        {
            try
            {
                if (snapshot.Exists)
                {
                    _restoreAllBytes(snapshot.Path, snapshot.Content);
                    return;
                }

                if (File.Exists(snapshot.Path))
                {
                    _deleteFile(snapshot.Path);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"Restore file failed: {snapshot.Path}; {ex.Message}");
            }
        }

        private void DeleteTransactionVersionFiles(RecipeTransactionSnapshot snapshot, List<string> failures)
        {
            foreach (TrackedVersionFile tracked in snapshot.TransactionVersionFiles)
            {
                if (snapshot.VersionFiles.ContainsKey(tracked.RelativePath))
                {
                    continue;
                }

                if (!TryResolveVersionPath(tracked.RelativePath, out string fullPath, out string error))
                {
                    failures.Add(error);
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    continue;
                }

                if (!CanConfirmTransactionVersionFile(fullPath, tracked, out string confirmError))
                {
                    failures.Add(confirmError);
                    continue;
                }

                try
                {
                    _deleteFile(fullPath);
                }
                catch (Exception ex)
                {
                    failures.Add($"Delete transaction version file failed: {fullPath}; {ex.Message}");
                }
            }
        }

        private void RestoreVersionsDirectoryState(RecipeTransactionSnapshot snapshot, List<string> failures)
        {
            try
            {
                if (snapshot.VersionsDirectoryExisted)
                {
                    if (!Directory.Exists(_versionsDirectory))
                    {
                        Directory.CreateDirectory(_versionsDirectory);
                    }

                    return;
                }

                if (Directory.Exists(_versionsDirectory))
                {
                    Directory.Delete(_versionsDirectory, recursive: false);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"Restore Versions directory state failed: {_versionsDirectory}; {ex.Message}");
            }
        }

        private IReadOnlyList<string> VerifyTransactionDiskSnapshot(RecipeTransactionSnapshot snapshot)
        {
            var failures = new List<string>();
            VerifyFile(snapshot.RecipeFile, failures);
            VerifyFile(snapshot.RecipeBackupFile, failures);
            VerifyFile(snapshot.HistoryFile, failures);
            VerifyFile(snapshot.HistoryBackupFile, failures);
            VerifyVersionFiles(snapshot, failures);
            VerifyTransactionVersionFilesRemoved(snapshot, failures);
            VerifyRecoveryArtifacts(snapshot, failures);
            return failures;
        }

        private IReadOnlyList<string> VerifyTransactionMemorySnapshot(RecipeTransactionSnapshot snapshot)
        {
            var failures = new List<string>();
            if (!RecipesEquivalent(CurrentRecipe, snapshot.CurrentRecipe))
            {
                failures.Add("CurrentRecipe does not match transaction snapshot after restore.");
            }

            return failures;
        }

        private void VerifyFile(FileContentSnapshot snapshot, List<string> failures)
        {
            try
            {
                bool exists = File.Exists(snapshot.Path);
                if (snapshot.Exists != exists)
                {
                    failures.Add($"File existence mismatch after restore: {snapshot.Path}; ExpectedExists={snapshot.Exists}; ActualExists={exists}");
                    return;
                }

                if (!snapshot.Exists)
                {
                    return;
                }

                byte[] actual = File.ReadAllBytes(snapshot.Path);
                if (!actual.SequenceEqual(snapshot.Content))
                {
                    failures.Add($"File content mismatch after restore: {snapshot.Path}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"File verification failed: {snapshot.Path}; {ex.Message}");
            }
        }

        private void VerifyVersionFiles(RecipeTransactionSnapshot snapshot, List<string> failures)
        {
            bool versionsExists = Directory.Exists(_versionsDirectory);
            if (snapshot.VersionsDirectoryExisted != versionsExists)
            {
                failures.Add($"Versions directory existence mismatch after restore: {_versionsDirectory}; ExpectedExists={snapshot.VersionsDirectoryExisted}; ActualExists={versionsExists}");
            }

            foreach (VersionFileSnapshot expected in snapshot.VersionFiles.Values)
            {
                if (!TryResolveVersionPath(expected.RelativePath, out string fullPath, out string error))
                {
                    failures.Add(error);
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    failures.Add($"Existing version file missing after restore: {fullPath}");
                    continue;
                }

                try
                {
                    string actualHash = ComputeFileSha256(fullPath);
                    if (!string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        failures.Add($"Existing version file hash mismatch after restore: {fullPath}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"Existing version file verification failed: {fullPath}; {ex.Message}");
                }
            }
        }

        private void VerifyTransactionVersionFilesRemoved(RecipeTransactionSnapshot snapshot, List<string> failures)
        {
            foreach (TrackedVersionFile tracked in snapshot.TransactionVersionFiles)
            {
                if (snapshot.VersionFiles.ContainsKey(tracked.RelativePath))
                {
                    continue;
                }

                if (!TryResolveVersionPath(tracked.RelativePath, out string fullPath, out string error))
                {
                    failures.Add(error);
                    continue;
                }

                if (File.Exists(fullPath))
                {
                    failures.Add($"Transaction version file still exists after restore: {fullPath}");
                }
            }
        }

        private void VerifyRecoveryArtifacts(RecipeTransactionSnapshot snapshot, List<string> failures)
        {
            try
            {
                HashSet<string> currentArtifacts = CaptureRecoveryArtifacts()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                currentArtifacts.ExceptWith(snapshot.RecoveryArtifactRelativePaths);
                if (currentArtifacts.Count > 0)
                {
                    failures.Add($"Recovery left temporary or backup artifacts: {string.Join(", ", currentArtifacts.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"Recovery artifact verification failed: {ex.Message}");
            }
        }

        private bool CanConfirmTransactionVersionFile(
            string fullPath,
            TrackedVersionFile tracked,
            out string error)
        {
            error = string.Empty;
            try
            {
                string hash = ComputeFileSha256(fullPath);
                if (string.Equals(hash, tracked.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                Recipe? recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(fullPath), JsonOptions);
                if (recipe != null &&
                    string.Equals(recipe.RecipeId, tracked.RecipeId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(recipe.Version, tracked.Version, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                error = $"Cannot confirm transaction-created version file, leaving it untouched: {fullPath}";
                return false;
            }
            catch (Exception ex)
            {
                error = $"Cannot inspect transaction-created version file: {fullPath}; {ex.Message}";
                return false;
            }
        }

        private bool TryResolveVersionPath(string relativePath, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(
                    _versionsDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)));
                string versionsRoot = Path.GetFullPath(_versionsDirectory);
                if (!IsPathUnderDirectory(candidate, versionsRoot))
                {
                    error = $"Version path escapes Versions directory: {relativePath}";
                    return false;
                }

                fullPath = candidate;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid version path: {relativePath}; {ex.Message}";
                return false;
            }
        }

        private string GetVersionRelativePath(string path)
        {
            return NormalizeRelativePath(Path.GetRelativePath(_versionsDirectory, Path.GetFullPath(path)));
        }

        private string GetRecipeRelativePath(string path)
        {
            string recipeDirectory = Path.GetDirectoryName(_recipePath) ?? string.Empty;
            return NormalizeRelativePath(Path.GetRelativePath(recipeDirectory, Path.GetFullPath(path)));
        }

        private static bool IsPathUnderDirectory(string path, string directory)
        {
            string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(
                       normalizedDirectory + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static bool IsRecoveryArtifact(string path)
        {
            string fileName = Path.GetFileName(path);
            return fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".bak.bak", StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeFileSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return ComputeSha256(stream);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes ?? Array.Empty<byte>());
            return ComputeSha256(stream);
        }

        private static string ComputeSha256(Stream stream)
        {
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }

        private List<RecipeVersionInfo> LoadVersionHistory()
        {
            try
            {
                if (!File.Exists(_historyPath))
                {
                    return new List<RecipeVersionInfo>();
                }

                string json = File.ReadAllText(_historyPath);
                return JsonSerializer.Deserialize<List<RecipeVersionInfo>>(json, JsonOptions) ?? new List<RecipeVersionInfo>();
            }
            catch
            {
                return new List<RecipeVersionInfo>();
            }
        }

        private void SaveVersionHistory(List<RecipeVersionInfo> history)
        {
            string json = JsonSerializer.Serialize(
                history
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                JsonOptions);
            _writeAllText(_historyPath, json);
        }

        private void TryRestoreRecipeFile(Recipe oldRecipe)
        {
            try
            {
                NormalizeRecipeMetadata(oldRecipe);
                _writeAllText(_recipePath, JsonSerializer.Serialize(oldRecipe, JsonOptions));
            }
            catch
            {
                // Best-effort rollback; the original save exception remains the actionable failure.
            }
        }

        private void TryRestoreVersionHistory(List<RecipeVersionInfo> oldHistory)
        {
            try
            {
                SaveVersionHistory(oldHistory);
            }
            catch
            {
                // Best-effort rollback; the original save exception remains the actionable failure.
            }
        }

        private static void EnsureUniqueVersion(Recipe recipe, List<RecipeVersionInfo> existingHistory)
        {
            var existing = new HashSet<string>(
                existingHistory
                    .Where(item => string.Equals(item.RecipeId, recipe.RecipeId, StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Version),
                StringComparer.OrdinalIgnoreCase);

            string baseVersion = string.IsNullOrWhiteSpace(recipe.Version)
                ? DateTimeOffset.Now.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture)
                : recipe.Version;
            string version = baseVersion;
            int suffix = 1;
            while (existing.Contains(version))
            {
                version = $"{baseVersion}-{suffix++}";
            }

            recipe.Version = version;
        }

        private string BuildSnapshotPath(Recipe recipe)
        {
            string fileName = $"{SanitizeFileName(recipe.RecipeId)}_{SanitizeFileName(recipe.Version)}.json";
            return Path.Combine(_versionsDirectory, fileName);
        }

        private static RecipeVersionInfo ToVersionInfo(Recipe recipe, string snapshotPath)
        {
            return new RecipeVersionInfo
            {
                RecipeId = recipe.RecipeId,
                Version = recipe.Version,
                CreatedAt = recipe.CreatedAt,
                OperatorId = recipe.OperatorId ?? string.Empty,
                OperatorRole = recipe.OperatorRole ?? string.Empty,
                ChangeSummary = recipe.ChangeSummary ?? string.Empty,
                SnapshotPath = snapshotPath ?? string.Empty
            };
        }

        private static void NormalizeRecipeMetadata(Recipe recipe)
        {
            NormalizeNestedSnapshots(recipe);
            if (string.IsNullOrWhiteSpace(recipe.RecipeId))
            {
                recipe.RecipeId = "default";
            }

            if (string.IsNullOrWhiteSpace(recipe.Version))
            {
                recipe.Version = DateTimeOffset.Now.ToString(
                    "yyyyMMddHHmmssfff",
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (recipe.CreatedAt == default)
            {
                recipe.CreatedAt = DateTimeOffset.Now;
            }

            recipe.OperatorId ??= string.Empty;
            recipe.OperatorRole ??= string.Empty;
            recipe.ChangeSummary ??= string.Empty;
        }

        private static Recipe CloneRecipe(Recipe recipe)
        {
            string json = JsonSerializer.Serialize(recipe ?? new Recipe(), JsonOptions);
            Recipe clone = JsonSerializer.Deserialize<Recipe>(json, JsonOptions) ?? new Recipe();
            NormalizeRecipeMetadata(clone);
            return clone;
        }

        private static bool RecipesEquivalent(Recipe left, Recipe right)
        {
            string leftJson = JsonSerializer.Serialize(CloneRecipe(left), JsonOptions);
            string rightJson = JsonSerializer.Serialize(CloneRecipe(right), JsonOptions);
            return string.Equals(leftJson, rightJson, StringComparison.Ordinal);
        }

        private static string SanitizeFileName(string value)
        {
            string raw = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
            foreach (char ch in Path.GetInvalidFileNameChars())
            {
                raw = raw.Replace(ch, '_');
            }

            return raw.Length <= 96 ? raw : raw[..96];
        }
    }

    internal sealed class RecipeTransactionSnapshot
    {
        private readonly List<TrackedVersionFile> _transactionVersionFiles = new();

        public RecipeTransactionSnapshot(
            string recipePath,
            string backupPath,
            string historyPath,
            string historyBackupPath,
            string versionsDirectory,
            Recipe currentRecipe,
            FileContentSnapshot recipeFile,
            FileContentSnapshot recipeBackupFile,
            FileContentSnapshot historyFile,
            FileContentSnapshot historyBackupFile,
            bool versionsDirectoryExisted,
            IReadOnlyDictionary<string, VersionFileSnapshot> versionFiles,
            IReadOnlySet<string> recoveryArtifactRelativePaths)
        {
            RecipePath = Path.GetFullPath(recipePath);
            BackupPath = Path.GetFullPath(backupPath);
            HistoryPath = Path.GetFullPath(historyPath);
            HistoryBackupPath = Path.GetFullPath(historyBackupPath);
            VersionsDirectory = Path.GetFullPath(versionsDirectory);
            CurrentRecipe = currentRecipe ?? throw new ArgumentNullException(nameof(currentRecipe));
            RecipeFile = recipeFile;
            RecipeBackupFile = recipeBackupFile;
            HistoryFile = historyFile;
            HistoryBackupFile = historyBackupFile;
            VersionsDirectoryExisted = versionsDirectoryExisted;
            VersionFiles = new Dictionary<string, VersionFileSnapshot>(
                versionFiles ?? new Dictionary<string, VersionFileSnapshot>(),
                StringComparer.OrdinalIgnoreCase);
            RecoveryArtifactRelativePaths = new HashSet<string>(
                recoveryArtifactRelativePaths ?? new HashSet<string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public string RecipePath { get; }
        public string BackupPath { get; }
        public string HistoryPath { get; }
        public string HistoryBackupPath { get; }
        public string VersionsDirectory { get; }
        public Recipe CurrentRecipe { get; }
        public FileContentSnapshot RecipeFile { get; }
        public FileContentSnapshot RecipeBackupFile { get; }
        public FileContentSnapshot HistoryFile { get; }
        public FileContentSnapshot HistoryBackupFile { get; }
        public bool VersionsDirectoryExisted { get; }
        public IReadOnlyDictionary<string, VersionFileSnapshot> VersionFiles { get; }
        public IReadOnlySet<string> RecoveryArtifactRelativePaths { get; }
        public IReadOnlyList<TrackedVersionFile> TransactionVersionFiles => _transactionVersionFiles;

        internal void TrackTransactionVersionFile(string fullPath, string content)
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(VersionsDirectory, Path.GetFullPath(fullPath)));
            if (VersionFiles.ContainsKey(relativePath) ||
                _transactionVersionFiles.Any(item => string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Recipe? recipe = JsonSerializer.Deserialize<Recipe>(content ?? string.Empty);
            _transactionVersionFiles.Add(new TrackedVersionFile(
                relativePath,
                ComputeSha256(Encoding.UTF8.GetBytes(content ?? string.Empty)),
                recipe?.RecipeId ?? string.Empty,
                recipe?.Version ?? string.Empty));
        }

        private static string NormalizeRelativePath(string path)
        {
            return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        private static string ComputeSha256(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes ?? Array.Empty<byte>());
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    internal sealed record FileContentSnapshot(string Path, bool Exists, byte[] Content);

    internal sealed record VersionFileSnapshot(string RelativePath, string Sha256);

    internal sealed record TrackedVersionFile(string RelativePath, string Sha256, string RecipeId, string Version);
}
