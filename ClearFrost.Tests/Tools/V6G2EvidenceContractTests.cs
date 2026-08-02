using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearFrost.Services;
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

    [Theory]
    [InlineData("has-target", 1, "", "Successful", "inference-result", true)]
    [InlineData("no-target", 0, "", "Successful", "inference-result", true)]
    [InlineData("multi-target", 2, "", "Successful", "inference-result", true)]
    [InlineData("short-frame", 0, "CaptureFrameFailed", "ExplicitFailure", "camera", true)]
    [InlineData("wrong-size", 0, "InputSizeMismatch", "ExplicitFailure", "input-contract", true)]
    [InlineData("inference-exception", 0, "DetectionServiceError", "ExplicitFailure", "inference", true)]
    public void ScenarioExecutionEvaluator_六类场景_按边界与结果数验收(
        string kind,
        int resultCount,
        string errorCode,
        string terminalState,
        string boundary,
        bool expectedPass)
    {
        ScenarioExecutionEvaluation result = ScenarioExecutionEvaluator.Evaluate(
            kind,
            resultCount,
            errorCode,
            terminalState,
            boundary);

        result.Status.Should().Be(expectedPass ? "PASS" : "BLOCKED");
    }

    [Fact]
    public void ScenarioExecutionEvaluator_目标数不匹配_必须BLOCKED()
    {
        ScenarioExecutionEvaluator.Evaluate("multi-target", 1, "", "Successful", "inference-result")
            .Status.Should().Be("BLOCKED");
    }

    [Fact]
    public void FaultRecoveryScheduler_全部故障类型_各有唯一健康恢复周期()
    {
        var scheduler = new FaultRecoveryScheduler();
        string[] faultKinds =
        {
            "CameraShortFrame", "CameraCaptureFailure", "PlcDisconnect", "PlcWriteFailure",
            "ResultAckTimeout", "DatabaseLock", "ImageTargetUnavailable", "ImageQueueBackpressure",
            "RecordQueueBackpressure", "ModelUnavailable", "Cancellation"
        };

        foreach (string kind in faultKinds)
        {
            scheduler.CanInjectFault.Should().BeTrue();
            string faultId = $"fault-{kind}";
            string healthyId = $"healthy-{kind}";
            scheduler.RecordFault(faultId);
            scheduler.CanInjectFault.Should().BeFalse();
            scheduler.TryRecover(healthyId, cycleSucceeded: true, out string recoveredFault).Should().BeTrue();
            recoveredFault.Should().Be(faultId);
        }

        scheduler.IsComplete.Should().BeTrue();
    }

    [Fact]
    public void FaultRecoveryScheduler_缺少恢复周期_保持未完成()
    {
        var scheduler = new FaultRecoveryScheduler();
        scheduler.RecordFault("fault-1");

        scheduler.IsComplete.Should().BeFalse();
        scheduler.PendingRecoveryCount.Should().Be(1);
        scheduler.CanInjectFault.Should().BeFalse();
    }

    [Fact]
    public void ExternalDependencyDigest_名称排序后确定性一致()
    {
        var first = new[]
        {
            new V6G2ExternalDependency { Name = "B.dll", Version = "2", Bytes = 2, Sha256 = new string('b', 64), Role = "camera" },
            new V6G2ExternalDependency { Name = "A.dll", Version = "1", Bytes = 1, Sha256 = new string('a', 64), Role = "plc" }
        };

        string forward = V6G2EvidenceIdentity.ComputeExternalDependencySetDigest(first);
        string reverse = V6G2EvidenceIdentity.ComputeExternalDependencySetDigest(first.Reverse());

        forward.Should().Be(reverse);
    }

    [Fact]
    public async Task ExternalInputContract_Detect通过且可选缺失_RequiredStatus仍可PASS()
    {
        string root = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClearFrostTests", "V6G2Required", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string modelPath = Path.Combine(tempRoot, "external-detect.onnx");
            string imagePath = Path.Combine(tempRoot, "external-image.bin");
            File.WriteAllBytes(modelPath, Encoding.UTF8.GetBytes("external-model-contract-only"));
            File.WriteAllBytes(imagePath, Encoding.UTF8.GetBytes("external-image-contract-only"));
            var model = new FileInfo(modelPath);
            var image = new FileInfo(imagePath);
            string manifestPath = Path.Combine(tempRoot, "inputs.json");
            string reportPath = Path.Combine(tempRoot, "report.json");
            var manifest = new
            {
                schemaVersion = "v6-g2-inputs-1.0",
                models = new[]
                {
                    new
                    {
                        lane = "Detect",
                        name = "external-detect",
                        fileName = model.Name,
                        path = modelPath,
                        sha256 = V6G2EvidenceIdentity.ComputeFileSha256(modelPath),
                        bytes = model.Length,
                        source = "laboratory handoff",
                        allowed = true,
                        opset = "17",
                        task = "detect",
                        validationImage = new
                        {
                            path = imagePath,
                            sha256 = V6G2EvidenceIdentity.ComputeFileSha256(imagePath),
                            bytes = image.Length
                        }
                    }
                },
                dependencies = Array.Empty<object>()
            };
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));

            int exitCode = await RunPowerShellFileAsync(
                Path.Combine(root, "tools", "verify_v6_external_inputs.ps1"),
                "-Root", root,
                "-ManifestPath", manifestPath,
                "-ReportPath", reportPath);

            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            exitCode.Should().Be(2, "optional lanes and release-only DLLs remain NOT_VERIFIED");
            report.RootElement.GetProperty("requiredStatus").GetString().Should().Be("PASS");
            report.RootElement.GetProperty("compatibilityStatus").GetString().Should().Be("NOT_VERIFIED");
            report.RootElement.GetProperty("overallStatus").GetString().Should().Be("NOT_VERIFIED");
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
    public async Task ExternalInputContract_缺少Detect_RequiredStatus不可PASS()
    {
        string root = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClearFrostTests", "V6G2Required", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string manifestPath = Path.Combine(tempRoot, "inputs.json");
            string reportPath = Path.Combine(tempRoot, "report.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = "v6-g2-inputs-1.0",
                models = Array.Empty<object>(),
                dependencies = Array.Empty<object>()
            }));

            await RunPowerShellFileAsync(
                Path.Combine(root, "tools", "verify_v6_external_inputs.ps1"),
                "-Root", root,
                "-ManifestPath", manifestPath,
                "-ReportPath", reportPath);

            using JsonDocument report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            report.RootElement.GetProperty("requiredStatus").GetString().Should().NotBe("PASS");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("commitSha")]
    [InlineData("candidateDigest")]
    [InlineData("evidenceSetId")]
    [InlineData("orchestratorRunId")]
    [InlineData("externalDependencySetDigest")]
    public async Task UnifiedEvidenceIdentity_跨候选或跨运行拼接_Validator必须失败(string conflictingField)
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
                ["productAssemblySha256"] = "",
                ["externalDependencySetDigest"] = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
                ["externalDependencies"] = Array.Empty<object>(),
                ["candidateDigest"] = new string('d', 64),
                ["evidenceSetId"] = "test-evidence-set",
                ["orchestratorRunId"] = "test-run",
                ["workflowRunId"] = "",
                ["provider"] = "NOT_APPLICABLE",
                ["machineIdentityDigest"] = machine,
                ["runStartedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                ["runFinishedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };

            string inputPath = WriteMinimalReport(tempRoot, "v6-g2-inputs-1.0", baseIdentity);
            var conflictingIdentity = new Dictionary<string, object?>(baseIdentity)
            {
                [conflictingField] = conflictingField switch
                {
                    "commitSha" => commitB,
                    "candidateDigest" => new string('e', 64),
                    "externalDependencySetDigest" => new string('f', 64),
                    _ => conflictingField + "-other"
                }
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

    [Theory]
    [InlineData("provider-mismatch", "strict DML PASS cannot report CPUExecutionProvider")]
    [InlineData("dependency-digest", "externalDependencySetDigest does not match name-sorted externalDependencies")]
    [InlineData("missing-scenario", "soak PASS requires every declared scenario sample to execute and match its contract")]
    [InlineData("shared-fault-recovery", "soak PASS must not use one healthy cycle to recover multiple faults")]
    public async Task Validator_手工正向报告缺少关键合同_必须BLOCKED(string mutation, string expectedError)
    {
        string root = FindRepositoryRoot();
        string tempRoot = Path.Combine(Path.GetTempPath(), "ClearFrostTests", "V6G2Validator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = new Dictionary<string, object?>
            {
                ["commitSha"] = new string('a', 40),
                ["productVersion"] = "6.1.0-preview.1",
                ["inputManifestSha256"] = "",
                ["detectModelSha256"] = "",
                ["validationImageSha256"] = "",
                ["productAssemblySha256"] = "",
                ["externalDependencySetDigest"] = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
                ["externalDependencies"] = Array.Empty<object>(),
                ["candidateDigest"] = new string('d', 64),
                ["evidenceSetId"] = "test-evidence-set",
                ["orchestratorRunId"] = "test-run",
                ["workflowRunId"] = "",
                ["provider"] = "NOT_APPLICABLE",
                ["machineIdentityDigest"] = new string('c', 64),
                ["runStartedAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
                ["runFinishedAtUtc"] = DateTimeOffset.UtcNow.ToString("O")
            };

            var modelOverrides = new Dictionary<string, object?>();
            var soakOverrides = new Dictionary<string, object?>();
            switch (mutation)
            {
                case "provider-mismatch":
                    modelOverrides["lanes"] = new object[]
                    {
                        new
                        {
                            lane = "Detect",
                            status = "NOT_VERIFIED",
                            cpu = new { status = "NOT_VERIFIED" },
                            dml = new { status = "PASS", actualProvider = "CPUExecutionProvider", report = new { } },
                            negativeContracts = new { status = "NOT_VERIFIED" }
                        },
                        new { lane = "Classification", status = "NOT_VERIFIED", cpu = new { status = "NOT_VERIFIED" }, dml = new { status = "NOT_VERIFIED" }, negativeContracts = new { status = "NOT_VERIFIED" } },
                        new { lane = "Segmentation", status = "NOT_VERIFIED", cpu = new { status = "NOT_VERIFIED" }, dml = new { status = "NOT_VERIFIED" }, negativeContracts = new { status = "NOT_VERIFIED" } },
                        new { lane = "OBB", status = "NOT_VERIFIED", cpu = new { status = "NOT_VERIFIED" }, dml = new { status = "NOT_VERIFIED" }, negativeContracts = new { status = "NOT_VERIFIED" } },
                        new { lane = "Pose", status = "NOT_VERIFIED", cpu = new { status = "NOT_VERIFIED" }, dml = new { status = "NOT_VERIFIED" }, negativeContracts = new { status = "NOT_VERIFIED" } }
                    };
                    modelOverrides["requiredStatus"] = "NOT_VERIFIED";
                    modelOverrides["compatibilityStatus"] = "NOT_VERIFIED";
                    modelOverrides["overallStatus"] = "NOT_VERIFIED";
                    break;
                case "dependency-digest":
                    identity["externalDependencySetDigest"] = new string('f', 64);
                    break;
                case "missing-scenario":
                    soakOverrides["status"] = "PASS";
                    soakOverrides["scenarioCoverageStatus"] = "PASS";
                    soakOverrides["scenarioContract"] = new { samples = Array.Empty<object>() };
                    soakOverrides["scenarioExecution"] = new
                    {
                        status = "PASS",
                        expectedSamples = 6,
                        executedSamples = 5,
                        samples = Array.Empty<object>()
                    };
                    soakOverrides["requiredStatus"] = "PASS";
                    soakOverrides["compatibilityStatus"] = "NOT_VERIFIED";
                    soakOverrides["overallStatus"] = "PASS";
                    break;
                case "shared-fault-recovery":
                    object[] faults =
                    {
                        new { planned = true, injected = true, faultCleared = true, nextHealthyCycleRecovered = true, recoveryStatus = "RECOVERED", errorCode = "CaptureFrameFailed", expectedErrorCode = "CaptureFrameFailed", actualTerminalState = "ExplicitFailure", expectedTerminalState = "ExplicitFailure", nextHealthyInspectionId = "SOAK-RECOVERY-000001" },
                        new { planned = true, injected = true, faultCleared = true, nextHealthyCycleRecovered = true, recoveryStatus = "RECOVERED", errorCode = "CaptureFrameFailed", expectedErrorCode = "CaptureFrameFailed", actualTerminalState = "ExplicitFailure", expectedTerminalState = "ExplicitFailure", nextHealthyInspectionId = "SOAK-RECOVERY-000001" }
                    };
                    soakOverrides["faults"] = new { events = faults };
                    soakOverrides["status"] = "PASS";
                    soakOverrides["requiredStatus"] = "PASS";
                    soakOverrides["compatibilityStatus"] = "NOT_VERIFIED";
                    soakOverrides["overallStatus"] = "PASS";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown validator mutation.");
            }

            string inputPath = WriteReport(tempRoot, "v6-g2-inputs-1.0", identity);
            string modelPath = WriteReport(tempRoot, "v6-g2-model-matrix-1.0", identity, modelOverrides);
            string migrationPath = WriteReport(tempRoot, "v6-g2-migration-lab-1.0", identity);
            string releasePath = WriteReport(tempRoot, "v6-g2-release-lab-1.0", identity);
            string isolationPath = WriteReport(tempRoot, "v6-g2-isolated-lab-1.0", identity);
            string soakPath = WriteReport(tempRoot, "v6-g2-soak-1.0", identity, soakOverrides);

            (int exitCode, string output) = await RunValidatorAsync(root, inputPath, modelPath, migrationPath, releasePath, isolationPath, soakPath, Path.Combine(tempRoot, "validation.json"));

            exitCode.Should().NotBe(0);
            output.Should().Contain(expectedError);
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
        return WriteReport(root, schemaVersion, identity);
    }

    private static string WriteReport(
        string root,
        string schemaVersion,
        object identity,
        IReadOnlyDictionary<string, object?>? overrides = null)
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
        if (overrides != null)
        {
            foreach (KeyValuePair<string, object?> item in overrides)
            {
                report[item.Key] = item.Value;
            }
        }
        File.WriteAllText(path, JsonSerializer.Serialize(report));
        return path;
    }

    private static async Task<(int ExitCode, string Output)> RunValidatorAsync(
        string root,
        string inputPath,
        string modelPath,
        string migrationPath,
        string releasePath,
        string isolationPath,
        string soakPath,
        string outputPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindPowerShell(),
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
        foreach (string argument in new[]
        {
            "-Root", root,
            "-InputReportPath", inputPath,
            "-ModelMatrixPath", modelPath,
            "-MigrationEvidencePath", migrationPath,
            "-ReleaseEvidencePath", releasePath,
            "-IsolationEvidencePath", isolationPath,
            "-SoakEvidencePath", soakPath,
            "-OutputPath", outputPath
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the V6 G2 evidence validator.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        string[] streams = await Task.WhenAll(standardOutput, standardError);
        return (process.ExitCode, string.Concat(streams));
    }

    private static string FindPowerShell()
    {
        string pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? pwsh : "powershell.exe";
    }

    private static async Task<int> RunPowerShellFileAsync(string scriptPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FindPowerShell(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(startInfo);
        process.Should().NotBeNull();
        Task<string> standardOutput = process!.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process!.WaitForExitAsync();
        await Task.WhenAll(standardOutput, standardError);
        return process.ExitCode;
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
