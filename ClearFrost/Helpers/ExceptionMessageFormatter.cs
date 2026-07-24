using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace ClearFrost.Helpers
{
    /// <summary>
    /// 将嵌套异常整理成适合现场日志显示的短消息。
    /// </summary>
    internal static class ExceptionMessageFormatter
    {
        private const string NativeMethodsTypeName = "Microsoft.ML.OnnxRuntime.NativeMethods";

        public static string FormatForLog(Exception exception)
        {
            if (exception == null) throw new ArgumentNullException(nameof(exception));

            var messages = Flatten(exception)
                .Select(ex => ex.Message?.Trim() ?? string.Empty)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (IsOnnxRuntimeNativeInitializerFailure(exception))
            {
                messages.Insert(
                    0,
                    "ONNX Runtime 原生库初始化失败，请检查程序目录中的 onnxruntime.dll/DirectML.dll、x64 架构、VC++ 运行库、Windows/显卡驱动和 CPU 指令集是否满足当前 ORT 版本要求");
            }

            return messages.Count == 0 ? exception.GetType().Name : string.Join(" | ", messages);
        }

        private static bool IsOnnxRuntimeNativeInitializerFailure(Exception exception)
        {
            return Flatten(exception).Any(ex =>
                ex is TypeInitializationException typeInit &&
                string.Equals(typeInit.TypeName, NativeMethodsTypeName, StringComparison.Ordinal));
        }

        private static IEnumerable<Exception> Flatten(Exception exception)
        {
            var current = exception;
            while (current != null)
            {
                yield return current;

                current = current switch
                {
                    TargetInvocationException targetInvocation => targetInvocation.InnerException,
                    TypeInitializationException typeInitialization => typeInitialization.InnerException,
                    InvalidOperationException invalidOperation when invalidOperation.InnerException != null => invalidOperation.InnerException,
                    FileNotFoundException fileNotFound when fileNotFound.InnerException != null => fileNotFound.InnerException,
                    BadImageFormatException badImageFormat when badImageFormat.InnerException != null => badImageFormat.InnerException,
                    DllNotFoundException dllNotFound when dllNotFound.InnerException != null => dllNotFound.InnerException,
                    _ => current.InnerException
                };
            }
        }
    }
}
