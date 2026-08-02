using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace ClearFrost.Tests.Tools;

public sealed class V6G2EvidenceContractTests
{
    [Fact]
    public void SoakConsistency_丢失一个DetectionRecord_必须失败()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        SoakConsistencyResult result = SoakConsistencyEvaluator.Evaluate(
            new[] { "INS-001", "INS-002" },
            new[]
            {
                new SoakConsistencyRecord
                {
                    InspectionId = "INS-001",
                    CycleSucceeded = true,
                    ImagePresent = true,
                    TracePresent = true
                }
            },
            queuesDrained: true,
            started,
            started.AddSeconds(1),
            "DRAINED");

        result.Status.Should().Be("BLOCKED");
        result.MissingRecords.Should().Be(1);
        result.MissingInspectionIds.Should().ContainSingle("INS-002");
    }

    [Fact]
    public void SoakConsistency_超过1000周期_不会遗漏早期记录()
    {
        string[] expected = Enumerable.Range(1, 1001).Select(index => $"INS-{index:0000}").ToArray();
        SoakConsistencyRecord[] records = expected.Select(id => new SoakConsistencyRecord
        {
            InspectionId = id,
            CycleSucceeded = true,
            ImagePresent = true,
            TracePresent = true
        }).ToArray();

        SoakConsistencyResult result = SoakConsistencyEvaluator.Evaluate(
            expected,
            records,
            queuesDrained: true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddSeconds(1),
            "DRAINED");

        result.Status.Should().Be("PASS");
        result.ExpectedInspectionIds.Should().Be(1001);
        result.RecordsRead.Should().Be(1001);
        result.MissingRecords.Should().Be(0);
    }

    [Fact]
    public void SoakConsistency_队列未排空_禁止通过()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        SoakConsistencyResult result = SoakConsistencyEvaluator.Evaluate(
            new[] { "INS-001" },
            new[]
            {
                new SoakConsistencyRecord
                {
                    InspectionId = "INS-001",
                    CycleSucceeded = true,
                    ImagePresent = true,
                    TracePresent = true
                }
            },
            queuesDrained: false,
            started,
            started.AddSeconds(1),
            "TIMEOUT");

        result.Status.Should().Be("BLOCKED");
        result.QueueStatus.Should().Be("TIMEOUT");
        result.Findings.Should().Contain(item => item.Contains("drained", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SoakConsistency_成功记录缺图_必须失败()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        SoakConsistencyResult result = SoakConsistencyEvaluator.Evaluate(
            new[] { "INS-001" },
            new[]
            {
                new SoakConsistencyRecord
                {
                    InspectionId = "INS-001",
                    CycleSucceeded = true,
                    ImagePresent = false,
                    TracePresent = true
                }
            },
            queuesDrained: true,
            started,
            started.AddSeconds(1),
            "DRAINED");

        result.Status.Should().Be("BLOCKED");
        result.MissingImages.Should().Be(1);
        result.Findings.Should().Contain(item => item.Contains("persisted image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SoakConsistency_成功记录缺Trace_必须失败()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        SoakConsistencyResult result = SoakConsistencyEvaluator.Evaluate(
            new[] { "INS-001" },
            new[]
            {
                new SoakConsistencyRecord
                {
                    InspectionId = "INS-001",
                    CycleSucceeded = true,
                    ImagePresent = true,
                    TracePresent = false
                }
            },
            queuesDrained: true,
            started,
            started.AddSeconds(1),
            "DRAINED");

        result.Status.Should().Be("BLOCKED");
        result.MissingTraceRecords.Should().Be(1);
        result.Findings.Should().Contain(item => item.Contains("valid trace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SoakQueueWaiter_队列未排空超时_返回明确TIMEOUT()
    {
        SoakQueueWaitResult result = await SoakQueueWaiter.WaitAsync(
            () => new SoakQueueSnapshot
            {
                ImagePending = 0,
                RecordPending = 0,
                ImageInFlight = 1,
                RecordInFlight = 0
            },
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(1));

        result.Drained.Should().BeFalse();
        result.Status.Should().Be("TIMEOUT");
        result.ImageInFlight.Should().Be(1);
        result.Reason.Should().Contain("in-flight");
    }

    [Fact]
    public void ScenarioManifest_六类样本绑定哈希和终态合同()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClearFrostTests", "V6G2Scenarios", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string[] kinds = { "has-target", "no-target", "multi-target", "short-frame", "wrong-size", "inference-exception" };
            var scenarios = kinds.Select((kind, index) =>
            {
                byte[] sampleBytes = Encoding.UTF8.GetBytes($"scenario-sample-{kind}");
                string fileName = $"sample-{index}.bin";
                File.WriteAllBytes(Path.Combine(tempRoot, fileName), sampleBytes);
                return new
                {
                    name = $"sample-{index}",
                    kind,
                    path = fileName,
                    expectedSha256 = Convert.ToHexString(SHA256.HashData(sampleBytes)),
                    expectedBytes = sampleBytes.Length,
                    expectedOutcome = kind is "has-target" ? "OK" : "NG",
                    expectedErrorCode = kind is "short-frame" ? "CaptureFrameFailed" :
                        kind is "wrong-size" ? "InputSizeMismatch" :
                        kind is "inference-exception" ? "DetectionServiceError" : "",
                    expectedTerminalState = kind is "short-frame" or "wrong-size" or "inference-exception"
                        ? "ExplicitFailure"
                        : "Successful"
                };
            }).ToArray();
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new { scenarios }));

            ExternalScenarioContract contract = ExternalScenarioContract.FromJson(
                tempRoot,
                document.RootElement,
                Path.Combine(tempRoot, "scenarios.json"));

            contract.Status.Should().Be("PASS");
            contract.Samples.Should().HaveCount(6);
            contract.Samples.Should().OnlyContain(sample => sample.Status == "PASS");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ScenarioManifest_重复同一张图_不能声称完整覆盖()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClearFrostTests", "V6G2Scenarios", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            byte[] sampleBytes = Encoding.UTF8.GetBytes("one-image-cannot-cover-six-cases");
            string samplePath = Path.Combine(tempRoot, "same.bin");
            File.WriteAllBytes(samplePath, sampleBytes);
            string hash = Convert.ToHexString(SHA256.HashData(sampleBytes));
            string[] kinds = { "has-target", "no-target", "multi-target", "short-frame", "wrong-size", "inference-exception" };
            var scenarios = kinds.Select((kind, index) => new
            {
                name = $"duplicate-{index}",
                kind,
                path = "same.bin",
                expectedSha256 = hash,
                expectedBytes = sampleBytes.Length,
                expectedOutcome = kind == "has-target" ? "OK" : "NG",
                expectedErrorCode = kind is "short-frame" ? "CaptureFrameFailed" :
                    kind is "wrong-size" ? "InputSizeMismatch" :
                    kind is "inference-exception" ? "DetectionServiceError" : "",
                expectedTerminalState = kind is "short-frame" or "wrong-size" or "inference-exception"
                    ? "ExplicitFailure"
                    : "Successful"
            }).ToArray();
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new { scenarios }));

            ExternalScenarioContract contract = ExternalScenarioContract.FromJson(
                tempRoot,
                document.RootElement,
                Path.Combine(tempRoot, "scenarios.json"));

            contract.Status.Should().Be("BLOCKED");
            contract.BlockingReasons.Should().Contain(reason => reason.Contains("same SHA-256", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void FaultRecovery_显式失败但没有下一健康周期_不能算恢复()
    {
        var contract = new FaultRecoveryContract
        {
            Planned = true,
            Injected = true,
            ExpectedErrorCode = "CaptureFrameFailed",
            ActualErrorCode = "CaptureFrameFailed",
            ExpectedTerminalState = "ExplicitFailure",
            ActualTerminalState = "ExplicitFailure",
            ExpectedTerminalErrorCode = "CaptureFrameFailed",
            ActualTerminalErrorCode = "CaptureFrameFailed",
            FaultCleared = true,
            NextHealthyCycleSucceeded = false
        };

        contract.IsRecovered.Should().BeFalse();
    }

    [Fact]
    public async Task UnifiedEvidenceIdentity_提交冲突_Validator必须失败()
    {
        string root = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClearFrostTests", "V6G2Identity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string commitA = new string('a', 40);
            string commitB = new string('b', 40);
            string machine = new string('c', 64);
            var baseIdentity = new Dictionary<string, object?>
            {
                ["commitSha"] = commitA,
                ["productVersion"] = "6.1.0-preview.1",
                ["inputManifestSha256"] = "",
                ["detectModelSha256"] = "",
                ["validationImageSha256"] = "",
                ["dllSha256"] = "",
                ["provider"] = "NOT_VERIFIED",
                ["machineIdentityDigest"] = machine,
                ["runStartedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                ["runFinishedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };

            string inputPath = WriteMinimalReport(tempRoot, "v6-g2-inputs-1.0", baseIdentity);
            var conflictingIdentity = new Dictionary<string, object?>(baseIdentity)
            {
                ["commitSha"] = commitB
            };
            string modelPath = WriteMinimalReport(tempRoot, "v6-g2-model-matrix-1.0", conflictingIdentity);
            string migrationPath = WriteMinimalReport(tempRoot, "v6-g2-migration-lab-1.0", baseIdentity);
            string releasePath = WriteMinimalReport(tempRoot, "v6-g2-release-lab-1.0", baseIdentity);
            string isolationPath = WriteMinimalReport(tempRoot, "v6-g2-isolated-lab-1.0", baseIdentity);
            string soakPath = WriteMinimalReport(tempRoot, "v6-g2-soak-1.0", baseIdentity);
            string outputPath = Path.Combine(tempRoot, "validation.json");

            string shell = FindPowerShell();
            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(root, "tools", "validate_v6_g2_evidence.ps1"));
            startInfo.ArgumentList.Add("-Root");
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add("-InputReportPath");
            startInfo.ArgumentList.Add(inputPath);
            startInfo.ArgumentList.Add("-ModelMatrixPath");
            startInfo.ArgumentList.Add(modelPath);
            startInfo.ArgumentList.Add("-MigrationEvidencePath");
            startInfo.ArgumentList.Add(migrationPath);
            startInfo.ArgumentList.Add("-ReleaseEvidencePath");
            startInfo.ArgumentList.Add(releasePath);
            startInfo.ArgumentList.Add("-IsolationEvidencePath");
            startInfo.ArgumentList.Add(isolationPath);
            startInfo.ArgumentList.Add("-SoakEvidencePath");
            startInfo.ArgumentList.Add(soakPath);
            startInfo.ArgumentList.Add("-OutputPath");
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo);
            process.Should().NotBeNull();
            Task<string> standardOutput = process!.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            bool exited = true;
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (TimeoutException)
            {
                exited = false;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync();
            }

            string[] streams = await Task.WhenAll(standardOutput, standardError);
            string output = string.Concat(streams);
            exited.Should().BeTrue();
            process.ExitCode.Should().NotBe(0);
            output.Should().Contain("Unified evidence identity conflict");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static string WriteMinimalReport(string root, string schemaVersion, object identity)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        var report = new Dictionary<string, object?>
        {
            ["schemaVersion"] = schemaVersion,
            ["status"] = "NOT_VERIFIED",
            ["identity"] = identity,
            ["blockingReasons"] = Array.Empty<string>(),
            ["notVerifiedReasons"] = new[] { "test" }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report));
        return path;
    }

    private static string FindPowerShell()
    {
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? pwsh : "powershell.exe";
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ClearFrost.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ClearFrost.sln.");
    }
}
