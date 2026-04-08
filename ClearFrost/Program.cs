using System;
using System.Windows.Forms;
using System.IO;
// ============================================================================
// 文件名: Program.cs
// 作者: 蘅芜君
// 创建日期: 2024
// 描述:   应用程序主入口点
// ============================================================================
using System.Text;
using ClearFrost.Helpers;

namespace ClearFrost
{
    /// <summary>
    /// 应用程序的主入口点类
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// 应用程序的主入口点。
        /// 负责初始化环境、日志记录和启动主窗口。
        /// </summary>
        [STAThread]
        static void Main()
        {
            try
            {
                // 确保工作目录正确（修复从 IDE 启动时工作目录不对的问题）
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                Environment.CurrentDirectory = baseDir;

                AppendStartupLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting...");
                AppendStartupLog($"Working Directory: {Environment.CurrentDirectory}");
                AppendStartupLog($"Base Directory: {baseDir}");

                // 设置全局异常处理，阻止闪退
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += Application_ThreadException;
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                AppendStartupLog("Exception handlers registered");

                // To customize application configuration such as set high DPI settings or default font,
                // see https://aka.ms/applicationconfiguration.
                ApplicationConfiguration.Initialize();
                AppendStartupLog("ApplicationConfiguration initialized");

                Application.Run(new 主窗口());
                AppendStartupLog("Application exited normally");
            }
            catch (Exception ex)
            {
                AppendStartupLog($"CRASH: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    AppendStartupLog(ex.StackTrace);
                }

                if (ex.InnerException != null)
                {
                    AppendStartupLog($"Inner: {ex.InnerException.Message}");
                    if (!string.IsNullOrWhiteSpace(ex.InnerException.StackTrace))
                    {
                        AppendStartupLog(ex.InnerException.StackTrace);
                    }
                }

                LogException("Startup Exception", ex);
                TryShowFatalMessage($"程序启动失败，请查看日志:\n{RuntimePaths.StartupLogPath}\n\n{ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        /// <summary>
        /// 处理 UI 线程异常
        /// </summary>
        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            LogException("UI Thread Exception", e.Exception);
            TryShowFatalMessage($"发生错误，程序已记录日志:\n{e.Exception.Message}", "错误");
        }

        /// <summary>
        /// 处理非 UI 线程异常
        /// </summary>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException("Unhandled Exception", ex);
                TryShowFatalMessage($"发生严重错误，程序即将退出:\n{ex.Message}", "严重错误");
            }
        }

        /// <summary>
        /// 记录异常到日志文件
        /// </summary>
        private static void LogException(string type, Exception ex)
        {
            try
            {
                string logFile = RuntimePaths.CrashLogPath(DateTime.Now);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine($"===== {type} at {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
                sb.AppendLine($"Message: {ex.Message}");
                sb.AppendLine($"StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"InnerException: {ex.InnerException.Message}");
                    sb.AppendLine($"InnerStackTrace: {ex.InnerException.StackTrace}");
                }
                sb.AppendLine();

                File.AppendAllText(logFile, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception logEx) { System.Diagnostics.Debug.WriteLine($"[Program] 日志写入失败: {logEx.Message}"); }
        }

        private static void AppendStartupLog(string message)
        {
            try
            {
                File.AppendAllText(RuntimePaths.StartupLogPath, message + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] 启动日志写入失败: {ex.Message}");
            }
        }

        private static void TryShowFatalMessage(string message, string title = "严重错误")
        {
            try
            {
                MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] 错误弹窗显示失败: {ex.Message}");
            }
        }
    }
}
