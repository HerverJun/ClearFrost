using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClearFrost;
using ClearFrost.Config;
using ClearFrost.Core.Inspection;
using ClearFrost.Hardware;
using ClearFrost.Hardware.Triggers;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Yolo;
using Microsoft.Data.Sqlite;
using OpenCvSharp;

return await SoakHost.RunAsync(args);

internal static class SoakHost
{
    private const string SchemaVersion = "v6-g2-soak-1.0";
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        SoakOptions options;
        try
        {
            options = SoakOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            SoakOptions.PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            SoakOptions.PrintUsage();
            return 0;
        }

        SoakEvidence evidence = SoakEvidence.Create(options, TryReadCommitSha(options.Root));
        try
        {
            ExternalInputContract input = ExternalInputContract.Load(options);
            evidence.InputContract = input.ToEvidence();
            evidence.ScenarioContract = input.ScenarioContract.ToEvidence();
            evidence.ScenarioCoverageStatus = input.ScenarioContract.Status;
            if (input.ScenarioContract.Status != "PASS")
            {
                evidence.BlockingReasons.AddRange(input.ScenarioContract.BlockingReasons);
                evidence.NotVerifiedReasons.AddRange(input.ScenarioContract.NotVerifiedReasons);
                if (input.ScenarioContract.BlockingReasons.Count > 0)
                {
                    evidence.Status = "BLOCKED";
                }
            }
            if (input.Status != "PASS")
            {
                evidence.Status = input.Status;
                evidence.PromotionEligibility = "BLOCKED";
                evidence.BlockingReasons.AddRange(input.BlockingReasons);
                evidence.NotVerifiedReasons.AddRange(input.NotVerifiedReasons);
                evidence.Complete();
                await WriteEvidenceAsync(options.OutputPath, evidence).ConfigureAwait(false);
                return input.Status == "BLOCKED" ? 1 : 2;
            }

            evidence.Model = input.Model.ToEvidence();
            evidence.ValidationImage = input.Image.ToEvidence();

            string originalAppDataRoot = Environment.GetEnvironmentVariable("CLEARFROST_APPDATA_ROOT") ?? string.Empty;
            string originalProfileRoot = Environment.GetEnvironmentVariable("CLEARFROST_DML_PROFILE_ROOT") ?? string.Empty;
            string runtimeRoot = ResolveRuntimeRoot(options);
            Directory.CreateDirectory(runtimeRoot);
            string appDataRoot = Path.Combine(runtimeRoot, "appdata");
            string storageRoot = Path.Combine(runtimeRoot, "storage");
            string profileRoot = Path.Combine(appDataRoot, "Profiles");
            Directory.CreateDirectory(appDataRoot);
            Directory.CreateDirectory(storageRoot);
            Directory.CreateDirectory(profileRoot);
            Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", appDataRoot);
            Environment.SetEnvironmentVariable("CLEARFROST_DML_PROFILE_ROOT", profileRoot);
            evidence.Runtime = new RuntimeEvidence
            {
                Root = runtimeRoot,
                AppDataRoot = appDataRoot,
                StorageRoot = storageRoot,
                ProfileRoot = profileRoot,
                DatabasePath = Path.Combine(storageRoot, "detection.db"),
                ConfigPath = Path.Combine(appDataRoot, "Config", "config.json"),
                ProcessId = Environment.ProcessId,
                BaselineThreadCount = ExternalFileIdentity.GetCurrentThreadCount(),
                IsolatedAppData = string.Equals(
                    Path.GetFullPath(appDataRoot).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(runtimeRoot).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFullPath(appDataRoot).StartsWith(Path.GetFullPath(runtimeRoot), StringComparison.OrdinalIgnoreCase),
                IsolatedStorage = Path.GetFullPath(storageRoot).StartsWith(
                    Path.GetFullPath(runtimeRoot),
                    StringComparison.OrdinalIgnoreCase),
                SourceTreeReferenced = false,
                DevelopmentAppDataReferenced = false
            };

            try
            {
                await RunProductionGraphAsync(options, input, evidence, storageRoot).ConfigureAwait(false);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CLEARFROST_APPDATA_ROOT", originalAppDataRoot);
                Environment.SetEnvironmentVariable("CLEARFROST_DML_PROFILE_ROOT", originalProfileRoot);
            }
        }
        catch (Exception ex)
        {
            evidence.Status = "BLOCKED";
            evidence.PromotionEligibility = "BLOCKED";
            evidence.BlockingReasons.Add($"Soak host failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex);
        }

        evidence.Complete();
        await WriteEvidenceAsync(options.OutputPath, evidence).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(evidence, JsonOptions));
        return evidence.Status switch
        {
            "PASS" => 0,
            "NOT_VERIFIED" => 2,
            _ => 1
        };
    }

