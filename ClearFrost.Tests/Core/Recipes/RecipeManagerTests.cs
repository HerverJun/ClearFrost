using ClearFrost.Config;
using ClearFrost.Core.Recipes;
using FluentAssertions;

using System.Text.Json;

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
    public void LoadOrCreateDefault_拒绝链接当前配方文件且不加载外部内容()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string recipePath = Path.Combine(tempDir, "default_recipe.json");
        try
        {
            string externalRecipe = Path.Combine(externalDir, "external-recipe.json");
            File.WriteAllText(externalRecipe, """
            {
              "RecipeId": "default",
              "Version": "external-v1",
              "TargetLabel": "external"
            }
            """);
            if (!TryCreateFileSymbolicLink(recipePath, externalRecipe))
            {
                return;
            }

            var manager = new RecipeManager(recipePath);

            Action act = () => manager.LoadOrCreateDefault(new AppConfig { TargetLabel = "local" });

            act.Should().Throw<IOException>().WithMessage("*当前配方文件*链接文件*");
            manager.CurrentRecipe.TargetLabel.Should().BeEmpty();
            File.ReadAllText(externalRecipe).Should().Contain("external");
        }
        finally
        {
            TryDeleteFileLink(recipePath);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
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
    public void RollbackLastVersion_拒绝链接备份文件且不修改当前配方()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });
            manager.Save(new Recipe { RecipeId = "default", Version = "v2", TargetLabel = "new" });

            string externalBackup = Path.Combine(externalDir, "external-backup.json");
            File.WriteAllText(externalBackup, """
            {
              "RecipeId": "default",
              "Version": "external",
              "TargetLabel": "external"
            }
            """);
            File.Delete(manager.BackupPath);
            if (!TryCreateFileSymbolicLink(manager.BackupPath, externalBackup))
            {
                return;
            }

            Action act = () => manager.RollbackLastVersion();

            act.Should().Throw<IOException>().WithMessage("*配方备份文件*链接文件*");
            manager.CurrentRecipe.Version.Should().Be("v2");
            File.ReadAllText(recipePath).Should().Contain("\"v2\"").And.NotContain("external");
            File.ReadAllText(externalBackup).Should().Contain("external");
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void RollbackLastVersion_拒绝链接当前配方文件且不修改外部文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.Save(new Recipe { RecipeId = "default", Version = "v1", TargetLabel = "old" });
            manager.Save(new Recipe { RecipeId = "default", Version = "v2", TargetLabel = "new" });

            string externalRecipe = Path.Combine(externalDir, "external-current.json");
            File.WriteAllText(externalRecipe, "external current");
            File.Delete(recipePath);
            if (!TryCreateFileSymbolicLink(recipePath, externalRecipe))
            {
                return;
            }

            Action act = () => manager.RollbackLastVersion();

            act.Should().Throw<IOException>().WithMessage("*链接文件*");
            manager.CurrentRecipe.Version.Should().Be("v2");
            File.ReadAllText(externalRecipe).Should().Be("external current");
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
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
    public void TryLoadVersion_拒绝历史指向目录外快照()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.SaveNewVersion(new AppConfig { TargetLabel = "local" }, null, "op", "Engineer", "local");

            string externalSnapshot = Path.Combine(externalDir, "external-snapshot.json");
            File.WriteAllText(externalSnapshot, """
            {
              "RecipeId": "default",
              "Version": "external-v1",
              "TargetLabel": "external"
            }
            """);
            File.WriteAllText(manager.HistoryPath, $$"""
            [
              {
                "RecipeId": "default",
                "Version": "external-v1",
                "CreatedAt": "2026-07-05T00:00:00+00:00",
                "OperatorId": "attacker",
                "OperatorRole": "Engineer",
                "ChangeSummary": "escape",
                "SnapshotPath": {{JsonSerializer.Serialize(externalSnapshot)}}
              }
            ]
            """);

            bool loaded = manager.TryLoadVersion("default", "external-v1", out Recipe recipe, out string error);

            loaded.Should().BeFalse();
            error.Should().Contain("outside Versions directory");
            recipe.TargetLabel.Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void TryLoadVersion_拒绝链接版本快照文件()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.SaveNewVersion(new AppConfig { TargetLabel = "local" }, null, "op", "Engineer", "local");

            string externalSnapshot = Path.Combine(externalDir, "external-snapshot.json");
            File.WriteAllText(externalSnapshot, """
            {
              "RecipeId": "default",
              "Version": "linked-v1",
              "TargetLabel": "external"
            }
            """);
            string linkedSnapshot = Path.Combine(manager.VersionsDirectory, "linked-v1.json");
            if (!TryCreateFileSymbolicLink(linkedSnapshot, externalSnapshot))
            {
                return;
            }

            File.WriteAllText(manager.HistoryPath, $$"""
            [
              {
                "RecipeId": "default",
                "Version": "linked-v1",
                "CreatedAt": "2026-07-05T00:00:00+00:00",
                "OperatorId": "attacker",
                "OperatorRole": "Engineer",
                "ChangeSummary": "linked",
                "SnapshotPath": {{JsonSerializer.Serialize(linkedSnapshot)}}
              }
            ]
            """);

            bool loaded = manager.TryLoadVersion("default", "linked-v1", out Recipe recipe, out string error);

            loaded.Should().BeFalse();
            error.Should().Contain("链接文件");
            recipe.TargetLabel.Should().BeEmpty();
            File.ReadAllText(externalSnapshot).Should().Contain("external");
        }
        finally
        {
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
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

    [Fact]
    public void RestoreTransactionSnapshot_事务前文件不存在_恢复后仍不存在()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            RecipeTransactionSnapshot snapshot = manager.CaptureTransactionSnapshot();

            manager.SaveNewVersionForActivationTransaction(
                new AppConfig { TargetLabel = "new" },
                null,
                "op",
                "Engineer",
                "transaction",
                snapshot);

            File.Exists(recipePath).Should().BeTrue();
            File.Exists(manager.HistoryPath).Should().BeTrue();
            Directory.Exists(manager.VersionsDirectory).Should().BeTrue();

            IReadOnlyList<string> failures = manager.RestoreTransactionSnapshot(snapshot);

            failures.Should().BeEmpty();
            File.Exists(recipePath).Should().BeFalse();
            File.Exists(manager.BackupPath).Should().BeFalse();
            File.Exists(manager.HistoryPath).Should().BeFalse();
            File.Exists(manager.HistoryPath + ".bak").Should().BeFalse();
            Directory.Exists(manager.VersionsDirectory).Should().BeFalse();
            manager.CurrentRecipe.TargetLabel.Should().Be(snapshot.CurrentRecipe.TargetLabel);
            Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Should()
                .NotContain(name =>
                    name!.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bak.bak", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void CaptureTransactionSnapshot_跳过链接版本子目录且不捕获外部快照()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDirectory = string.Empty;
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.SaveNewVersion(new AppConfig { TargetLabel = "local" }, null, "op", "Engineer", "local");

            string externalSnapshot = Path.Combine(externalDir, "external-v1.json");
            File.WriteAllText(externalSnapshot, """
            {
              "RecipeId": "default",
              "Version": "external-v1",
              "TargetLabel": "external"
            }
            """);
            linkedDirectory = Path.Combine(manager.VersionsDirectory, "linked-external");
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDir))
            {
                return;
            }

            RecipeTransactionSnapshot snapshot = manager.CaptureTransactionSnapshot();

            snapshot.VersionFiles.Keys.Should().Contain(key => key.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            snapshot.VersionFiles.Keys.Should().NotContain(key => key.Contains("linked-external", StringComparison.OrdinalIgnoreCase));
            snapshot.VersionFiles.Keys.Should().NotContain(key => key.EndsWith("external-v1.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectoryLink(linkedDirectory);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void CaptureTransactionSnapshot_忽略Versions目录下链接恢复产物()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedDirectory = string.Empty;
        try
        {
            string recipePath = Path.Combine(tempDir, "default_recipe.json");
            var manager = new RecipeManager(recipePath);
            manager.SaveNewVersion(new AppConfig { TargetLabel = "local" }, null, "op", "Engineer", "local");

            string externalArtifact = Path.Combine(externalDir, "evil.tmp");
            File.WriteAllText(externalArtifact, "external artifact");
            linkedDirectory = Path.Combine(manager.VersionsDirectory, "linked-artifacts");
            if (!TryCreateDirectorySymbolicLink(linkedDirectory, externalDir))
            {
                return;
            }

            RecipeTransactionSnapshot snapshot = manager.CaptureTransactionSnapshot();

            snapshot.RecoveryArtifactRelativePaths.Should().NotContain(path => path.Contains("linked-artifacts", StringComparison.OrdinalIgnoreCase));
            snapshot.RecoveryArtifactRelativePaths.Should().NotContain(path => path.EndsWith("evil.tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectoryLink(linkedDirectory);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(RecipeManagerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = File.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            FileSystemInfo link = Directory.CreateSymbolicLink(linkPath, targetPath);
            link.Refresh();
            return link.Exists && (link.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectoryLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void TryDeleteFileLink(string linkPath)
    {
        if (string.IsNullOrWhiteSpace(linkPath))
        {
            return;
        }

        try
        {
            var info = new FileInfo(linkPath);
            info.Refresh();
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            info.Refresh();
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info.Delete();
                return;
            }

            Directory.Delete(path, true);
        }
    }
}
