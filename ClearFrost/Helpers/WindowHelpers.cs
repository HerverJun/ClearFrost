// ============================================================================
// 
// 
// 
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ClearFrost.Helpers
{
    /// <summary>
    /// 
    /// </summary>
    public static class WindowHelpers
    {
        #region ��ֹ����

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        private const uint ES_DISPLAY_REQUIRED = 0x00000002;
        private const uint ES_CONTINUOUS = 0x80000000;

        /// <summary>
        /// 
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
        /// 
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

        #region �����϶�

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION = 0x2;

        /// <summary>
        /// 
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

