using System;
using System.Windows.Forms;
using System.IO;
using System.Text;

namespace YOLO
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // 设置全局异常处理，阻止闪退
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += Application_ThreadException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();
            Application.Run(new 主窗口());
        }

        /// <summary>
        /// 处理 UI 线程异常
        /// </summary>
        private static void Application_ThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            LogException("UI Thread Exception", e.Exception);
            MessageBox.Show($"发生错误，程序已记录日志:\n{e.Exception.Message}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// 处理非 UI 线程异常
        /// </summary>
        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException("Unhandled Exception", ex);
                MessageBox.Show($"发生严重错误，程序即将退出:\n{ex.Message}", "严重错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 记录异常到日志文件
        /// </summary>
        private static void LogException(string type, Exception ex)
        {
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
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
            catch { /* 日志写入失败时静默处理 */ }
        }
    }
}