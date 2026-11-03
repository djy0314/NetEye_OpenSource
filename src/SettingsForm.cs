using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace NetEye
{
    /// <summary>
    /// v0.9.2：⚙ 设置小窗（参考 CurrPorts 的 Options 那种「一个输入框就完事」的极简做法）。
    ///
    /// 只放一个真正需要改的项：自定义查询 IP 的网址（config.ini 里的 IpQueryUrl）。
    /// 保存即写盘（UTF-8 带 BOM，注释原样保留），并要求「只认 http/https」——
    /// 这样「查归属地」这个功能永远不会被 ini 变成一个任意程序启动器。
    ///
    /// 深色风格与主窗一致；尺寸全部走 Theme.Px，和主窗共用同一套 DPI 策略（AutoScaleMode.None）。
    /// </summary>
    internal class SettingsForm : Form
    {
        private readonly AppConfig _cfg;
        private TextBox _box;
        private Label _lblTip;
        private Label _lblTitle;
        private FlatButton _btnSave, _btnCancel, _btnReset;
        // v0.9.3：离线 IP 库更新器（纯 C# 下载，不再依赖外部 download_qqwry.ps1 / PowerShell）
        private FlatButton _btnUpdateLib;
        private Label _lblLib;

        /// <summary>保存后的错误信息（失败时非 null，调用方可直接拿来提示）</summary>
        internal string LastError { get; private set; }

        /// <summary>
        /// v0.9.3：离线库更新成功后的回调（MainForm 用来重载 qqwry.dat）。
        /// 会在后台线程被调用，接收方自己负责切回 UI 线程。
        /// </summary>
        internal Action OfflineLibUpdated;

        /// <summary>
        /// v0.9.2 测试钩子：非 null 时代替模态提示框（生产恒 null）。
        /// 验收脚本里「非法网址」这一条必须能在无人值守下跑完，不能卡在弹窗上。
        /// </summary>
        internal static Action<string> AlertHook = null;

        /// <summary>保存成功时写回去的网址（测试断言用）</summary>
        internal string SavedUrl { get; private set; }

        internal SettingsForm(AppConfig cfg)
        {
            _cfg = cfg;
            Text = "设置";
            BackColor = Color.FromArgb(32, 32, 32);
            ForeColor = Theme.Text;
            Font = Theme.Ui(FontStyle.Regular);
            AutoScaleMode = AutoScaleMode.None;   // 与主窗同一策略：尺寸自己按 Theme.Px 算
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            ClientSize = new Size(Theme.Px(560), Theme.Px(330));

            _lblTitle = new Label();
            _lblTitle.Text = "自定义查询 IP 的网址";
            _lblTitle.Font = Theme.Ui(10f, FontStyle.Bold);
            _lblTitle.ForeColor = Theme.Text;
            _lblTitle.BackColor = Color.Transparent;
            _lblTitle.AutoSize = false;
            _lblTitle.Bounds = new Rectangle(Theme.Px(24), Theme.Px(22),
                ClientSize.Width - Theme.Px(48), Theme.Px(24));
            Controls.Add(_lblTitle);

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.FixedSingle;
            _box.BackColor = Theme.InputBg;
            _box.ForeColor = Theme.Text;
            _box.Font = Theme.Mono(9.5f, FontStyle.Regular);
            _box.Text = _cfg == null ? "" : _cfg.IpQueryUrl;
            _box.Bounds = new Rectangle(Theme.Px(24), Theme.Px(54),
                ClientSize.Width - Theme.Px(48), Theme.Px(28));
            _box.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; TrySave(); }
            };
            Controls.Add(_box);

            _lblTip = new Label();
            _lblTip.Text = "支持 {ip} 占位符，例如 https://www.ip.cn/?ip={ip}\r\n"
                + "不写占位符也行：目标 IP 会自动拼在网址末尾。只支持 http:// 或 https://。\r\n"
                + "当前配置文件：" + (_cfg == null ? "config.ini" : _cfg.FilePath);
            _lblTip.Font = Theme.Ui(8.5f, FontStyle.Regular);
            _lblTip.ForeColor = Theme.TextDim;
            _lblTip.BackColor = Color.Transparent;
            _lblTip.AutoSize = false;
            _lblTip.Bounds = new Rectangle(Theme.Px(24), Theme.Px(92),
                ClientSize.Width - Theme.Px(48), Theme.Px(62));
            Controls.Add(_lblTip);

            // v0.9.3：离线 IP 归属地库（qqwry.dat）一键更新 —— 纯 C# 走 HttpClient 下 4 个镜像，
            // 校验通过才替换，全程不碰 PowerShell（企业机 ExecutionPolicy 拦不到它）。
            _lblLib = new Label();
            _lblLib.Text = BuildLibStatus();
            _lblLib.Font = Theme.Ui(8.5f, FontStyle.Regular);
            _lblLib.ForeColor = Theme.TextDim;
            _lblLib.BackColor = Color.Transparent;
            _lblLib.AutoSize = false;
            _lblLib.Bounds = new Rectangle(Theme.Px(24), Theme.Px(160),
                ClientSize.Width - Theme.Px(48) - Theme.Px(120), Theme.Px(52));
            Controls.Add(_lblLib);

            _btnUpdateLib = new FlatButton();
            _btnUpdateLib.Text = "更新离线库";
            _btnUpdateLib.Font = Theme.Ui(9f, FontStyle.Regular);
            _btnUpdateLib.FaceColor = Color.FromArgb(58, 58, 58);
            _btnUpdateLib.HoverColor = Color.FromArgb(72, 72, 72);
            _btnUpdateLib.PressColor = Color.FromArgb(48, 48, 48);
            _btnUpdateLib.ForeColor = Theme.Text;
            _btnUpdateLib.Bounds = new Rectangle(ClientSize.Width - Theme.Px(24) - Theme.Px(112),
                Theme.Px(160), Theme.Px(112), Theme.Px(30));
            _btnUpdateLib.Click += delegate { UpdateOfflineLib(); };
            Controls.Add(_btnUpdateLib);

            // 上一次关窗时下载可能还在后台跑：重新打开设置窗要把「下载中」状态接上，
            // 否则按钮看着能点、点了又什么都不发生，用户会以为坏了。
            if (LibUpdater.IsBusy) SetLibBusy(true, 1, 0, -1);

            int btnH = Theme.Px(30);
            int btnW = Theme.Px(88);
            int by = ClientSize.Height - Theme.Px(20) - btnH;

            _btnReset = new FlatButton();
            _btnReset.Text = "恢复默认";
            _btnReset.Font = Theme.Ui(9f, FontStyle.Regular);
            _btnReset.FaceColor = Color.FromArgb(58, 58, 58);
            _btnReset.HoverColor = Color.FromArgb(72, 72, 72);
            _btnReset.PressColor = Color.FromArgb(48, 48, 48);
            _btnReset.ForeColor = Theme.Text;
            _btnReset.Bounds = new Rectangle(Theme.Px(24), by, btnW + Theme.Px(16), btnH);
            _btnReset.Click += delegate { _box.Text = AppConfig.DefaultIpQueryUrl; _box.Focus(); _box.SelectAll(); };
            Controls.Add(_btnReset);

            _btnCancel = new FlatButton();
            _btnCancel.Text = "取消";
            _btnCancel.Font = Theme.Ui(9f, FontStyle.Regular);
            _btnCancel.FaceColor = Color.FromArgb(58, 58, 58);
            _btnCancel.HoverColor = Color.FromArgb(72, 72, 72);
            _btnCancel.PressColor = Color.FromArgb(48, 48, 48);
            _btnCancel.ForeColor = Theme.Text;
            _btnCancel.Bounds = new Rectangle(ClientSize.Width - Theme.Px(24) - btnW * 2 - Theme.Px(10), by, btnW, btnH);
            _btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(_btnCancel);

            _btnSave = new FlatButton();
            _btnSave.Text = "保存";
            _btnSave.Font = Theme.Ui(9f, FontStyle.Bold);
            _btnSave.Bounds = new Rectangle(ClientSize.Width - Theme.Px(24) - btnW, by, btnW, btnH);
            _btnSave.Click += delegate { TrySave(); };
            Controls.Add(_btnSave);

            CancelButton = _btnCancel;
            AcceptButton = _btnSave;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _box.Focus();
            _box.SelectAll();
        }

        /// <summary>
        /// 取输入框「当下」的文本：直接从窗口句柄读原生文字，而不是走 Text 属性。
        ///
        /// 原因：Text 属性有托管层缓存，遇到外部程序/输入法/远程控制直接 WM_SETTEXT 改控件的情况，
        /// 界面显示已经变了、属性却还可能返回旧值 —— 保存时就会把旧网址又写回去（实测过）。
        /// 用户看到什么就该保存什么，所以这里以原生文字为准；句柄没建好时才退回属性。
        /// </summary>
        internal string CurrentUrl()
        {
            if (_box != null && _box.IsHandleCreated)
            {
                try
                {
                    int len = GetWindowTextLength(_box.Handle);
                    StringBuilder sb = new StringBuilder(len + 2);
                    if (GetWindowText(_box.Handle, sb, sb.Capacity) > 0) return sb.ToString();
                    return "";
                }
                catch (Exception) { }
            }
            return _box == null ? "" : _box.Text;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        /// <summary>校验 + 写盘。失败留在本窗提示（不关窗，用户改一下就能重试）。</summary>
        internal bool TrySave()
        {
            if (_cfg == null) return false;
            string err;
            if (!_cfg.SaveIpQueryUrl(CurrentUrl(), out err))
            {
                LastError = err;
                if (AlertHook != null) AlertHook(err);
                else Theme.Alert(this, "没能保存", err);
                _box.Focus();
                _box.SelectAll();
                return false;
            }
            LastError = null;
            SavedUrl = _cfg.IpQueryUrl;
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        // ================= v0.9.3：离线 IP 库一键更新（纯 C#，无脚本、无 PowerShell） =================

        private static string LibDir()
        {
            return Application.StartupPath;
        }

        private string BuildLibStatus()
        {
            const string title = "离线 IP 归属地库（qqwry.dat，约 25MB）";
            if (LibUpdater.IsBusy)
                return title + "\r\n正在后台下载，完成后自动生效（关掉本窗也不影响）。";
            try
            {
                string dat = System.IO.Path.Combine(LibDir(), LibUpdater.DestName);
                if (System.IO.File.Exists(dat))
                {
                    System.IO.FileInfo fi = new System.IO.FileInfo(dat);
                    return title + " · 已启用 " + (fi.Length / 1048576.0).ToString("0.0") + " MB"
                        + "（" + fi.LastWriteTime.ToString("yyyy-MM-dd") + "）\r\n"
                        + "点右侧按钮直接下最新版：4 个镜像自动切换，完成立即生效、不用重启。";
                }
            }
            catch (Exception) { }   // 库被占用/没权限：状态就当「未安装」，不影响其它功能
            return title + " · 未安装，归属地走浏览器查询\r\n"
                + "点右侧按钮直接下最新版：4 个镜像自动切换，完成立即生效、不用重启。";
        }

        /// <summary>
        /// 点「更新离线库」的完整流程：
        ///   1) 先探写入权限 —— Program Files / 只读盘当场拦下，不让用户白等 25MB；
        ///   2) 按钮禁用 + 文字实时显示百分比；
        ///   3) 下载/校验/替换全在后台线程；关设置窗不中断，关主程序才取消。
        /// </summary>
        private void UpdateOfflineLib()
        {
            if (LibUpdater.IsBusy) return;
            string dir = LibDir();
            string err;
            if (!LibUpdater.EnsureWritable(dir, out err))
            {
                _lblLib.Text = err;
                ShowAlert("无法更新离线库", err);
                return;
            }

            // 抓一份回调：即使本窗先被关掉，主窗照样要把新库装上（否则「更新成功但没生效」）
            Action refresh = OfflineLibUpdated;
            SetLibBusy(true, 1, 0, -1);

            bool started = LibUpdater.StartJob(dir,
                delegate (int idx, long got, long total) { OnLibProgress(idx, got, total); },
                delegate (LibUpdateResult r) { OnLibDone(r, refresh); });
            if (!started)
            {
                SetLibBusy(false, 1, 0, 0);
                _lblLib.Text = "已经有下载任务在跑了，等它结束再点。";
            }
        }

        private void OnLibProgress(int idx, long got, long total)
        {
            InvokeSafe(delegate { SetLibBusy(true, idx, got, total); });
        }

        private void OnLibDone(LibUpdateResult r, Action refresh)
        {
            bool ok = r != null && r.OkFlag;

            // 先把新库装上：主窗那边自己会切回 UI 线程重载 qqwry.dat（旧缓存随之作废）
            if (ok && refresh != null)
            {
                try { refresh(); } catch (Exception) { }
            }

            InvokeSafe(delegate
            {
                SetLibBusy(false, 1, 0, 0);
                if (ok)
                {
                    _lblLib.ForeColor = Theme.Text;
                    _lblLib.Text = "离线库更新成功 · " + r.DetailText + " · "
                        + DateTime.Now.ToString("HH:mm:ss")
                        + "\r\n右键「查询远程 IP 归属地」现在走本地离线解析，无需重启。";
                    ShowAlert("离线库更新成功",
                        "离线库更新成功。\r\n\r\n" + r.DetailText
                        + "\r\n来源：" + ShortHost(r.MirrorUrl)
                        + "\r\n\r\n旧库已备份为 " + LibUpdater.BakName
                        + "，新库已立即生效（不用重启）。");
                }
                else
                {
                    bool canceled = r != null && r.WasCanceled;
                    _lblLib.ForeColor = Theme.TextDim;
                    string why = r == null ? "未知错误" : r.DetailText;
                    _lblLib.Text = (canceled ? "已取消下载。" : "更新失败。")
                        + "\r\n" + why.Replace("\r\n\r\n", "\r\n");
                    if (!canceled) ShowAlert("离线库更新失败", why);
                }
            });
        }

        /// <summary>
        /// 下载中：按钮禁用 + 文字「下载中 N%…」；结束：恢复原文字并启用。
        /// total &lt;= 0（还没拿到 Content-Length）时按已下 MB 显示，绝不显示假百分比。
        /// </summary>
        private void SetLibBusy(bool busy, int idx, long got, long total)
        {
            if (IsDisposed || _btnUpdateLib == null) return;
            _btnUpdateLib.Enabled = !busy;
            if (!busy)
            {
                _btnUpdateLib.Text = "更新离线库";
                _btnUpdateLib.Invalidate();
                return;
            }

            int pct = total > 0 ? (int)(got * 100L / total) : -1;
            if (pct >= 0) _btnUpdateLib.Text = "下载中 " + pct.ToString() + "%…";
            else if (got > 0) _btnUpdateLib.Text = "下载中 " + (got / 1048576.0).ToString("0.0") + "M…";
            else _btnUpdateLib.Text = "连接中…";

            if (_lblLib != null && idx > 0 && idx <= LibUpdater.Mirrors.Length)
            {
                _lblLib.Text = "正在从镜像 " + idx.ToString() + "/" + LibUpdater.Mirrors.Length.ToString()
                    + " 下载（约 25MB，可随时关窗，后台接着下）…";
            }
            _btnUpdateLib.Invalidate();   // 自绘按钮：文字变了立刻重画，不用等下一次鼠标事件
        }

        /// <summary>把长 URL 缩成主机名，弹窗里一行放得下</summary>
        private static string ShortHost(string url)
        {
            try { return new Uri(url).Host; }
            catch (Exception) { return string.IsNullOrEmpty(url) ? "未知来源" : url; }
        }

        /// <summary>
        /// 后台线程 → UI 线程的安全转场：窗口已销毁/句柄已释放就安静丢弃。
        /// 「下载中关窗不崩」就靠这一道：BeginInvoke 打在已 Dispose 的控件上会抛
        /// InvalidOperationException，而那时已经没人能接住它了。
        /// </summary>
        private void InvokeSafe(MethodInvoker act)
        {
            try
            {
                if (IsDisposed || Disposing || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(act);
                else act();
            }
            catch (Exception) { }    // 判活之后、投递之前被销毁的竞态：丢掉即可
        }

        private void ShowAlert(string title, string msg)
        {
            if (AlertHook != null) { AlertHook(msg); return; }
            try { Theme.Alert(this, title, msg); }
            catch (Exception) { }
        }

        internal TextBox UrlBox { get { return _box; } }
        internal FlatButton SaveButton { get { return _btnSave; } }
        /// <summary>v0.9.3：离线库更新按钮（测试断言用）</summary>
        internal FlatButton UpdateLibButton { get { return _btnUpdateLib; } }
    }
}
