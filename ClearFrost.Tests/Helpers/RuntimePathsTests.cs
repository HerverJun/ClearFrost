using System.Reflection;
using ClearFrost.Helpers;
using FluentAssertions;

namespace ClearFrost.Tests.Helpers;

public class RuntimePathsTests
{
    [Fact]
    public void ScopedRoot_不同安装目录生成不同运行时目录()
    {
        MethodInfo method = GetScopedRootMethod();

        string rootA = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"C:\Apps\ClearFrost\publish");
        string rootB = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"D:\Backup\ClearFrost\publish");

        rootA.Should().NotBe(rootB);
        rootA.Should().StartWith(@"C:\Users\Test\AppData\Local\ClearFrost");
        rootB.Should().StartWith(@"C:\Users\Test\AppData\Local\ClearFrost");
    }

    [Fact]
    public void ScopedRoot_相同安装目录生成稳定运行时目录()
    {
        MethodInfo method = GetScopedRootMethod();

        string rootA = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"C:\Apps\ClearFrost\publish");
        string rootB = InvokeScopedRootMethod(
            method,
            @"C:\Users\Test\AppData\Local",
            @"C:\Apps\ClearFrost\publish");

        rootA.Should().Be(rootB);
    }

    private static MethodInfo GetScopedRootMethod()
    {
        MethodInfo? method = typeof(RuntimePaths).GetMethod(
            "GetScopedDefaultRootCandidate",
            BindingFlags.Static | BindingFlags.NonPublic);

        method.Should().NotBeNull();
        return method!;
    }

    private static string InvokeScopedRootMethod(MethodInfo method, string parentRoot, string baseDirectory)
    {
        object? result = method.Invoke(null, new object[] { parentRoot, baseDirectory });
        result.Should().BeOfType<string>();
        return (string)result!;
    }
}
