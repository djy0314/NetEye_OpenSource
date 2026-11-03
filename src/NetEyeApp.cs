using System;
using System.Windows.Forms;

namespace NetEye
{
    /// <summary>
    /// 程序入口。DPI 策略：清单声明 PerMonitorV2（真·每显示器 DPI 感知，4K 下不糊），
    /// 布局由 MainForm 按当前显示器 DPI 手工换算，绝不叠加系统缩放。
    /// </summary>
    internal static class NetEyeApp
    {
        private static bool _reported;

        // v0.9.2：清单里已经声明了 PerMonitorV2，但某些"二次打包/换壳"的 exe 会把清单弄丢，
        // 那时进程就退回 DPI 无感知——界面被系统拉伸放大，糊成一片，也就是用户反馈的"缩放失效"。
        // 这里在建任何窗口之前再主动要一次（已经有了会返回 FALSE，无害）。
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        private static void EnsureDpiAware()
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch (Exception) { }   // Win10 1703 以下没有这个导出，清单里的 true/pm 兜着
        }

        [STAThread]
        private static void Main(string[] args)
        {
            EnsureDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 兜底：任何未捕获异常都弹深色提示，绝不让系统「已停止工作」对话框糊上来
            Application.ThreadException += delegate (object s, System.Threading.ThreadExceptionEventArgs e)
            {
                ReportError(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                ReportError(e.ExceptionObject as Exception);
            };

            Application.Run(new MainForm());
        }

        private static void ReportError(Exception ex)
        {
            if (ex == null) return;
            // 先落盘完整堆栈再弹窗：对话框只能看到一行消息，排障靠的是堆栈
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neteye_error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + ex + "\r\n\r\n");
            }
            catch (Exception) { }
            try
            {
                if (_reported) return;   // 防止连环异常刷屏
                _reported = true;
                Theme.Alert(Form.ActiveForm, "NetEye 遇到问题",
                    "操作没有完成，程序仍在运行。\n\n" + ex.GetType().Name + "：" + ex.Message);
            }
            catch (Exception) { }
        }
    }
}
