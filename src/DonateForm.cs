using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace NetEye
{
    /// <summary>
    /// v0.8：极客式赞赏彩蛋（与 LAN快传 v1.3.8 同一套组件） —— 深色无边框小窗（宽 350，高按文案实际行数自适应）。
    /// 图片来源：exe 同级 donate.png（想换图就放一张，优先级高）；没放就用内嵌 Base64，
    /// 所以发布的 exe 永远是纯单文件、开箱即显。
    /// 原则：赞赏纯属彩蛋，任何异常（图片读不出、GDI 抽风）都不能影响主程序，
    /// 所以图片加载全部兜底成占位文字，绝不让弹窗半路崩掉。
    ///
    /// v0.8 修：文案 Label 原来写死 118px 高，四段文字实测要 7 行 ≈ 140px，
    /// 最后一句「也是对我最大的支持！」被窗口下缘裁掉。现在：
    ///   1) 文案 Label 用 AutoSize + MaximumSize(内容宽, 0) 自适应换行高度；
    ///   2) 图片、按钮的位置全部按「上一个控件下缘 + 间距」链式排布，不再写死坐标；
    ///   3) 窗体高度 = 最后一个控件下缘 + 底部留白，文案与图片、图片与按钮各保 14px 安全间距。
    ///
    /// v0.8 终调（坛友反馈排版粗糙）：四段文案拆成 4 个 Label、段间 7px 垂直间距拉开行距；
    /// 四周内边距 18→22px；二维码上下留白 14→22px、左右各 30px，不再贴着文字和按钮；
    /// StartPosition = CenterParent，弹出时相对主窗口居中。
    /// </summary>
    internal class DonateForm : Form
    {
        private const int DesignW = 350;
        private const int SideMargin = 22;      // 左右内边距（坛友要求 ≥20px）
        private const int TopMargin = 20;       // 顶部内边距
        private const int GapTitleBody = 12;    // 标题 → 文案
        private const int ParaGap = 7;          // 文案段落之间的垂直间距（拉开行距）
        private const int GapBodyPic = 22;      // 文案 → 赞赏码（≥10px 安全间距，留白更舒展）
        private const int GapPicBtn = 22;       // 赞赏码 → 关闭按钮（≥10px 安全间距）
        private const int GapBottom = 18;       // 按钮 → 窗体下缘
        private const int PicW = 290;           // 二维码宽（左右各留 30px 空隙）
        private const int PicH = 216;

        // 供测试核验布局（内部只读）
        internal Label[] BodyParas;             // 四段文案各自一个 Label（段间拉开行距）
        internal PictureBox CodeBox;
        internal Button CloseButton;

        public DonateForm(string imagePath)
        {
            this.Text = "关于这杯咖啡 ☕";
            this.FormBorderStyle = FormBorderStyle.None;
            this.StartPosition = FormStartPosition.CenterParent;
            this.ShowInTaskbar = false;
            this.BackColor = Color.FromArgb(24, 24, 24);
            this.ForeColor = Color.FromArgb(224, 224, 224);
            this.Font = new Font("Microsoft YaHei", 9F);
            this.KeyPreview = true;
            this.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) this.Close();   // Esc 也能关
            };

            int contentW = DesignW - SideMargin * 2;          // 314

            // ---- 标题（同时是拖拽把手——无边框窗口得能挪）----
            Label lblTitle = new Label
            {
                Text = "关于这杯咖啡 ☕",
                AutoSize = true,
                Location = new Point(SideMargin, 14),
                ForeColor = Color.FromArgb(76, 175, 80),
                Font = new Font("Microsoft YaHei", 12F, FontStyle.Bold),
                Cursor = Cursors.SizeAll
            };
            lblTitle.MouseDown += DragMove;
            this.Controls.Add(lblTitle);

            // ---- 走心文案（原样，一字不改；四段各一个 Label，段间拉开行距）----
            // AutoSize + MaximumSize：先按内容宽度折行，再让 Label 自己长高，
            // 文字有多少行就占多少行，从根上杜绝「最后一句被裁」。
            string[] paras = new string[]
            {
                "这个工具完全免费、无广告、纯本地不联网。",
                "如果你觉得它帮你省下了敲命令的时间，可以随缘请作者喝杯咖啡。",
                "纯自愿！所有功能永远免费！不赞赏也完全不影响任何使用。",
                "如果您不想花钱，去论坛帖子里点个赞、回个帖，也是对我最大的支持！"
            };
            Label[] bodyParas = new Label[paras.Length];
            for (int i = 0; i < paras.Length; i++)
            {
                Label p = new Label
                {
                    MaximumSize = new Size(contentW, 0),
                    Text = paras[i],
                    AutoSize = true,
                    Location = new Point(SideMargin, 0),   // 纵向位置随前一个控件下缘算
                    ForeColor = Color.FromArgb(170, 170, 170),
                    Font = new Font("Microsoft YaHei", 9F)
                };
                // 双保险：万一 AutoSize 没算出多行高度（字体缺失 / 主题接管），
                // 用 TextRenderer 手量一遍，取更大值，宁多勿少。
                int needH = MeasureBodyHeight(p.Text, p.Font, contentW);
                if (p.Height < needH)
                {
                    p.AutoSize = false;
                    p.Size = new Size(contentW, needH);
                }
                bodyParas[i] = p;
                this.Controls.Add(p);
            }
            BodyParas = bodyParas;
            Label lastPara = bodyParas[bodyParas.Length - 1];

            // ---- 纵向链式排布：标题 → 文案（4 段）→ 赞赏码 → 按钮 ----
            int y = lblTitle.Bottom + GapTitleBody;
            for (int i = 0; i < bodyParas.Length; i++)
            {
                bodyParas[i].Top = y;
                y = bodyParas[i].Bottom + (i == bodyParas.Length - 1 ? 0 : ParaGap);
            }

            int picH = PicH;
            // 小屏幕兜底：整窗超过工作区 92% 就压一压赞赏码，保证按钮一定在屏内
            Size wa = Screen.PrimaryScreen == null ? new Size(1080, 1920)
                : Screen.PrimaryScreen.WorkingArea.Size;
            int estimate = lastPara.Bottom + GapBodyPic + picH + GapPicBtn + 32 + GapBottom;
            if (estimate > wa.Height * 0.92)
            {
                picH = Math.Max(120, picH - (estimate - (int)(wa.Height * 0.92)));
            }

            // 赞赏码：整窗最大的区域；读不出就给占位文字，绝不报错
            PictureBox pic = new PictureBox
            {
                Location = new Point((DesignW - PicW) / 2, lastPara.Bottom + GapBodyPic),
                Size = new Size(PicW, picH),
                BackColor = Color.FromArgb(32, 32, 32),
                BorderStyle = BorderStyle.FixedSingle,
                SizeMode = PictureBoxSizeMode.Zoom,
                WaitOnLoad = false
            };
            // 赞赏码来源：imagePath 非 null = exe 同级 donate.png（用户自选图，优先）；
            // null = 用内嵌 Base64（DonateImage）。两边都读不出才显示占位文字，绝不报错。
            try
            {
                if (imagePath != null)
                {
                    pic.Image = Image.FromFile(imagePath);
                }
                else
                {
                    byte[] raw = DonateImage.Bytes;
                    if (raw == null || raw.Length == 0) throw new InvalidOperationException("no embedded image");
                    using (System.IO.MemoryStream ms = new System.IO.MemoryStream(raw))
                    using (Image tmp = Image.FromStream(ms))
                    {
                        pic.Image = new Bitmap(tmp);   // 复制一份，和已关闭的流脱钩
                    }
                }
                pic.SizeMode = ImageTooBig(pic.Image, pic.Size)
                    ? PictureBoxSizeMode.Zoom : PictureBoxSizeMode.CenterImage;
            }
            catch
            {
                pic.Image = null;
                pic.Paint += delegate(object s, PaintEventArgs e2)
                {
                    e2.Graphics.DrawString("赞赏码加载失败\n请检查 donate.png 是否完好",
                        new Font("Microsoft YaHei", 9F),
                        Brushes.Gray,
                        new RectangleF(0, 0, pic.Width, pic.Height),
                        new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        });
                };
                pic.Invalidate();
            }
            this.Controls.Add(pic);
            CodeBox = pic;

            // ---- 底部绿色「关闭」----
            Button btnClose = new Button
            {
                Text = "关闭",
                Location = new Point((DesignW - 110) / 2, pic.Bottom + GapPicBtn),
                Size = new Size(110, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(76, 175, 80),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei", 9.75F, FontStyle.Bold)
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.Click += delegate { this.Close(); };
            this.Controls.Add(btnClose);
            CloseButton = btnClose;

            // 窗体高度跟着内容走：按钮下缘 + 底部留白
            this.ClientSize = new Size(DesignW, btnClose.Bottom + GapBottom);

            // 点窗体空白处也能拖（无边框窗口的基本体面）
            this.MouseDown += DragMove;
        }

        /// <summary>按实际字体量一遍折行后的文字高度（Label 默认走 GDI/TextRenderer，量法一致）。</summary>
        internal static int MeasureBodyHeight(string text, Font font, int width)
        {
            Size sz = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
            // 减去 TextBoxControl 那点额外内边距，再加 2px 余量
            return Math.Max(sz.Height, 1) + 2;
        }

        private static bool ImageTooBig(Image img, Size box)
        {
            return img.Width > box.Width || img.Height > box.Height;
        }

        /// <summary>无边框窗口拖拽：把按下区域伪装成标题栏丢给系统（最省事也最稳的老办法）。</summary>
        private void DragMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            try
            {
                ReleaseCapture();
                SendMessage(this.Handle, 0x00A1 /*WM_NCLBUTTONDOWN*/, 0x0002 /*HTCAPTION*/, 0);
            }
            catch { }   // 拖不动也不影响任何功能
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 无边框窗口加一圈细描边，否则和背后的主窗口糊成一片
            this.Paint += delegate(object s, PaintEventArgs e2)
            {
                using (Pen p = new Pen(Color.FromArgb(70, 70, 70)))
                {
                    Rectangle rc = this.ClientRectangle;
                    rc.Width--; rc.Height--;
                    e2.Graphics.DrawRectangle(p, rc);
                }
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Enter) { this.Close(); return true; }   // 回车关闭，键盘党友好
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Image.FromFile 会锁文件，主动释放让用户能随时替换 donate.png
                foreach (Control c in this.Controls)
                {
                    PictureBox pic = c as PictureBox;
                    if (pic != null && pic.Image != null)
                    {
                        pic.Image.Dispose();
                        pic.Image = null;
                    }
                }
            }
            base.Dispose(disposing);
        }
    }
}
