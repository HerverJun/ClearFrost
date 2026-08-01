using System.Reflection;
using System.Text.Json;
using ClearFrost;
using FluentAssertions;

namespace ClearFrost.Tests.Views;

public class RuntimeConfigJsonParserTests
{
    [Theory]
    [InlineData("""{"value":0.72}""", 0.72)]
    [InlineData("""{"value":"0.72"}""", 0.72)]
    [InlineData("""{"value":"1.25e2"}""", 125.0)]
    public void GetJsonDoubleValue_AcceptsNumbersAndInvariantText(string json, double expected)
    {
        InvokeGetJsonDoubleValue(json, 9.0).Should().BeApproximately(expected, 0.000001);
    }

    [Theory]
    [InlineData("""{"value":""}""")]
    [InlineData("""{"value":"Infinity"}""")]
    [InlineData("""{"value":"not-a-number"}""")]
    [InlineData("""{"value":true}""")]
    public void GetJsonDoubleValue_FallsBackForInvalidValues(string json)
    {
        InvokeGetJsonDoubleValue(json, 9.0).Should().Be(9.0);
    }

    [Theory]
    [InlineData("""{"value":9600}""", 9600)]
    [InlineData("""{"value":"9600"}""", 9600)]
    [InlineData("""{"value":" 42 "}""", 42)]
    public void GetJsonInt32Value_AcceptsNumbersAndInvariantText(string json, int expected)
    {
        InvokeGetJsonInt32Value(json, 7).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"value":"1.5"}""")]
    [InlineData("""{"value":"not-a-number"}""")]
    [InlineData("""{"value":2147483648}""")]
    [InlineData("""{"value":true}""")]
    public void GetJsonInt32Value_FallsBackForInvalidValues(string json)
    {
        InvokeGetJsonInt32Value(json, 7).Should().Be(7);
    }

    [Theory]
    [InlineData("""{"value":1}""", 1)]
    [InlineData("""{"value":"-1"}""", -1)]
    public void GetJsonInt16Value_AcceptsNumbersAndInvariantText(string json, short expected)
    {
        InvokeGetJsonInt16Value(json, 3).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"value":"32768"}""")]
    [InlineData("""{"value":"1.5"}""")]
    [InlineData("""{"value":false}""")]
    public void GetJsonInt16Value_FallsBackForInvalidValues(string json)
    {
        InvokeGetJsonInt16Value(json, 3).Should().Be(3);
    }

    [Theory]
    [InlineData("""{"value":true}""", true)]
    [InlineData("""{"value":false}""", false)]
    [InlineData("""{"value":"true"}""", true)]
    [InlineData("""{"value":" FALSE "}""", false)]
    [InlineData("""{"value":1}""", true)]
    [InlineData("""{"value":"0"}""", false)]
    public void GetJsonBooleanValue_AcceptsBooleanTextAndBinaryValues(string json, bool expected)
    {
        InvokeGetJsonBooleanValue(json, !expected).Should().Be(expected);
    }

    [Theory]
    [InlineData("""{"value":"yes"}""")]
    [InlineData("""{"value":2}""")]
    [InlineData("""{"value":{}}""")]
    public void GetJsonBooleanValue_FallsBackForInvalidValues(string json)
    {
        InvokeGetJsonBooleanValue(json, true).Should().BeTrue();
        InvokeGetJsonBooleanValue(json, false).Should().BeFalse();
    }

    private static double InvokeGetJsonDoubleValue(string json, double fallback)
    {
        MethodInfo method = typeof(主窗口).GetMethod(
            "GetJsonDoubleValue",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(主窗口), "GetJsonDoubleValue");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement.GetProperty("value");
        return (double)method.Invoke(null, new object[] { value, fallback })!;
    }

    private static int InvokeGetJsonInt32Value(string json, int fallback)
    {
        MethodInfo method = typeof(主窗口).GetMethod(
            "GetJsonInt32Value",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(主窗口), "GetJsonInt32Value");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement.GetProperty("value");
        return (int)method.Invoke(null, new object[] { value, fallback })!;
    }

    private static short InvokeGetJsonInt16Value(string json, short fallback)
    {
        MethodInfo method = typeof(主窗口).GetMethod(
            "GetJsonInt16Value",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(主窗口), "GetJsonInt16Value");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement.GetProperty("value");
        return (short)method.Invoke(null, new object[] { value, fallback })!;
    }

    private static bool InvokeGetJsonBooleanValue(string json, bool fallback)
    {
        MethodInfo method = typeof(主窗口).GetMethod(
            "GetJsonBooleanValue",
            BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingMethodException(nameof(主窗口), "GetJsonBooleanValue");

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement value = document.RootElement.GetProperty("value");
        return (bool)method.Invoke(null, new object[] { value, fallback })!;
    }
}