    private static async Task RunProductionGraphAsync(
        SoakOptions options,
        ExternalInputContract input,
        SoakEvidence evidence,
        string storageRoot)
    {
        YoloExportProbeReport descriptorReport = YoloExportProbe.Inspect(
            input.Model.Path,
            requestedYoloVersion: 0,
            preprocessingMode: YoloPreprocessingMode.StandardLetterBox,
            requestedTaskMode: YoloTaskType.Detect);
        evidence.ModelDescriptor = descriptorReport.Descriptor;
        if (!descriptorReport.Descriptor.IsSupported)
        {
            evidence.Status = "BLOCKED";
            evidence.BlockingReasons.Add($"The external Detect model is not supported: {descriptorReport.Descriptor.SupportMessage}");
            evidence.PromotionEligibility = "BLOCKED";
            return;
        }

        var faultPlan = new FaultPlan(options.Seed, options.EnableFaultInjection);
        var camera = new SoakCameraService(input.Image.Path, faultPlan);
        var plc = new SoakPlcService(faultPlan);
        var database = new FaultInjectingSqliteDatabaseService(
            Path.Combine(storageRoot, "detection.db"),
            faultPlan);
        var storage = new StorageService(storageRoot);
        var statistics = new StatisticsService(storageRoot);
        ImageSaveQueue? imageQueue = null;
        DetectionRecordQueue? recordQueue = null;
        AppRuntime? runtime = null;

        try
        {
            imageQueue = new ImageSaveQueue(
                options.ImageQueueCapacity,
                options.ImageQueueMaxBytes,
                payload => WriteSoakImage(payload, faultPlan, options.ImageWriteDelayMs));
            recordQueue = new DetectionRecordQueue(database, options.RecordQueueCapacity);

            var config = CreateSoakConfig(options, input.Model.Path, storageRoot);
            var cameraManager = new CameraManager();
            runtime = new AppRuntime(
                config,
                cameraManager,
                camera,
                plc,
                null,
                storage,
                statistics,
                database,
                imageQueue,
                recordQueue,
                null);

            evidence.Startup = runtime.StartupDiagnostics.CurrentReport;
            if (!config.Save())
            {
                evidence.BlockingReasons.Add($"Isolated soak configuration could not be saved: {config.LastError}");
            }

            await runtime.DatabaseService.InitializeAsync().ConfigureAwait(false);
            bool cameraOpened = camera.Open("SOAK-BOUNDARY-CAMERA", "BoundaryAdapter");
            if (!cameraOpened)
            {
                evidence.BlockingReasons.Add($"Camera boundary could not be opened: {camera.LastError}");
            }
            camera.StartCapture();

            bool plcConnected = await plc.ConnectAsync(new PlcConnectionOptions
            {
                Protocol = "BoundaryAdapter",
                DriverProvider = "BoundaryAdapter",
                Ip = "127.0.0.1",
                Port = 0,
                TriggerAddress = config.PlcTriggerAddress
            }).ConfigureAwait(false);
            if (!plcConnected)
            {
                evidence.BlockingReasons.Add($"PLC boundary could not be connected: {plc.LastError}");
            }

            bool modelLoaded = await runtime.DetectionService.LoadModelAsync(
                input.Model.Path,
                options.UseGpu,
                options.GpuIndex).ConfigureAwait(false);
            evidence.Provider = runtime.DetectionService.RuntimeStatus;
            if (!modelLoaded)
            {
                evidence.Status = "BLOCKED";
                evidence.BlockingReasons.Add("The real external Detect model could not be loaded by DetectionService.");
                evidence.PromotionEligibility = "BLOCKED";
                return;
            }

            if (options.UseGpu && !runtime.DetectionService.RuntimeStatus.GpuActive)
            {
                evidence.Status = "BLOCKED";
                evidence.BlockingReasons.Add(
                    $"Strict DML soak was requested but actual provider was {runtime.DetectionService.RuntimeStatus.ExecutionProvider}: " +
                    runtime.DetectionService.RuntimeStatus.GpuFailureReason);
                evidence.PromotionEligibility = "BLOCKED";
                return;
            }

            var pipeline = new InspectionPipelineService(
                config,
                camera,
                runtime.DetectionService,
                plc,
                storage,
                statistics,
                imageQueue,
                recordQueue,
                runtime.RecipeManager,
                runtime.ModelRegistry,
                runtime.HealthMonitor,
                () => null,
                () => "SOAK-BOUNDARY-CAMERA",
                runtime.DecisionEvaluator,
                message => evidence.RecentLogs.Add(message));

            var runner = new ProductionGraphRunner(
                options,
                input,
                evidence,
                runtime,
                pipeline,
                camera,
                plc,
                database,
                faultPlan);
            await runner.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            if (runtime != null)
            {
                try
                {
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await runtime.StopAsync(stopCts.Token).ConfigureAwait(false);
                    evidence.Runtime.NormalShutdownCompleted = true;
                }
                catch (Exception ex)
                {
                    evidence.BlockingReasons.Add($"Runtime stop failed: {ex.Message}");
                }

                evidence.Queues = QueueEvidence.From(runtime.ImageSaveQueue, runtime.DetectionRecordQueue);
                evidence.Health = runtime.HealthMonitor.GetSnapshot();
                await runtime.DisposeAsync().ConfigureAwait(false);
                ExternalFileIdentity.VerifyShutdownResources(evidence, runtime);
            }
            else
            {
                recordQueue?.Dispose();
                imageQueue?.Dispose();
                database.Dispose();
                statistics.Dispose();
                storage.Dispose();
                camera.Dispose();
                plc.Dispose();
            }
        }
    }

    private static AppConfig CreateSoakConfig(SoakOptions options, string modelPath, string storageRoot)
    {
        var camera = new CameraConfig
        {
            Id = "soak-boundary-camera",
            SerialNumber = "SOAK-BOUNDARY-CAMERA",
            DisplayName = "V6 soak boundary camera",
            Manufacturer = "BoundaryAdapter",
            IsEnabled = true
        };
        var config = new AppConfig
        {
            StoragePath = storageRoot,
            IsDebugMode = true,
            TriggerSource = TriggerSource.PLC,
            PlcProtocolMode = PlcProtocolMode.HandshakeV1,
            PlcResultAckTimeoutMs = 500,
            PlcDriverProvider = "BoundaryAdapter",
            PlcProtocol = "BoundaryAdapter",
            BarcodeEnabled = false,
            EnableGpu = options.UseGpu,
            GpuIndex = options.GpuIndex,
            CurrentModelFileName = Path.GetFileName(modelPath),
            OnnxModelDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty,
            ModelPackageDirectory = Path.Combine(storageRoot, "Models"),
            MaxRetryCount = 1,
            RetryIntervalMs = 0,
            Confidence = 0.25f,
            IouThreshold = 0.45f,
            RequireApprovedModelsForProduction = false,
            StrictModelPackageMode = false,
            Cameras = new List<CameraConfig> { camera },
            ActiveCameraId = camera.Id
        };
        return config;
    }

