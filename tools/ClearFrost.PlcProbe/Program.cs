using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ClearFrost.Hardware;

return await PlcProbe.RunAsync(args);

internal static class PlcProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.FromArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            ProbeOptions.PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            ProbeOptions.PrintUsage();
            return 0;
        }

        try
        {
            options.ApplyConfigDefaults();
            options.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or JsonException)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            return 2;
        }

        Console.WriteLine("ClearFrost PLC probe");
        Console.WriteLine($"  Driver:        {options.DriverProvider}");
        Console.WriteLine($"  Protocol:      {options.Protocol}");
        Console.WriteLine($"  Endpoint:      {options.Ip}:{options.Port}");
        Console.WriteLine($"  Read address:  {options.ReadAddress}");
        Console.WriteLine($"  Duration:      {options.Duration}");
        Console.WriteLine($"  Interval:      {options.Interval}");
        Console.WriteLine($"  Write test:    {(options.WriteAddress == null ? "disabled" : $"{options.WriteAddress}={options.WriteValue}")}");
        Console.WriteLine();

        using var cancellation = new CancellationTokenSource(options.Duration);
        var summary = await RunDriverProbeAsync(options, cancellation.Token);

        Console.WriteLine();
        summary.Print();
        return summary.Failures == 0 ? 0 : 1;
    }

    private static async Task<ProbeSummary> RunDriverProbeAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        var summary = new ProbeSummary(options.DriverProvider);
        IPlcDevice? device = null;

        try
        {
            device = PlcFactory.Create(options.ToConnectionOptions());
            var connectWatch = Stopwatch.StartNew();
            bool connected = await device.ConnectAsync();
            connectWatch.Stop();
            summary.ConnectMilliseconds = connectWatch.Elapsed.TotalMilliseconds;

            if (!connected)
            {
                summary.RecordFailure(device.LastError ?? "connect failed");
                return summary;
            }

            Console.WriteLine($"Connected in {summary.ConnectMilliseconds:F1} ms. Probing...");

            while (!cancellationToken.IsCancellationRequested)
            {
                var iterationWatch = Stopwatch.StartNew();
                var readWatch = Stopwatch.StartNew();

                try
                {
                    var (success, value) = await device.ReadInt16Async(options.ReadAddress);
                    readWatch.Stop();

                    if (success)
                    {
                        summary.RecordRead(readWatch.Elapsed.TotalMilliseconds, value);
                    }
                    else
                    {
                        summary.RecordFailure(device.LastError ?? "read failed");
                    }

                    if (success &&
                        options.WriteAddress != null &&
                        options.WriteEvery > 0 &&
                        summary.Reads % options.WriteEvery == 0)
                    {
                        var writeWatch = Stopwatch.StartNew();
                        bool writeSuccess = await device.WriteInt16Async(options.WriteAddress, options.WriteValue);
                        writeWatch.Stop();

                        if (writeSuccess)
                        {
                            summary.RecordWrite(writeWatch.Elapsed.TotalMilliseconds);
                        }
                        else
                        {
                            summary.RecordFailure(device.LastError ?? "write failed");
                        }
                    }

                    if (!device.IsConnected && options.Reconnect)
                    {
                        summary.Reconnects++;
                        device.Disconnect();
                        await Task.Delay(options.ReconnectDelay, cancellationToken);
                        device = PlcFactory.Create(options.ToConnectionOptions());
                        bool reconnected = await device.ConnectAsync();
                        if (!reconnected)
                        {
                            summary.RecordFailure(device.LastError ?? "reconnect failed");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    readWatch.Stop();
                    summary.RecordFailure(ex.Message);
                }

                var remainingDelay = options.Interval - iterationWatch.Elapsed;
                if (remainingDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingDelay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            device?.Disconnect();
        }

        return summary;
    }
}

internal sealed class ProbeOptions
{
    private readonly HashSet<string> _specifiedKeys = new(StringComparer.OrdinalIgnoreCase);

    public bool ShowHelp { get; private set; }

    public string ConfigPath { get; private set; } = Path.Combine("ClearFrost", "config.json");

    public string DriverProvider { get; private set; } = "Hsl";

    public string Protocol { get; private set; } = "Mitsubishi_MC_ASCII";

    public string Ip { get; private set; } = string.Empty;

    public int Port { get; private set; }

    public string ReadAddress { get; private set; } = string.Empty;

    public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(10);

    public TimeSpan Interval { get; private set; } = TimeSpan.FromMilliseconds(500);

    public string? WriteAddress { get; private set; }

    public short WriteValue { get; private set; }

    public int WriteEvery { get; private set; } = 100;

    public bool Reconnect { get; private set; } = true;

    public TimeSpan ReconnectDelay { get; private set; } = TimeSpan.FromSeconds(2);

    public string SiemensCpuModel { get; private set; } = "S1200";

    public int SiemensRack { get; private set; }

    public int SiemensSlot { get; private set; } = 2;

    public static ProbeOptions FromArgs(string[] args)
    {
        var options = new ProbeOptions();
        var values = ParseArgs(args);
        options._specifiedKeys.UnionWith(values.Keys);

        if (values.ContainsKey("help") || values.ContainsKey("h"))
        {
            options.ShowHelp = true;
            return options;
        }

        options.ConfigPath = GetString(values, "config", options.ConfigPath);
        options.DriverProvider = GetString(values, "driver", options.DriverProvider);
        options.Protocol = GetString(values, "protocol", options.Protocol);
        options.Ip = GetString(values, "ip", options.Ip);
        options.Port = GetInt(values, "port", options.Port);
        options.ReadAddress = GetString(values, "read-address", options.ReadAddress);
        options.Duration = TimeSpan.FromSeconds(GetDouble(values, "duration-seconds", options.Duration.TotalSeconds));
        options.Interval = TimeSpan.FromMilliseconds(GetDouble(values, "interval-ms", options.Interval.TotalMilliseconds));
        options.WriteAddress = GetNullableString(values, "write-address");
        options.WriteValue = (short)GetInt(values, "write-value", options.WriteValue);
        options.WriteEvery = GetInt(values, "write-every", options.WriteEvery);
        options.Reconnect = GetBool(values, "reconnect", options.Reconnect);
        options.ReconnectDelay = TimeSpan.FromMilliseconds(GetDouble(values, "reconnect-delay-ms", options.ReconnectDelay.TotalMilliseconds));
        options.SiemensCpuModel = GetString(values, "siemens-cpu-model", options.SiemensCpuModel);
        options.SiemensRack = GetInt(values, "siemens-rack", options.SiemensRack);
        options.SiemensSlot = GetInt(values, "siemens-slot", options.SiemensSlot);
        return options;
    }

    public void ApplyConfigDefaults()
    {
        if (!File.Exists(ConfigPath))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));
        var root = document.RootElement;

        if (!WasSpecified("driver"))
        {
            DriverProvider = GetJsonString(root, "PlcDriverProvider", DriverProvider);
        }

        if (!WasSpecified("protocol"))
        {
            Protocol = GetJsonString(root, "PlcProtocol", Protocol);
        }

        if (!WasSpecified("ip") && string.IsNullOrWhiteSpace(Ip))
        {
            Ip = GetJsonString(root, "PlcIp", Ip);
        }

        if (!WasSpecified("port") && Port <= 0)
        {
            Port = GetJsonInt(root, "PlcPort", Port);
        }

        if (!WasSpecified("read-address") && string.IsNullOrWhiteSpace(ReadAddress))
        {
            ReadAddress = GetJsonString(root, "PlcTriggerAddress", ReadAddress);
        }

        if (!WasSpecified("siemens-cpu-model"))
        {
            SiemensCpuModel = GetJsonString(root, "PlcSiemensCpuModel", SiemensCpuModel);
        }

        if (!WasSpecified("siemens-rack"))
        {
            SiemensRack = GetJsonInt(root, "PlcSiemensRack", SiemensRack);
        }

        if (!WasSpecified("siemens-slot"))
        {
            SiemensSlot = GetJsonInt(root, "PlcSiemensSlot", SiemensSlot);
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DriverProvider))
        {
            throw new ArgumentException("--driver cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Protocol))
        {
            throw new ArgumentException("--protocol cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(Ip))
        {
            throw new ArgumentException("--ip is required when config.json is unavailable or incomplete.");
        }

        if (Port <= 0)
        {
            throw new ArgumentException("--port must be greater than 0.");
        }

        if (string.IsNullOrWhiteSpace(ReadAddress))
        {
            throw new ArgumentException("--read-address is required when config.json is unavailable or incomplete.");
        }

        if (Duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("--duration-seconds must be greater than 0.");
        }

        if (Interval <= TimeSpan.Zero)
        {
            throw new ArgumentException("--interval-ms must be greater than 0.");
        }

        if (WriteAddress != null && WriteEvery <= 0)
        {
            throw new ArgumentException("--write-every must be greater than 0 when write testing is enabled.");
        }
    }

    public PlcConnectionOptions ToConnectionOptions()
    {
        return new PlcConnectionOptions
        {
            DriverProvider = DriverProvider,
            Protocol = Protocol,
            Ip = Ip,
            Port = Port,
            TriggerAddress = ReadAddress,
            SiemensCpuModel = SiemensCpuModel,
            SiemensRack = SiemensRack,
            SiemensSlot = SiemensSlot
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
Usage:
  dotnet run --project tools/ClearFrost.PlcProbe -- [options]

Examples:
  dotnet run --project tools/ClearFrost.PlcProbe -- --driver McpX --duration-seconds 3600
  dotnet run --project tools/ClearFrost.PlcProbe -- --driver Hsl --duration-seconds 600
  dotnet run --project tools/ClearFrost.PlcProbe -- --driver McpX --interval-ms 100 --write-address D556 --write-value 1 --write-every 100

Options:
  --config <path>              Defaults to ClearFrost/config.json.
  --driver <Hsl|HaoCommunication|McpX>
                                Defaults to config value, then Hsl.
  --protocol <name>            Defaults to config value, then Mitsubishi_MC_ASCII.
  --ip <address>               PLC IP, defaults to config value.
  --port <number>              PLC port, defaults to config value.
  --read-address <address>     Read target, defaults to PlcTriggerAddress.
  --duration-seconds <number>  Defaults to 600.
  --interval-ms <number>       Defaults to 500.
  --write-address <address>    Disabled unless explicitly provided.
  --write-value <short>        Defaults to 0.
  --write-every <number>       Defaults to 100 read cycles.
  --reconnect <true|false>     Defaults to true.
  --siemens-cpu-model <model>   Defaults to config value, then S1200.
  --siemens-rack <number>       Defaults to config value, then 0.
  --siemens-slot <number>       Defaults to config value, then 2.
""");
    }

    private bool WasSpecified(string key)
    {
        return _specifiedKeys.Contains(key);
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument: {token}");
            }

            string key = token.Substring(2);
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            values[key] = value;
        }

        return values;
    }

    private static string GetString(Dictionary<string, string?> values, string key, string fallback)
    {
        return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string? GetNullableString(Dictionary<string, string?> values, string key)
    {
        return values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int GetInt(Dictionary<string, string?> values, string key, int fallback)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"--{key} must be an integer.");
    }

    private static double GetDouble(Dictionary<string, string?> values, string key, double fallback)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new ArgumentException($"--{key} must be a number.");
    }

    private static bool GetBool(Dictionary<string, string?> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ArgumentException($"--{key} must be true or false.");
    }

    private static string GetJsonString(JsonElement root, string propertyName, string fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback
            : fallback;
    }

    private static int GetJsonInt(JsonElement root, string propertyName, int fallback)
    {
        return root.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out int value)
            ? value
            : fallback;
    }
}

internal sealed class ProbeSummary
{
    private readonly List<double> _readLatencies = new();
    private readonly List<double> _writeLatencies = new();
    private readonly Dictionary<string, int> _errors = new(StringComparer.Ordinal);

    public ProbeSummary(string driverProvider)
    {
        DriverProvider = driverProvider;
    }

    public string DriverProvider { get; }

    public double ConnectMilliseconds { get; set; }

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public int Failures { get; private set; }

    public int Reconnects { get; set; }

    public short LastValue { get; private set; }

    public void RecordRead(double milliseconds, short value)
    {
        Reads++;
        LastValue = value;
        _readLatencies.Add(milliseconds);

        if (Reads % 100 == 0)
        {
            Console.WriteLine($"Reads={Reads}, Failures={Failures}, Last={LastValue}, P95={Percentile(_readLatencies, 95):F1} ms");
        }
    }

    public void RecordWrite(double milliseconds)
    {
        Writes++;
        _writeLatencies.Add(milliseconds);
    }

    public void RecordFailure(string error)
    {
        Failures++;
        string normalized = string.IsNullOrWhiteSpace(error) ? "unknown error" : error.Trim();
        _errors[normalized] = _errors.TryGetValue(normalized, out int count) ? count + 1 : 1;
        Console.WriteLine($"Failure #{Failures}: {normalized}");
    }

    public void Print()
    {
        Console.WriteLine($"Summary for {DriverProvider}");
        Console.WriteLine($"  Connect:    {ConnectMilliseconds:F1} ms");
        Console.WriteLine($"  Reads:      {Reads}");
        Console.WriteLine($"  Writes:     {Writes}");
        Console.WriteLine($"  Failures:   {Failures}");
        Console.WriteLine($"  Reconnects: {Reconnects}");
        Console.WriteLine($"  Last value: {LastValue}");
        PrintLatency("Read", _readLatencies);
        PrintLatency("Write", _writeLatencies);

        if (_errors.Count > 0)
        {
            Console.WriteLine("  Errors:");
            foreach (var pair in _errors.OrderByDescending(pair => pair.Value).Take(10))
            {
                Console.WriteLine($"    {pair.Value}x {pair.Key}");
            }
        }
    }

    private static void PrintLatency(string name, List<double> latencies)
    {
        if (latencies.Count == 0)
        {
            Console.WriteLine($"  {name}:       no samples");
            return;
        }

        Console.WriteLine(
            $"  {name}:       avg={latencies.Average():F1} ms, p50={Percentile(latencies, 50):F1} ms, " +
            $"p95={Percentile(latencies, 95):F1} ms, p99={Percentile(latencies, 99):F1} ms, max={latencies.Max():F1} ms");
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        double rank = (percentile / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);

        if (lower == upper)
        {
            return sorted[lower];
        }

        double weight = rank - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * weight;
    }
}
