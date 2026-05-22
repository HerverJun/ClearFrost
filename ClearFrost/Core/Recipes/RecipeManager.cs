using System;
using System.IO;
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

        public RecipeManager(string? recipePath = null)
        {
            _recipePath = string.IsNullOrWhiteSpace(recipePath)
                ? Path.Combine(RuntimePaths.DataDirectory, "Recipes", "default_recipe.json")
                : recipePath;
            _backupPath = _recipePath + ".bak";
        }

        public string RecipePath => _recipePath;

        public string BackupPath => _backupPath;

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
                    CurrentRecipe = EnsureProductionSnapshot(loadedRecipe, config, out bool migrated);
                    if (migrated)
                    {
                        Save(CurrentRecipe);
                    }

                    return CurrentRecipe;
                }
                catch
                {
                    // Fall back to a fresh AppConfig snapshot and overwrite the damaged recipe atomically.
                }
            }

            CurrentRecipe = Recipe.FromAppConfig(config);
            Save(CurrentRecipe);
            return CurrentRecipe;
        }

        public Recipe GenerateDefault(AppConfig config, float[]? roi = null)
        {
            CurrentRecipe = Recipe.FromAppConfig(config ?? throw new ArgumentNullException(nameof(config)), roi);
            return CurrentRecipe;
        }

        public void Save(Recipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));

            string json = JsonSerializer.Serialize(recipe, JsonOptions);
            AtomicFileWriter.WriteAllText(_recipePath, json);
            CurrentRecipe = recipe;
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
            return true;
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
    }
}