    private static bool WriteSoakImage(ImageSavePayload payload, FaultPlan faultPlan, int configuredDelayMs)
    {
        string path = payload.Path;
        if (path.Contains("image-save-failure", StringComparison.OrdinalIgnoreCase) &&
            faultPlan.TryFailImage(path))
        {
            return false;
        }

        int delayMs = configuredDelayMs;
        if (path.Contains("queue-pressure", StringComparison.OrdinalIgnoreCase))
        {
            delayMs = Math.Max(delayMs, 500);
        }
        if (delayMs > 0)
        {
            Thread.Sleep(delayMs);
        }

        string extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        Cv2.ImEncode(extension, payload.Image, out byte[] encoded);
        if (encoded.Length == 0)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllBytes(path, encoded);
        return true;
    }

    private static string ResolveRuntimeRoot(SoakOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.RuntimeRoot))
        {
            return Path.GetFullPath(options.RuntimeRoot);
        }

        return Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)) ?? Path.GetTempPath(),
            "runtime");
    }

    private static async Task WriteEvidenceAsync(string path, SoakEvidence evidence)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(evidence, JsonOptions), new UTF8Encoding(false)).ConfigureAwait(false);
    }

    private static string TryReadCommitSha(string root)
    {
        string? githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(githubSha) && githubSha.Trim().Length == 40)
        {
            return githubSha.Trim();
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("rev-parse");
            process.StartInfo.ArgumentList.Add("HEAD");
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
}

