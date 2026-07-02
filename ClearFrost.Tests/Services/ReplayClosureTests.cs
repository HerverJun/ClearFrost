using System.Security.Cryptography;
using System.Text.Json;
using ClearFrost.Config;
using ClearFrost.Core.Models;
using ClearFrost.Core.Recipes;
using ClearFrost.Core.Rules;
using ClearFrost.Core.Security;
using ClearFrost.Interfaces;
using ClearFrost.Services;
using ClearFrost.Services.Replay;
using ClearFrost.Yolo;
using FluentAssertions;
using OpenCvSharp;

namespace ClearFrost.Tests.Services;

#pragma warning disable CS0067
public class ReplayClosureTests
{
    [Fact]
    public async Task DeterministicFixtureReplay_生成8样本并精确报告差异指标()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions);
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);

            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-required-matrix",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            report.Status.Should().Be(ReplayRunStatuses.Completed);
            report.Metrics.SampleCount.Should().Be(8);
            report.Metrics.CandidateNewMissedDetectionCount.Should().Be(1);
            report.Metrics.CandidateFixedMissedDetectionCount.Should().Be(1);
            report.Metrics.CandidateNewFalseRejectCount.Should().Be(1);
            report.Metrics.CandidateFixedFalseRejectCount.Should().Be(1);
            report.Metrics.ChangedDecisionCount.Should().Be(4);
            File.Exists(report.ReportJsonPath).Should().BeTrue();
            File.Exists(report.ReportCsvPath).Should().BeTrue();

            runner.CallCount(fixture.BaselineModel.ModelId).Should().Be(8);
            runner.CallCount(fixture.CandidateModel.ModelId).Should().Be(8);
            runner.Events.Should().ContainInOrder(
                $"create:{fixture.BaselineModel.ModelId}",
                $"dispose:{fixture.BaselineModel.ModelId}",
                $"create:{fixture.CandidateModel.ModelId}");

            ReplayApprovalDecision decision = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
            {
                MaximumNewMissedDetections = 0,
                MaximumNewFalseRejects = 0
            }).Evaluate(report);
            decision.Approved.Should().BeFalse();
            decision.Reasons.Should().Contain(reason => reason.Contains("missed detections", StringComparison.OrdinalIgnoreCase));
            decision.Reasons.Should().Contain(reason => reason.Contains("false rejects", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ReplayMetrics_V2使用候选总漏检率而不是新增漏检数()
    {
        var samples = new List<ReplaySampleComparison>
        {
            new ReplaySampleComparison
            {
                SampleId = "S1",
                GroundTruth = ReplayDecisions.NG,
                BaselineDecision = ReplayDecisions.OK,
                CandidateDecision = ReplayDecisions.OK
            }
        };
        ReplayComparisonMetrics metrics = ReplayMetrics.Compute(samples);
        var report = new ReplayRunReport
        {
            Status = ReplayRunStatuses.Completed,
            Metrics = metrics,
            Samples = samples
        };

        metrics.CandidateNewMissedDetectionCount.Should().Be(0);
        metrics.CandidateMissedDetectionCount.Should().Be(1);
        metrics.CandidateMissedDetectionRate.Should().Be(1d);

        ReplayApprovalDecision decision = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
        {
            Version = 2,
            MaximumCandidateMissedDetectionRate = 0
        }).Evaluate(report);
        decision.Approved.Should().BeFalse();
        decision.Reasons.Should().Contain(reason => reason.Contains("missed detection rate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReplayMetrics_V2误拒率增幅按两个总率之差计算()
    {
        var samples = new List<ReplaySampleComparison>
        {
            new ReplaySampleComparison
            {
                SampleId = "S1",
                GroundTruth = ReplayDecisions.OK,
                BaselineDecision = ReplayDecisions.OK,
                CandidateDecision = ReplayDecisions.NG
            },
            new ReplaySampleComparison
            {
                SampleId = "S2",
                GroundTruth = ReplayDecisions.OK,
                BaselineDecision = ReplayDecisions.NG,
                CandidateDecision = ReplayDecisions.OK
            }
        };
        ReplayComparisonMetrics metrics = ReplayMetrics.Compute(samples);
        var report = new ReplayRunReport
        {
            Status = ReplayRunStatuses.Completed,
            Metrics = metrics,
            Samples = samples
        };

        metrics.CandidateNewFalseRejectCount.Should().Be(1);
        metrics.CandidateFixedFalseRejectCount.Should().Be(1);
        metrics.BaselineFalseRejectRate.Should().Be(0.5d);
        metrics.CandidateFalseRejectRate.Should().Be(0.5d);
        metrics.FalseRejectRateIncrease.Should().Be(0d);

        ReplayApprovalDecision decision = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
        {
            Version = 2,
            MaximumFalseRejectRateIncrease = 0
        }).Evaluate(report);
        decision.Approved.Should().BeTrue();
    }

    [Fact]
    public void ReplayMetrics_InvalidSample进入InvalidSampleCount并受Policy限制()
    {
        var samples = new List<ReplaySampleComparison>
        {
            new ReplaySampleComparison
            {
                SampleId = "S1",
                GroundTruth = ReplayDecisions.OK,
                BaselineDecision = ReplayDecisions.OK,
                CandidateDecision = ReplayDecisions.OK
            },
            new ReplaySampleComparison
            {
                SampleId = "S2",
                GroundTruth = ReplayDecisions.NG,
                IsValid = false,
                InvalidReason = "Candidate inference failed."
            }
        };
        samples[1].Classification = ReplayMetrics.Classify(samples[1]);
        ReplayComparisonMetrics metrics = ReplayMetrics.Compute(samples);
        var report = new ReplayRunReport
        {
            Status = ReplayRunStatuses.Completed,
            Metrics = metrics,
            Samples = samples
        };

        metrics.TotalSampleCount.Should().Be(2);
        metrics.ValidSampleCount.Should().Be(1);
        metrics.InvalidSampleCount.Should().Be(1);
        samples[1].Classification.Should().Be("InvalidSample");

        ReplayApprovalDecision decision = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
        {
            Version = 2,
            MaximumInvalidSampleCount = 0
        }).Evaluate(report);
        decision.Approved.Should().BeFalse();
        decision.Reasons.Should().Contain(reason => reason.Contains("invalid samples", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReplayPolicy_V1保持旧语义_V2使用新语义且未知版本FailClosed()
    {
        var samples = new List<ReplaySampleComparison>
        {
            new ReplaySampleComparison
            {
                SampleId = "S1",
                GroundTruth = ReplayDecisions.OK,
                BaselineDecision = ReplayDecisions.OK,
                CandidateDecision = ReplayDecisions.NG
            },
            new ReplaySampleComparison
            {
                SampleId = "S2",
                GroundTruth = ReplayDecisions.OK,
                BaselineDecision = ReplayDecisions.NG,
                CandidateDecision = ReplayDecisions.OK
            }
        };
        ReplayComparisonMetrics metrics = ReplayMetrics.Compute(samples);
        var report = new ReplayRunReport
        {
            Status = ReplayRunStatuses.Completed,
            Metrics = metrics,
            Samples = samples
        };

        ReplayApprovalDecision v1 = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
        {
            Version = 1,
            MaximumFalseRejectRateIncrease = 0
        }).Evaluate(report);
        ReplayApprovalDecision v2 = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
        {
            Version = 2,
            MaximumFalseRejectRateIncrease = 0
        }).Evaluate(report);
        ReplayApprovalDecision unsupported = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
        {
            Version = 99
        }).Evaluate(report);

        v1.Approved.Should().BeFalse();
        v2.Approved.Should().BeTrue();
        unsupported.Approved.Should().BeFalse();
        unsupported.Reasons.Should().Contain(reason => reason.Contains("not supported", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReplayArtifactHashing_GoldenFixtures匹配冻结V1V2()
    {
        GoldenReplayArtifactFixture v1 = CreateGoldenArtifactFixture(1);
        GoldenReplayArtifactFixture v2 = CreateGoldenArtifactFixture(2);

        ReplayArtifactHashing.ComputeReportHash(v1.Report).Should().Be(v1.ExpectedReportHash);
        ReplayArtifactHashing.ComputeEvidenceHash(v1.Evidence).Should().Be(v1.ExpectedEvidenceHash);
        ReplayAcceptancePolicy.ComputePolicyHash(v1.Report.PolicySnapshot).Should().Be(v1.ExpectedPolicyHash);
        v1.Report.ReportHash.Should().Be(v1.ExpectedReportHash);
        v1.Evidence.EvidenceHash.Should().Be(v1.ExpectedEvidenceHash);

        ReplayArtifactHashing.ComputeReportHash(v2.Report).Should().Be(v2.ExpectedReportHash);
        ReplayArtifactHashing.ComputeEvidenceHash(v2.Evidence).Should().Be(v2.ExpectedEvidenceHash);
        ReplayAcceptancePolicy.ComputePolicyHash(v2.Report.PolicySnapshot).Should().Be(v2.ExpectedPolicyHash);
        v2.Report.ReportHash.Should().Be(v2.ExpectedReportHash);
        v2.Evidence.EvidenceHash.Should().Be(v2.ExpectedEvidenceHash);
    }

    [Fact]
    public void ReplayArtifactHashing_拒绝缺失未知版本且禁止V2降级()
    {
        ReplayRunReport missingVersion = CreateGoldenArtifactFixture(2).Report;
        missingVersion.PolicyVersion = 0;
        ReplayArtifactHashing.TryComputeReportHash(missingVersion, out _, out string missingError)
            .Should().BeFalse();
        missingError.Should().Contain("missing");

        ReplayRunReport unsupported = CreateGoldenArtifactFixture(2).Report;
        unsupported.PolicyVersion = 99;
        unsupported.PolicySnapshot.Version = 99;
        ReplayArtifactHashing.TryComputeReportHash(unsupported, out _, out string unsupportedError)
            .Should().BeFalse();
        unsupportedError.Should().Contain("not supported");

        ReplayRunReport missingSnapshot = CreateGoldenArtifactFixture(2).Report;
        missingSnapshot.PolicySnapshot = null!;
        ReplayArtifactHashing.TryComputeReportHash(missingSnapshot, out _, out string snapshotError)
            .Should().BeFalse();
        snapshotError.Should().Contain("snapshot");

        ReplayRunReport versionMismatch = CreateGoldenArtifactFixture(2).Report;
        versionMismatch.PolicySnapshot.Version = 1;
        ReplayArtifactHashing.TryComputeReportHash(versionMismatch, out _, out string mismatchError)
            .Should().BeFalse();
        mismatchError.Should().Contain("does not match");

        GoldenReplayArtifactFixture v2 = CreateGoldenArtifactFixture(2);
        ReplayRunReport downgraded = v2.Report;
        downgraded.PolicyVersion = 1;
        downgraded.PolicySnapshot = CreateGoldenPolicy(1);
        downgraded.PolicyHash = ReplayAcceptancePolicy.ComputePolicyHash(downgraded.PolicySnapshot);

        ReplayArtifactHashing.ComputeReportHash(downgraded).Should().NotBe(v2.ExpectedReportHash);
        Action forceV2 = () => ReplayArtifactHashing.ComputeReportHashV2(downgraded);
        forceV2.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match hash algorithm v2*");
    }

    [Fact]
    public void ReplayArtifactHashing_V1忽略V2字段且V2绑定这些字段()
    {
        GoldenReplayArtifactFixture v1 = CreateGoldenArtifactFixture(1);
        v1.Report.Metrics.TotalSampleCount = 99;
        v1.Report.Metrics.ValidSampleCount = 98;
        v1.Report.Metrics.CandidateMissedDetectionRate = 0.75;
        foreach (ReplaySampleComparison sample in v1.Report.Samples)
        {
            sample.IsValid = false;
            sample.InvalidReason = "v2-only mutation";
        }

        ReplayArtifactHashing.ComputeReportHash(v1.Report).Should().Be(v1.ExpectedReportHash);

        GoldenReplayArtifactFixture v2 = CreateGoldenArtifactFixture(2);
        v2.Report.Samples[0].InvalidReason = "v2 mutation";
        ReplayArtifactHashing.ComputeReportHash(v2.Report).Should().NotBe(v2.ExpectedReportHash);
    }

    [Fact]
    public async Task ReplayDataset_Manifest不写绝对路径并可移动后校验()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            string manifestPath = Path.Combine(fixture.Dataset.RootDirectory, "manifest.json");
            ReplayDatasetSnapshot manifest = JsonSerializer.Deserialize<ReplayDatasetSnapshot>(
                await File.ReadAllTextAsync(manifestPath),
                ReplayJson.Options) ?? throw new InvalidOperationException("Manifest parse failed.");

            manifest.RootDirectory.Should().BeEmpty();
            manifest.BaselineModel.ModelPath.Should().BeEmpty();
            manifest.CandidateModel.ModelPath.Should().BeEmpty();
            manifest.Samples.Should().OnlyContain(sample =>
                !Path.IsPathRooted(sample.ImagePath) &&
                sample.SourceImagePath.StartsWith("record:", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(sample.Record.ImagePath) &&
                string.IsNullOrWhiteSpace(sample.Record.TraceImagePath) &&
                string.IsNullOrWhiteSpace(sample.Record.RenderedImagePath));

            string movedRoot = Path.Combine(tempDir, "moved");
            Directory.CreateDirectory(movedRoot);
            string movedDirectory = Path.Combine(movedRoot, fixture.Dataset.DatasetId);
            Directory.Move(fixture.Dataset.RootDirectory, movedDirectory);

            var movedStore = new FileReplayDatasetStore(
                new FakeDatabaseService(new List<DetectionRecord>()),
                Path.Combine(tempDir, "unused-datasets"));
            ReplayDatasetSnapshot moved = await movedStore.LoadSnapshotAsync(movedDirectory);

            moved.DatasetHash.Should().Be(fixture.Dataset.DatasetHash);
            moved.RootDirectory.Should().Be(Path.GetFullPath(movedDirectory));
            moved.Samples.Should().OnlyContain(sample => Path.IsPathRooted(sample.ImagePath) && File.Exists(sample.ImagePath));
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayDataset_重复InspectionId按DetectionRecordId分别冻结并拒绝绑定不一致()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            string imageSourceDir = Path.Combine(tempDir, "source-duplicate");
            Directory.CreateDirectory(imageSourceDir);
            string imageA = Path.Combine(imageSourceDir, "a.png");
            string imageB = Path.Combine(imageSourceDir, "b.png");
            using (var image = new Mat(24, 24, MatType.CV_8UC3, new Scalar(30, 80, 160)))
            {
                Cv2.ImWrite(imageA, image);
                Cv2.ImWrite(imageB, image);
            }

            var records = new List<DetectionRecord>
            {
                new DetectionRecord
                {
                    Id = 101,
                    InspectionId = "DUP-INSPECTION",
                    Timestamp = new DateTime(2026, 6, 30, 10, 0, 0),
                    IsQualified = true,
                    ImagePath = imageA,
                    RecipeId = "recipe-dup",
                    RecipeVersion = "1"
                },
                new DetectionRecord
                {
                    Id = 102,
                    InspectionId = "DUP-INSPECTION",
                    Timestamp = new DateTime(2026, 6, 30, 10, 1, 0),
                    IsQualified = false,
                    ImagePath = imageB,
                    RecipeId = "recipe-dup",
                    RecipeVersion = "1"
                }
            };
            var reviews = new Dictionary<long, ReplayManualReviewRecord>
            {
                [101] = new ReplayManualReviewRecord
                {
                    SampleId = "dup-a",
                    InspectionId = "DUP-INSPECTION",
                    GroundTruth = ReplayDecisions.OK,
                    SystemDecision = ReplayDecisions.OK,
                    Disposition = ReplayReviewDispositions.Confirmed,
                    ReviewerId = "qa01",
                    ReviewerRole = "Engineer",
                    Revision = 1,
                    ReviewedAt = DateTimeOffset.UtcNow
                },
                [102] = new ReplayManualReviewRecord
                {
                    SampleId = "dup-b",
                    InspectionId = "DUP-INSPECTION",
                    GroundTruth = ReplayDecisions.NG,
                    SystemDecision = ReplayDecisions.NG,
                    Disposition = ReplayReviewDispositions.Confirmed,
                    ReviewerId = "qa01",
                    ReviewerRole = "Engineer",
                    Revision = 1,
                    ReviewedAt = DateTimeOffset.UtcNow
                }
            };
            var store = new FileReplayDatasetStore(
                new FakeDatabaseService(records),
                Path.Combine(tempDir, "datasets-duplicate"));
            var request = new ReplayDatasetCreateRequest
            {
                DatasetId = "duplicate-inspection",
                Query = new DetectionReplayQuery { Limit = 2 },
                Recipe = new ReplayRecipeSnapshot
                {
                    RecipeId = "recipe-dup",
                    RecipeVersion = "1",
                    RuleSet = new InspectionRuleSet(),
                    RuleSetJson = "{}"
                },
                BaselineModel = new ReplayModelIdentity { ModelId = "baseline", Version = "1", Sha256 = "baseline-hash" },
                CandidateModel = new ReplayModelIdentity { ModelId = "candidate", Version = "2", Sha256 = "candidate-hash" },
                ManualReviewsByDetectionRecordId = reviews
            };

            ReplayDatasetSnapshot snapshot = await store.CreateSnapshotAsync(request);

            snapshot.Samples.Should().HaveCount(2);
            snapshot.Samples.Should().Contain(sample => sample.DetectionRecordId == 101 && sample.SampleId == "dup-a");
            snapshot.Samples.Should().Contain(sample => sample.DetectionRecordId == 102 && sample.SampleId == "dup-b");

            var mismatchedReviews = new Dictionary<long, ReplayManualReviewRecord>(reviews)
            {
                [101] = new ReplayManualReviewRecord
                {
                    SampleId = "dup-a",
                    InspectionId = "OTHER-INSPECTION",
                    GroundTruth = ReplayDecisions.OK,
                    SystemDecision = ReplayDecisions.OK,
                    Disposition = ReplayReviewDispositions.Confirmed,
                    ReviewerId = "qa01",
                    ReviewerRole = "Engineer",
                    Revision = 1,
                    ReviewedAt = DateTimeOffset.UtcNow
                }
            };
            Func<Task> mismatched = () => store.CreateSnapshotAsync(new ReplayDatasetCreateRequest
            {
                DatasetId = "duplicate-inspection-mismatch",
                Query = request.Query,
                Recipe = request.Recipe,
                BaselineModel = request.BaselineModel,
                CandidateModel = request.CandidateModel,
                ManualReviewsByDetectionRecordId = mismatchedReviews
            });
            await mismatched.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*binding mismatch*");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayDatasetLifecycle_归档经应用服务并阻止引用和残留()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture unauthorizedFixture = await ReplayFixture.CreateAsync(
                Path.Combine(tempDir, "unauthorized"),
                CandidateMatrixWithoutRegressions());
            using var unauthorizedCoordinator = new ReplayAssetChangeCoordinator();
            var unauthorizedLifecycle = new ReplayDatasetLifecycleService(
                unauthorizedFixture.DatasetStore,
                unauthorizedFixture.RunStore,
                new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "unauthorized-evidence")),
                unauthorizedCoordinator);
            ReplayDatasetArchiveResult unauthorized = await unauthorizedLifecycle.ArchiveSnapshotAsync(
                unauthorizedFixture.Dataset.DatasetId);
            unauthorized.Succeeded.Should().BeFalse();
            unauthorized.ErrorCode.Should().Be("ReplayDatasetArchiveAuthorizationProviderMissing");

            ReplayFixture activeFixture = await ReplayFixture.CreateAsync(
                Path.Combine(tempDir, "active"),
                CandidateMatrixWithoutRegressions());
            using var activeCoordinator = new ReplayAssetChangeCoordinator();
            var activeLifecycle = CreateDatasetLifecycle(activeFixture, activeCoordinator, activeDatasetId: activeFixture.Dataset.DatasetId);
            ReplayDatasetArchiveResult active = await activeLifecycle.ArchiveSnapshotAsync(activeFixture.Dataset.DatasetId);
            active.Succeeded.Should().BeFalse();
            active.ErrorCode.Should().Be("ReplayDatasetRunActive");

            ReplayFixture runFixture = await ReplayFixture.CreateAsync(
                Path.Combine(tempDir, "run"),
                CandidateMatrixWithoutRegressions());
            ReplayRunReport completedReport = await new ReplayApplicationService(
                runFixture.DatasetStore,
                new DeterministicReplayRunner(runFixture.Decisions),
                new PassingReplayModelValidator(),
                runFixture.RunStore).RunComparisonAsync(new ReplayComparisonRequest
                {
                    RunId = "run-archive-blocked",
                    DatasetId = runFixture.Dataset.DatasetId,
                    BaselineModel = runFixture.BaselineModel,
                    CandidateModel = runFixture.CandidateModel
                });
            completedReport.Status.Should().Be(ReplayRunStatuses.Completed);
            using var runCoordinator = new ReplayAssetChangeCoordinator();
            ReplayDatasetArchiveResult runReferenced = await CreateDatasetLifecycle(runFixture, runCoordinator)
                .ArchiveSnapshotAsync(runFixture.Dataset.DatasetId);
            runReferenced.Succeeded.Should().BeFalse();
            runReferenced.ErrorCode.Should().Be("ReplayDatasetRunReferenced");

            ReplayFixture evidenceFixture = await ReplayFixture.CreateAsync(
                Path.Combine(tempDir, "evidence"),
                CandidateMatrixWithoutRegressions());
            var policy = new ReplayAcceptancePolicy();
            ReplayRunReport evidenceReport = await new ReplayApplicationService(
                evidenceFixture.DatasetStore,
                new DeterministicReplayRunner(evidenceFixture.Decisions),
                new PassingReplayModelValidator(),
                evidenceFixture.RunStore,
                policy).RunComparisonAsync(new ReplayComparisonRequest
                {
                    RunId = "run-evidence-archive-blocked",
                    DatasetId = evidenceFixture.Dataset.DatasetId,
                    BaselineModel = evidenceFixture.BaselineModel,
                    CandidateModel = evidenceFixture.CandidateModel
                });
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "evidence-store"), policy);
            evidenceStore.SaveEvidence(evidenceReport, "qa01", evidenceFixture.Dataset.RootDirectory, evidenceReport.PolicyHash);
            using var evidenceCoordinator = new ReplayAssetChangeCoordinator();
            var evidenceLifecycle = CreateDatasetLifecycle(evidenceFixture, evidenceCoordinator, evidenceStore);
            ReplayDatasetArchiveResult evidenceReferenced = await evidenceLifecycle.ArchiveSnapshotAsync(evidenceFixture.Dataset.DatasetId);
            evidenceReferenced.Succeeded.Should().BeFalse();
            evidenceReferenced.ErrorCode.Should().Be("ReplayDatasetEvidenceReferenced");

            ReplayFixture stagingFixture = await ReplayFixture.CreateAsync(
                Path.Combine(tempDir, "staging"),
                CandidateMatrixWithoutRegressions());
            Directory.CreateDirectory(Path.Combine(
                stagingFixture.DatasetStore.RootDirectory,
                $".{stagingFixture.Dataset.DatasetId}.staging-leftover"));
            using var stagingCoordinator = new ReplayAssetChangeCoordinator();
            ReplayDatasetArchiveResult staging = await CreateDatasetLifecycle(stagingFixture, stagingCoordinator)
                .ArchiveSnapshotAsync(stagingFixture.Dataset.DatasetId);
            staging.Succeeded.Should().BeFalse();
            staging.ErrorCode.Should().Be("ReplayDatasetStagingPresent");

            ReplayFixture idleFixture = await ReplayFixture.CreateAsync(
                Path.Combine(tempDir, "idle"),
                CandidateMatrixWithoutRegressions());
            string originalDatasetPath = idleFixture.Dataset.RootDirectory;
            using var idleCoordinator = new ReplayAssetChangeCoordinator();
            ReplayDatasetArchiveResult idle = await CreateDatasetLifecycle(idleFixture, idleCoordinator)
                .ArchiveSnapshotAsync(idleFixture.Dataset.DatasetId);
            idle.Succeeded.Should().BeTrue();
            Directory.Exists(originalDatasetPath).Should().BeFalse();
            Directory.Exists(idle.ArchivePath).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayEvidence_批准后生产激活接受_篡改Dataset后拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions);
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);

            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-approval",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            ReplayApprovalDecision policyDecision = new ReplayAcceptancePolicy().Evaluate(report);
            policyDecision.Approved.Should().BeTrue();

            var replayPolicy = new ReplayAcceptancePolicy();
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "evidence"), replayPolicy);

            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: false));
            ModelRegistryEntry candidateBeforeApproval = registry.Resolve(fixture.CandidateModel.ModelPath)!;
            var evidenceGate = new ReplayApprovalEvidenceProductionGate(evidenceStore, fixture.DatasetStore, fixture.RunStore);
            var approval = new ReplayApprovalApplicationService(
                registry,
                () => registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true)),
                fixture.RunStore,
                fixture.DatasetStore,
                evidenceStore,
                evidenceGate,
                replayPolicy,
                null,
                () => "qa01",
                () => ProductionRole.Engineer);
            ReplayApprovalResult approvalResult = await approval.ApproveCandidateAsync(new ReplayApprovalRequest
            {
                RunId = report.RunId,
                Report = report,
                CandidateEntry = candidateBeforeApproval,
                ApprovedBy = "qa01",
                ApprovedByRole = "Engineer",
                DatasetPath = fixture.Dataset.RootDirectory
            });
            approvalResult.Succeeded.Should().BeTrue();
            ModelApprovalEvidence evidence = approvalResult.Evidence!;

            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            ModelRegistryEntry approvedCandidate = registry.Resolve(fixture.CandidateModel.ModelPath)!;
            approvedCandidate.ApprovedForProduction.Should().BeTrue();
            approvedCandidate.Manifest!.Approval.ReplayEvidenceId.Should().Be(evidence.EvidenceId);
            approvedCandidate.Manifest.Approval.ReplayEvidenceHash.Should().Be(evidence.EvidenceHash);

            var config = new AppConfig
            {
                StoragePath = tempDir,
                RequireApprovedModelsForProduction = true
            };
            var recipeManager = new RecipeManager(Path.Combine(tempDir, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            var activation = new ProductionModelActivationService(
                config,
                registry,
                recipeManager,
                detection,
                () => registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true)),
                () => true,
                () => null,
                () => "op",
                () => "Engineer",
                (role, entry, reference) => evidenceGate.Validate(role, entry, reference));

            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(approvedCandidate);
            ProductionModelActivationResult activationResult = await activation.ActivatePrimaryAsync(
                reference.ToSelectionValue(),
                "replay-approved-switch",
                useGpu: false,
                gpuIndex: 0);

            activationResult.Succeeded.Should().BeTrue();
            activation.EnsureReadyForProduction().Succeeded.Should().BeTrue();

            var stricterEvidenceStore = new FileModelApprovalEvidenceStore(
                Path.Combine(tempDir, "evidence"),
                new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
                {
                    MinimumValidSamples = 999,
                    MaximumNewMissedDetections = 0
                }));
            var stricterGate = new ReplayApprovalEvidenceProductionGate(stricterEvidenceStore, fixture.DatasetStore, fixture.RunStore);
            stricterGate.ValidateEvidenceBacked(approvedCandidate).Succeeded.Should().BeTrue();

            byte[] originalReport = await File.ReadAllBytesAsync(report.ReportJsonPath);
            WriteValidReportTamper(report.ReportJsonPath);
            ProductionModelReadinessResult readinessAfterReportTamper = activation.EnsureReadyForProduction();
            readinessAfterReportTamper.Succeeded.Should().BeFalse();
            readinessAfterReportTamper.ErrorCode.Should().Be("ReplayEvidenceReportHashMismatch");
            await File.WriteAllBytesAsync(report.ReportJsonPath, originalReport);
            activation.EnsureReadyForProduction().Succeeded.Should().BeTrue();

            await File.AppendAllTextAsync(fixture.Dataset.Samples[0].ImagePath, "tampered");

            ProductionModelReadinessResult readinessAfterTamper = activation.EnsureReadyForProduction();
            readinessAfterTamper.Succeeded.Should().BeFalse();
            readinessAfterTamper.ErrorCode.Should().Be("ReplayEvidenceDatasetHashMismatch");

            var scanner = new ReplayIntegrityScanner(
                registry,
                evidenceGate,
                fixture.DatasetStore,
                fixture.RunStore,
                evidenceStore);
            ReplayIntegrityScanResult scan = await scanner.ScanApprovedModelsAsync();
            scan.Status.Should().Be("Blocking");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayDatasetIntegrityFailed" ||
                finding.ErrorCode == "ReplayEvidenceDatasetHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayApproval_Gate失败会恢复Manifest并清理未发布Evidence()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions);
            var policy = new ReplayAcceptancePolicy();
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore,
                policy);
            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-cleanup",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: false));
            ModelRegistryEntry candidate = registry.Resolve(fixture.CandidateModel.ModelPath)!;
            byte[] originalManifest = await File.ReadAllBytesAsync(candidate.ManifestPath);
            string evidenceRoot = Path.Combine(tempDir, "evidence-cleanup");
            var evidenceStore = new FileModelApprovalEvidenceStore(evidenceRoot, policy);
            var rejectingGate = new ReplayApprovalEvidenceProductionGate(
                evidenceStore,
                new HashMismatchDatasetStore(),
                fixture.RunStore);
            var approval = new ReplayApprovalApplicationService(
                registry,
                () => registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true)),
                fixture.RunStore,
                fixture.DatasetStore,
                evidenceStore,
                rejectingGate,
                policy,
                null,
                () => "qa01",
                () => ProductionRole.Engineer);

            ReplayApprovalResult result = await approval.ApproveCandidateAsync(new ReplayApprovalRequest
            {
                RunId = report.RunId,
                Report = report,
                CandidateEntry = candidate,
                ApprovedBy = "qa01",
                ApprovedByRole = "Engineer",
                DatasetPath = fixture.Dataset.RootDirectory
            });

            result.Succeeded.Should().BeFalse();
            result.IsFaulted.Should().BeFalse();
            (await File.ReadAllBytesAsync(candidate.ManifestPath)).Should().Equal(originalManifest);
            Directory.Exists(evidenceRoot).Should().BeTrue();
            Directory.EnumerateFiles(evidenceRoot, "*.json").Should().BeEmpty();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayApproval_拒绝没有DB运行记录的客户端Report()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var policy = new ReplayAcceptancePolicy();
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "evidence-fabricated"), policy);
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: false));
            var gate = new ReplayApprovalEvidenceProductionGate(evidenceStore, fixture.DatasetStore, fixture.RunStore);
            var approval = new ReplayApprovalApplicationService(
                registry,
                () => registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true)),
                fixture.RunStore,
                fixture.DatasetStore,
                evidenceStore,
                gate,
                policy,
                null,
                () => "qa01",
                () => ProductionRole.Engineer);

            ReplayApprovalResult result = await approval.ApproveCandidateAsync(new ReplayApprovalRequest
            {
                Report = new ReplayRunReport
                {
                    RunId = "fabricated-client-run",
                    Status = ReplayRunStatuses.Completed,
                    DatasetId = fixture.Dataset.DatasetId,
                    DatasetHash = fixture.Dataset.DatasetHash,
                    CandidateModel = fixture.CandidateModel,
                    BaselineModel = fixture.BaselineModel
                },
                CandidateEntry = registry.Resolve(fixture.CandidateModel.ModelPath)!,
                ApprovedBy = "qa01",
                ApprovedByRole = "Engineer"
            });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ReplayApprovalRunMissing");
            Directory.Exists(Path.Combine(tempDir, "evidence-fabricated")).Should().BeFalse();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayApproval_OperatorProvider无法绕过且不产生Evidence或Manifest变更()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var policy = new ReplayAcceptancePolicy();
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                new DeterministicReplayRunner(fixture.Decisions),
                new PassingReplayModelValidator(),
                fixture.RunStore,
                policy);
            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-operator-denied",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: false));
            ModelRegistryEntry candidate = registry.Resolve(fixture.CandidateModel.ModelPath)!;
            byte[] originalManifest = await File.ReadAllBytesAsync(candidate.ManifestPath);
            string evidenceRoot = Path.Combine(tempDir, "evidence-operator-denied");
            var evidenceStore = new FileModelApprovalEvidenceStore(evidenceRoot, policy);
            var gate = new ReplayApprovalEvidenceProductionGate(evidenceStore, fixture.DatasetStore, fixture.RunStore);
            var approval = new ReplayApprovalApplicationService(
                registry,
                () => registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true)),
                fixture.RunStore,
                fixture.DatasetStore,
                evidenceStore,
                gate,
                policy,
                null,
                () => "operator01",
                () => ProductionRole.Operator);

            ReplayApprovalResult result = await approval.ApproveCandidateAsync(new ReplayApprovalRequest
            {
                RunId = report.RunId,
                ApprovedBy = "spoofed-engineer",
                ApprovedByRole = "Engineer",
                CandidateEntry = candidate
            });

            result.Succeeded.Should().BeFalse();
            result.ErrorCode.Should().Be("ReplayApprovalUnauthorized");
            Directory.Exists(evidenceRoot).Should().BeFalse();
            (await File.ReadAllBytesAsync(candidate.ManifestPath)).Should().Equal(originalManifest);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task FormalCompositionRootChain_历史真值DatasetReplay批准激活重启Ready与篡改拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture regressedFixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithRegressions());

            Func<Task> unreviewedDataset = () => regressedFixture.DatasetStore.CreateSnapshotAsync(new ReplayDatasetCreateRequest
            {
                DatasetId = "unreviewed-dataset",
                Query = new DetectionReplayQuery { Limit = 8 },
                Recipe = regressedFixture.Dataset.Recipe,
                BaselineModel = regressedFixture.BaselineModel,
                CandidateModel = regressedFixture.CandidateModel,
                ManualReviewsByDetectionRecordId = new Dictionary<long, ReplayManualReviewRecord>()
            });
            await unreviewedDataset.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Manual review is required*");

            var regressedRunner = new DeterministicReplayRunner(regressedFixture.Decisions);
            var regressedService = new ReplayApplicationService(
                regressedFixture.DatasetStore,
                regressedRunner,
                new PassingReplayModelValidator(),
                regressedFixture.RunStore,
                new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
                {
                    MaximumNewMissedDetections = 0,
                    MaximumNewFalseRejects = 0
                }));
            ReplayRunReport regressedReport = await regressedService.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-regressed-candidate",
                DatasetId = regressedFixture.Dataset.DatasetId,
                BaselineModel = regressedFixture.BaselineModel,
                CandidateModel = regressedFixture.CandidateModel
            });
            ReplayApprovalDecision regressedDecision = new ReplayAcceptancePolicy(new ReplayAcceptancePolicyOptions
            {
                MaximumNewMissedDetections = 0,
                MaximumNewFalseRejects = 0
            }).Evaluate(regressedReport);
            regressedDecision.Approved.Should().BeFalse();

            string cleanRoot = Path.Combine(tempDir, "clean");
            ReplayFixture cleanFixture = await ReplayFixture.CreateAsync(cleanRoot, CandidateMatrixWithoutRegressions());
            var runner = new DeterministicReplayRunner(cleanFixture.Decisions);
            var replayPolicy = new ReplayAcceptancePolicy();
            var service = new ReplayApplicationService(
                cleanFixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                cleanFixture.RunStore,
                replayPolicy);
            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-good-candidate",
                DatasetId = cleanFixture.Dataset.DatasetId,
                BaselineModel = cleanFixture.BaselineModel,
                CandidateModel = cleanFixture.CandidateModel
            });
            report.Status.Should().Be(ReplayRunStatuses.Completed);
            replayPolicy.Evaluate(report).Approved.Should().BeTrue();

            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(cleanFixture.PackageRoot, requireProductionApproval: false));
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(cleanRoot, "evidence"), replayPolicy);
            var evidenceGate = new ReplayApprovalEvidenceProductionGate(evidenceStore, cleanFixture.DatasetStore, cleanFixture.RunStore);
            var approval = new ReplayApprovalApplicationService(
                registry,
                () => registry.Scan(ScanOptions(cleanFixture.PackageRoot, requireProductionApproval: true)),
                cleanFixture.RunStore,
                cleanFixture.DatasetStore,
                evidenceStore,
                evidenceGate,
                replayPolicy,
                null,
                () => "qa01",
                () => ProductionRole.Engineer);
            ReplayApprovalResult approvalResult = await approval.ApproveCandidateAsync(new ReplayApprovalRequest
            {
                RunId = report.RunId,
                Report = report,
                CandidateEntry = registry.Resolve(cleanFixture.CandidateModel.ModelPath)!,
                ApprovedBy = "qa01",
                ApprovedByRole = "Engineer",
                DatasetPath = cleanFixture.Dataset.RootDirectory
            });
            approvalResult.Succeeded.Should().BeTrue();

            registry.Scan(ScanOptions(cleanFixture.PackageRoot, requireProductionApproval: true));
            ModelRegistryEntry approvedCandidate = registry.Resolve(cleanFixture.CandidateModel.ModelPath)!;
            ProductionModelReference reference = ProductionModelReference.FromApprovedPackage(approvedCandidate);
            var config = new AppConfig
            {
                StoragePath = cleanRoot,
                RequireApprovedModelsForProduction = true,
                CurrentModelReference = reference,
                CurrentModelFileName = Path.GetFileName(cleanFixture.CandidateModel.ModelPath)
            };
            var recipeManager = new RecipeManager(Path.Combine(cleanRoot, "recipe.json"));
            recipeManager.LoadOrCreateDefault(config);
            var detection = new FakeDetectionService();
            var activation = new ProductionModelActivationService(
                config,
                registry,
                recipeManager,
                detection,
                () => registry.Scan(ScanOptions(cleanFixture.PackageRoot, requireProductionApproval: true)),
                () => true,
                () => null,
                () => "qa01",
                () => "Engineer",
                (role, entry, reference) => evidenceGate.Validate(role, entry, reference));

            ProductionModelActivationResult activationResult = await activation.ActivatePrimaryAsync(
                reference.ToSelectionValue(),
                "formal-chain",
                useGpu: false,
                gpuIndex: 0);
            activationResult.Succeeded.Should().BeTrue();
            activation.EnsureReadyForProduction().Succeeded.Should().BeTrue();

            var restartedActivation = new ProductionModelActivationService(
                config,
                registry,
                recipeManager,
                detection,
                () => registry.Scan(ScanOptions(cleanFixture.PackageRoot, requireProductionApproval: true)),
                () => true,
                () => null,
                () => "qa01",
                () => "Engineer",
                (role, entry, reference) => evidenceGate.Validate(role, entry, reference));
            restartedActivation.EnsureReadyForProduction().Succeeded.Should().BeTrue();

            WriteValidReportTamper(report.ReportJsonPath);
            ProductionModelReadinessResult tamperedReady = restartedActivation.EnsureReadyForProduction();
            tamperedReady.Succeeded.Should().BeFalse();
            tamperedReady.ErrorCode.Should().Be("ReplayEvidenceReportHashMismatch");

            ProductionModelActivationResult tamperedActivation = await restartedActivation.ActivatePrimaryAsync(
                reference.ToSelectionValue(),
                "tampered-report",
                useGpu: false,
                gpuIndex: 0);
            tamperedActivation.Succeeded.Should().BeFalse();
            tamperedActivation.ErrorCode.Should().Be("ReplayEvidenceReportHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayApplicationService_样本异常形成InvalidSample且取消释放当前模型会话()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions)
            {
                ThrowOnModelId = fixture.CandidateModel.ModelId,
                ThrowOnSampleId = "S2"
            };
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);

            ReplayRunReport invalidReport = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-exception",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            invalidReport.Status.Should().Be(ReplayRunStatuses.Completed);
            invalidReport.Metrics.InvalidSampleCount.Should().Be(1);
            invalidReport.Samples.Should().ContainSingle(sample =>
                sample.SampleId == "S2" &&
                !sample.IsValid &&
                sample.InvalidReason.Contains("fixture inference failed", StringComparison.OrdinalIgnoreCase));
            runner.Events.Should().Contain($"dispose:{fixture.BaselineModel.ModelId}");
            runner.Events.Should().Contain($"dispose:{fixture.CandidateModel.ModelId}");

            using var cts = new CancellationTokenSource();
            var cancelRunner = new DeterministicReplayRunner(fixture.Decisions)
            {
                CancellationSource = cts,
                CancelAfterModelId = fixture.BaselineModel.ModelId,
                CancelAfterCallCount = 2
            };
            var cancelService = new ReplayApplicationService(
                fixture.DatasetStore,
                cancelRunner,
                new PassingReplayModelValidator(),
                new SqliteReplayRunStore(Path.Combine(tempDir, "replay-cancel.db"), Path.Combine(tempDir, "reports-cancel")));

            Func<Task> cancelAct = () => cancelService.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-cancel",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            }, cancellationToken: cts.Token);

            await cancelAct.Should().ThrowAsync<OperationCanceledException>();
            cancelRunner.Events.Should().Contain($"dispose:{fixture.BaselineModel.ModelId}");
            cancelRunner.Events.Should().NotContain($"create:{fixture.CandidateModel.ModelId}");

            var resumeRunner = new DeterministicReplayRunner(fixture.Decisions);
            var resumeService = new ReplayApplicationService(
                fixture.DatasetStore,
                resumeRunner,
                new PassingReplayModelValidator(),
                new SqliteReplayRunStore(Path.Combine(tempDir, "replay-resume.db"), Path.Combine(tempDir, "reports-resume")));
            ReplayRunReport resumed = await resumeService.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-cancel-resume",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });
            resumed.Status.Should().Be(ReplayRunStatuses.Completed);
            resumeRunner.CallCount(fixture.BaselineModel.ModelId).Should().Be(8);
            resumeRunner.CallCount(fixture.CandidateModel.ModelId).Should().Be(8);
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayRunCoordinator_生产与Replay互斥并可取消()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions)
            {
                DelayMs = 150,
                IgnoreCancellationDuringDelay = true
            };
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);
            using var coordinator = new ReplayRunCoordinator(service);

            (await coordinator.TryBeginProductionAsync()).Should().BeTrue();
            Func<Task> blockedByProduction = async () => await coordinator.StartAsync(new ReplayComparisonRequest
            {
                RunId = "run-production-busy",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });
            await blockedByProduction.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("ReplayProductionBusy");
            coordinator.EndProduction();

            Task<ReplayRunReport> runTask = coordinator.StartAsync(new ReplayComparisonRequest
            {
                RunId = "run-coordinator-cancel",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });
            for (int i = 0; i < 20 && !coordinator.IsReplayRunning; i++)
            {
                await Task.Delay(10);
            }

            coordinator.IsReplayRunning.Should().BeTrue();
            (await coordinator.TryBeginProductionAsync()).Should().BeFalse();
            ReplayCancelRequestResult requested = await coordinator.RequestCancelAsync();
            requested.Succeeded.Should().BeTrue();
            requested.Progress.Should().NotBeNull();
            requested.Progress!.Status.Should().Be(ReplayRunStatuses.CancelRequested);

            ReplayRunRecord? cancelRequestedRecord = await fixture.RunStore.LoadRunRecordAsync("run-coordinator-cancel");
            cancelRequestedRecord.Should().NotBeNull();
            cancelRequestedRecord!.Status.Should().Be(ReplayRunStatuses.CancelRequested);

            Func<Task> canceled = async () => await runTask;
            await canceled.Should().ThrowAsync<OperationCanceledException>();
            coordinator.IsReplayRunning.Should().BeFalse();
            ReplayRunRecord? canceledRecord = await fixture.RunStore.LoadRunRecordAsync("run-coordinator-cancel");
            canceledRecord.Should().NotBeNull();
            canceledRecord!.Status.Should().Be(ReplayRunStatuses.Canceled);

            ReplayCancelRequestResult noRunning = await coordinator.RequestCancelAsync();
            noRunning.Succeeded.Should().BeFalse();
            noRunning.ErrorCode.Should().Be("ReplayNotRunning");

            ReplayRunReport completed = await coordinator.StartAsync(new ReplayComparisonRequest
            {
                RunId = "run-coordinator-completed",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });
            completed.Status.Should().Be(ReplayRunStatuses.Completed);
            ReplayCancelRequestResult completedCancel = await coordinator.RequestCancelAsync();
            completedCancel.Succeeded.Should().BeFalse();
            completedCancel.ErrorCode.Should().Be("ReplayNotRunning");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayIntegrityScanner_报告Staging未发布Evidence和Run残留()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var runner = new DeterministicReplayRunner(fixture.Decisions);
            var service = new ReplayApplicationService(
                fixture.DatasetStore,
                runner,
                new PassingReplayModelValidator(),
                fixture.RunStore);
            ReplayRunReport report = await service.RunComparisonAsync(new ReplayComparisonRequest
            {
                RunId = "run-for-orphan-evidence",
                DatasetId = fixture.Dataset.DatasetId,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel
            });

            string stagingDirectory = Path.Combine(fixture.DatasetStore.RootDirectory, ".fixture-dataset.staging-leftover");
            Directory.CreateDirectory(stagingDirectory);

            await fixture.RunStore.RecordRunStartedAsync(new ReplayRunReport
            {
                RunId = "run-interrupted-residue",
                DatasetId = fixture.Dataset.DatasetId,
                DatasetHash = fixture.Dataset.DatasetHash,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel,
                Status = ReplayRunStatuses.Running,
                StartedAt = DateTimeOffset.UtcNow
            });
            await fixture.RunStore.MarkNonTerminalRunsInterruptedAsync("default");
            await fixture.RunStore.RecordRunStartedAsync(new ReplayRunReport
            {
                RunId = "run-non-terminal-residue",
                DatasetId = fixture.Dataset.DatasetId,
                DatasetHash = fixture.Dataset.DatasetHash,
                BaselineModel = fixture.BaselineModel,
                CandidateModel = fixture.CandidateModel,
                Status = ReplayRunStatuses.Running,
                StartedAt = DateTimeOffset.UtcNow
            });

            var policy = new ReplayAcceptancePolicy();
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "evidence-orphan"), policy);
            evidenceStore.SaveEvidence(report, "qa01", fixture.Dataset.RootDirectory, report.PolicyHash);
            await File.WriteAllTextAsync(Path.Combine(evidenceStore.RootDirectory, "broken.json"), "{");

            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            var gate = new ReplayApprovalEvidenceProductionGate(evidenceStore, fixture.DatasetStore, fixture.RunStore);
            var scanner = new ReplayIntegrityScanner(
                registry,
                gate,
                fixture.DatasetStore,
                fixture.RunStore,
                evidenceStore);

            ReplayIntegrityScanResult scan = await scanner.ScanApprovedModelsAsync();

            scan.Status.Should().Be("Blocking");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayDatasetStagingOrphan" &&
                finding.Severity == "Warning");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayEvidenceUnpublished" &&
                finding.Severity == "Warning");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayEvidenceParseFailed" &&
                finding.Scope == "EvidenceStorage");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayRunInterruptedResidue" &&
                finding.Severity == "Warning");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayRunNonTerminalResidue" &&
                finding.Severity == "Blocking");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayLegacyApproval_仅允许当前Primary原槽且Hash变化后拒绝()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            ModelRegistryEntry legacyEntry = registry.Resolve(fixture.BaselineModel.ModelPath)!;
            ProductionModelReference originalReference = ProductionModelReference.FromApprovedPackage(legacyEntry);

            ModelPackageManifest manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
                File.ReadAllText(legacyEntry.ManifestPath),
                ReplayJson.Options) ?? throw new InvalidOperationException("Manifest parse failed.");
            manifest.Approval.LegacyMigration = new ModelApprovalLegacyMigration
            {
                MigrationId = "legacy-test",
                ModelRole = "Primary",
                ModelId = legacyEntry.ModelId,
                Version = legacyEntry.Version,
                ModelHash = legacyEntry.ModelHash,
                ManifestHash = string.Empty,
                ConfigReference = originalReference.ToSelectionValue(),
                MigratedAt = DateTimeOffset.UtcNow
            };
            manifest.Approval.LegacyMigration.ManifestHash =
                ReplayApprovalEvidenceProductionGate.ComputeLegacyManifestHash(manifest);
            File.WriteAllText(legacyEntry.ManifestPath, JsonSerializer.Serialize(manifest, ReplayJson.Options));

            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            legacyEntry = registry.Resolve(fixture.BaselineModel.ModelPath)!;
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "legacy-evidence"));
            var originalSlotGate = new ReplayApprovalEvidenceProductionGate(
                evidenceStore,
                fixture.DatasetStore,
                fixture.RunStore);

            originalSlotGate.Validate(ModelRole.Primary, legacyEntry, originalReference).Succeeded.Should().BeTrue();

            var scanner = new ReplayIntegrityScanner(
                registry,
                originalSlotGate,
                fixture.DatasetStore,
                fixture.RunStore,
                evidenceStore);
            ReplayIntegrityScanResult scan = await scanner.ScanApprovedModelsAsync();
            scan.Status.Should().Be("Warning");
            scan.Findings.Should().Contain(finding =>
                finding.ErrorCode == "ReplayLegacyApprovalActive" &&
                finding.Severity == "Warning");

            var wrongSlotGate = new ReplayApprovalEvidenceProductionGate(
                evidenceStore,
                fixture.DatasetStore,
                fixture.RunStore);
            ProductionModelReadinessResult wrongSlot = wrongSlotGate.Validate(
                ModelRole.Auxiliary1,
                legacyEntry,
                originalReference);
            wrongSlot.Succeeded.Should().BeFalse();
            wrongSlot.ErrorCode.Should().Be("ReplayLegacyRoleMismatch");

            await using (FileStream tamperStream = new FileStream(
                legacyEntry.ModelPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read))
            {
                await tamperStream.WriteAsync(new byte[] { 9 });
            }
            ProductionModelReadinessResult tampered = originalSlotGate.Validate(ModelRole.Primary, legacyEntry, originalReference);
            tampered.Succeeded.Should().BeFalse();
            tampered.ErrorCode.Should().Be("ReplayLegacyHashMismatch");
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ReplayLegacyApproval_Auxiliary槽绑定角色和原配置引用()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            ReplayFixture fixture = await ReplayFixture.CreateAsync(tempDir, CandidateMatrixWithoutRegressions());
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            ModelRegistryEntry legacyEntry = registry.Resolve(fixture.BaselineModel.ModelPath)!;
            ProductionModelReference auxiliaryReference = ProductionModelReference.FromApprovedPackage(legacyEntry);
            WriteLegacyMigration(legacyEntry, ModelRole.Auxiliary1, auxiliaryReference);

            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            legacyEntry = registry.Resolve(fixture.BaselineModel.ModelPath)!;
            var evidenceStore = new FileModelApprovalEvidenceStore(Path.Combine(tempDir, "legacy-aux-evidence"));
            var gate = new ReplayApprovalEvidenceProductionGate(
                evidenceStore,
                fixture.DatasetStore,
                fixture.RunStore);

            gate.Validate(ModelRole.Auxiliary1, legacyEntry, auxiliaryReference)
                .Succeeded.Should().BeTrue();

            ProductionModelReadinessResult wrongRole = gate.Validate(
                ModelRole.Primary,
                legacyEntry,
                auxiliaryReference);
            wrongRole.Succeeded.Should().BeFalse();
            wrongRole.ErrorCode.Should().Be("ReplayLegacyRoleMismatch");

            ProductionModelReference legacyOnnxReference = ProductionModelReference.FromLegacyOnnx(
                Path.GetFileName(legacyEntry.ModelPath),
                legacyEntry.ModelHash);
            ProductionModelReadinessResult wrongReference = gate.Validate(
                ModelRole.Auxiliary1,
                legacyEntry,
                legacyOnnxReference);
            wrongReference.Succeeded.Should().BeFalse();
            wrongReference.ErrorCode.Should().Be("ReplayEvidenceConfigReferenceMismatch");

            WriteLegacyMigration(
                legacyEntry,
                ModelRole.Auxiliary1,
                auxiliaryReference,
                legacyOnnxReference.ToSelectionValue());
            registry.Scan(ScanOptions(fixture.PackageRoot, requireProductionApproval: true));
            legacyEntry = registry.Resolve(fixture.BaselineModel.ModelPath)!;
            ProductionModelReadinessResult tamperedConfigReference = gate.Validate(
                ModelRole.Auxiliary1,
                legacyEntry,
                auxiliaryReference);
            tamperedConfigReference.Succeeded.Should().BeFalse();
            tamperedConfigReference.ErrorCode.Should().Be("ReplayLegacyConfigReferenceMismatch");

            await Task.CompletedTask;
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    private static Dictionary<string, string> CandidateMatrixWithRegressions()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S1"] = ReplayDecisions.OK,
            ["S2"] = ReplayDecisions.NG,
            ["S3"] = ReplayDecisions.NG,
            ["S4"] = ReplayDecisions.OK,
            ["S5"] = ReplayDecisions.OK,
            ["S6"] = ReplayDecisions.NG,
            ["S7"] = ReplayDecisions.OK,
            ["S8"] = ReplayDecisions.NG
        };
    }

    private static ReplayDatasetLifecycleService CreateDatasetLifecycle(
        ReplayFixture fixture,
        ReplayAssetChangeCoordinator coordinator,
        IModelApprovalEvidenceStore? evidenceStore = null,
        string activeDatasetId = "")
    {
        evidenceStore ??= new FileModelApprovalEvidenceStore(
            Path.Combine(Path.GetDirectoryName(fixture.Dataset.RootDirectory) ?? fixture.Dataset.RootDirectory, "evidence-lifecycle"));
        return new ReplayDatasetLifecycleService(
            fixture.DatasetStore,
            fixture.RunStore,
            evidenceStore,
            coordinator,
            null,
            () => "qa01",
            () => ProductionRole.Engineer,
            id => string.Equals(id, activeDatasetId, StringComparison.OrdinalIgnoreCase));
    }

    private static GoldenReplayArtifactFixture CreateGoldenArtifactFixture(int version)
    {
        bool v2 = version == 2;
        ReplayAcceptancePolicyOptions policy = CreateGoldenPolicy(version);
        string policyHash = ReplayAcceptancePolicy.ComputePolicyHash(policy);
        var report = new ReplayRunReport
        {
            RunId = $"golden-v{version}-run",
            Status = ReplayRunStatuses.Completed,
            DatasetId = $"golden-v{version}-dataset",
            DatasetHash = $"dataset-v{version}-hash",
            BaselineModel = CreateGoldenModel(
                "baseline-golden",
                "1.0.0",
                "1111111111111111111111111111111111111111111111111111111111111111",
                ModelApprovalStatuses.Approved),
            CandidateModel = CreateGoldenModel(
                "candidate-golden",
                version == 1 ? "1.1.0" : "2.0.0",
                "2222222222222222222222222222222222222222222222222222222222222222",
                ModelApprovalStatuses.Pending),
            StartedAt = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2026, 7, 1, 8, 5, 0, TimeSpan.Zero),
            Metrics = CreateGoldenMetrics(v2),
            Samples = CreateGoldenSamples(v2),
            Errors = Array.Empty<string>(),
            ReportJsonPath = $"reports/golden-v{version}-run/report.json",
            ReportCsvPath = $"reports/golden-v{version}-run/report.csv",
            PolicyVersion = version,
            RecipeHash = $"recipe-v{version}-hash",
            RuleSetHash = $"rules-v{version}-hash",
            PolicyHash = policyHash,
            PolicySnapshot = policy.Clone(),
            BaselineModelHash = "1111111111111111111111111111111111111111111111111111111111111111",
            CandidateModelHash = "2222222222222222222222222222222222222222222222222222222222222222"
        };
        report.ReportHash = ReplayArtifactHashing.ComputeReportHash(report);

        var evidence = new ModelApprovalEvidence
        {
            EvidenceId = $"golden-v{version}-evidence",
            CreatedAt = new DateTimeOffset(2026, 7, 1, 8, 10, 0, TimeSpan.Zero),
            ApprovedBy = "qa01",
            DatasetId = report.DatasetId,
            DatasetHash = report.DatasetHash,
            DatasetPath = $"datasets/{report.DatasetId}",
            ReplayRunId = report.RunId,
            ReplayReportHash = report.ReportHash,
            BaselineModel = report.BaselineModel,
            CandidateModel = report.CandidateModel,
            Metrics = report.Metrics,
            PolicyReasons = Array.Empty<string>(),
            ReplayReportPath = report.ReportJsonPath,
            PolicyVersion = report.PolicyVersion,
            PolicyHash = report.PolicyHash,
            PolicySnapshot = report.PolicySnapshot.Clone(),
            RecipeHash = report.RecipeHash,
            RuleSetHash = report.RuleSetHash,
            BaselineModelHash = report.BaselineModelHash,
            CandidateModelHash = report.CandidateModelHash
        };
        evidence.EvidenceHash = ReplayArtifactHashing.ComputeEvidenceHash(evidence);

        return version switch
        {
            1 => new GoldenReplayArtifactFixture(
                report,
                evidence,
                "8a4a2dd5fd32b41547e17bfe88f3f7eaf64c9957dfbd16a07fc7e4abfdac52ab",
                "9919096ca8ad0838f0788aef7cfd1401eb9f2f999a27deac8167f80a71ddc830",
                "e690d82296ad19f4547eaa4c52ff47a86a721042c24212387b929f0541e9d57e"),
            2 => new GoldenReplayArtifactFixture(
                report,
                evidence,
                "c22ef524f3222cdf606be93b43cf63ae4bd68eed9a2d3e9b5ad1851e173d9f5b",
                "ce08dc4f13cffe017ed665c5aa38ae96e547f4f68547ec08fdc0601a88c68aa7",
                "9c43ef531e53d167254dabae9a60f8601893ee1a398d9f7c0cedd3f137d0e253"),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, "Unsupported golden fixture version.")
        };
    }

    private static ReplayAcceptancePolicyOptions CreateGoldenPolicy(int version)
    {
        return new ReplayAcceptancePolicyOptions
        {
            Version = version,
            MinimumValidSamples = 4,
            MaximumInvalidSampleCount = version >= 2 ? 1 : int.MaxValue,
            MaximumInvalidSampleRate = version >= 2 ? 0.25 : null,
            MaximumNewMissedDetections = 0,
            MaximumCandidateMissedDetectionRate = version >= 2 ? 0 : null,
            MaximumNewFalseRejects = 0,
            MaximumFalseRejectRateIncrease = version >= 2 ? 0 : null,
            MinimumCandidateAccuracy = 0.95,
            MaximumCandidateP95ElapsedMs = 100,
            MaximumP95InferenceIncreaseRatio = 2
        };
    }

    private static ReplayModelIdentity CreateGoldenModel(
        string modelId,
        string version,
        string hash,
        string approvalStatus)
    {
        return new ReplayModelIdentity
        {
            ModelId = modelId,
            Version = version,
            Sha256 = hash,
            ModelPath = $"models/{modelId}/model.onnx",
            ManifestPath = $"models/{modelId}/manifest.json",
            Labels = new[] { "part", "defect" },
            TaskType = "Detect",
            InputWidth = 640,
            InputHeight = 640,
            ApprovalStatus = approvalStatus,
            IsPackage = true
        };
    }

    private static ReplayComparisonMetrics CreateGoldenMetrics(bool v2)
    {
        return new ReplayComparisonMetrics
        {
            SampleCount = 4,
            TotalSampleCount = v2 ? 5 : 0,
            ValidSampleCount = v2 ? 4 : 0,
            CandidateNewMissedDetectionCount = 0,
            CandidateFixedMissedDetectionCount = 1,
            CandidateNewFalseRejectCount = 0,
            CandidateFixedFalseRejectCount = 1,
            BaselineMissedDetectionCount = v2 ? 1 : 0,
            BaselineMissedDetectionRate = v2 ? 0.5 : 0,
            CandidateMissedDetectionCount = 0,
            CandidateMissedDetectionRate = 0,
            BaselineFalseRejectCount = v2 ? 1 : 0,
            BaselineFalseRejectRate = v2 ? 0.5 : 0,
            CandidateFalseRejectCount = 0,
            CandidateFalseRejectRate = 0,
            FalseRejectRateIncrease = v2 ? -0.5 : 0,
            ChangedDecisionCount = 2,
            InvalidSampleCount = v2 ? 1 : 0,
            BaselineCorrectCount = 2,
            CandidateCorrectCount = 4,
            BaselineAccuracy = 0.5,
            CandidateAccuracy = 1,
            BaselineP95ElapsedMs = 41,
            CandidateP95ElapsedMs = 43
        };
    }

    private static List<ReplaySampleComparison> CreateGoldenSamples(bool v2)
    {
        return new List<ReplaySampleComparison>
        {
            CreateGoldenSample("S1", "INS-001", ReplayDecisions.OK, ReplayDecisions.OK, ReplayDecisions.OK, "Confirmed", false, 21, 22, true, string.Empty),
            CreateGoldenSample("S2", "INS-002", ReplayDecisions.NG, ReplayDecisions.OK, ReplayDecisions.NG, "FixedMissedDetection", true, 39, 42, true, string.Empty),
            CreateGoldenSample("S3", "INS-003", ReplayDecisions.OK, ReplayDecisions.NG, ReplayDecisions.OK, "FixedFalseReject", true, 41, 43, true, string.Empty),
            CreateGoldenSample("S4", "INS-004", ReplayDecisions.NG, ReplayDecisions.NG, ReplayDecisions.NG, "Confirmed", false, 30, 31, true, string.Empty),
            CreateGoldenSample("S5", "INS-005", ReplayDecisions.NG, ReplayDecisions.NG, ReplayDecisions.NG, "InvalidSample", false, 0, 0, !v2, v2 ? "candidate inference failed" : string.Empty)
        };
    }

    private static ReplaySampleComparison CreateGoldenSample(
        string sampleId,
        string inspectionId,
        string groundTruth,
        string baselineDecision,
        string candidateDecision,
        string classification,
        bool decisionChanged,
        long baselineElapsedMs,
        long candidateElapsedMs,
        bool isValid,
        string invalidReason)
    {
        return new ReplaySampleComparison
        {
            SampleId = sampleId,
            InspectionId = inspectionId,
            GroundTruth = groundTruth,
            BaselineDecision = baselineDecision,
            CandidateDecision = candidateDecision,
            Classification = classification,
            DecisionChanged = decisionChanged,
            BaselineElapsedMs = baselineElapsedMs,
            CandidateElapsedMs = candidateElapsedMs,
            BaselineRuleSummary = "count>=1",
            CandidateRuleSummary = "count>=1",
            IsValid = isValid,
            InvalidReason = invalidReason
        };
    }

    private sealed record GoldenReplayArtifactFixture(
        ReplayRunReport Report,
        ModelApprovalEvidence Evidence,
        string ExpectedReportHash,
        string ExpectedEvidenceHash,
        string ExpectedPolicyHash);

    private static Dictionary<string, string> CandidateMatrixWithoutRegressions()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S1"] = ReplayDecisions.OK,
            ["S2"] = ReplayDecisions.NG,
            ["S3"] = ReplayDecisions.NG,
            ["S4"] = ReplayDecisions.NG,
            ["S5"] = ReplayDecisions.OK,
            ["S6"] = ReplayDecisions.OK,
            ["S7"] = ReplayDecisions.OK,
            ["S8"] = ReplayDecisions.NG
        };
    }

    private static ModelRegistryScanOptions ScanOptions(string packageRoot, bool requireProductionApproval)
    {
        return new ModelRegistryScanOptions
        {
            PackageDirectory = packageRoot,
            RequireProductionApproval = requireProductionApproval,
            Warmup = (_, _) => true
        };
    }

    private static string CreatePackage(string packageRoot, string modelId, string version, bool approved)
    {
        string packageDir = Path.Combine(packageRoot, modelId);
        Directory.CreateDirectory(packageDir);
        string modelPath = Path.Combine(packageDir, "model.onnx");
        File.WriteAllBytes(modelPath, new byte[] { (byte)modelId.Length, (byte)version.Length, 1, 2, 3 });
        string hash = ComputeSha256(modelPath);
        File.WriteAllText(
            Path.Combine(packageDir, "manifest.json"),
            JsonSerializer.Serialize(new ModelPackageManifest
            {
                ModelId = modelId,
                Version = version,
                ModelFileName = "model.onnx",
                ModelHash = hash,
                Labels = new List<string> { "part" },
                TaskType = "Detect",
                InputWidth = 640,
                InputHeight = 640,
                Approval = new ModelApprovalMetadata
                {
                    Status = approved ? ModelApprovalStatuses.Approved : ModelApprovalStatuses.Pending,
                    ApprovedBy = approved ? "qa" : string.Empty,
                    ApprovedAt = approved ? DateTimeOffset.UtcNow : null
                }
            }));
        return modelPath;
    }

    private static void WriteValidReportTamper(string reportPath)
    {
        ReplayRunReport tampered = JsonSerializer.Deserialize<ReplayRunReport>(
            File.ReadAllText(reportPath),
            ReplayJson.Options) ?? throw new InvalidOperationException("Report parse failed.");
        tampered.Metrics.CandidateCorrectCount = Math.Max(0, tampered.Metrics.CandidateCorrectCount - 1);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(tampered, ReplayJson.Options));
    }

    private static void WriteLegacyMigration(
        ModelRegistryEntry entry,
        ModelRole role,
        ProductionModelReference reference,
        string? configReferenceOverride = null)
    {
        ModelPackageManifest manifest = JsonSerializer.Deserialize<ModelPackageManifest>(
            File.ReadAllText(entry.ManifestPath),
            ReplayJson.Options) ?? throw new InvalidOperationException("Manifest parse failed.");
        manifest.Approval.LegacyMigration = new ModelApprovalLegacyMigration
        {
            MigrationId = $"legacy-{role}",
            ModelRole = role.ToString(),
            ModelId = entry.ModelId,
            Version = entry.Version,
            ModelHash = entry.ModelHash,
            ManifestHash = string.Empty,
            ConfigReference = configReferenceOverride ?? reference.ToSelectionValue(),
            MigratedAt = DateTimeOffset.UtcNow
        };
        manifest.Approval.LegacyMigration.ManifestHash =
            ReplayApprovalEvidenceProductionGate.ComputeLegacyManifestHash(manifest);
        File.WriteAllText(entry.ManifestPath, JsonSerializer.Serialize(manifest, ReplayJson.Options));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "ClearFrostTests", nameof(ReplayClosureTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class ReplayFixture
    {
        private ReplayFixture() { }

        public string PackageRoot { get; private init; } = string.Empty;
        public ReplayDatasetSnapshot Dataset { get; private init; } = new ReplayDatasetSnapshot();
        public FileReplayDatasetStore DatasetStore { get; private init; } = null!;
        public SqliteReplayRunStore RunStore { get; private init; } = null!;
        public ReplayModelIdentity BaselineModel { get; private init; } = new ReplayModelIdentity();
        public ReplayModelIdentity CandidateModel { get; private init; } = new ReplayModelIdentity();
        public Dictionary<string, Dictionary<string, string>> Decisions { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

        public static async Task<ReplayFixture> CreateAsync(string tempDir, Dictionary<string, string> candidateDecisions)
        {
            string imageSourceDir = Path.Combine(tempDir, "source-images");
            Directory.CreateDirectory(imageSourceDir);
            List<DetectionRecord> records = new();
            Dictionary<long, ReplayManualReviewRecord> reviews = new();
            string[] groundTruth = { "OK", "NG", "NG", "NG", "OK", "OK", "OK", "NG" };
            string[] baseline = { "OK", "NG", "OK", "NG", "NG", "OK", "OK", "NG" };

            for (int i = 0; i < 8; i++)
            {
                string sampleId = $"S{i + 1}";
                string imagePath = Path.Combine(imageSourceDir, $"{sampleId}.png");
                using (var image = new Mat(32, 32, MatType.CV_8UC3, new Scalar(20 + i * 20, 80, 160)))
                {
                    Cv2.Rectangle(image, new Rect(4 + i, 4, 12, 12), new Scalar(250, 250, 250), thickness: 1);
                    Cv2.ImWrite(imagePath, image);
                }

                string inspectionId = $"CF-FIXTURE-{sampleId}";
                long detectionRecordId = i + 1;
                records.Add(new DetectionRecord
                {
                    Id = detectionRecordId,
                    Timestamp = new DateTime(2026, 6, 30, 8, 0, 0).AddMinutes(i),
                    IsQualified = baseline[i] == ReplayDecisions.OK,
                    InspectionId = inspectionId,
                    ImagePath = imagePath,
                    RecipeId = "recipe-clearfrost-fixture",
                    RecipeVersion = "20260630080000000",
                    ModelId = "baseline-model",
                    ModelVersion = "1",
                    ModelName = "baseline-model",
                    RuleSummary = "fixture rule",
                    RuleSetJson = JsonSerializer.Serialize(CreateRuleSet(), ReplayJson.Options),
                    ResultJson = "{}"
                });
                reviews[detectionRecordId] = new ReplayManualReviewRecord
                {
                    SampleId = sampleId,
                    InspectionId = inspectionId,
                    GroundTruth = groundTruth[i],
                    SystemDecision = baseline[i],
                    Disposition = ResolveDisposition(baseline[i], groundTruth[i]),
                    ReviewerId = "qa01",
                    ReviewerRole = "Engineer",
                    Revision = 1,
                    ReviewedAt = new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero).AddMinutes(i)
                };
            }

            string packageRoot = Path.Combine(tempDir, "models");
            string baselinePath = CreatePackage(packageRoot, "baseline-model", "1", approved: true);
            string candidatePath = CreatePackage(packageRoot, "candidate-model", "2", approved: false);
            var registry = new ModelRegistry();
            registry.Scan(ScanOptions(packageRoot, requireProductionApproval: false));
            ReplayModelIdentity baselineModel = ReplayModelIdentity.FromRegistryEntry(registry.Resolve(baselinePath)!);
            ReplayModelIdentity candidateModel = ReplayModelIdentity.FromRegistryEntry(registry.Resolve(candidatePath)!);

            var database = new FakeDatabaseService(records);
            var datasetStore = new FileReplayDatasetStore(database, Path.Combine(tempDir, "datasets"));
            ReplayDatasetSnapshot dataset = await datasetStore.CreateSnapshotAsync(new ReplayDatasetCreateRequest
            {
                DatasetId = "fixture-dataset",
                Query = new DetectionReplayQuery { Limit = 8 },
                Recipe = new ReplayRecipeSnapshot
                {
                    RecipeId = "recipe-clearfrost-fixture",
                    RecipeVersion = "20260630080000000",
                    Confidence = 0.5f,
                    IouThreshold = 0.45f,
                    Roi = new[] { 0.1f, 0.1f, 0.8f, 0.8f },
                    RuleSet = CreateRuleSet(),
                    RuleSetJson = JsonSerializer.Serialize(CreateRuleSet(), ReplayJson.Options)
                },
                BaselineModel = baselineModel,
                CandidateModel = candidateModel,
                ManualReviewsByDetectionRecordId = reviews
            });

            return new ReplayFixture
            {
                PackageRoot = packageRoot,
                Dataset = dataset,
                DatasetStore = datasetStore,
                RunStore = new SqliteReplayRunStore(Path.Combine(tempDir, "replay.db"), Path.Combine(tempDir, "reports")),
                BaselineModel = baselineModel,
                CandidateModel = candidateModel,
                Decisions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [baselineModel.ModelId] = Enumerable.Range(0, 8)
                        .ToDictionary(index => $"S{index + 1}", index => baseline[index], StringComparer.OrdinalIgnoreCase),
                    [candidateModel.ModelId] = candidateDecisions
                }
            };
        }

        private static InspectionRuleSet CreateRuleSet()
        {
            return new InspectionRuleSet
            {
                Rules = new List<InspectionRule>
                {
                    new InspectionRule
                    {
                        Name = "fixture-count",
                        Type = InspectionRuleTypes.Count,
                        Label = "part",
                        Operator = InspectionRuleOperators.GreaterThanOrEqual,
                        Count = 1
                    }
                }
            };
        }

        private static string ResolveDisposition(string systemDecision, string groundTruth)
        {
            if (string.Equals(systemDecision, groundTruth, StringComparison.Ordinal))
            {
                return ReplayReviewDispositions.Confirmed;
            }

            return string.Equals(systemDecision, ReplayDecisions.NG, StringComparison.Ordinal)
                ? ReplayReviewDispositions.FalseReject
                : ReplayReviewDispositions.MissedDetection;
        }
    }

    private sealed class PassingReplayModelValidator : IReplayModelValidator
    {
        public Task<ReplayModelValidationResult> ValidateAsync(
            ReplayModelIdentity model,
            ReplayModelValidationOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ReplayModelValidationResult.Ok());
        }
    }

    private sealed class DeterministicReplayRunner : IReplayInferenceRunner
    {
        private readonly Dictionary<string, Dictionary<string, string>> _decisions;
        private readonly Dictionary<string, int> _callCounts = new(StringComparer.OrdinalIgnoreCase);

        public DeterministicReplayRunner(Dictionary<string, Dictionary<string, string>> decisions)
        {
            _decisions = decisions;
        }

        public List<string> Events { get; } = new();
        public string ThrowOnModelId { get; init; } = string.Empty;
        public string ThrowOnSampleId { get; init; } = string.Empty;
        public CancellationTokenSource? CancellationSource { get; init; }
        public string CancelAfterModelId { get; init; } = string.Empty;
        public int CancelAfterCallCount { get; init; }
        public int DelayMs { get; init; }
        public bool IgnoreCancellationDuringDelay { get; init; }

        public int CallCount(string modelId)
        {
            return _callCounts.TryGetValue(modelId, out int count) ? count : 0;
        }

        public Task<IReplayInferenceSession> CreateSessionAsync(
            ReplayModelIdentity model,
            ReplayRecipeSnapshot recipe,
            CancellationToken cancellationToken = default)
        {
            Events.Add($"create:{model.ModelId}");
            return Task.FromResult<IReplayInferenceSession>(new Session(this, model));
        }

        private sealed class Session : IReplayInferenceSession
        {
            private readonly DeterministicReplayRunner _owner;

            public Session(DeterministicReplayRunner owner, ReplayModelIdentity model)
            {
                _owner = owner;
                Model = model;
            }

            public ReplayModelIdentity Model { get; }

            public async Task<ReplayInferenceOutput> RunAsync(
                ReplayDatasetSample sample,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_owner.DelayMs > 0)
                {
                    if (_owner.IgnoreCancellationDuringDelay)
                    {
                        await Task.Delay(_owner.DelayMs);
                    }
                    else
                    {
                        await Task.Delay(_owner.DelayMs, cancellationToken);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();

                _owner._callCounts[Model.ModelId] = _owner.CallCount(Model.ModelId) + 1;
                _owner.Events.Add($"run:{Model.ModelId}:{sample.SampleId}");
                if (_owner.CancellationSource != null &&
                    _owner.CancelAfterCallCount > 0 &&
                    string.Equals(_owner.CancelAfterModelId, Model.ModelId, StringComparison.OrdinalIgnoreCase) &&
                    _owner.CallCount(Model.ModelId) >= _owner.CancelAfterCallCount)
                {
                    _owner.CancellationSource.Cancel();
                }

                if (string.Equals(_owner.ThrowOnModelId, Model.ModelId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_owner.ThrowOnSampleId, sample.SampleId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("fixture inference failed");
                }

                string decision = _owner._decisions[Model.ModelId][sample.SampleId];
                return new ReplayInferenceOutput
                {
                    SampleId = sample.SampleId,
                    InspectionId = sample.InspectionId,
                    Decision = decision,
                    Confidence = 0.9f,
                    ElapsedMs = 1,
                    ModelId = Model.ModelId,
                    ModelVersion = Model.Version,
                    ModelHash = Model.Sha256,
                    RuleSummary = $"fixture {decision}"
                };
            }

            public ValueTask DisposeAsync()
            {
                _owner.Events.Add($"dispose:{Model.ModelId}");
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeDatabaseService : IDatabaseService
    {
        private readonly List<DetectionRecord> _records;

        public FakeDatabaseService(List<DetectionRecord> records)
        {
            _records = records;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task SaveDetectionRecordAsync(DetectionRecord record) => Task.CompletedTask;
        public Task<List<DetectionRecord>> GetRecordsAsync(DateTime? startDate = null, DateTime? endDate = null, bool? isQualified = null, int limit = 100)
            => Task.FromResult(_records.Take(limit).ToList());
        public Task<DetectionRecord?> GetDetectionRecordByIdAsync(long id)
            => Task.FromResult(_records.FirstOrDefault(record => record.Id == id));
        public Task<List<DetectionRecord>> GetDetectionRecordsByInspectionIdAsync(string inspectionId)
            => Task.FromResult(_records
                .Where(record => string.Equals(record.InspectionId, inspectionId, StringComparison.OrdinalIgnoreCase))
                .ToList());
        public Task<List<DetectionTraceRecord>> GetTraceRecordsAsync(DetectionTraceQuery query)
            => Task.FromResult(new List<DetectionTraceRecord>());
        public Task<DetectionTracePage> GetTraceRecordPageAsync(DetectionTraceQuery query)
            => Task.FromResult(new DetectionTracePage());
        public Task<List<DetectionRecord>> GetReplayRecordsAsync(DetectionReplayQuery query)
        {
            IEnumerable<DetectionRecord> result = _records;
            if (query.IsQualified.HasValue)
            {
                result = result.Where(record => record.IsQualified == query.IsQualified.Value);
            }

            return Task.FromResult(result.Take(query.Limit <= 0 ? 100 : query.Limit).ToList());
        }

        public Task<List<string>> GetTraceDateKeysAsync(bool? isQualified = null, int limit = 60)
            => Task.FromResult(new List<string>());
        public Task<List<string>> GetTraceHourKeysAsync(DateTime date, bool? isQualified = null)
            => Task.FromResult(new List<string>());
        public Task<(int total, int pass, int fail)> GetStatisticsAsync(DateTime date)
            => Task.FromResult((_records.Count, _records.Count(r => r.IsQualified), _records.Count(r => !r.IsQualified)));
        public Task<int> CleanupOldRecordsAsync(int daysToKeep) => Task.FromResult(0);
        public void Dispose() { }
    }

    private sealed class FakeDetectionService : IDetectionService
    {
        private string _primaryPath = string.Empty;

        public event Action<DetectionResultData>? DetectionCompleted;
        public event Action<string>? ModelLoaded;
        public event Action<string>? ErrorOccurred;

        public bool IsModelLoaded => !string.IsNullOrWhiteSpace(_primaryPath);
        public string CurrentModelName => Path.GetFileNameWithoutExtension(_primaryPath);
        public IReadOnlyList<string> AvailableModels => Array.Empty<string>();
        public long LastInferenceMs => 0;
        public DetectionRuntimeStatus RuntimeStatus { get; } = new DetectionRuntimeStatus();
        public DetectionRuntimeModelSnapshot RuntimeModelSnapshot => new DetectionRuntimeModelSnapshot
        {
            Primary = new DetectionModelSlotSnapshot
            {
                Role = ModelRole.Primary,
                IsLoaded = !string.IsNullOrWhiteSpace(_primaryPath),
                ModelPath = _primaryPath
            }
        };

        public Task<bool> LoadModelAsync(string modelPath, bool useGpu, int gpuDeviceId = 0)
        {
            _primaryPath = Path.GetFullPath(modelPath);
            return Task.FromResult(true);
        }

        public void UnloadPrimaryModel() => _primaryPath = string.Empty;
        public Task<bool> ScanAndLoadModelsAsync(string modelsDirectory, bool useGpu, int gpuDeviceId = 0) => Task.FromResult(false);
        public Task<bool> SwitchModelAsync(string modelName) => Task.FromResult(false);
        public Task<DetectionResultData> DetectAsync(Mat image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null) => Task.FromResult(new DetectionResultData());
        public Task<DetectionResultData> DetectAsync(System.Drawing.Bitmap image, float confidence, float iouThreshold, InspectionFallbackGoal? fallbackGoal = null, MultiModelCandidateEvaluator? candidateEvaluator = null) => Task.FromResult(new DetectionResultData());
        public System.Drawing.Bitmap GenerateResultImage(System.Drawing.Bitmap original, List<YoloResult> results, string[] labels) => new System.Drawing.Bitmap(original);
        public void SetTaskMode(int taskType) { }
        public void SetEnableFallback(bool enabled) { }
        public Task<bool> LoadAuxiliary1ModelAsync(string modelPath) => Task.FromResult(true);
        public Task<bool> LoadAuxiliary2ModelAsync(string modelPath) => Task.FromResult(true);
        public void UnloadAuxiliary1Model() { }
        public void UnloadAuxiliary2Model() { }
        public string[] GetLabels() => Array.Empty<string>();
        public object? GetLastMetrics() => null;
        public void Dispose() { }
    }

    private sealed class HashMismatchDatasetStore : IReplayDatasetStore
    {
        public Task<ReplayDatasetSnapshot> CreateSnapshotAsync(
            ReplayDatasetCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ReplayDatasetSnapshot> LoadSnapshotAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> ComputeSnapshotHashAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult("mismatched-dataset-hash");
        }

        public Task<IReadOnlyList<ReplayDatasetSummary>> ListSnapshotsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReplayDatasetSummary>>(Array.Empty<ReplayDatasetSummary>());
        }

        public Task<ReplayDatasetArchiveResult> ArchiveSnapshotAsync(
            string datasetId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReplayDatasetArchiveResult
            {
                Succeeded = false,
                ErrorCode = "NotSupported",
                Message = "Hash mismatch fixture does not support archive."
            });
        }
    }
}
#pragma warning restore CS0067
