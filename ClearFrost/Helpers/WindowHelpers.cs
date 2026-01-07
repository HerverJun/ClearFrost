// ============================================================================
// 文件名: WindowHelpers.cs
// 描述:   Windows 系统交互工具类
//         包含阻止休眠和窗口拖动的 Win32 API 封装
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YOLO.Helpers
{
    /// <summary>
    /// Windows 系统交互工具类
    /// </summary>
    public static class WindowHelpers
    {
        #region 阻止休眠

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_CONTINUOUS = 0x80000000;

        /// <summary>
        /// 阻止系统休眠和屏幕关闭
        /// </summary>
        public static void PreventSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sleep] PreventSleep error: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复系统正常休眠策略
        /// </summary>
        public static void RestoreSleep()
        {
            try
            {
                SetThreadExecutionState(ES_CONTINUOUS);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sleep] RestoreSleep error: {ex.Message}");
            }
        }

        #endregion

        #region 窗口拖动

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        /// <summary>
        /// 启动窗口拖动（模拟标题栏拖动）
        /// </summary>
        /// <param name="form">要拖动的窗体</param>
        public static void StartWindowDrag(Form form)
        {
            if (form == null) return;
            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }

        #endregion
    }
}
