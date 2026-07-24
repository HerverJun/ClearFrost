using System.Reflection;
using ClearFrost.Interfaces;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUIControllerImageEvidenceTests
{
    [Fact]
    public void IsSafeImageMappingDirectory_接受普通目录()
    {
        string tempDir = CreateTempDirectory();
        try
        {
            WebUIController.IsSafeImageMappingDirectory(tempDir).Should().BeTrue();
        }
        finally
        {
            DeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void IsSafeImageMappingDirectory_拒绝链接目录()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedImageDir = Path.Combine(tempDir, "linked-images");
        try
        {
            if (!TryCreateDirectorySymbolicLink(linkedImageDir, externalDir))
            {
                return;
            }

            WebUIController.IsSafeImageMappingDirectory(linkedImageDir).Should().BeFalse();
        }
        finally
        {
            TryDeleteDirectoryLink(linkedImageDir);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void BuildTraceRecordPayload_拒绝链接带框图Url并保留安全原图()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedRendered = string.Empty;
        try
        {
            string imagePath = Path.Combine(tempDir, "FAIL_CF-TRACE-001.jpg");
            string renderedDir = Path.Combine(tempDir, "Rendered");
            linkedRendered = Path.Combine(renderedDir, "FAIL_CF-TRACE-001_rendered.jpg");
            string externalRendered = Path.Combine(externalDir, "external-rendered.jpg");
            Directory.CreateDirectory(renderedDir);
            File.WriteAllText(imagePath, "trusted original");
            File.WriteAllText(externalRendered, "external rendered");
            if (!TryCreateFileSymbolicLink(linkedRendered, externalRendered))
            {
                return;
            }

            using var controller = new WebUIController
            {
                ImageBasePath = tempDir
            };

            object payload = BuildTraceRecordPayload(
                controller,
                new DetectionTraceRecord
                {
                    ImagePath = imagePath,
                    RenderedImagePath = linkedRendered,
                    RuleSummary = "分类规则 OK",
                    ResultJson = "{\"DeepLearningSummary\":{\"Classification\":{\"Top1Label\":\"OK\",\"Top1Confidence\":0.93}}}"
                });

            payload.GetPropertyValue("hasImage").Should().Be(true);
            payload.GetPropertyValue("hasRenderedImage").Should().Be(false);
            payload.GetPropertyValue("renderedImageUrl").Should().BeNull();
            payload.GetPropertyValue("imageUrl").Should().Be("http://ng-images.local/FAIL_CF-TRACE-001.jpg");
            payload.GetPropertyValue("displayImageUrl").Should().Be("http://ng-images.local/FAIL_CF-TRACE-001.jpg");
            payload.GetPropertyValue("ruleSummary").Should().Be("分类规则 OK");
            payload.GetPropertyValue("resultJson").Should().Be("{\"DeepLearningSummary\":{\"Classification\":{\"Top1Label\":\"OK\",\"Top1Confidence\":0.93}}}");
            File.ReadAllText(externalRendered).Should().Be("external rendered");
        }
        finally
        {
            TryDeleteFileLink(linkedRendered);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    [Fact]
    public void BuildTraceRecordPayload_兜底扫描跳过链接原图Url()
    {
        string tempDir = CreateTempDirectory();
        string externalDir = CreateTempDirectory();
        string linkedImage = string.Empty;
        try
        {
            string imageDir = Path.Combine(tempDir, "Unqualified", "2026年07月05日", "10");
            Directory.CreateDirectory(imageDir);
            const string inspectionId = "CF-20260705-104743741-TEST-000001";
            string externalImage = Path.Combine(externalDir, "external-original.jpg");
            linkedImage = Path.Combine(imageDir, $"FAIL_{inspectionId}.jpg");
            File.WriteAllText(externalImage, "external original");
            if (!TryCreateFileSymbolicLink(linkedImage, externalImage))
            {
                return;
            }

            using var controller = new WebUIController
            {
                ImageBasePath = tempDir
            };

            object payload = BuildTraceRecordPayload(
                controller,
                new DetectionTraceRecord
                {
                    Timestamp = new DateTime(2026, 7, 5, 10, 47, 43, 741),
                    IsQualified = false,
                    InspectionId = inspectionId
                });

            payload.GetPropertyValue("hasImage").Should().Be(false);
            payload.GetPropertyValue("imageUrl").Should().BeNull();
            payload.GetPropertyValue("usedFallbackImagePath").Should().Be(false);
            payload.GetPropertyValue("displayImageUrl").Should().BeNull();
            File.ReadAllText(externalImage).Should().Be("external original");
        }
        finally
        {
            TryDeleteFileLink(linkedImage);
            DeleteDirectory(tempDir);
            DeleteDirectory(externalDir);
        }
    }

    private static object BuildTraceRecordPayload(WebUIController controller, DetectionTraceRecord record)
    {
        MethodInfo method = typeof(WebUIController).GetMethod(
            "BuildTraceRecordPayload",
            BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMethodException(nameof(WebUIController), "BuildTraceRecordPayload");

        return method.Invoke(controller, new object?[] { record, null })!;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClearFrostTests",
            nameof(WebUIControllerImageEvidenceTests),
            Guid.NewGuid().ToString("N"));

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

    private static void TryDeleteDirectoryLink(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(path);
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
        if (!Directory.Exists(path))
        {
            return;
        }

        var info = new DirectoryInfo(path);
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete();
            return;
        }

        Directory.Delete(path, recursive: true);
    }
}