internal sealed class SoakOptions
{
    public string Root { get; init; } = Directory.GetCurrentDirectory();
    public string ManifestPath { get; init; } = string.Empty;
    public string ScenarioManifestPath { get; init; } = string.Empty;
    public string ModelPath { get; init; } = string.Empty;
    public string ImagePath { get; init; } = string.Empty;
    public string ModelSha256 { get; init; } = string.Empty;
    public long ModelBytes { get; init; }
    public string ImageSha256 { get; init; } = string.Empty;
    public long ImageBytes { get; init; }
    public string OutputPath { get; init; } = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "v6-g2", "soak", "soak-evidence.json");
    public string RuntimeRoot { get; init; } = string.Empty;
    public int Seed { get; init; } = 20260802;
    public int PreflightCycles { get; init; } = 100;
    public int Cycles { get; init; }
    public double DurationMinutes { get; init; }
    public int SampleEvery { get; init; } = 10;
    public bool UseGpu { get; init; }
    public int GpuIndex { get; init; }
    public bool EnableFaultInjection { get; init; } = true;
    public int ImageQueueCapacity { get; init; } = 8;
    public long ImageQueueMaxBytes { get; init; } = 64L * 1024L * 1024L;
    public int RecordQueueCapacity { get; init; } = 256;
    public int ImageWriteDelayMs { get; init; }
    public bool ShowHelp { get; init; }

    public static SoakOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new SoakOptions { ShowHelp = true };
        }

        string root = ReadString(args, "--root") ?? Directory.GetCurrentDirectory();
        string output = ReadString(args, "--output") ?? Path.Combine(root, "artifacts", "v6-g2", "soak", "soak-evidence.json");
        double duration = ReadDouble(args, "--duration-minutes", 0);
        int cycles = ReadInt(args, "--cycles", 0);
        int preflight = ReadInt(args, "--preflight-cycles", 100);
        int queueCapacity = ReadInt(args, "--image-queue-capacity", 8);
        int recordCapacity = ReadInt(args, "--record-queue-capacity", 256);
        int sampleEvery = ReadInt(args, "--sample-every", 10);
        int imageDelay = ReadInt(args, "--image-write-delay-ms", 0);
        bool noFaults = HasOption(args, "--no-fault-injection");

        if (duration < 0 || cycles < 0 || preflight < 0 || queueCapacity <= 0 || recordCapacity <= 0 || sampleEvery <= 0 || imageDelay < 0)
        {
            throw new ArgumentException("Duration, cycle, queue, and sampling values must be non-negative; capacities and sampling must be positive.");
        }

        return new SoakOptions
        {
            Root = Path.GetFullPath(root),
            ManifestPath = ReadString(args, "--manifest") ?? string.Empty,
            ScenarioManifestPath = ReadString(args, "--scenario-manifest") ?? string.Empty,
            ModelPath = ReadString(args, "--model") ?? string.Empty,
            ImagePath = ReadString(args, "--image") ?? string.Empty,
            ModelSha256 = ReadString(args, "--model-sha256") ?? string.Empty,
            ModelBytes = ReadLong(args, "--model-bytes", 0),
            ImageSha256 = ReadString(args, "--image-sha256") ?? string.Empty,
            ImageBytes = ReadLong(args, "--image-bytes", 0),
            OutputPath = Path.GetFullPath(output),
            RuntimeRoot = ReadString(args, "--runtime-root") ?? string.Empty,
            Seed = ReadInt(args, "--seed", 20260802),
            PreflightCycles = preflight,
            Cycles = cycles,
            DurationMinutes = duration,
            SampleEvery = sampleEvery,
            UseGpu = HasOption(args, "--gpu"),
            GpuIndex = Math.Max(0, ReadInt(args, "--gpu-index", 0)),
            EnableFaultInjection = !noFaults,
            ImageQueueCapacity = queueCapacity,
            ImageQueueMaxBytes = Math.Max(1024, ReadLong(args, "--image-queue-max-bytes", 64L * 1024L * 1024L)),
            RecordQueueCapacity = recordCapacity,
            ImageWriteDelayMs = imageDelay,
            ShowHelp = HasOption(args, "--help") || HasOption(args, "-h")
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: ClearFrost.V6SoakHost --manifest <external-inputs.json> [options]");
        Console.WriteLine("       or: ClearFrost.V6SoakHost --model <model.onnx> --image <image> [identity options]");
        Console.WriteLine();
        Console.WriteLine("Required external input identity: --model-sha256/--model-bytes and --image-sha256/--image-bytes when using direct paths.");
        Console.WriteLine("--preflight-cycles <n>       Deterministic preflight cycles, default 100.");
        Console.WriteLine("--cycles <n>                 Main soak cycle limit; duration may be used instead.");
        Console.WriteLine("--duration-minutes <n>      Main soak duration; use 60 or 480 for the required lanes.");
        Console.WriteLine("--gpu                       Require actual DirectML execution; CPU fallback is BLOCKED.");
        Console.WriteLine("--seed <n>                  Deterministic trigger/fault seed.");
        Console.WriteLine("--output <path>             Evidence output path.");
        Console.WriteLine("--runtime-root <path>       Isolated runtime root; defaults beside the evidence file.");
        Console.WriteLine("--scenario-manifest <path>  External scenario contract with hashes and expected outcomes.");
        Console.WriteLine("--no-fault-injection        Run only the normal production graph path.");
    }

    private static bool HasOption(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(string[] args, string name)
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

    private static int ReadInt(string[] args, string name, int fallback)
    {
        string? value = ReadString(args, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : fallback;
    }

    private static long ReadLong(string[] args, string name, long fallback)
    {
        string? value = ReadString(args, name);
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : fallback;
    }

    private static double ReadDouble(string[] args, string name, double fallback)
    {
        string? value = ReadString(args, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : fallback;
    }
}

internal sealed class ExternalInputContract
{
    public string Status { get; private set; } = "NOT_VERIFIED";
    public string ManifestPath { get; private set; } = string.Empty;
    public ExternalFileIdentity Model { get; private set; } = ExternalFileIdentity.Missing("model");
    public ExternalFileIdentity Image { get; private set; } = ExternalFileIdentity.Missing("image");
    public ExternalScenarioContract ScenarioContract { get; private set; } = ExternalScenarioContract.Missing(string.Empty);
    public string Task { get; private set; } = "Detect";
    public string Opset { get; private set; } = "";
    public List<string> BlockingReasons { get; } = new List<string>();
    public List<string> NotVerifiedReasons { get; } = new List<string>();

    public static ExternalInputContract Load(SoakOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ManifestPath))
        {
            return LoadManifest(options);
        }

        var direct = new ExternalInputContract
        {
            ManifestPath = string.Empty,
            Task = "Detect",
            ScenarioContract = ExternalScenarioContract.Load(options.Root, options.ScenarioManifestPath)
        };
        if (string.IsNullOrWhiteSpace(options.ModelPath) || string.IsNullOrWhiteSpace(options.ImagePath))
        {
            direct.NotVerifiedReasons.Add("No external input manifest or explicit model/image paths were supplied.");
            return direct;
        }

        direct.Model = ValidateFile(
            options.Root,
            "model",
            options.ModelPath,
            Path.GetFileName(options.ModelPath),
            options.ModelSha256,
            options.ModelBytes,
            "explicit command-line model");
        direct.Image = ValidateFile(
            options.Root,
            "validation image",
            options.ImagePath,
            Path.GetFileName(options.ImagePath),
            options.ImageSha256,
            options.ImageBytes,
            "explicit command-line validation image");
        direct.CollectStatus();
        return direct;
    }

    public object ToEvidence()
    {
        return new
        {
            status = Status,
            manifestPath = ManifestPath,
            task = Task,
            opset = Opset,
            model = Model.ToEvidence(),
            validationImage = Image.ToEvidence(),
            scenarios = ScenarioContract.ToEvidence(),
            blockingReasons = BlockingReasons.ToArray(),
            notVerifiedReasons = NotVerifiedReasons.ToArray()
        };
    }

    private static ExternalInputContract LoadManifest(SoakOptions options)
    {
        string manifestPath = ResolvePath(options.Root, options.ManifestPath);
        var contract = new ExternalInputContract { ManifestPath = manifestPath };
        if (!File.Exists(manifestPath))
        {
            contract.NotVerifiedReasons.Add($"External input manifest is unavailable: {manifestPath}");
            return contract;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            string schema = GetString(root, "schemaVersion");
            if (schema != "v6-g2-inputs-1.0")
            {
                contract.BlockingReasons.Add("External input manifest schemaVersion must be v6-g2-inputs-1.0.");
                return contract;
            }

            JsonElement sourceManifest = root;
            string linkedManifestPath = GetString(root, "manifestPath");
            if (!string.IsNullOrWhiteSpace(linkedManifestPath) && File.Exists(ResolvePath(options.Root, linkedManifestPath)))
            {
                string linkedPath = ResolvePath(options.Root, linkedManifestPath);
                using JsonDocument linkedDocument = JsonDocument.Parse(File.ReadAllText(linkedPath));
                if (GetString(linkedDocument.RootElement, "schemaVersion") == "v6-g2-inputs-1.0")
                {
                    sourceManifest = linkedDocument.RootElement.Clone();
                    contract.ManifestPath = linkedPath;
                }
            }

            contract.ScenarioContract = string.IsNullOrWhiteSpace(options.ScenarioManifestPath)
                ? ExternalScenarioContract.FromJson(options.Root, sourceManifest, contract.ManifestPath)
                : ExternalScenarioContract.Load(options.Root, options.ScenarioManifestPath);

            JsonElement? detect = FindLane(sourceManifest, "Detect");
            if (!detect.HasValue)
            {
                contract.NotVerifiedReasons.Add("The external input manifest does not declare a Detect model.");
                return contract;
            }

            JsonElement entry = detect.Value;
            contract.Task = GetString(entry, "task");
            contract.Opset = GetString(entry, "opset");
            if (string.IsNullOrWhiteSpace(contract.Task))
            {
                contract.Task = "Detect";
            }

            bool allowed = GetBool(entry, "allowed", true);
            string modelPath = GetString(entry, "path");
            string modelFileName = GetString(entry, "fileName");
            string modelHash = FirstNonEmpty(
                GetString(entry, "expectedSha256"),
                GetString(entry, "sha256"));
            long modelBytes = FirstPositive(
                GetLong(entry, "expectedBytes"),
                GetLong(entry, "bytes"));
            string modelSource = FirstNonEmpty(GetString(entry, "source"), "manifest model input");
            contract.Model = ValidateFile(options.Root, "model", modelPath, modelFileName, modelHash, modelBytes, modelSource, allowed);

            JsonElement imageEntry = default;
            bool hasNestedImage = entry.TryGetProperty("validationImage", out imageEntry) && imageEntry.ValueKind == JsonValueKind.Object;
            string imagePath = hasNestedImage
                ? GetString(imageEntry, "path")
                : GetString(entry, "validationImagePath");
            string imageFileName = hasNestedImage
                ? GetString(imageEntry, "fileName")
                : Path.GetFileName(imagePath);
            string imageHash = hasNestedImage
                ? FirstNonEmpty(GetString(imageEntry, "expectedSha256"), GetString(imageEntry, "sha256"))
                : FirstNonEmpty(GetString(entry, "validationImageSha256"), GetString(entry, "validationImageHash"));
            long imageBytes = hasNestedImage
                ? FirstPositive(GetLong(imageEntry, "expectedBytes"), GetLong(imageEntry, "bytes"))
                : FirstPositive(GetLong(entry, "validationImageBytes"), GetLong(entry, "validationImageSize"));
            string imageSource = FirstNonEmpty(
                hasNestedImage ? GetString(imageEntry, "source") : GetString(entry, "validationImageSource"),
                "manifest validation image");

            if (string.IsNullOrWhiteSpace(imageHash) || imageBytes <= 0)
            {
                contract.NotVerifiedReasons.Add("The validation image must declare its SHA-256 and byte size.");
            }
            else
            {
                contract.Image = ValidateFile(options.Root, "validation image", imagePath, imageFileName, imageHash, imageBytes, imageSource, allowed);
            }

            if (GetString(root, "status") == "BLOCKED" || GetString(sourceManifest, "status") == "BLOCKED")
            {
                contract.BlockingReasons.Add("The external input validator marked the supplied contract BLOCKED.");
            }
            contract.CollectStatus();
            return contract;
        }
        catch (JsonException ex)
        {
            contract.BlockingReasons.Add($"External input manifest is not valid JSON: {ex.Message}");
            return contract;
        }
        catch (IOException ex)
        {
            contract.NotVerifiedReasons.Add($"External input manifest could not be read: {ex.Message}");
            return contract;
        }
    }

    private void CollectStatus()
    {
        if (BlockingReasons.Count > 0 || Model.Status == "BLOCKED" || Image.Status == "BLOCKED")
        {
            Status = "BLOCKED";
        }
        else if (NotVerifiedReasons.Count > 0 || Model.Status != "PASS" || Image.Status != "PASS")
        {
            Status = "NOT_VERIFIED";
        }
        else
        {
            Status = "PASS";
        }
    }

    private static ExternalFileIdentity ValidateFile(
        string root,
        string kind,
        string pathValue,
        string fileName,
        string expectedHash,
        long expectedBytes,
        string source,
        bool allowed = true)
    {
        string path = ResolvePath(root, pathValue);
        var identity = new ExternalFileIdentity
        {
            Kind = kind,
            Path = path,
            FileName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(path) : fileName,
            ExpectedSha256 = expectedHash.Trim().ToUpperInvariant(),
            ExpectedBytes = expectedBytes,
            Source = source
        };
        if (!allowed)
        {
            identity.Status = "NOT_VERIFIED";
            identity.Reason = $"The {kind} input is not allowed by the external manifest.";
            return identity;
        }
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            identity.Status = "NOT_VERIFIED";
            identity.Reason = $"The {kind} input path was not supplied.";
            return identity;
        }
        if (!File.Exists(path))
        {
            identity.Status = "NOT_VERIFIED";
            identity.Reason = $"The external {kind} file is unavailable.";
            return identity;
        }
        if (identity.ExpectedSha256.Length != 64 || identity.ExpectedBytes <= 0 || string.IsNullOrWhiteSpace(source))
        {
            identity.Status = "BLOCKED";
            identity.Reason = $"The {kind} input must declare source, SHA-256, and positive byte size.";
            return identity;
        }
        if (HasReparsePoint(path))
        {
            identity.Status = "BLOCKED";
            identity.Reason = $"The {kind} input path contains a reparse point.";
            return identity;
        }
        if (IsTracked(root, path))
        {
            identity.Status = "BLOCKED";
            identity.Reason = $"The external {kind} input is tracked by Git.";
            return identity;
        }

        FileInfo file = new FileInfo(path);
        identity.ActualBytes = file.Length;
        identity.ActualSha256 = ComputeSha256(path);
        if (identity.ActualBytes != identity.ExpectedBytes ||
            !string.Equals(identity.ActualSha256, identity.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            identity.Status = "BLOCKED";
            identity.Reason = $"The external {kind} input does not match its declared identity.";
            return identity;
        }

        identity.Status = "PASS";
        identity.Reason = "The explicit external input exists and matches its declared identity.";
        return identity;
    }

    private static JsonElement? FindLane(JsonElement root, string lane)
    {
        if (!root.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement entry in models.EnumerateArray())
        {
            if (string.Equals(GetString(entry, "lane"), lane, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    private static string ResolvePath(string root, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return Path.GetFullPath(Path.IsPathRooted(value) ? value.Trim() : Path.Combine(root, value.Trim()));
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            var current = new FileInfo(path);
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
            DirectoryInfo? parent = current.Directory;
            while (parent != null)
            {
                parent.Refresh();
                if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                parent = parent.Parent;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsTracked(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                return false;
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("ls-files");
            process.StartInfo.ArgumentList.Add("--error-unmatch");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(relative);
            process.Start();
            _ = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string GetString(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool GetBool(JsonElement value, string name, bool fallback)
    {
        return value.TryGetProperty(name, out JsonElement property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
    }

    private static long GetLong(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement property) && property.TryGetInt64(out long result) ? result : 0;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static long FirstPositive(params long[] values) => values.FirstOrDefault(value => value > 0);
}

internal sealed class ExternalFileIdentity
{
    public string Kind { get; init; } = string.Empty;
    public string Status { get; set; } = "NOT_VERIFIED";
    public string Path { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string ExpectedSha256 { get; init; } = string.Empty;
    public string ActualSha256 { get; set; } = string.Empty;
    public long ExpectedBytes { get; init; }
    public long ActualBytes { get; set; }
    public string Reason { get; set; } = string.Empty;

    public static ExternalFileIdentity Missing(string kind) => new ExternalFileIdentity { Kind = kind, Reason = "No explicit external input was supplied." };

    public object ToEvidence()
    {
        return new
        {
            kind = Kind,
            status = Status,
            path = Path,
            fileName = FileName,
            source = Source,
            expectedSha256 = ExpectedSha256,
            actualSha256 = ActualSha256,
            expectedBytes = ExpectedBytes,
            actualBytes = ActualBytes,
            reason = Reason
        };
    }

    internal static void VerifyShutdownResources(SoakEvidence evidence, AppRuntime runtime)
    {
        Thread.Sleep(100);
        bool queuesDrained = runtime.ImageSaveQueue.PendingCount == 0 &&
            runtime.DetectionRecordQueue.PendingCount == 0 &&
            runtime.ImageSaveQueue.InFlightCount == 0 &&
            runtime.DetectionRecordQueue.InFlightCount == 0;
        bool workersCompleted = runtime.ImageSaveQueue.WorkerCompleted &&
            runtime.DetectionRecordQueue.WorkerCompleted;
        evidence.Runtime.QueueDrainStatus = queuesDrained && workersCompleted ? "DRAINED" : "BLOCKED";
        evidence.Runtime.FileRenameVerification = VerifyFileRename(evidence.Runtime.DatabasePath) &&
            VerifyFileRename(evidence.Runtime.ConfigPath) ? "PASS" : "BLOCKED";
        evidence.Runtime.SqliteOpenVerification = VerifySqliteOpen(evidence.Runtime.DatabasePath) ? "PASS" : "BLOCKED";
        evidence.Runtime.ProfileResidualStatus = CountFiles(evidence.Runtime.ProfileRoot) == 0 ? "PASS" : "BLOCKED";

        int childProcessCount = CountChildProcesses(evidence.Runtime.ProcessId);
        evidence.Runtime.ChildProcessCountAfterShutdown = Math.Max(0, childProcessCount);
        evidence.Runtime.ProcessCountAfterShutdown = evidence.Runtime.ChildProcessCountAfterShutdown;
        evidence.Runtime.ChildProcessStatus = childProcessCount == 0 ? "PASS" : "BLOCKED";

        int currentThreadCount = GetCurrentThreadCount();
        if (evidence.Runtime.BaselineThreadCount < 0 || currentThreadCount < 0)
        {
            evidence.Runtime.ResidualThreadCount = -1;
            evidence.Runtime.ThreadStatus = "BLOCKED";
        }
        else
        {
            evidence.Runtime.ResidualThreadCount = currentThreadCount - evidence.Runtime.BaselineThreadCount;
            evidence.Runtime.ThreadStatus = currentThreadCount == evidence.Runtime.BaselineThreadCount ? "PASS" : "BLOCKED";
        }
        evidence.Runtime.ResidualTaskCount = (runtime.ImageSaveQueue.WorkerCompleted ? 0 : 1) +
            (runtime.DetectionRecordQueue.WorkerCompleted ? 0 : 1);
        evidence.Runtime.TaskStatus = evidence.Runtime.ResidualTaskCount == 0 ? "PASS" : "BLOCKED";

        bool verified = queuesDrained && workersCompleted &&
            evidence.Runtime.FileRenameVerification == "PASS" &&
            evidence.Runtime.SqliteOpenVerification == "PASS" &&
            evidence.Runtime.ProfileResidualStatus == "PASS" &&
            evidence.Runtime.ChildProcessStatus == "PASS" &&
            evidence.Runtime.ThreadStatus == "PASS" &&
            evidence.Runtime.TaskStatus == "PASS";
        evidence.Runtime.FileLocksReleased = verified;
        if (!verified)
        {
            evidence.BlockingReasons.Add(
                $"Shutdown resource verification failed: queues={evidence.Runtime.QueueDrainStatus}, " +
                $"sqlite={evidence.Runtime.SqliteOpenVerification}, rename={evidence.Runtime.FileRenameVerification}, " +
                $"profile={evidence.Runtime.ProfileResidualStatus}, childProcesses={evidence.Runtime.ChildProcessStatus}, " +
                $"threads={evidence.Runtime.ThreadStatus}, tasks={evidence.Runtime.TaskStatus}.");
        }
    }

    internal static bool VerifySqliteOpen(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Cache=Shared");
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            return string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool VerifyFileRename(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        string temporaryPath = path + ".shutdown-check-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(path, temporaryPath);
            File.Move(temporaryPath, path);
            return File.Exists(path) && !File.Exists(temporaryPath);
        }
        catch
        {
            try
            {
                if (!File.Exists(path) && File.Exists(temporaryPath))
                {
                    File.Move(temporaryPath, path);
                }
            }
            catch
            {
            }
            return false;
        }
    }

    internal static int CountFiles(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
    }

    internal static int GetCurrentThreadCount()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.Threads.Count;
        }
        catch
        {
            return -1;
        }
    }

    internal static int GetCurrentHandleCount()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.HandleCount;
        }
        catch
        {
            return -1;
        }
    }

    internal static int CountChildProcesses(int parentProcessId)
    {
        if (parentProcessId <= 0)
        {
            return -1;
        }

        try
        {
            var pending = new Queue<int>(new[] { parentProcessId });
            var children = new HashSet<int>();
            while (pending.Count > 0)
            {
                int parent = pending.Dequeue();
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parent}");
                foreach (ManagementObject process in searcher.Get())
                {
                    int child = Convert.ToInt32(process["ProcessId"], CultureInfo.InvariantCulture);
                    if (children.Add(child))
                    {
                        pending.Enqueue(child);
                    }
                }
            }
            return children.Count;
        }
        catch
        {
            return -1;
        }
    }
}

public sealed class ExternalScenarioContract
{
    public string SchemaVersion { get; init; } = "v6-g2-scenarios-1.0";
    public string Status { get; private set; } = "NOT_VERIFIED";
    public string ManifestPath { get; init; } = string.Empty;
    public string ManifestSha256 { get; init; } = string.Empty;
    public List<ExternalScenarioSample> Samples { get; } = new List<ExternalScenarioSample>();
    public List<string> BlockingReasons { get; } = new List<string>();
    public List<string> NotVerifiedReasons { get; } = new List<string>();

    public static ExternalScenarioContract Missing(string path)
    {
        var result = new ExternalScenarioContract { ManifestPath = path ?? string.Empty };
        result.NotVerifiedReasons.Add("No external scenario manifest was supplied; a single image cannot claim complete scenario coverage.");
        return result;
    }

    public static ExternalScenarioContract Load(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Missing(string.Empty);
        }

        string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        if (!File.Exists(fullPath))
        {
            var missing = Missing(fullPath);
            missing.NotVerifiedReasons.Add($"External scenario manifest is unavailable: {fullPath}");
            return missing;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullPath));
            return FromJson(root, document.RootElement, fullPath);
        }
        catch (JsonException ex)
        {
            var invalid = new ExternalScenarioContract { ManifestPath = fullPath };
            invalid.BlockingReasons.Add($"External scenario manifest is not valid JSON: {ex.Message}");
            invalid.Status = "BLOCKED";
            return invalid;
        }
        catch (IOException ex)
        {
            var unreadable = new ExternalScenarioContract { ManifestPath = fullPath };
            unreadable.NotVerifiedReasons.Add($"External scenario manifest could not be read: {ex.Message}");
            return unreadable;
        }
    }

    public static ExternalScenarioContract FromJson(string root, JsonElement document, string manifestPath)
    {
        if (!document.TryGetProperty("scenarios", out JsonElement scenarios) || scenarios.ValueKind != JsonValueKind.Array)
        {
            return Missing(manifestPath);
        }

        var result = new ExternalScenarioContract
        {
            ManifestPath = manifestPath,
            ManifestSha256 = V6G2EvidenceIdentity.ComputeFileSha256(manifestPath)
        };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sampleHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in scenarios.EnumerateArray())
        {
            string name = GetString(item, "name");
            string kind = GetString(item, "kind");
            string path = ResolvePath(root, GetString(item, "path"));
            string expectedSha = FirstNonEmpty(
                GetString(item, "expectedSha256"),
                GetString(item, "sha256")).Trim().ToUpperInvariant();
            long expectedBytes = FirstPositive(GetLong(item, "expectedBytes"), GetLong(item, "bytes"));
            string expectedOutcome = GetString(item, "expectedOutcome");
            string expectedErrorCode = GetString(item, "expectedErrorCode");
            string expectedTerminalState = GetString(item, "expectedTerminalState");
            if (item.TryGetProperty("expected", out JsonElement expected) && expected.ValueKind == JsonValueKind.Object)
            {
                expectedOutcome = FirstNonEmpty(expectedOutcome, GetString(expected, "outcome"));
                expectedErrorCode = FirstNonEmpty(expectedErrorCode, GetString(expected, "errorCode"));
                expectedTerminalState = FirstNonEmpty(expectedTerminalState, GetString(expected, "terminalState"));
            }

            var sample = new ExternalScenarioSample
            {
                Name = name,
                Kind = kind,
                Path = path,
                ExpectedSha256 = expectedSha,
                ExpectedBytes = expectedBytes,
                ExpectedOutcome = expectedOutcome,
                ExpectedErrorCode = expectedErrorCode,
                ExpectedTerminalState = expectedTerminalState
            };
            result.ValidateSample(sample, names);
            if (sample.Status == "PASS" && sampleHashes.TryGetValue(sample.ActualSha256, out string? duplicateName))
            {
                result.BlockingReasons.Add(
                    $"Scenario samples '{duplicateName}' and '{sample.Name}' resolve to the same SHA-256; one image cannot cover multiple scenario cases.");
                sample.Status = "BLOCKED";
            }
            else if (sample.Status == "PASS")
            {
                sampleHashes[sample.ActualSha256] = sample.Name;
            }
            result.Samples.Add(sample);
        }

        if (result.Samples.Count == 0)
        {
            result.NotVerifiedReasons.Add("External scenario manifest contains no samples.");
        }
        else
        {
            foreach (string requiredKind in new[]
                     {
                         "has-target", "no-target", "multi-target", "short-frame", "wrong-size", "inference-exception"
                     })
            {
                if (!result.Samples.Any(sample =>
                        string.Equals(sample.Kind, requiredKind, StringComparison.OrdinalIgnoreCase) &&
                        sample.Status == "PASS"))
                {
                    result.NotVerifiedReasons.Add($"External scenario manifest has no valid '{requiredKind}' sample.");
                }
            }
        }

        if (result.BlockingReasons.Count > 0)
        {
            result.Status = "BLOCKED";
        }
        else if (result.NotVerifiedReasons.Count == 0 && result.Samples.Count > 0)
        {
            result.Status = "PASS";
        }

        return result;
    }

    public object ToEvidence()
    {
        return new
        {
            schemaVersion = SchemaVersion,
            status = Status,
            manifestPath = ManifestPath,
            manifestSha256 = ManifestSha256,
            samples = Samples,
            blockingReasons = BlockingReasons.ToArray(),
            notVerifiedReasons = NotVerifiedReasons.ToArray()
        };
    }

    private void ValidateSample(ExternalScenarioSample sample, HashSet<string> names)
    {
        string[] kinds = { "has-target", "no-target", "multi-target", "short-frame", "wrong-size", "inference-exception" };
        if (string.IsNullOrWhiteSpace(sample.Name) || !names.Add(sample.Name))
        {
            BlockingReasons.Add("Scenario names must be non-empty and unique.");
            sample.Status = "BLOCKED";
            return;
        }
        if (!kinds.Contains(sample.Kind, StringComparer.OrdinalIgnoreCase))
        {
            BlockingReasons.Add($"Unsupported scenario kind: {sample.Kind}");
            sample.Status = "BLOCKED";
            return;
        }
        if (sample.ExpectedOutcome is not ("OK" or "NG"))
        {
            BlockingReasons.Add($"Scenario {sample.Name} must declare expectedOutcome OK or NG.");
            sample.Status = "BLOCKED";
            return;
        }
        if (!string.Equals(sample.ExpectedTerminalState, "Successful", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sample.ExpectedTerminalState, "ExplicitFailure", StringComparison.OrdinalIgnoreCase))
        {
            BlockingReasons.Add($"Scenario {sample.Name} must declare expectedTerminalState Successful or ExplicitFailure.");
            sample.Status = "BLOCKED";
            return;
        }
        if ((sample.Kind is "short-frame" or "wrong-size" or "inference-exception") && string.IsNullOrWhiteSpace(sample.ExpectedErrorCode))
        {
            BlockingReasons.Add($"Scenario {sample.Name} must declare expectedErrorCode.");
            sample.Status = "BLOCKED";
            return;
        }
        if (string.IsNullOrWhiteSpace(sample.Path) || sample.ExpectedBytes <= 0 || sample.ExpectedSha256.Length != 64)
        {
            NotVerifiedReasons.Add($"Scenario {sample.Name} must bind a path, SHA-256, and positive byte size.");
            return;
        }
        if (!File.Exists(sample.Path))
        {
            NotVerifiedReasons.Add($"Scenario {sample.Name} sample is unavailable.");
            return;
        }

        FileInfo file = new FileInfo(sample.Path);
        sample.ActualBytes = file.Length;
        sample.ActualSha256 = V6G2EvidenceIdentity.ComputeFileSha256(sample.Path);
        if (sample.ActualBytes != sample.ExpectedBytes || !string.Equals(sample.ActualSha256, sample.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            BlockingReasons.Add($"Scenario {sample.Name} sample identity does not match its declared contract.");
            sample.Status = "BLOCKED";
            return;
        }

        sample.Status = "PASS";
    }

    private static string ResolvePath(string root, string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    private static string GetString(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static long GetLong(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out JsonElement property) && property.TryGetInt64(out long result) ? result : 0;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static long FirstPositive(params long[] values) => values.FirstOrDefault(value => value > 0);
}

public sealed class ExternalScenarioSample
{
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Status { get; set; } = "NOT_VERIFIED";
    public string Path { get; init; } = string.Empty;
    public string ExpectedSha256 { get; init; } = string.Empty;
    public string ActualSha256 { get; set; } = string.Empty;
    public long ExpectedBytes { get; init; }
    public long ActualBytes { get; set; }
    public string ExpectedOutcome { get; init; } = string.Empty;
    public string ExpectedErrorCode { get; init; } = string.Empty;
    public string ExpectedTerminalState { get; init; } = string.Empty;
}
