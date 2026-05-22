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
