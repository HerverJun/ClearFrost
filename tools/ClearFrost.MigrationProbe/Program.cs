using ClearFrost.Config;
using ClearFrost.Helpers;
using ClearFrost.Core.Inspection;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
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
        string packagePath = ReadOption(args, "--v6-package") ?? string.Empty;
        string inputManifestPath = ReadOption(args, "--input-manifest") ?? Environment.GetEnvironmentVariable("CLEARFROST_V6_INPUT_MANIFEST") ?? string.Empty;
        string detectModelPath = ReadOption(args, "--detect-model") ?? string.Empty;
        string validationImagePath = ReadOption(args, "--validation-image") ?? string.Empty;
        root = Path.GetFullPath(root);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(root);
        DateTimeOffset generatedAt = DateTimeOffset.UtcNow;
        string identityRoot = FindRepositoryRoot();

        var report = new MigrationLabReport
        {
            SchemaVersion = "v6-g2-migration-lab-1.0",
            GeneratedAtUtc = generatedAt,
            Root = root,
            CommitSha = TryReadCommitSha(),
            LabType = "config-import lab",
            Identity = V6G2EvidenceIdentity.Create(
                identityRoot,
                inputManifestPath,
                detectModelPath,
                validationImagePath,
                null,
                "NOT_APPLICABLE",
                generatedAt)
        };

        report.Scenarios.Add(RunScenario("config-import-lab-valid-migration-idempotence", root, RunValidMigration));
        report.Scenarios.Add(RunScenario("config-import-lab-missing-fields", root, RunMissingFields));
        report.Scenarios.Add(RunScenario("config-import-lab-historical-path", root, RunHistoricalPath));
        report.Scenarios.Add(RunScenario("config-import-lab-corrupt-config", root, RunCorruptConfig));
        report.Scenarios.Add(RunScenario("config-import-lab-model-reference", root, RunModelReference));
        report.Scenarios.Add(RunScenario("config-import-lab-mid-migration-failure-recovery", root, RunMidMigrationFailure));
        report.Scenarios.Add(RunScenario("real-v6-upgrade-startup", root, scenarioRoot => RunRealUpgrade(scenarioRoot, packagePath)));

        report.Status = report.Scenarios.Any(item => item.Status == "BLOCKED")
            ? "BLOCKED"
            : report.Scenarios.Any(item => item.Status == "NOT_VERIFIED") ? "NOT_VERIFIED" : "PASS";
        report.Rollback = RunSnapshotRollback(root);
        if (report.Rollback.Status != "PASS")
        {
            report.Status = "BLOCKED";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? ".");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report.Status switch
        {
            "PASS" => 0,
            "NOT_VERIFIED" => 2,
            _ => 1
        };
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
            MigrationScenarioResult result = scenario(scenarioRoot);
            return new MigrationScenarioResult
            {
                Name = name,
                Status = result.Status,
                Reason = result.Reason,
                Details = result.Details
            };
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

    private static MigrationScenarioResult RunRealUpgrade(string root, string packagePath)
    {
        string v59Root = Path.Combine(root, "v5.9-snapshot");
        string runRoot = Path.Combine(root, "v6-isolated-run");
        SetRuntimeRoot(v59Root);
        PrepareCurrentState(v59Root, "V59-UPGRADE-CAMERA", "v5.9-detect.onnx");
        Snapshot before = Snapshot.Capture();
        DatabaseState beforeDatabase = ReadDatabaseState(RuntimePaths.DatabasePath);

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            before.Restore();
            return new MigrationScenarioResult
            {
                Name = "real-v6-upgrade-startup",
                Status = "NOT_VERIFIED",
                Reason = "A positive V6 package was not supplied; direct config-import lab results cannot substitute for a real package startup.",
                Details = new Dictionary<string, object?>
                {
                    ["v59ConfigAndDatabaseConstructed"] = true,
                    ["v59DatabasePath"] = RuntimePaths.DatabasePath,
                    ["v59DatabaseHash"] = beforeDatabase.FileHash,
                    ["packageRequired"] = true
                }
            };
        }

        string resolvedPackage = Path.GetFullPath(packagePath);
        string executable = FindV6Executable(resolvedPackage);
        if (!Directory.Exists(resolvedPackage) || string.IsNullOrWhiteSpace(executable))
        {
            before.Restore();
            return new MigrationScenarioResult
            {
                Name = "real-v6-upgrade-startup",
                Status = "BLOCKED",
                Reason = "The declared V6 package does not contain the production executable.",
                Details = new Dictionary<string, object?> { ["packagePath"] = resolvedPackage }
            };
        }

        string packageRunRoot = Path.Combine(runRoot, "package");
        string appDataRoot = Path.Combine(runRoot, "appdata");
        CopyDirectory(resolvedPackage, packageRunRoot);
        CopyDirectory(Path.Combine(v59Root, "Config"), Path.Combine(appDataRoot, "Config"));
        CopyDirectory(Path.Combine(v59Root, "Data"), Path.Combine(appDataRoot, "Data"));
        RewriteIsolatedStoragePath(Path.Combine(appDataRoot, "Config", "config.json"), Path.Combine(appDataRoot, "Data"));

        ProcessRunResult firstRun = RunPackagedV6(executable, packageRunRoot, appDataRoot);
        DatabaseState firstDatabase = ReadDatabaseState(Path.Combine(appDataRoot, "Data", "detection.db"), requireV6Schema: true);
        bool firstStartup = firstRun.Started && firstRun.NormalExit &&
            File.Exists(Path.Combine(appDataRoot, "Logs", "startup.log"));

        ProcessRunResult secondRun = RunPackagedV6(executable, packageRunRoot, appDataRoot);
        DatabaseState secondDatabase = ReadDatabaseState(Path.Combine(appDataRoot, "Data", "detection.db"), requireV6Schema: true);
        bool secondStartup = secondRun.Started && secondRun.NormalExit;
        bool idempotent = firstDatabase.Valid && secondDatabase.Valid &&
            string.Equals(firstDatabase.FileHash, secondDatabase.FileHash, StringComparison.OrdinalIgnoreCase) &&
            firstDatabase.RecordCount == secondDatabase.RecordCount &&
            string.Equals(firstDatabase.RepresentativeInspectionId, secondDatabase.RepresentativeInspectionId, StringComparison.Ordinal);

        SetRuntimeRoot(v59Root);
        before.Restore();
        bool rollback = before.Equals(Snapshot.Capture());
        DatabaseState rollbackDatabase = ReadDatabaseState(RuntimePaths.DatabasePath);
        bool rollbackContent = rollback && rollbackDatabase.Valid &&
            string.Equals(beforeDatabase.FileHash, rollbackDatabase.FileHash, StringComparison.OrdinalIgnoreCase) &&
            beforeDatabase.RecordCount == rollbackDatabase.RecordCount &&
            string.Equals(beforeDatabase.RepresentativeInspectionId, rollbackDatabase.RepresentativeInspectionId, StringComparison.Ordinal);

        return new MigrationScenarioResult
        {
            Name = "real-v6-upgrade-startup",
            Status = firstStartup && secondStartup && idempotent && rollbackContent ? "PASS" : "BLOCKED",
            Reason = firstStartup && secondStartup && idempotent && rollbackContent
                ? "The production V6 package started, closed normally, restarted idempotently, preserved SQLite schema and records, and rolled back to the V5.9 snapshot."
                : "The real V6 package startup, idempotence, SQLite validation, or rollback contract failed.",
            Details = new Dictionary<string, object?>
            {
                ["packagePath"] = resolvedPackage,
                ["firstStartup"] = firstStartup,
                ["firstRun"] = firstRun,
                ["secondStartup"] = secondStartup,
                ["secondRun"] = secondRun,
                ["beforeDatabase"] = beforeDatabase,
                ["firstDatabase"] = firstDatabase,
                ["secondDatabase"] = secondDatabase,
                ["idempotent"] = idempotent,
                ["rollbackHashMatch"] = rollback,
                ["rollbackDatabase"] = rollbackDatabase,
                ["rollbackContentMatch"] = rollbackContent
            }
        };
    }

    private static string FindV6Executable(string packagePath)
    {
        if (!Directory.Exists(packagePath))
        {
            return string.Empty;
        }

        string expected = Path.Combine(packagePath, "清霜视觉.exe");
        if (File.Exists(expected))
        {
            return expected;
        }

        return Directory.EnumerateFiles(packagePath, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? string.Empty;
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {source}");
        }

        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RewriteIsolatedStoragePath(string configPath, string storagePath)
    {
        JsonNode? node = JsonNode.Parse(File.ReadAllText(configPath));
        if (node is not JsonObject config)
        {
            throw new InvalidOperationException("V5.9 config snapshot is not a JSON object.");
        }
        config["StoragePath"] = storagePath;
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ProcessRunResult RunPackagedV6(string executable, string workingDirectory, string appDataRoot)
    {
        string logRoot = Path.Combine(appDataRoot, "Logs");
        Directory.CreateDirectory(logRoot);
        string stdoutPath = Path.Combine(logRoot, "migration-probe.stdout.log");
        string stderrPath = Path.Combine(logRoot, "migration-probe.stderr.log");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["CLEARFROST_APPDATA_ROOT"] = appDataRoot;
        startInfo.Environment["CLEARFROST_DML_PROFILE_ROOT"] = Path.Combine(appDataRoot, "Profiles");
        try
        {
            using Process process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return new ProcessRunResult { Started = false, NormalExit = false, ExitCode = -1 };
            }
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            bool exited = process.WaitForExit(15000);
            bool forced = false;
            if (!exited)
            {
                try
                {
                    process.CloseMainWindow();
                    exited = process.WaitForExit(5000);
                }
                catch
                {
                }
            }
            if (!exited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    forced = true;
                    process.WaitForExit(5000);
                }
                catch
                {
                }
            }
            File.WriteAllText(stdoutPath, stdout.GetAwaiter().GetResult());
            File.WriteAllText(stderrPath, stderr.GetAwaiter().GetResult());
            return new ProcessRunResult
            {
                Started = true,
                NormalExit = exited && !forced && process.ExitCode == 0,
                ExitCode = exited ? process.ExitCode : null,
                ForcedTermination = forced,
                StdoutPath = stdoutPath,
                StderrPath = stderrPath
            };
        }
        catch (Exception ex)
        {
            File.WriteAllText(stderrPath, ex.ToString());
            return new ProcessRunResult { Started = false, NormalExit = false, ExitCode = -1, StderrPath = stderrPath };
        }
    }

    private static DatabaseState ReadDatabaseState(string path, bool requireV6Schema = false)
    {
        if (!File.Exists(path))
        {
            return new DatabaseState { FileHash = string.Empty, Valid = false };
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            bool integrityCheckPassed = string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase);
            bool schemaValid = HasRequiredV6Schema(connection);
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM DetectionRecords;";
            int recordCount = Convert.ToInt32(count.ExecuteScalar());
            using SqliteCommand sample = connection.CreateCommand();
            sample.CommandText = "SELECT InspectionId FROM DetectionRecords ORDER BY Id LIMIT 1;";
            string inspectionId = Convert.ToString(sample.ExecuteScalar()) ?? string.Empty;
            return new DatabaseState
            {
                FileHash = ComputeFileHash(path),
                Valid = integrityCheckPassed && (!requireV6Schema || schemaValid),
                IntegrityCheckPassed = integrityCheckPassed,
                SchemaValid = schemaValid,
                RecordCount = recordCount,
                RepresentativeInspectionId = inspectionId
            };
        }
        catch
        {
            return new DatabaseState { FileHash = ComputeFileHash(path), Valid = false };
        }
    }

    private static bool HasRequiredV6Schema(SqliteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(DetectionRecords);";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        string[] requiredColumns =
        {
            "Id", "Timestamp", "IsQualified", "InspectionId", "TriggerSource", "TriggerSeq", "ResultSeq",
            "CycleSucceeded", "TraceStatus", "QueueStatus", "ImagePath", "RenderedImagePath", "TraceImagePath",
            "ErrorStage", "ErrorCode", "ErrorMessage", "TotalMs", "SaveRecordMs", "ModelName", "ResultJson"
        };
        return requiredColumns.All(columns.Contains);
    }

    private static string ComputeFileHash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
        CreateV59DatabaseSnapshot(RuntimePaths.DatabasePath);
    }

    private static void CreateV59DatabaseSnapshot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE DetectionRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    IsQualified INTEGER NOT NULL,
                    InspectionId TEXT,
                    TriggerSource TEXT,
                    CycleSucceeded INTEGER,
                    TraceStatus TEXT,
                    QueueStatus TEXT,
                    ImagePath TEXT,
                    RenderedImagePath TEXT,
                    TraceImagePath TEXT,
                    ErrorCode TEXT,
                    ErrorMessage TEXT,
                    TotalMs INTEGER,
                    ModelName TEXT,
                    ResultJson TEXT
                );
                CREATE INDEX idx_v59_inspection_id ON DetectionRecords(InspectionId);
            ";
            command.ExecuteNonQuery();
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"
                INSERT INTO DetectionRecords
                    (Timestamp, IsQualified, InspectionId, TriggerSource, CycleSucceeded,
                     TraceStatus, QueueStatus, ImagePath, RenderedImagePath, TraceImagePath,
                     ErrorCode, ErrorMessage, TotalMs, ModelName, ResultJson)
                VALUES
                    ($timestamp, 1, $inspectionId, 'PLC', 1, 'Full', 'saved',
                     $imagePath, $renderedImagePath, $traceImagePath, '', '', 42,
                      'v5.9-detect.onnx', '{""result"":""OK""}');
            ";
            command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            command.Parameters.AddWithValue("$inspectionId", "V59-SAMPLE-0001");
            command.Parameters.AddWithValue("$imagePath", "images/V59-SAMPLE-0001.jpg");
            command.Parameters.AddWithValue("$renderedImagePath", "images/V59-SAMPLE-0001-rendered.jpg");
            command.Parameters.AddWithValue("$traceImagePath", "images/V59-SAMPLE-0001-rendered.jpg");
            command.ExecuteNonQuery();
        }
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

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return Directory.GetCurrentDirectory();
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
    public string LabType { get; init; } = "config-import lab";
    public V6G2EvidenceIdentity Identity { get; init; } = new V6G2EvidenceIdentity();
    public string Status { get; set; } = "BLOCKED";
    public List<MigrationScenarioResult> Scenarios { get; } = new();
    public RollbackResult Rollback { get; set; } = new();
}

internal sealed class DatabaseState
{
    public string FileHash { get; init; } = string.Empty;
    public bool Valid { get; init; }
    public bool IntegrityCheckPassed { get; init; }
    public bool SchemaValid { get; init; }
    public int RecordCount { get; init; }
    public string RepresentativeInspectionId { get; init; } = string.Empty;
}

internal sealed class ProcessRunResult
{
    public bool Started { get; init; }
    public bool NormalExit { get; init; }
    public int? ExitCode { get; init; }
    public bool ForcedTermination { get; init; }
    public string StdoutPath { get; init; } = string.Empty;
    public string StderrPath { get; init; } = string.Empty;
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
