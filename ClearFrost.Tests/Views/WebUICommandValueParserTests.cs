using System.Reflection;
using System.Text.Json;
using ClearFrost;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class WebUICommandValueParserTests
{
    [Fact]
    public void TryReadUnitFloatCommandValue_AcceptsOnlyZeroToOne()
    {
        using JsonDocument valid = JsonDocument.Parse("""{"value":0.72}""");
        object?[] validArgs = { valid.RootElement, 0f };

        InvokeBool("TryReadUnitFloatCommandValue", validArgs).Should().BeTrue();
        ((float)validArgs[1]!).Should().BeApproximately(0.72f, 0.0001f);

        using JsonDocument invalid = JsonDocument.Parse("""{"value":1.5}""");
        object?[] invalidArgs = { invalid.RootElement, 0f };

        InvokeBool("TryReadUnitFloatCommandValue", invalidArgs).Should().BeFalse();
    }

    [Fact]
    public void TryReadBoolCommandValue_AcceptsBooleanAndBooleanText()
    {
        using JsonDocument valid = JsonDocument.Parse("""{"value":"true"}""");
        object?[] validArgs = { valid.RootElement, false };

        InvokeBool("TryReadBoolCommandValue", validArgs).Should().BeTrue();
        ((bool)validArgs[1]!).Should().BeTrue();

        using JsonDocument invalid = JsonDocument.Parse("""{"value":1}""");
        object?[] invalidArgs = { invalid.RootElement, false };

        InvokeBool("TryReadBoolCommandValue", invalidArgs).Should().BeFalse();
    }

    [Fact]
    public void TryReadObjectCommandValue_AcceptsOnlyJsonObjects()
    {
        using JsonDocument valid = JsonDocument.Parse("""{"value":{"serialNumber":"CAM-01"}}""");
        object?[] validArgs = { valid.RootElement, "{}" };

        InvokeBool("TryReadObjectCommandValue", validArgs).Should().BeTrue();
        validArgs[1].Should().Be("""{"serialNumber":"CAM-01"}""");

        using JsonDocument invalid = JsonDocument.Parse("""{"value":"CAM-01"}""");
        object?[] invalidArgs = { invalid.RootElement, "{}" };

        InvokeBool("TryReadObjectCommandValue", invalidArgs).Should().BeFalse();
        invalidArgs[1].Should().Be("{}");
    }

    [Fact]
    public void TryReadStringCommandValue_AcceptsEmptyStringAndRejectsNonString()
    {
        using JsonDocument empty = JsonDocument.Parse("""{"value":""}""");
        object?[] emptyArgs = { empty.RootElement, "fallback" };

        InvokeBool("TryReadStringCommandValue", emptyArgs).Should().BeTrue();
        emptyArgs[1].Should().Be(string.Empty);

        using JsonDocument valid = JsonDocument.Parse("""{"value":"approved:model:v1:sha"}""");
        object?[] validArgs = { valid.RootElement, string.Empty };

        InvokeBool("TryReadStringCommandValue", validArgs).Should().BeTrue();
        validArgs[1].Should().Be("approved:model:v1:sha");

        using JsonDocument invalid = JsonDocument.Parse("""{"value":{"model":"x"}}""");
        object?[] invalidArgs = { invalid.RootElement, "fallback" };

        InvokeBool("TryReadStringCommandValue", invalidArgs).Should().BeFalse();
        invalidArgs[1].Should().Be(string.Empty);
    }

    [Fact]
    public void TryReadRoiRect_RequiresFourFloatValues()
    {
        using JsonDocument valid = JsonDocument.Parse("""{"value":{"rect":[0.1,0.2,0.3,0.4]}}""");
        object?[] validArgs = { valid.RootElement, Array.Empty<float>(), string.Empty };

        InvokeBool("TryReadRoiRect", validArgs).Should().BeTrue();
        ((float[])validArgs[1]!).Should().HaveCount(4);

        using JsonDocument clear = JsonDocument.Parse("""{"value":{"rect":[0,0,0,0]}}""");
        object?[] clearArgs = { clear.RootElement, Array.Empty<float>(), string.Empty };

        InvokeBool("TryReadRoiRect", clearArgs).Should().BeTrue();
        ((float[])clearArgs[1]!).Should().Equal(0f, 0f, 0f, 0f);

        using JsonDocument invalid = JsonDocument.Parse("""{"value":{"rect":[0.1,0.2]}}""");
        object?[] invalidArgs = { invalid.RootElement, Array.Empty<float>(), string.Empty };

        InvokeBool("TryReadRoiRect", invalidArgs).Should().BeFalse();
        ((string)invalidArgs[2]!).Should().Contain("ROI rect 必须包含 4 个数值");

        using JsonDocument outOfRange = JsonDocument.Parse("""{"value":{"rect":[-0.1,0.2,0.3,0.4]}}""");
        object?[] outOfRangeArgs = { outOfRange.RootElement, Array.Empty<float>(), string.Empty };

        InvokeBool("TryReadRoiRect", outOfRangeArgs).Should().BeFalse();
        ((string)outOfRangeArgs[2]!).Should().Contain("ROI rect 数值必须在 0 到 1 之间");

        using JsonDocument outOfBounds = JsonDocument.Parse("""{"value":{"rect":[0.8,0.2,0.3,0.4]}}""");
        object?[] outOfBoundsArgs = { outOfBounds.RootElement, Array.Empty<float>(), string.Empty };

        InvokeBool("TryReadRoiRect", outOfBoundsArgs).Should().BeFalse();
        ((string)outOfBoundsArgs[2]!).Should().Contain("ROI rect 不能超出图像边界");

        using JsonDocument zeroSize = JsonDocument.Parse("""{"value":{"rect":[0.1,0.2,0,0.4]}}""");
        object?[] zeroSizeArgs = { zeroSize.RootElement, Array.Empty<float>(), string.Empty };

        InvokeBool("TryReadRoiRect", zeroSizeArgs).Should().BeFalse();
        ((string)zeroSizeArgs[2]!).Should().Contain("ROI rect 宽高必须大于 0");
    }

    [Fact]
    public void TryReadTraceImagesRequest_RequiresDateAndClampsPageSize()
    {
        using JsonDocument valid = JsonDocument.Parse(
            """{"value":{"date":"2026-07-06","hour":"08","pageSize":999,"afterTimestamp":"2026-07-06T08:00:00","afterId":42}}""");
        object?[] validArgs = { valid.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", validArgs).Should().BeTrue();
        validArgs[1].Should().Be("2026-07-06");
        validArgs[2].Should().Be("08");
        validArgs[3].Should().Be(200);
        validArgs[4].Should().Be("2026-07-06 08:00:00.000");
        validArgs[5].Should().Be(42L);

        using JsonDocument invalid = JsonDocument.Parse("""{"value":{"hour":"08"}}""");
        object?[] invalidArgs = { invalid.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", invalidArgs).Should().BeFalse();
        invalidArgs[6].Should().Be("追溯图片请求缺少 date");

        using JsonDocument invalidDate = JsonDocument.Parse("""{"value":{"date":"not-a-date","hour":"08"}}""");
        object?[] invalidDateArgs = { invalidDate.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", invalidDateArgs).Should().BeFalse();
        invalidDateArgs[6].Should().Be("追溯图片请求 date 格式无效");

        using JsonDocument invalidHour = JsonDocument.Parse("""{"value":{"date":"2026-07-06","hour":"24"}}""");
        object?[] invalidHourArgs = { invalidHour.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", invalidHourArgs).Should().BeFalse();
        invalidHourArgs[6].Should().Be("追溯图片请求 hour 必须是 0 到 23");

        using JsonDocument missingCursorId = JsonDocument.Parse("""{"value":{"date":"2026-07-06","afterTimestamp":"2026-07-06 08:00:00.000"}}""");
        object?[] missingCursorIdArgs = { missingCursorId.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", missingCursorIdArgs).Should().BeFalse();
        missingCursorIdArgs[6].Should().Be("追溯图片分页游标必须同时包含 afterTimestamp 和 afterId");

        using JsonDocument invalidCursorTimestamp = JsonDocument.Parse("""{"value":{"date":"2026-07-06","afterTimestamp":"bad-cursor","afterId":42}}""");
        object?[] invalidCursorTimestampArgs = { invalidCursorTimestamp.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", invalidCursorTimestampArgs).Should().BeFalse();
        invalidCursorTimestampArgs[6].Should().Be("追溯图片分页游标 afterTimestamp 格式无效");

        using JsonDocument invalidCursorId = JsonDocument.Parse("""{"value":{"date":"2026-07-06","afterTimestamp":"2026-07-06 08:00:00.000","afterId":0}}""");
        object?[] invalidCursorIdArgs = { invalidCursorId.RootElement, string.Empty, string.Empty, 0, null, null, string.Empty };

        InvokeBool("TryReadTraceImagesRequest", invalidCursorIdArgs).Should().BeFalse();
        invalidCursorIdArgs[6].Should().Be("追溯图片分页游标 afterId 必须大于 0");
    }

    private static bool InvokeBool(string methodName, object?[] args)
    {
        MethodInfo method = typeof(WebUIController).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(WebUIController), methodName);

        return (bool)method.Invoke(null, args)!;
    }
}
