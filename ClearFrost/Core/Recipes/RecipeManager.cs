using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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

        public RecipeManager(string? recipePath = null)
            : this(recipePath, AtomicFileWriter.WriteAllText)
        {
        }

        internal RecipeManager(string? recipePath, Action<string, string> writeAllText)
        {
            _recipePath = string.IsNullOrWhiteSpace(recipePath)
                ? Path.Combine(RuntimePaths.DataDirectory, "Recipes", "default_recipe.json")
                : recipePath;
            _backupPath = _recipePath + ".bak";
            string recipeDirectory = Path.GetDirectoryName(_recipePath) ?? RuntimePaths.DataDirectory;
            _historyPath = Path.Combine(recipeDirectory, "recipe_versions.json");
            _versionsDirectory = Path.Combine(recipeDirectory, "Versions");
            _writeAllText = writeAllText ?? throw new ArgumentNullException(nameof(writeAllText));
        }

        public string RecipePath => _recipePath;

        public string BackupPath => _backupPath;

        public string HistoryPath => _historyPath;

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

        private void SaveInternal(Recipe recipe, bool ensureUniqueVersion)
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
}
