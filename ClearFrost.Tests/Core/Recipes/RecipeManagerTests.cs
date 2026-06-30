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
                CurrentModelFileName = "main.onnx",
                ActiveCameraId = "cam01",
                Cameras =
                [
                    new CameraConfig
                    {
                        Id = "cam01",
                        SerialNumber = "SN001",
                        DisplayName = "主相机",
                        ExposureTime = 12000,
                        Gain = 1.5,
                        Manufacturer = "Huaray",
                        PixelFormat = "Mono8"
                    }
                ],
                PlcTriggerAddress = "D100",
                PlcResultAddress = "D101",
                BarcodeEnabled = true,
                BarcodeAddress = "D120",
                BarcodeRequired = true
            };
            var manager = new RecipeManager(recipePath);

            Recipe recipe = manager.LoadOrCreateDefault(config);

            recipe.RecipeId.Should().Be("default");
            recipe.TargetLabel.Should().Be("screw");
            recipe.TargetCount.Should().Be(4);
            recipe.CurrentModelFileName.Should().Be("main.onnx");
            recipe.ActiveCameraId.Should().Be("cam01");
            recipe.Cameras.Should().ContainSingle();
            recipe.Cameras[0].SerialNumber.Should().Be("SN001");
            recipe.Plc.TriggerAddress.Should().Be("D100");
            recipe.Plc.ResultAddress.Should().Be("D101");
            recipe.Barcode.Enabled.Should().BeTrue();
            recipe.Barcode.Address.Should().Be("D120");
            recipe.Barcode.Required.Should().BeTrue();
            File.Exists(recipePath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void GenerateDefault_包含归一化ROI()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            var config = new AppConfig();

            Recipe recipe = manager.GenerateDefault(config, new[] { 0.2f, 0.3f, 0.4f, 0.5f });

            recipe.Roi.Should().Equal(0.2f, 0.3f, 0.4f, 0.5f);
            recipe.GetRoiSnapshot().Should().Equal(0.2f, 0.3f, 0.4f, 0.5f);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void GenerateDefault_不会提前修改CurrentRecipe()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });

            Recipe candidate = manager.GenerateDefault(new AppConfig { TargetLabel = "new" });

            candidate.TargetLabel.Should().Be("new");
            manager.CurrentRecipe.TargetLabel.Should().Be("old");
            manager.CurrentRecipe.Version.Should().Be("v1");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void LoadOrCreateDefault_迁移旧版Recipe并保留版本()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            File.WriteAllText(recipePath, """
            {
              "RecipeId": "default",
              "Version": "legacy-v1",
              "TargetLabel": "screw"
            }
            """);
            var config = new AppConfig
            {
                ActiveCameraId = "cam01",
                Cameras =
                [
                    new CameraConfig
                    {
                        Id = "cam01",
                        SerialNumber = "SN001",
                        DisplayName = "主相机"
                    }
                ],
                PlcTriggerAddress = "D200",
                PlcResultAddress = "D201"
            };
            var manager = new RecipeManager(recipePath);

            Recipe recipe = manager.LoadOrCreateDefault(config);

            recipe.Version.Should().Be("legacy-v1");
            recipe.ActiveCameraId.Should().Be("cam01");
            recipe.Cameras.Should().ContainSingle(c => c.SerialNumber == "SN001");
            recipe.Plc.TriggerAddress.Should().Be("D200");
            recipe.Plc.ResultAddress.Should().Be("D201");
            File.Exists(manager.BackupPath).Should().BeTrue();
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

    [Fact]
    public void SaveNewVersion_生成可查询的版本历史()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            var config = new AppConfig
            {
                CurrentOperatorId = "op01",
                CurrentOperatorRole = ClearFrost.Core.Security.ProductionRole.Engineer,
                CurrentModelFileName = "main.onnx",
                TargetLabel = "part",
                TargetCount = 1
            };

            Recipe first = manager.SaveNewVersion(config, null, "op01", "Engineer", "初始配方");
            Thread.Sleep(2);
            config.TargetCount = 2;
            Recipe second = manager.SaveNewVersion(config, null, "op02", "ShiftLead", "数量调整");

            first.Version.Should().NotBe(second.Version);
            RecipeVersionInfo current = manager.GetCurrentVersionInfo();
            current.Version.Should().Be(second.Version);
            current.OperatorId.Should().Be("op02");
            current.ChangeSummary.Should().Be("数量调整");

            IReadOnlyList<RecipeVersionInfo> history = manager.GetVersionHistory();
            history.Should().HaveCount(2);
            history.Should().Contain(item => item.Version == first.Version && item.ChangeSummary == "初始配方");
            history.Should().Contain(item => File.Exists(item.SnapshotPath));
            File.Exists(manager.HistoryPath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void SaveNewVersion_快照写入失败时保持旧版本()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            bool failSnapshot = false;
            var manager = new RecipeManager(recipePath, (path, content) =>
            {
                if (failSnapshot && path.Contains($"{Path.DirectorySeparatorChar}Versions{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("snapshot failed");
                }

                ClearFrost.Helpers.AtomicFileWriter.WriteAllText(path, content);
            });
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });
            failSnapshot = true;

            Action act = () => manager.SaveNewVersion(new AppConfig { TargetLabel = "new" }, null, "op", "Engineer", "fail");

            act.Should().Throw<IOException>();
            manager.CurrentRecipe.Version.Should().Be("v1");
            manager.CurrentRecipe.TargetLabel.Should().Be("old");
            File.ReadAllText(recipePath).Should().Contain("old").And.NotContain("new");
            manager.GetVersionHistory().Should().NotContain(item => item.ChangeSummary == "fail");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void SaveNewVersion_当前文件写入失败时保持旧版本()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            bool failCurrent = false;
            var manager = new RecipeManager(recipePath, (path, content) =>
            {
                if (failCurrent && string.Equals(path, recipePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("current failed");
                }

                ClearFrost.Helpers.AtomicFileWriter.WriteAllText(path, content);
            });
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });
            failCurrent = true;

            Action act = () => manager.SaveNewVersion(new AppConfig { TargetLabel = "new" }, null, "op", "Engineer", "fail-current");

            act.Should().Throw<IOException>();
            manager.CurrentRecipe.Version.Should().Be("v1");
            File.ReadAllText(recipePath).Should().Contain("old").And.NotContain("new");
            manager.GetVersionHistory().Should().NotContain(item => item.ChangeSummary == "fail-current");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void SaveNewVersion_历史写入失败时回滚当前文件并保持旧版本()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            bool failHistoryOnce = false;
            var manager = new RecipeManager(recipePath, (path, content) =>
            {
                if (failHistoryOnce && path.EndsWith("recipe_versions.json", StringComparison.OrdinalIgnoreCase))
                {
                    failHistoryOnce = false;
                    throw new IOException("history failed");
                }

                ClearFrost.Helpers.AtomicFileWriter.WriteAllText(path, content);
            });
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });
            failHistoryOnce = true;

            Action act = () => manager.SaveNewVersion(new AppConfig { TargetLabel = "new" }, null, "op", "Engineer", "fail-history");

            act.Should().Throw<IOException>();
            manager.CurrentRecipe.Version.Should().Be("v1");
            File.ReadAllText(recipePath).Should().Contain("old").And.NotContain("new");
            manager.GetVersionHistory().Should().NotContain(item => item.ChangeSummary == "fail-history");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task SaveNewVersion_并发保存生成唯一版本()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });

            Task<Recipe>[] tasks = Enumerable.Range(0, 20)
                .Select(index => Task.Run(() => manager.SaveNewVersion(
                    new AppConfig { TargetLabel = $"part-{index}" },
                    null,
                    $"op{index}",
                    "Engineer",
                    $"change-{index}")))
                .ToArray();

            Recipe[] recipes = await Task.WhenAll(tasks);

            recipes.Select(recipe => recipe.Version).Should().OnlyHaveUniqueItems();
            manager.GetVersionHistory(50).Select(item => item.Version).Should().OnlyHaveUniqueItems();
            manager.CurrentRecipe.TargetLabel.Should().StartWith("part-");
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
