using System;
using System.Drawing;
using System.Windows.Forms;

namespace NetEye
{
    /// <summary>
    /// v0.9.3：网速悬停折线小图（状态栏「↑ ↓」格上悬停时弹出）。
    ///
    /// 一个无边框小窗 + 自绘折线：下行一条亮线、上行一条暗线，纵轴按窗口内峰值自适应
    /// （下限 64 KB/s，免得空闲时一条线贴着顶）。数据来自 NetSpeed 的 120 帧环形历史，
    /// 每 250ms 轮询一次鼠标位置 —— 光标既不在状态栏格上、也不在小图上就自动关掉。
    /// 用轮询而不是 MouseLeave：状态栏格与小图之间隔着一两像素缝隙，MouseLeave 会误关。
    /// </summary>
    internal class SpeedGraphForm : Form
    {
        private const int Cap = 120;
        private readonly double[] _down = new double[Cap];
        private readonly double[] _up = new double[Cap];
        private Timer _poll;
        private readonly Rectangle _anchor;   // 状态栏网速格的屏幕矩形

        private SpeedGraphForm(Rectangle anchorScreen)
        {
            _anchor = anchorScreen;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            MinimizeBox = MaximizeBox = false;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(28, 28, 28);
            KeyPreview = true;

            int w = Theme.Px(210), h = Theme.Px(96);
            ClientSize = new Size(w, h);

            // 贴着网速格上沿弹出（压 1px 免得鼠标穿缝），左右跟随格子的左对齐
            Point tl = new Point(anchorScreen.Left, anchorScreen.Top - h + 1);
            Rectangle wa = Screen.FromPoint(anchorScreen.Location).WorkingArea;
            if (tl.X < wa.Left) tl.X = wa.Left;
            if (tl.X + w > wa.Right) tl.X = wa.Right - w;
            if (tl.Y < wa.Top) tl.Y = anchorScreen.Bottom - 1;   // 上面放不下就弹到下面
            Bounds = new Rectangle(tl, new Size(w, h));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            _poll = new Timer();
            _poll.Interval = 250;
            _poll.Tick += delegate
            {
                if (!CursorNear()) { Close(); return; }
                Invalidate();
            };
            _poll.Start();
        }

        private bool CursorNear()
        {
            Point p = Cursor.Position;
            Rectangle near = Rectangle.Union(_anchor, Bounds);
            near.Inflate(Theme.Px(6), Theme.Px(6));
            return near.Contains(p);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            int padL = Theme.Px(10), padR = Theme.Px(10), padT = Theme.Px(22), padB = Theme.Px(8);
            int w = ClientSize.Width - padL - padR;
            int h = ClientSize.Height - padT - padB;

            int n = NetSpeed.SnapshotHistory(_down, _up);

            // 图例：↓ 下载（亮） ↑ 上传（暗），峰值速率实时数字放右上
            using (Font f = Theme.Ui(8f, FontStyle.Regular))
            {
                TextRenderer.DrawText(g, "↓ " + NetSpeed.FormatRate(NetSpeed.DownBytesPerSec), f,
                    new Point(padL, Theme.Px(3)), Color.FromArgb(126, 217, 138));
                Size szUp = TextRenderer.MeasureText("↓ ", f);
                TextRenderer.DrawText(g, "↑ " + NetSpeed.FormatRate(NetSpeed.UpBytesPerSec), f,
                    new Point(padL + szUp.Width + Theme.Px(6), Theme.Px(3)), Color.FromArgb(150, 150, 150));
            }

            // 绘图区底框
            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, padL, padT, w, h);

            if (n < 2) return;
            double max = 64.0 * 1024;                      // 下限 64KB/s，空闲时曲线不至于糊成横线
            for (int i = 0; i < n; i++)
            {
                if (_down[i] > max) max = _down[i];
                if (_up[i] > max) max = _up[i];
            }
            max *= 1.15;                                   // 峰值留 15% 头部空间

            DrawLine(g, _down, n, max, padL, padT, w, h, Color.FromArgb(126, 217, 138));
            DrawLine(g, _up, n, max, padL, padT, w, h, Color.FromArgb(120, 130, 170));
        }

        private static void DrawLine(Graphics g, double[] data, int n, double max,
            int x, int y, int w, int h, Color c)
        {
            using (Pen p = new Pen(c, Math.Max(1f, Theme.Px(1.4f))))
            {
                PointF[] pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    float px = x + w * i / (float)(n - 1);
                    double v = data[i] < 0 ? 0 : data[i];
                    float py = y + h - (float)Math.Min(1.0, v / max) * h;
                    pts[i] = new PointF(px, py);
                }
                g.DrawLines(p, pts);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_poll != null) { _poll.Stop(); _poll.Dispose(); }
            if (ReferenceEquals(_open, this)) _open = null;   // 静态引用别悬空
            base.OnFormClosed(e);
        }

        // ================= 弹出/关闭管理（MainForm 调用） =================

        private static SpeedGraphForm _open;

        /// <summary>光标悬到状态栏网速格时调用：没有就弹一个，有就刷新锚点</summary>
        public static void ShowOrRefresh(Label anchorLabel)
        {
            if (anchorLabel == null || !anchorLabel.IsHandleCreated) return;
            if (_open != null && !_open.IsDisposed) return;
            Rectangle r = anchorLabel.RectangleToScreen(
                new Rectangle(Point.Empty, anchorLabel.Size));
            _open = new SpeedGraphForm(r);
            _open.Show(anchorLabel.FindForm());
        }

        /// <summary>光标离开时调用（MainForm 在 MouseLeave 里调；内部有防误关缓冲）</summary>
        public static void HideIfOpen(Label anchorLabel)
        {
            if (_open == null || _open.IsDisposed) return;
            if (anchorLabel == null) { _open.Close(); _open = null; return; }
            Rectangle r = anchorLabel.RectangleToScreen(
                new Rectangle(Point.Empty, anchorLabel.Size));
            r.Inflate(Theme.Px(4), Theme.Px(4));
            if (!r.Contains(Cursor.Position) && !_open.Bounds.Contains(Cursor.Position))
            {
                _open.Close();
                _open = null;
            }
        }
    }
}
