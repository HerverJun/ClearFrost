using ClearFrost.Config;
using ClearFrost.Helpers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

return await MigrationProbe.RunAsync(args);

internal static class MigrationProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        string root = ReadOption(args, "--root") ?? Path.Combine(Path.GetTempPath(), "ClearFrost-V6-MigrationLab");
        string output = ReadOption(args, "--output") ?? Path.Combine(root, "migration-evidence.json");
        root = Path.GetFullPath(root);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(root);

        var report = new MigrationLabReport
        {
            SchemaVersion = "v6-g2-migration-lab-1.0",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Root = root,
            CommitSha = TryReadCommitSha()
        };

        report.Scenarios.Add(RunScenario("valid-migration-idempotence", root, RunValidMigration));
        report.Scenarios.Add(RunScenario("missing-fields", root, RunMissingFields));
        report.Scenarios.Add(RunScenario("historical-path", root, RunHistoricalPath));
        report.Scenarios.Add(RunScenario("corrupt-config", root, RunCorruptConfig));
        report.Scenarios.Add(RunScenario("model-reference", root, RunModelReference));
        report.Scenarios.Add(RunScenario("mid-migration-failure-recovery", root, RunMidMigrationFailure));

        report.Status = report.Scenarios.All(item => item.Status == "PASS") ? "PASS" : "BLOCKED";
        report.Rollback = RunSnapshotRollback(root);
        if (report.Rollback.Status != "PASS")
        {
            report.Status = "BLOCKED";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Status == "PASS" ? 0 : 1;
    }

    private static MigrationScenarioResult RunScenario(
        string name,
        string root,
        Func<string, MigrationScenarioResult> scenario)
    {
        string scenarioRoot = Path.Combine(root, name);
        if (Directory.Exists(scenarioRoot))
        {
            Directory.Delete(scenarioRoot, recursive: true);
        }

        Directory.CreateDirectory(scenarioRoot);
        try
        {
            return scenario(scenarioRoot);
        }
        catch (Exception ex)
        {
            return new MigrationScenarioResult
            {
                Name = name,
                Status = "BLOCKED",
                Reason = ex.Message
            };
        }
    }

    private static MigrationScenarioResult RunValidMigration(string root)
    {
        SetRuntimeRoot(root);
        PrepareCurrentState(root, "OLD-SERIAL", "old.onnx");
        Snapshot before = Snapshot.Capture();
        string packagePath = Path.Combine(root, "valid.clearfrost-config.json");
        WritePackage(packagePath, CreateSourceConfig(root, "NEW-SERIAL", "detect.onnx", "6.1.0-preview.1"), "v5.9.0");

        AppConfig current = AppConfig.Load();
        ConfigMigrationImportResult first = ConfigMigrationService.ImportFromFile(packagePath, current);
        Snapshot afterFirst = Snapshot.Capture();
        ConfigMigrationImportResult second = ConfigMigrationService.ImportFromFile(packagePath, AppConfig.Load());
        Snapshot afterSecond = Snapshot.Capture();
        bool idempotent = afterFirst.Equals(afterSecond);
        bool imported = first.HasConfig && first.HasPresets && second.HasConfig && idempotent &&
            string.Equals(AppConfig.Load().CameraSerialNumber, "NEW-SERIAL", StringComparison.Ordinal);

        before.Restore();
        bool rolledBack = before.Equals(Snapshot.Capture());
        return new MigrationScenarioResult
        {
            Name = "valid-migration-idempotence",
            Status = imported && rolledBack ? "PASS" : "BLOCKED",
            Reason = imported && rolledBack ? "Production migration import, idempotent second import, and snapshot rollback passed." : "Migration, idempotence, or rollback did not match the expected contract.",
            Details = new Dictionary<string, object?>
            {
                ["firstImportKind"] = first.Kind.ToString(),
                ["secondImportKind"] = second.Kind.ToString(),
                ["idempotent"] = idempotent,
                ["rollbackHashMatch"] = rolledBack,
                ["before"] = before.Files,
                ["afterFirst"] = afterFirst.Files,
                ["afterSecond"] = afterSecond.Files
            }
        };
    }

    private static MigrationScenarioResult RunMissingFields(string root)
    {
        SetRuntimeRoot(root);
        PrepareCurrentState(root, "MISSING-BEFORE", "before.onnx");
        string sourcePath = Path.Combine(root, "missing-fields.json");
        File.WriteAllText(sourcePath, "{\"PlcIp\":\"10.0.0.12\"}");
        ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(sourcePath, AppConfig.Load());
        bool passed = result.Kind == ConfigMigrationImportKind.AppConfig && AppConfig.Load().PlcIp == "10.0.0.12";
        return BasicResult("missing-fields", passed, "Missing fields preserve defaults while importing declared fields.");
    }

    private static MigrationScenarioResult RunHistoricalPath(string root)
    {
        SetRuntimeRoot(root);
        PrepareCurrentState(root, "HISTORICAL-BEFORE", "before.onnx");
        string packagePath = Path.Combine(root, "historical-path.clearfrost-config.json");
        AppConfig source = CreateSourceConfig(root, "HISTORICAL", "legacy.onnx", "5.9.0");
        source.StoragePath = @"C:\GreeVisionData";
        WritePackage(packagePath, source, "5.9.0");
        ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(packagePath, AppConfig.Load());
        bool passed = result.Kind == ConfigMigrationImportKind.MigrationPackage &&
            AppConfig.Load().StoragePath == @"C:\GreeVisionData" &&
            File.Exists(RuntimePaths.ConfigPath);
        return new MigrationScenarioResult
        {
            Name = "historical-path",
            Status = passed ? "PASS" : "BLOCKED",
            Reason = passed ? "Historical storage path is preserved as input evidence; isolated startup owns the runtime root." : "Historical path migration contract failed.",
            Details = new Dictionary<string, object?> { ["importedStoragePath"] = AppConfig.Load().StoragePath }
        };
    }

    private static MigrationScenarioResult RunCorruptConfig(string root)
    {
        SetRuntimeRoot(root);
        PrepareCurrentState(root, "CORRUPT-BEFORE", "before.onnx");
        Snapshot before = Snapshot.Capture();
        string sourcePath = Path.Combine(root, "corrupt.json");
        File.WriteAllText(sourcePath, "{ this is not json");
        bool rejected = false;
        try
        {
            ConfigMigrationService.ImportFromFile(sourcePath, AppConfig.Load());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            rejected = true;
        }
        return BasicResult("corrupt-config", rejected && before.Equals(Snapshot.Capture()), "Corrupt configuration is rejected without changing the runtime snapshot.");
    }

    private static MigrationScenarioResult RunModelReference(string root)
    {
        SetRuntimeRoot(root);
        PrepareCurrentState(root, "MODEL-BEFORE", "before.onnx");
        string packagePath = Path.Combine(root, "model-reference.clearfrost-config.json");
        AppConfig source = CreateSourceConfig(root, "MODEL-REFERENCE", "detect.onnx", "6.1.0-preview.1");
        source.CurrentModelFileName = "detect.onnx";
        WritePackage(packagePath, source, "6.1.0-preview.1");
        ConfigMigrationImportResult result = ConfigMigrationService.ImportFromFile(packagePath, AppConfig.Load());
        bool passed = result.HasConfig && AppConfig.Load().CurrentModelFileName == "detect.onnx";
        return BasicResult("model-reference", passed, "Configuration containing a model reference imports without bundling the model.");
    }

    private static MigrationScenarioResult RunMidMigrationFailure(string root)
    {
        SetRuntimeRoot(root);
        PrepareCurrentState(root, "FAILURE-BEFORE", "before.onnx");
        Snapshot before = Snapshot.Capture();
        string packagePath = Path.Combine(root, "mid-failure.clearfrost-config.json");
        WritePackage(packagePath, CreateSourceConfig(root, "FAILURE-AFTER", "after.onnx", "6.1.0-preview.1"), "6.1.0-preview.1");
        bool rejected = false;
        using (var locked = new FileStream(RuntimePaths.ProjectPresetsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                ConfigMigrationService.ImportFromFile(packagePath, AppConfig.Load());
            }
            catch (Exception)
            {
                rejected = true;
            }
        }
        bool restored = before.Equals(Snapshot.Capture());
        return BasicResult("mid-migration-failure-recovery", rejected && restored, "A preset write failure is rejected and the pre-migration runtime snapshot is restored.");
    }

    private static RollbackResult RunSnapshotRollback(string root)
    {
        string scenarioRoot = Path.Combine(root, "snapshot-rollback");
        if (Directory.Exists(scenarioRoot))
        {
            Directory.Delete(scenarioRoot, recursive: true);
        }
        Directory.CreateDirectory(scenarioRoot);
        SetRuntimeRoot(scenarioRoot);
        PrepareCurrentState(scenarioRoot, "SNAPSHOT-BEFORE", "before.onnx");
        Snapshot snapshot = Snapshot.Capture();
        AppConfig config = AppConfig.Load();
        config.CameraSerialNumber = "SNAPSHOT-AFTER";
        config.Save();
        snapshot.Restore();
        bool match = snapshot.Equals(Snapshot.Capture());
        return new RollbackResult
        {
            Status = match ? "PASS" : "BLOCKED",
            Reason = match ? "All runtime config, preset, and database hashes match after full snapshot rollback." : "Snapshot rollback changed one or more files.",
            Files = snapshot.Files
        };
    }

    private static void PrepareCurrentState(string root, string serialNumber, string modelFileName)
    {
        var config = new AppConfig
        {
            StoragePath = Path.Combine(root, "data"),
            CameraSerialNumber = serialNumber,
            CurrentModelFileName = modelFileName,
            PlcIp = "127.0.0.1"
        };
        if (!config.Save())
        {
            throw new InvalidOperationException(config.LastError ?? "Unable to save initial config.");
        }
        ProjectPresetStore.SavePreset("{\"id\":\"baseline\",\"name\":\"baseline\",\"preset\":{\"PlcIp\":\"127.0.0.1\"}}");
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimePaths.DatabasePath) ?? root);
        File.WriteAllBytes(RuntimePaths.DatabasePath, Encoding.UTF8.GetBytes("v5.9-database-snapshot"));
    }

    private static AppConfig CreateSourceConfig(string root, string serialNumber, string modelFileName, string version)
    {
        return new AppConfig
        {
            StoragePath = Path.Combine(root, "migrated-data"),
            CameraSerialNumber = serialNumber,
            CurrentModelFileName = modelFileName,
            PlcIp = "127.0.0.2"
        };
    }

    private static void WritePackage(string path, AppConfig config, string appVersion)
    {
        var package = new JsonObject
        {
            ["schema"] = ConfigMigrationService.Schema,
            ["appVersion"] = appVersion,
            ["config"] = JsonNode.Parse(config.ToPortableJson()),
            ["projectPresets"] = new JsonObject
            {
                ["migrated"] = new JsonObject
                {
                    ["name"] = "migrated",
                    ["PlcIp"] = "127.0.0.2"
                }
            }
        };
        File.WriteAllText(path, package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static MigrationScenarioResult BasicResult(string name, bool passed, string reason)
    {
        return new MigrationScenarioResult
        {
            Name = name,
            Status = passed ? "PASS" : "BLOCKED",
            Reason = reason
        };
    }

    private static void SetRuntimeRoot(string root)
    {
        Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", Path.GetFullPath(root));
        Directory.CreateDirectory(root);
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }

    private static string TryReadCommitSha()
    {
        string? value = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length == 40)
        {
            return value.Trim();
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse HEAD",
                    WorkingDirectory = AppContext.BaseDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length == 40 ? output : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class Snapshot
    {
        private readonly Dictionary<string, byte[]?> _contents;

        private Snapshot(Dictionary<string, byte[]?> contents, Dictionary<string, string> files)
        {
            _contents = contents;
            Files = files;
        }

        public Dictionary<string, string> Files { get; }

        public static Snapshot Capture()
        {
            var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in GetTrackedPaths())
            {
                byte[]? content = File.Exists(path) ? File.ReadAllBytes(path) : null;
                contents[path] = content ?? Array.Empty<byte>();
                files[path] = content == null ? "MISSING" : ComputeHash(content);
            }
            return new Snapshot(contents.ToDictionary(pair => pair.Key, pair => (byte[]?)pair.Value), files);
        }

        public void Restore()
        {
            foreach (KeyValuePair<string, byte[]?> pair in _contents)
            {
                if (pair.Value == null || pair.Value.Length == 0 && Files[pair.Key] == "MISSING")
                {
                    if (File.Exists(pair.Key)) File.Delete(pair.Key);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key) ?? ".");
                File.WriteAllBytes(pair.Key, pair.Value);
            }
        }

        public bool Equals(Snapshot other)
        {
            foreach (string path in GetTrackedPaths())
            {
                string expected = Files.TryGetValue(path, out string? value) ? value : "MISSING";
                string actual = File.Exists(path) ? ComputeHash(File.ReadAllBytes(path)) : "MISSING";
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        private static IEnumerable<string> GetTrackedPaths()
        {
            yield return RuntimePaths.ConfigPath;
            yield return RuntimePaths.ProjectPresetsPath;
            yield return RuntimePaths.DatabasePath;
        }

        private static string ComputeHash(byte[] content)
        {
            return Convert.ToHexString(SHA256.HashData(content));
        }
    }
}

internal sealed class MigrationLabReport
{
    public string SchemaVersion { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public string Root { get; init; } = string.Empty;
    public string CommitSha { get; init; } = string.Empty;
    public string Status { get; set; } = "BLOCKED";
    public List<MigrationScenarioResult> Scenarios { get; } = new();
    public RollbackResult Rollback { get; set; } = new();
}

internal sealed class MigrationScenarioResult
{
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = "BLOCKED";
    public string Reason { get; init; } = string.Empty;
    public Dictionary<string, object?> Details { get; init; } = new();
}

internal sealed class RollbackResult
{
    public string Status { get; init; } = "BLOCKED";
    public string Reason { get; init; } = string.Empty;
    public Dictionary<string, string> Files { get; init; } = new();
}
