using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NetEye
{
    /// <summary>
    /// 深色主题（与 LAN快传 同一套色板）。
    /// DPI 策略：清单声明 PerMonitorV2（进程级真 DPI 感知，不模糊），
    /// 布局尺寸全部按 96 DPI 的逻辑像素书写，由 Px() 换算成当前显示器像素；
    /// 字体用 GraphicsUnit.Pixel 精确控制，避免 WinForms 自动缩放叠加导致二次放大。
    /// </summary>
    internal static class Theme
    {
        // ---------- 色板 ----------
        public static readonly Color Bg = Color.FromArgb(24, 24, 24);
        public static readonly Color PanelBg = Color.FromArgb(30, 30, 30);
        public static readonly Color InputBg = Color.FromArgb(40, 40, 40);
        public static readonly Color Border = Color.FromArgb(58, 58, 58);
        public static readonly Color Text = Color.FromArgb(224, 224, 224);
        public static readonly Color TextWeak = Color.FromArgb(170, 170, 170);
        public static readonly Color TextDim = Color.FromArgb(128, 128, 128);
        public static readonly Color Accent = Color.FromArgb(76, 175, 80);      // #4CAF50
        public static readonly Color AccentHover = Color.FromArgb(92, 190, 96);
        public static readonly Color AccentDark = Color.FromArgb(43, 138, 62);  // #2b8a3e
        public static readonly Color Danger = Color.FromArgb(244, 67, 54);
        public static readonly Color DangerHover = Color.FromArgb(255, 92, 80);
        public static readonly Color Warn = Color.FromArgb(255, 193, 7);
        public static readonly Color GridBg = Color.FromArgb(28, 28, 28);
        public static readonly Color GridAlt = Color.FromArgb(33, 33, 33);
        public static readonly Color GridSel = Color.FromArgb(43, 74, 51);
        public static readonly Color GridLine = Color.FromArgb(45, 45, 45);
        public static readonly Color HeaderBg = Color.FromArgb(38, 38, 38);

        // 右键菜单（#1E1E1E 底 + #2b8a3e 极客绿悬停，与表格深色统一）
        public static readonly Color MenuBg = Color.FromArgb(30, 30, 30);       // #1E1E1E
        public static readonly Color MenuHover = Color.FromArgb(43, 138, 62);   // #2b8a3e
        public static readonly Color MenuPressed = Color.FromArgb(35, 110, 50);
        public static readonly Color MenuSep = Color.FromArgb(58, 58, 58);

        // ---------- DPI 与字体 ----------
        private static int _dpi = 96;
        private static float _scale = 1f;
        private static FontFamily _uiFamily;
        private static string _uiFamilyName = "Microsoft YaHei UI";
        private static float _uiPt = 9f;
        private static FontFamily _monoFamily;
        private static FontFamily _menuFamily;
        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();

        static Theme()
        {
            try
            {
                Font f = SystemFonts.MessageBoxFont;
                if (f != null)
                {
                    _uiFamily = f.FontFamily;
                    _uiFamilyName = f.FontFamily.Name;
                    _uiPt = f.SizeInPoints;
                }
            }
            catch { }
            if (_uiFamily == null)
            {
                try { _uiFamily = new FontFamily("Microsoft YaHei UI"); }
                catch { _uiFamily = FontFamily.GenericSansSerif; }
            }
            // 系统字体可能被改成很奇怪的字号，收敛到可读区间
            if (_uiPt < 8f || _uiPt > 13f) _uiPt = 9f;

            try { _monoFamily = new FontFamily("Consolas"); }
            catch { _monoFamily = FontFamily.GenericMonospace; }

            // 菜单按需求用 Microsoft YaHei（不是 YaHei UI）；机器上没有就退回界面字体
            try { _menuFamily = new FontFamily("Microsoft YaHei"); }
            catch { _menuFamily = _uiFamily; }
        }

        public static int Dpi { get { return _dpi; } }
        public static float Scale { get { return _scale; } }
        public static string UiFamilyName { get { return _uiFamilyName; } }

        /// <summary>切换 DPI：重建字体缓存（旧字体必须释放，否则 GDI 句柄泄漏）</summary>
        public static void SetDpi(int dpi)
        {
            if (dpi < 72) dpi = 96;
            if (dpi > 480) dpi = 480;
            if (dpi == _dpi) return;
            _dpi = dpi;
            _scale = dpi / 96f;
            foreach (KeyValuePair<string, Font> kv in _fonts) { try { kv.Value.Dispose(); } catch { } }
            _fonts.Clear();
        }

        /// <summary>96 DPI 逻辑像素 → 当前 DPI 物理像素</summary>
        public static int Px(int logical) { return (int)Math.Round(logical * (double)_scale); }
        public static int Px(float logical) { return (int)Math.Round(logical * (double)_scale); }

        /// <summary>界面字体（跟随系统 SystemFonts.MessageBoxFont 的字族与字号）</summary>
        public static Font Ui(FontStyle style) { return FontOf("ui", _uiPt, style); }
        public static Font Ui(float pt, FontStyle style) { return FontOf("ui", pt, style); }

        /// <summary>右键菜单字体（Microsoft YaHei 10.5pt，缺字体退系统 UI 字体）</summary>
        public static Font Menu() { return FontOf("menu", 10.5f, FontStyle.Regular); }

        /// <summary>表格等宽字体（Consolas，缺字体时退到系统等宽）</summary>
        public static Font Mono(float pt, FontStyle style) { return FontOf("mono", pt, style); }

        private static Font FontOf(string kind, float pt, FontStyle style)
        {
            string key = kind + "|" + pt.ToString("0.##") + "|" + ((int)style).ToString() + "|" + _dpi.ToString();
            Font f;
            if (_fonts.TryGetValue(key, out f)) return f;
            FontFamily fam = kind == "mono" ? _monoFamily : (kind == "menu" ? _menuFamily : _uiFamily);
            // 点 → 像素，并乘当前缩放：pt * 96/72 = 96 DPI 下的像素高
            float px = pt * (96f / 72f) * _scale;
            if (px < 6f) px = 6f;
            try { f = new Font(fam, px, style, GraphicsUnit.Pixel); }
            catch { f = new Font(FontFamily.GenericSansSerif, px, style, GraphicsUnit.Pixel); }
            _fonts[key] = f;
            return f;
        }

        // ---------- 深色标题栏（Win10 1809+ / Win11 生效，老系统静默忽略） ----------
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        /// <summary>把窗口标题栏切到深色；不支持的系统返回错误码，直接忽略</summary>
        public static void DarkTitleBar(IWin32Window window)
        {
            Control c = window as Control;
            if (c == null || !c.IsHandleCreated) return;
            try
            {
                int on = 1;
                DwmSetWindowAttribute(c.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
            }
            catch (Exception) { }
        }

        // ---------- 自绘工具 ----------
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0) { p.AddRectangle(r); return p; }
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>模态确认框（自绘深色，避免系统亮色弹窗破坏氛围）</summary>
        public static bool Confirm(IWin32Window owner, string title, string message, string okText, bool danger)
        {
            using (DarkDialog d = new DarkDialog(title, message, okText, danger, true))
                return d.ShowDialog(owner) == DialogResult.OK;
        }

        /// <summary>模态提示框（只有一个「知道了」）</summary>
        public static void Alert(IWin32Window owner, string title, string message)
        {
            using (DarkDialog d = new DarkDialog(title, message, "知道了", false, false))
                d.ShowDialog(owner);
        }
    }

    /// <summary>深色扁平按钮（自绘，避免视觉样式下的系统亮蓝）</summary>
    internal class FlatButton : Button
    {
        private bool _hover, _press;
        public Color FaceColor = Theme.Accent;
        public Color HoverColor = Theme.AccentHover;
        public Color PressColor = Theme.AccentDark;

        public FlatButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            ForeColor = Color.White;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color face = !Enabled ? Color.FromArgb(60, 60, 60)
                : (_press ? PressColor : (_hover ? HoverColor : FaceColor));
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.RoundRect(r, Theme.Px(4)))
            {
                using (SolidBrush b = new SolidBrush(face)) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Text, Font, r,
                Enabled ? ForeColor : Color.FromArgb(150, 150, 150),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
            {
                Rectangle fr = Rectangle.Inflate(r, -Theme.Px(4), -Theme.Px(4));
                ControlPaint.DrawFocusRectangle(g, fr, Color.White, face);
            }
        }
    }

    /// <summary>深色搜索框：Panel 画边框 + 内嵌无边框 TextBox + 放大镜图标 + 占位提示</summary>
    internal class SearchBox : Panel
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private readonly TextBox _box;
        private string _cue = "";

        public SearchBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.InputBg;
            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Theme.InputBg;
            _box.ForeColor = Theme.Text;
            _box.Font = Theme.Ui(FontStyle.Regular);
            _box.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            _box.GotFocus += delegate { Invalidate(); };
            _box.LostFocus += delegate { Invalidate(); };
            _box.KeyDown += delegate (object s, KeyEventArgs e) { OnKeyDown(e); };
            Controls.Add(_box);
        }

        public TextBox Inner { get { return _box; } }

        public string CueText
        {
            get { return _cue; }
            set { _cue = value == null ? "" : value; ApplyCue(); }
        }

        public override string Text
        {
            get { return _box.Text; }
            set { _box.Text = value; }
        }

        public new event EventHandler TextChanged
        {
            add { base.TextChanged += value; }
            remove { base.TextChanged -= value; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Relayout();
            ApplyCue();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _box.Font = Font;
            Relayout();
        }

        private void Relayout()
        {
            _box.Location = new Point(Theme.Px(32), Math.Max(0, (Height - _box.PreferredHeight) / 2));
            _box.Width = Math.Max(Theme.Px(20), Width - Theme.Px(32) - Theme.Px(10));
        }

        private void ApplyCue()
        {
            if (!_box.IsHandleCreated) return;
            try { SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, _cue); }
            catch { }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.RoundRect(r, Theme.Px(5)))
            {
                using (SolidBrush b = new SolidBrush(Theme.InputBg)) g.FillPath(b, p);
                Color bc = _box.Focused ? Theme.Accent : Theme.Border;
                using (Pen pen = new Pen(bc)) g.DrawPath(pen, p);
            }
            // 放大镜
            Color ic = _box.Focused ? Theme.Accent : Theme.TextDim;
            int cx = Theme.Px(15), cy = Height / 2 - Theme.Px(1), rad = Theme.Px(5);
            using (Pen pen = new Pen(ic, Math.Max(1f, Theme.Px(1.4f))))
            {
                g.DrawEllipse(pen, cx - rad, cy - rad, rad * 2, rad * 2);
                g.DrawLine(pen, cx + rad - Theme.Px(1), cy + rad - Theme.Px(1),
                    cx + rad + Theme.Px(3), cy + rad + Theme.Px(3));
            }
        }
    }

    /// <summary>深色模态对话框（确认 / 提示共用）</summary>
    internal class DarkDialog : Form
    {
        private static readonly Color OkDanger = Color.FromArgb(198, 60, 48);
        private readonly string _message;
        private readonly bool _danger;
        private readonly string _okText;

        public DarkDialog(string title, string message, string okText, bool danger, bool cancel)
        {
            _message = message == null ? "" : message;
            _danger = danger;
            _okText = string.IsNullOrEmpty(okText) ? "确定" : okText;

            FormBorderStyle = FormBorderStyle.FixedDialog;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Theme.Text;
            Font = Theme.Ui(FontStyle.Regular);
            Text = title;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;

            int w = Theme.Px(460);
            int titleH = Theme.Px(34);
            Font msgFont = Theme.Ui(9.5f, FontStyle.Regular);
            int textW = w - Theme.Px(56);
            Size sz = TextRenderer.MeasureText(_message, msgFont,
                new Size(textW, 2000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int h = titleH + sz.Height + Theme.Px(80);
            ClientSize = new Size(w, h);
            MinimumSize = MaximumSize = Size;

            Label lbl = new Label();
            lbl.Text = _message;
            lbl.Font = msgFont;
            lbl.ForeColor = Theme.Text;
            lbl.BackColor = Color.Transparent;
            lbl.Bounds = new Rectangle(Theme.Px(28), Theme.Px(26), textW, sz.Height + Theme.Px(8));
            Controls.Add(lbl);

            int btnH = Theme.Px(30);
            int btnW = Theme.Px(96);
            int by = h - Theme.Px(18) - btnH;

            FlatButton ok = new FlatButton();
            ok.Text = _okText;
            ok.Font = Theme.Ui(9f, FontStyle.Bold);
            ok.FaceColor = _danger ? OkDanger : Theme.Accent;
            ok.HoverColor = _danger ? Theme.DangerHover : Theme.AccentHover;
            ok.PressColor = _danger ? Color.FromArgb(160, 45, 35) : Theme.AccentDark;
            ok.Bounds = new Rectangle(w - Theme.Px(28) - btnW, by, btnW, btnH);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            if (cancel)
            {
                FlatButton no = new FlatButton();
                no.Text = "取消";
                no.Font = Theme.Ui(9f, FontStyle.Regular);
                no.FaceColor = Color.FromArgb(58, 58, 58);
                no.HoverColor = Color.FromArgb(72, 72, 72);
                no.PressColor = Color.FromArgb(48, 48, 48);
                no.ForeColor = Theme.Text;
                no.Bounds = new Rectangle(ok.Left - Theme.Px(10) - btnW, by, btnW, btnH);
                no.DialogResult = DialogResult.Cancel;
                Controls.Add(no);
                CancelButton = no;
            }
            AcceptButton = ok;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawLine(p, 0, 0, ClientSize.Width, 0);
        }
    }

    /// <summary>右键菜单的深色配色表（默认是系统亮色，会跟主题冲突）</summary>
    internal class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.MenuBg; } }
        public override Color ImageMarginGradientBegin { get { return Theme.MenuBg; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.MenuBg; } }
        public override Color ImageMarginGradientEnd { get { return Theme.MenuBg; } }
        // 无边框：边框色 = 底色，配合渲染器不画外框
        public override Color MenuBorder { get { return Theme.MenuBg; } }
        public override Color MenuItemBorder { get { return Theme.MenuBg; } }
        public override Color MenuItemSelected { get { return Theme.MenuHover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.MenuHover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.MenuHover; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.MenuPressed; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.MenuPressed; } }
        public override Color SeparatorDark { get { return Theme.MenuSep; } }
        public override Color SeparatorLight { get { return Theme.MenuSep; } }
        public override Color CheckBackground { get { return Theme.AccentDark; } }
        public override Color CheckSelectedBackground { get { return Theme.AccentDark; } }
    }

    /// <summary>极客风菜单渲染器：#1E1E1E 底、白字、悬停整行填 #2b8a3e、无外边框</summary>
    internal class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            // 故意不画边框：系统默认那条亮线是「突兀感」的主要来源
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            // 自己填纯色，避免专业渲染器的渐变在深色底上出现色带
            bool hot = e.Item.Selected || e.Item.Pressed;
            if (!hot || !e.Item.Enabled) return;
            Rectangle r = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            using (SolidBrush b = new SolidBrush(e.Item.Pressed ? Theme.MenuPressed : Theme.MenuHover))
                e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Color.FromArgb(105, 105, 105) : Color.White;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle r = new Rectangle(0, e.Item.Height / 2, e.Item.Width, 1);
            using (SolidBrush b = new SolidBrush(Theme.MenuSep))
                e.Graphics.FillRectangle(b, r);
        }
    }
}
