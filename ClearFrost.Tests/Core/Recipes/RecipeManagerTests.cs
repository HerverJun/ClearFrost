using ClearFrost.Config;
using ClearFrost.Core.Recipes;
using FluentAssertions;

namespace ClearFrost.Tests.Core.Recipes;

public class RecipeManagerTests
{
    [Fact]
    public void LoadOrCreateDefault_从AppConfig生成默认Recipe()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var config = new AppConfig
            {
                TargetLabel = "screw",
                TargetCount = 4,
                CurrentModelFileName = "main.onnx"
            };
            var manager = new RecipeManager(recipePath);

            Recipe recipe = manager.LoadOrCreateDefault(config);

            recipe.RecipeId.Should().Be("default");
            recipe.TargetLabel.Should().Be("screw");
            recipe.TargetCount.Should().Be(4);
            recipe.CurrentModelFileName.Should().Be("main.onnx");
            File.Exists(recipePath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void Save_会原子备份且Rollback恢复上一版本()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);

            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });
            manager.Save(new Recipe { RecipeId = "default", Version = "v2", TargetLabel = "new" });

            File.Exists(manager.BackupPath).Should().BeTrue();

            manager.RollbackLastVersion().Should().BeTrue();
            manager.CurrentRecipe.Version.Should().Be("v1");
            manager.CurrentRecipe.TargetLabel.Should().Be("old");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(RecipeManagerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, true);
        }
    }
}
