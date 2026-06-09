// ============================================================================
// 文件名: WindowHelpers.cs
// 描述:   窗口辅助方法
//
// 功能:
//   - 防止系统休眠
//   - 支持无边框窗口拖动
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClearFrost.Helpers
{
    /// <summary>
    /// 窗口辅助方法。
    /// </summary>
    public static class WindowHelpers
    {
        #region 防止休眠

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_CONTINUOUS = 0x80000000;

        /// <summary>
        /// 阻止系统和显示器进入休眠。
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
        /// 恢复系统默认休眠策略。
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
        /// 触发无边框窗体拖动。
        /// </summary>
        /// 
        public static void StartWindowDrag(Form form)
        {
            if (form == null) return;
            ReleaseCapture();
            SendMessage(form.Handle, WM_NCLBUTTONDOWN, HT_CAPTION, 0);
        }

        #endregion
    }
}

