using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NetEye
{
    /// <summary>
    /// 主窗口：极简深色网络连接查看器。
    /// 表格用 VirtualMode（只按需取单元格值），几百上千条连接也是瞬间出结果。
    /// </summary>
    internal class MainForm : Form
    {
        // DPI
        private const int WM_DPICHANGED = 0x02E0;
        private const int DesignWidth = 1080;
        private const int DesignHeight = 680;

        // 列索引
        // v0.9.2：新增「运行时长」列（参考 CurrPorts 的 Created），放在进程名之后、进程类型之前
        private const int ColProto = 0, ColLocal = 1, ColRemote = 2, ColState = 3, ColPid = 4, ColProc = 5,
            ColUptime = 6, ColKind = 7;

        private SearchBox _search;
        private ComboBox _cmbMatch;           // v0.9.3：搜索匹配模式（包含 / 精确 / 正则）
        private FlatButton _btnRefresh;
        private FlatButton _btnExport;
        private FlatButton _btnSettings;      // v0.9.2：⚙ 设置（内嵌 config.ini 编辑）
        private ComboBox _cmbRate;
        private Timer _autoTimer;             // v0.6 自动刷新（v0.9.2 起常开，不再有开关）
        private string _pendingSelKey;        // v0.6 刷新后要恢复的选中行键（null=清空选择）
        private readonly Qqwry _qqwry = new Qqwry();   // v0.6 离线 IP 库（可选外挂）
        private readonly AppConfig _cfg = new AppConfig();  // v0.7 同目录 config.ini（自定义查询网址）
        private Label _lblHint;
        private Panel _gridHost;
        private DataGridView _grid;
        private Label _lblLeft, _lblRight;
        private Label _lblDonate;             // v0.8 赞赏彩蛋入口（状态栏最右）
        private Panel _line;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _miCopy, _miKill, _miLookup, _miOpenPath, _miWebSearch;
        // v0.9.3 右键新成员
        private ToolStripMenuItem _miDns, _miFirewall, _miCompact, _miLog;
        private Timer _flash;
        private Timer _uptimeTimer;           // v0.9.1 运行时长：每秒刷新一次（v0.9.2 起顺带采网速）
        private Label _lblUptime;             // v0.9.1 状态栏「已运行 HH:mm:ss」
        private Label _lblSpeed;              // v0.9.2 状态栏「↑ 12 KB/s  ↓ 150 KB/s」
        private readonly Stopwatch _upWatch = Stopwatch.StartNew();   // v0.9.1 本次启动计时起点

        private List<NetRow> _all = new List<NetRow>();
        private List<NetRow> _view = new List<NetRow>();
        private string[] _hay = new string[0];
        private int _sortCol = ColLocal;
        private bool _sortAsc = true;
        private double _lastMs;
        private int _statusTop;
        private bool _laidOut;
        private bool _ready;
        private bool _busy;
        private bool _fitted;                 // v0.9.2：是否已按显示器工作区做过首次自适应
        private Size _screenCap;              // v0.9.2：当前显示器能容纳的最大客户区（Size.Empty=不限制）
        private readonly Dictionary<string, DataGridViewCellStyle> _styles = new Dictionary<string, DataGridViewCellStyle>();

        // v0.9.3：界面偏好（config.ini 持久化）
        private int _matchMode;               // 0 包含 / 1 精确 / 2 正则
        private bool _compact;                // 紧凑行高
        private bool _logNew;                 // 记录模式：新连接追加写 logs\NetEye_连接_日期.csv
        private readonly HashSet<string> _connSeen = new HashSet<string>();
        private bool _applyingColumnWidths;   // 程序内改列宽时不回写 config（防回环）

        /// <summary>结束进程黑名单：动它们等于给系统灌毒，一律拦截</summary>
        private static readonly string[] ProtectedProcessNames = new string[]
        {
            "system", "idle", "system idle process", "secure system", "registry",
            "memcompression", "csrss", "wininit", "services", "lsass", "lsm",
            "smss", "winlogon", "logonui", "svchost", "dwm", "fontdrvhost", "lsaiso"
        };

        public MainForm()
        {
            Theme.SetDpi(96);
            Text = "NetEye 网络透视镜 v0.9.3";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(FontStyle.Regular);
            // v0.8 说明：这里坚持 None，不是漏改 —— 本窗口所有尺寸都由 Theme.Px 按当前 DPI 手算
            // （见 ApplyDpi / ApplyLayout）。若改成 AutoScaleMode.Dpi，WinForms 会再按 dpi/96
            // 把控件乘一遍，叠上 Theme.Px 就是「缩放平方」：200% 屏上窗口 4 倍大、控件全飞出屏幕。
            // 高分屏自适应的正确做法是下面这套：OnHandleCreated 取真实 DPI → ApplyDpi →
            // ApplyLayout 重排；WM_DPICHANGED 时同样重排，窗口跨屏不模糊也不变形。
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadAppIcon();
            ClientSize = new Size(Theme.Px(DesignWidth), Theme.Px(DesignHeight));

            BuildUi();
            _ready = true;
            ApplyLayout(true);
            // v0.5：进程类型靠后台线程读签名（单次 27ms，绝不能同步做），读到就刷类型列
            ProcBadge.Updated += OnBadgeUpdated;
            ProcIcons.Updated += OnIconsUpdated;    // v0.9.3：图标读到就刷进程名列
            RefreshData();
            // 别一打开就选中第一行，看着像误操作过
            _grid.ClearSelection();
            _grid.CurrentCell = null;
            // v0.6：外挂离线 IP 库（可选）。exe 同目录有 qqwry.dat 就整块读进内存（只读），
            // 归属地查询变本地解析；没有就维持浏览器兜底，行为体积都不变。
            LoadQqwrySidecar();
            // v0.7：同目录 config.ini（没有就写一份默认的）——只影响浏览器查询网址，
            // 有 qqwry.dat 时仍然是本地离线优先，压根不打开浏览器。
            LoadConfig();
            // v0.9.3：读回界面偏好（匹配模式 / 行高 / 记录模式 / 列宽）
            LoadPrefs();
        }

        /// <summary>v0.7：读同目录 config.ini（缺文件先写一份默认的），失败一律回落内置默认值</summary>
        private void LoadConfig()
        {
            _cfg.EnsureFile();
            _cfg.Reload(true);
        }

        /// <summary>v0.9.3：把 config.ini 里的界面偏好应用到控件（启动时一次）</summary>
        private void LoadPrefs()
        {
            int mode = _cfg.GetInt("MatchMode", 0, 0, 2);
            if (mode != _matchMode) { _matchMode = mode; _cmbMatch.SelectedIndex = mode; }

            bool compact = _cfg.GetBool("RowCompact", false);
            if (compact != _compact)
            {
                _compact = compact;
                if (_miCompact != null) _miCompact.Text = CompactMenuText();
                ApplyGridMetrics();
            }

            bool log = _cfg.GetBool("LogNewConn", false);
            if (log != _logNew)
            {
                _logNew = log;
                if (_miLog != null) _miLog.Text = LogMenuText();
            }

            ApplyPersistedColumnWidths();
        }

        private string CompactMenuText()
        {
            return "紧凑行高：" + (_compact ? "开" : "关");
        }

        private string LogMenuText()
        {
            return "记录新连接：" + (_logNew ? "开" : "关");
        }

        private void LoadQqwrySidecar()
        {
            try
            {
                string path = System.IO.Path.Combine(Application.StartupPath, "qqwry.dat");
                if (_qqwry.Load(path))
                {
                    _miLookup.Text = "查询远程 IP 归属地（本地离线库）";
                    UpdateStatus();
                }
            }
            catch (Exception) { }    // 库坏/被占用都不致命：查询自动退回浏览器方案
        }

        /// <summary>
        /// 窗体图标：优先用 exe 同目录的 app.ico（用户可自行换图标），
        /// 没有就用 exe 内嵌的 win32 图标（/win32icon 编译进去的那份），
        /// 保证「只拷一个 exe」也有雷达图标，绝不因为缺文件崩掉。
        /// </summary>
        private static Icon LoadAppIcon()
        {
            try
            {
                string side = System.IO.Path.Combine(Application.StartupPath, "app.ico");
                if (System.IO.File.Exists(side)) return new Icon(side);
            }
            catch (Exception) { }
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }
            return null;
        }

        // ================= 构建界面 =================
        private void BuildUi()
        {
            _search = new SearchBox();
            _search.CueText = "搜索进程名 / 端口 / 地址 / PID";
            _search.TextChanged += delegate { BuildView(); };
            Controls.Add(_search);

            // v0.9.3：匹配模式下拉（包含 / 精确 / 正则）。放在搜索框右侧，宽度只占一小格。
            _cmbMatch = new ComboBox();
            _cmbMatch.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbMatch.FlatStyle = FlatStyle.Flat;
            _cmbMatch.BackColor = Theme.InputBg;
            _cmbMatch.ForeColor = Theme.Text;
            _cmbMatch.ItemHeight = Theme.Px(16);
            _cmbMatch.Items.Add("包含");
            _cmbMatch.Items.Add("精确");
            _cmbMatch.Items.Add("正则");
            _cmbMatch.SelectedIndex = 0;      // 默认「包含」，与 v0.9.2 之前的行为完全一致
            _cmbMatch.DrawMode = DrawMode.OwnerDrawFixed;
            _cmbMatch.DrawItem += CmbSimple_DrawItem;
            _cmbMatch.SelectedIndexChanged += delegate
            {
                _matchMode = _cmbMatch.SelectedIndex < 0 ? 0 : _cmbMatch.SelectedIndex;
                if (_ready) BuildView();
            };
            Controls.Add(_cmbMatch);

            _btnRefresh = new FlatButton();
            _btnRefresh.Text = "刷新";
            _btnRefresh.Click += delegate { RefreshData(); };
            Controls.Add(_btnRefresh);

            // v0.6：导出结果快照（UTF-8 带 BOM 的 CSV，Excel 双击即开）
            _btnExport = new FlatButton();
            _btnExport.Text = "导出";
            _btnExport.FaceColor = Color.FromArgb(64, 64, 64);
            _btnExport.HoverColor = Color.FromArgb(80, 80, 80);
            _btnExport.PressColor = Color.FromArgb(52, 52, 52);
            _btnExport.Click += delegate { ExportCsv(); };
            Controls.Add(_btnExport);

            // v0.9.2：⚙ 设置 —— 内嵌编辑 config.ini 里的「自定义查询网址」，
            // 让不懂 ini 的用户也能改（参考 CurrPorts 的 Options 小窗）。保存即写盘，不用重启。
            _btnSettings = new FlatButton();
            _btnSettings.Text = "⚙ 设置";
            _btnSettings.FaceColor = Color.FromArgb(64, 64, 64);
            _btnSettings.HoverColor = Color.FromArgb(80, 80, 80);
            _btnSettings.PressColor = Color.FromArgb(52, 52, 52);
            _btnSettings.Click += delegate { ShowSettings(); };
            Controls.Add(_btnSettings);

            // v0.9.2：自动刷新改成「常开」——刷新本身只要几毫秒，留着复选框纯占地方。
            // 频率还是让用户调（1/3/5 秒），只是不再需要先勾一下才生效。
            _cmbRate = new ComboBox();
            _cmbRate.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRate.FlatStyle = FlatStyle.Flat;
            _cmbRate.BackColor = Theme.InputBg;
            _cmbRate.ForeColor = Theme.Text;
            _cmbRate.DropDownWidth = Theme.Px(96);
            _cmbRate.Items.Add("每 1 秒");
            _cmbRate.Items.Add("每 3 秒");
            _cmbRate.Items.Add("每 5 秒");
            _cmbRate.SelectedIndex = 1;           // 默认 3 秒
            _cmbRate.DrawMode = DrawMode.OwnerDrawFixed;
            _cmbRate.DrawItem += CmbRate_DrawItem;
            _cmbRate.SelectedIndexChanged += delegate { ApplyAutoTimer(); };
            Controls.Add(_cmbRate);

            _autoTimer = new Timer();
            _autoTimer.Tick += delegate
            {
                // 弹窗 / 加载中时不叠刷新；keepSelection=true 保住用户正在盯的那一行
                if (_ready && !_busy) RefreshData(true);
            };
            ApplyAutoTimer();   // v0.9.2：创建即启动，界面上没有「开关」这一步了

            _lblHint = new Label();
            _lblHint.Text = "右键：位置 / 搜进程 / 查IP / 域名 / 防火墙 / 结束 · 空格多词 · pid:1234 · 悬停网速看曲线";
            _lblHint.ForeColor = Theme.TextDim;
            _lblHint.BackColor = Color.Transparent;
            _lblHint.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(_lblHint);

            _grid = new DataGridView();
            _grid.BackgroundColor = Theme.GridBg;
            _grid.BorderStyle = BorderStyle.None;
            _grid.GridColor = Theme.GridLine;
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.EnableHeadersVisualStyles = false;    // 关键：否则表头永远是系统亮色
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            // 注意：这里只能改「默认实例」的属性。给 DefaultCellStyle 赋一个 new DataGridViewCellStyle()
            // 之后，getter 会返回临时副本，后续设置全部失效（整表会变成白底黑字）。
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.TextWeak;
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.HeaderBg;
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.TextWeak;
            _grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

            _grid.DefaultCellStyle.BackColor = Theme.GridBg;
            _grid.DefaultCellStyle.ForeColor = Theme.Text;
            _grid.DefaultCellStyle.SelectionBackColor = Theme.GridSel;
            _grid.DefaultCellStyle.SelectionForeColor = Color.White;
            _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.GridAlt;
            _grid.AlternatingRowsDefaultCellStyle.ForeColor = Theme.Text;
            _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.GridSel;
            _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Color.White;

            _grid.RowHeadersVisible = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.AllowUserToOrderColumns = false;
            _grid.ReadOnly = true;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = true;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.ScrollBars = ScrollBars.Both;
            _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable;
            _grid.VirtualMode = true;
            _grid.CellValueNeeded += Grid_CellValueNeeded;
            _grid.CellFormatting += Grid_CellFormatting;
            _grid.CellToolTipTextNeeded += Grid_CellToolTipTextNeeded;
            _grid.ShowCellToolTips = true;
            _grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
            _grid.CellMouseDown += Grid_CellMouseDown;
            _grid.CellPainting += Grid_CellPainting;              // v0.9.3：进程名左侧 16x16 图标
            _grid.ColumnWidthChanged += Grid_ColumnWidthChanged;  // v0.9.3：列宽记忆

            _grid.Columns.Add(MakeColumn("协议", ColProto, false));
            _grid.Columns.Add(MakeColumn("本地地址:端口", ColLocal, true));
            _grid.Columns.Add(MakeColumn("远程地址:端口", ColRemote, true));
            _grid.Columns.Add(MakeColumn("状态", ColState, true));
            _grid.Columns.Add(MakeColumn("PID", ColPid, true));
            _grid.Columns.Add(MakeColumn("进程名", ColProc, true));
            // v0.9.2：进程已运行多久（参考 CurrPorts 的 Created 列）。读不到就是「-」，绝不显示 0s 骗人
            _grid.Columns.Add(MakeColumn("运行时长", ColUptime, true));
            // v0.5：进程类型（微软核心 / 第三方大厂 / 已签名 / 未知可疑 / 受保护）
            _grid.Columns.Add(MakeColumn("进程类型", ColKind, true));
            _grid.Columns[ColPid].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns[ColPid].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns[ColUptime].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            _grid.Columns[ColUptime].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;

            _gridHost = new Panel();
            _gridHost.BackColor = Theme.Border;
            _gridHost.Controls.Add(_grid);
            _grid.Dock = DockStyle.Fill;
            Controls.Add(_gridHost);

            _line = new Panel();
            _line.BackColor = Color.FromArgb(46, 46, 46);
            Controls.Add(_line);

            _lblLeft = new Label();
            _lblLeft.ForeColor = Theme.TextDim;
            _lblLeft.BackColor = Color.Transparent;
            _lblLeft.AutoSize = true;
            Controls.Add(_lblLeft);

            _lblRight = new Label();
            _lblRight.ForeColor = Theme.TextDim;
            _lblRight.BackColor = Color.Transparent;
            _lblRight.AutoSize = false;
            _lblRight.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_lblRight);

            // v0.8：极客式赞赏彩蛋 —— 状态栏最右一行极小灰字「♥ 赞赏」
            // 纯彩蛋：点开是独立小窗（内嵌赞赏码），不支持也不影响任何功能
            _lblDonate = new Label();
            _lblDonate.Text = "♥ 赞赏";
            _lblDonate.AutoSize = true;
            _lblDonate.BackColor = Color.Transparent;
            _lblDonate.ForeColor = Color.FromArgb(110, 110, 110);
            _lblDonate.Cursor = Cursors.Hand;
            _lblDonate.Click += LblDonate_Click;
            // 悬停变亮（极小的字得给点反馈，否则没人知道能点）
            _lblDonate.MouseEnter += delegate { _lblDonate.ForeColor = Color.FromArgb(230, 130, 130); };
            _lblDonate.MouseLeave += delegate { _lblDonate.ForeColor = Color.FromArgb(110, 110, 110); };
            Controls.Add(_lblDonate);

            // v0.9.1：状态栏「已运行 HH:mm:ss」——本次启动到现在的存活时长。
            // AutoSize=false：靠定时器改文字，不能让标签自己撑大触发重排（否则每秒闪一下布局）。
            _lblUptime = new Label();
            _lblUptime.AutoSize = false;
            _lblUptime.Text = FormatUptime(TimeSpan.Zero);
            _lblUptime.BackColor = Color.Transparent;
            _lblUptime.ForeColor = Theme.TextDim;
            _lblUptime.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_lblUptime);

            // v0.9.2：状态栏网速（↑ 上传 / ↓ 下载，全机所有网卡合计）。
            // AutoSize=false + 固定宽度（按最宽示例实测）：速率从 9 KB/s 跳到 10 MB/s 时格子不会左右抖。
            _lblSpeed = new Label();
            _lblSpeed.AutoSize = false;
            _lblSpeed.Text = NetSpeed.FormatPair(0, 0);
            _lblSpeed.BackColor = Color.Transparent;
            _lblSpeed.ForeColor = Theme.TextWeak;
            _lblSpeed.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(_lblSpeed);
            // v0.9.3：悬停网速格弹出实时折线小图
            _lblSpeed.MouseEnter += delegate { SpeedGraphForm.ShowOrRefresh(_lblSpeed); };
            _lblSpeed.MouseLeave += delegate { SpeedGraphForm.HideIfOpen(_lblSpeed); };

            _uptimeTimer = new Timer();
            _uptimeTimer.Interval = 1000;
            _uptimeTimer.Tick += delegate { OnSecondTick(); };
            NetSpeed.Reset();
            NetSpeed.Sample();            // 先打一次基准，第一秒过后才算是「速率」
            _uptimeTimer.Start();
            OnSecondTick();

            BuildMenu();
            _grid.ContextMenuStrip = _menu;

            UpdateStatus();
        }

        private DataGridViewTextBoxColumn MakeColumn(string header, int index, bool sortable)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.Name = "col" + index.ToString();
            c.SortMode = sortable ? DataGridViewColumnSortMode.Programmatic : DataGridViewColumnSortMode.NotSortable;
            c.Resizable = DataGridViewTriState.True;
            c.ReadOnly = true;
            return c;
        }

        private void BuildMenu()
        {
            _menu = new ContextMenuStrip();
            _menu.Renderer = new DarkMenuRenderer();
            _menu.BackColor = Theme.MenuBg;          // #1E1E1E
            _menu.ForeColor = Color.White;
            _menu.Font = Theme.Menu();               // Microsoft YaHei 10.5pt
            _menu.ShowImageMargin = false;           // 去掉左侧图标留白
            _menu.ShowCheckMargin = false;           // 连勾选留白一起去掉
            _menu.DropShadowEnabled = false;         // 无边框菜单配阴影反而显脏
            _menu.Padding = new Padding(Theme.Px(2), Theme.Px(5), Theme.Px(2), Theme.Px(5));

            _miCopy = new ToolStripMenuItem("复制所选（Ctrl+C）");
            _miCopy.Click += delegate { CopySelected(); };
            // v0.5：打开 exe 所在位置（资源管理器 /select 定位到文件）
            _miOpenPath = new ToolStripMenuItem("打开进程所在位置");
            _miOpenPath.Click += delegate { OpenProcessLocation(); };
            // v0.5：特征库没收录的进程，一键丢给浏览器查
            _miWebSearch = new ToolStripMenuItem("联网搜索此进程");
            _miWebSearch.Click += delegate { SearchProcessOnline(); };
            // v0.4：查远程 IP 归属地（不装任何本地 IP 库，直接拉系统浏览器查，体积零增长）
            _miLookup = new ToolStripMenuItem("查询远程 IP 归属地");
            _miLookup.Click += delegate { QueryRemoteIp(); };
            // v0.9.3：系统 DNS 反查远程 IP 的域名（内网主机名 / 反向 PTR 都走它，工具本体依旧不联网）
            _miDns = new ToolStripMenuItem("反查远程 IP 域名");
            _miDns.Click += delegate { ReverseDns(); };
            // v0.9.3：一键加 Windows 防火墙出站拦截规则（netsh advfirewall）
            _miFirewall = new ToolStripMenuItem("防火墙拦截此程序出站…");
            _miFirewall.Click += delegate { BlockInFirewall(); };
            _miKill = new ToolStripMenuItem("结束进程…（Delete）");
            _miKill.Click += delegate { KillSelected(); };
            // v0.9.3：紧凑行高 / 记录模式（带持久化）
            _miCompact = new ToolStripMenuItem(CompactMenuText());
            _miCompact.Click += delegate
            {
                _compact = !_compact;
                _miCompact.Text = CompactMenuText();
                SavePref("RowCompact", _compact ? "1" : "0");
                ApplyGridMetrics();
                Flash(_compact ? "已切到紧凑行高" : "已切回宽松行高");
            };
            _miLog = new ToolStripMenuItem(LogMenuText());
            _miLog.Click += delegate
            {
                _logNew = !_logNew;
                _miLog.Text = LogMenuText();
                SavePref("LogNewConn", _logNew ? "1" : "0");
                Flash(_logNew
                    ? "记录模式已开启：新出现的连接会追加到 logs\\NetEye_连接_日期.csv"
                    : "记录模式已关闭");
            };
            ToolStripMenuItem miRefresh = new ToolStripMenuItem("刷新（F5）");
            miRefresh.Click += delegate { RefreshData(); };

            _menu.Items.Add(_miCopy);
            _menu.Items.Add(_miOpenPath);
            _menu.Items.Add(_miWebSearch);
            _menu.Items.Add(_miLookup);
            _menu.Items.Add(_miDns);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miFirewall);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miKill);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_miCompact);
            _menu.Items.Add(_miLog);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(miRefresh);
            // 行高靠 Padding 撑开（10pt 中文在默认 22px 行高里太挤）
            foreach (ToolStripItem it in _menu.Items) StyleMenuItem(it);
            _menu.Opening += delegate
            {
                bool has = _grid.SelectedRows.Count > 0;
                _miCopy.Enabled = has;
                _miOpenPath.Enabled = has;
                _miWebSearch.Enabled = has;
                _miLookup.Enabled = has;
                _miDns.Enabled = has;
                _miFirewall.Enabled = has;
                _miKill.Enabled = has;
            };
        }

        /// <summary>v0.9.3：把界面偏好写进 config.ini（失败静默：只读目录下不打扰用户）</summary>
        private void SavePref(string key, string value)
        {
            string err;
            try { _cfg.SetKey(key, value, out err); } catch (Exception) { }
        }

        /// <summary>DPI 切换后重建菜单字体与行高（菜单不参与控件布局，必须手动刷）</summary>
        private void ApplyMenuMetrics()
        {
            if (_menu == null) return;
            _menu.Font = Theme.Menu();
            _menu.Padding = new Padding(Theme.Px(2), Theme.Px(5), Theme.Px(2), Theme.Px(5));
            foreach (ToolStripItem it in _menu.Items) StyleMenuItem(it);
        }

        /// <summary>统一菜单项的行高与字体（分隔线只加竖向留白）</summary>
        private static void StyleMenuItem(ToolStripItem it)
        {
            it.Font = Theme.Menu();
            it.ForeColor = Color.White;
            ToolStripSeparator sep = it as ToolStripSeparator;
            if (sep != null)
            {
                sep.Margin = new Padding(0, Theme.Px(5), 0, Theme.Px(5));
                return;
            }
            // 上下 8px 逻辑留白 → 中文 10.5pt 也有舒展的行高
            it.Padding = new Padding(Theme.Px(14), Theme.Px(8), Theme.Px(30), Theme.Px(8));
            it.Margin = Padding.Empty;
        }

        // ================= DPI 与布局 =================
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        // Shcore 只有 Win8.1+ 才有：老系统上 LoadLibrary 失败时，P/Invoke 会抛
        // EntryPointNotFoundException / DllNotFoundException —— 外面全部 try 住
        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        /// <summary>
        /// v0.9.2：取窗口真实 DPI，三级兜底。
        ///
        /// 以前只有「GetDpiForWindow → CreateGraphics().DpiX」两级，第二级在
        /// 窗口还没真正显示、或者跨屏切换的瞬间经常拿到 96（= 系统 DPI），
        /// 于是 Win10 上就出现「缩放失效：125% 屏上界面还是 100% 大小」。
        /// 现在补上 Win8.1 的 GetDpiForMonitor（按窗口最近的显示器取），
        /// 再退到屏幕 DC 的 LOGPIXELSX，最后才是 96。
        /// </summary>
        private int DpiOfWindow(IntPtr h)
        {
            // 1) Win10 1607+：直接给出这个窗口所在显示器的 DPI（PerMonitorV2 下的权威值）
            try
            {
                uint d = GetDpiForWindow(h);
                if (d >= 72 && d <= 960) return (int)d;
            }
            catch (Exception) { }
            // 2) Win8.1+：按「离窗口最近的显示器」取有效 DPI
            try
            {
                IntPtr mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
                if (mon != IntPtr.Zero)
                {
                    uint dx, dy;
                    if (GetDpiForMonitor(mon, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx >= 72 && dx <= 960)
                        return (int)dx;
                }
            }
            catch (Exception) { }
            // 3) 老系统 / 上两级都失灵：用屏幕 DC 的 LOGPIXELSX（DPI 感知进程拿到的是真实值）
            try
            {
                using (Graphics g = Graphics.FromHwnd(h == IntPtr.Zero ? IntPtr.Zero : h))
                {
                    int d = (int)Math.Round(g.DpiX);
                    if (d >= 72 && d <= 960) return d;
                }
            }
            catch (Exception) { }
            return 96;
        }

        /// <summary>v0.9.2：把窗口客户区收进显示器工作区（纯函数，便于测试）</summary>
        internal static Size FitToWorkArea(Size want, Size work, int margin)
        {
            if (margin < 0) margin = 0;
            int maxW = Math.Max(320, work.Width - margin * 2);
            int maxH = Math.Max(240, work.Height - margin * 2);
            return new Size(Math.Min(want.Width, maxW), Math.Min(want.Height, maxH));
        }

        /// <summary>
        /// v0.9.2：把窗口左上角收进工作区（纯函数，便于测试）。
        /// 高 DPI（125%/150%/200%）下 WinForms 的 CenterScreen 会把窗口摆到工作区外面去
        /// —— 实测 200% 时窗口左边缘被算成 983px（正确值是 443px），右边 100px 直接出屏。
        /// </summary>
        internal static Point FitToWorkArea(Point loc, Size size, Rectangle work)
        {
            int x = size.Width >= work.Width ? work.Left
                : Math.Min(Math.Max(loc.X, work.Left), work.Right - size.Width);
            int y = size.Height >= work.Height ? work.Top
                : Math.Min(Math.Max(loc.Y, work.Top), work.Bottom - size.Height);
            return new Point(x, y);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
            ApplyDpi(DpiOfWindow(Handle));
        }

        /// <summary>
        /// v0.9.2：窗口真正显示后再校准一次 DPI + 按屏幕工作区收一次尺寸。
        ///
        /// 为什么非要等 OnShown：StartPosition=CenterScreen 是在显示阶段才定位的，
        /// 句柄创建那一刻窗口还蹲在默认位置 —— 多屏机器上那里可能是另一台显示器，
        /// 拿到的 DPI 就是错的（Win10 上表现就是「界面没跟着系统缩放」）。
        /// </summary>
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_fitted) return;
            _fitted = true;
            ApplyDpi(DpiOfWindow(Handle));
            FitToScreenOnce();
        }

        /// <summary>
        /// v0.9.2：首次显示时把窗口收进当前显示器工作区（只缩不放）。
        /// 150% 缩放 + 1366x768 这类组合下，按 DPI 算出来的 1080x680 逻辑像素会变成 1620x1020 物理像素，
        /// 比工作区还大 —— 用户看到的就是「窗口跑到屏幕外面、底部状态栏永远看不见、又拖不回来」。
        /// </summary>
        private void FitToScreenOnce()
        {
            Rectangle wa;
            try { wa = Screen.FromHandle(Handle).WorkingArea; }
            catch (Exception) { return; }
            if (wa.Width <= 0 || wa.Height <= 0) return;

            int margin = Theme.Px(24);
            _screenCap = new Size(Math.Max(Theme.Px(360), wa.Width - margin * 2),
                Math.Max(Theme.Px(240), wa.Height - margin * 2));

            Size want = ClientSize;
            Size fit = FitToWorkArea(want, wa.Size, margin);
            if (fit.Width != want.Width || fit.Height != want.Height) ClientSize = fit;

            // 尺寸收好了再收位置：高 DPI 下 CenterScreen 会把窗口摆歪（实测 200% 时偏右 540px），
            // 这里一律按工作区夹一次，保证四个边都在屏幕里。
            Point at = FitToWorkArea(Location, Size, wa);
            if (at.X != Location.X || at.Y != Location.Y) Location = at;

            ApplyLayout(true);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DPICHANGED)
            {
                // PerMonitorV2：跨屏后系统会告知新 DPI + 建议矩形，必须自己应用，否则窗口不跟着变
                int dpi = (int)((long)m.WParam & 0xFFFF);
                if (dpi < 72 || dpi > 960) dpi = DpiOfWindow(Handle);   // 防御：wParam 脏了也不能把界面搞成 0
                ApplyDpi(dpi);
                int left = Marshal.ReadInt32(m.LParam, 0);
                int top = Marshal.ReadInt32(m.LParam, 4);
                int w = Marshal.ReadInt32(m.LParam, 8) - left;
                int h = Marshal.ReadInt32(m.LParam, 12) - top;
                if (w > 0 && h > 0) Bounds = new Rectangle(left, top, w, h);
            }
        }

        private void ApplyDpi(int dpi)
        {
            bool changed = dpi != Theme.Dpi;
            if (changed)
            {
                float oldScale = Theme.Scale;
                Theme.SetDpi(dpi);
                float ratio = Theme.Scale / oldScale;
                if (!_laidOut) ClientSize = new Size(Theme.Px(DesignWidth), Theme.Px(DesignHeight));
                else if (ratio > 0.1f)
                    ClientSize = new Size((int)Math.Round(ClientSize.Width * ratio),
                        (int)Math.Round(ClientSize.Height * ratio));
            }
            ApplyLayout(true);
            _laidOut = true;
        }

        private void ApplyLayout()
        {
            ApplyLayout(true);
        }

        private void ApplyLayout(bool full)
        {
            SuspendLayout();
            int pad = Theme.Px(16);
            int ctlH = Theme.Px(32);
            int top = Theme.Px(14);

            Font ui = Theme.Ui(FontStyle.Regular);
            Font uiSmall = Theme.Ui(8.5f, FontStyle.Regular);

            // v0.8：最小窗口放大到 800x500（逻辑像素）——再小的话顶部工具行会挤、状态栏会折字
            // v0.9.2：但必须再被「当前显示器工作区」卡一道：Win10 上 125%/150% 缩放时，
            // 800x500 逻辑像素会变成 1200x750 物理像素，比 1366x768 笔记本的可用区还高，
            // 于是窗口拖不小、状态栏永远在屏幕外——这就是老哥说的「窗口自适应失效」。
            Size minWant = new Size(Theme.Px(800), Theme.Px(500));
            if (!_screenCap.IsEmpty)
            {
                minWant.Width = Math.Min(minWant.Width, _screenCap.Width);
                minWant.Height = Math.Min(minWant.Height, _screenCap.Height);
            }
            MinimumSize = new Size(Math.Max(Theme.Px(360), minWant.Width), Math.Max(Theme.Px(240), minWant.Height));
            Font = ui;

            // v0.8：搜索框改成「Left+Right 拉伸」：窗口变宽它跟着变宽（到 620 逻辑像素封顶），
            // 下限 240 保证窄窗口也放得下提示文字。其余按钮保持左对齐定宽。
            int searchW = Math.Max(Theme.Px(240),
                Math.Min(Theme.Px(620), ClientSize.Width / 2));
            _search.Font = ui;
            _search.Bounds = new Rectangle(pad, top, searchW, ctlH);

            // v0.9.3：匹配模式下拉插在搜索框和刷新按钮之间（窄窗口时搜索框下限保证不重叠）
            _cmbMatch.Font = uiSmall;
            _cmbMatch.ItemHeight = Theme.Px(16);
            _cmbMatch.Bounds = new Rectangle(_search.Right + Theme.Px(8), top + (ctlH - Theme.Px(26)) / 2,
                Theme.Px(62), Theme.Px(26));

            _btnRefresh.Font = Theme.Ui(9f, FontStyle.Bold);
            _btnRefresh.Bounds = new Rectangle(_cmbMatch.Right + Theme.Px(10), top, Theme.Px(92), ctlH);

            // v0.6 顶部工具行：导出 / 频率 / 设置（v0.9.2：去掉了「自动刷新」复选框，省一格）
            Font ui9 = Theme.Ui(9f, FontStyle.Bold);
            _btnExport.Font = ui9;
            _btnExport.Bounds = new Rectangle(_btnRefresh.Right + Theme.Px(8), top, Theme.Px(64), ctlH);
            _cmbRate.Font = uiSmall;
            _cmbRate.ItemHeight = Theme.Px(16);
            _cmbRate.Bounds = new Rectangle(_btnExport.Right + Theme.Px(8), top + (ctlH - Theme.Px(26)) / 2,
                Theme.Px(84), Theme.Px(26));
            _btnSettings.Font = ui9;
            _btnSettings.Bounds = new Rectangle(_cmbRate.Right + Theme.Px(8), top, Theme.Px(78), ctlH);
            _lblHint.Font = uiSmall;
            _lblHint.Bounds = new Rectangle(_btnSettings.Right + Theme.Px(12), top,
                Math.Max(Theme.Px(10), ClientSize.Width - _btnSettings.Right - pad - Theme.Px(12)), ctlH);

            _statusTop = ClientSize.Height - Theme.Px(30);
            _line.Bounds = new Rectangle(0, _statusTop - Theme.Px(7), ClientSize.Width, 1);

            int gridTop = top + ctlH + Theme.Px(12);
            int gridH = _statusTop - Theme.Px(7) - Theme.Px(8) - gridTop;
            _gridHost.Bounds = new Rectangle(pad, gridTop, Math.Max(Theme.Px(120), ClientSize.Width - pad - pad),
                Math.Max(Theme.Px(80), gridH));
            _gridHost.Padding = new Padding(1);

            // 表格字体/行高/列宽只在 DPI 变化时重算，普通拉伸窗口不重置用户拖过的列宽
            if (full) ApplyGridMetrics();

            _lblLeft.Font = uiSmall;
            _lblLeft.Location = new Point(pad, _statusTop + Theme.Px(3));

            // v0.8：状态栏右边先放「♥ 赞赏」，右格文字再紧贴它左边排（两者都锚定右侧）
            _lblDonate.Font = Theme.Ui(8.25f, FontStyle.Regular);
            int donateW = _lblDonate.PreferredWidth + Theme.Px(2);
            int donateRight = ClientSize.Width - pad;
            _lblDonate.Location = new Point(Math.Max(pad, donateRight - donateW), _statusTop + Theme.Px(4));

            // v0.9.1：状态栏右边依次是「♥ 赞赏」→「已运行 HH:mm:ss」→ 右格状态文字，
            // 全部锚定右侧向左排。宽度用同字体实测（不写死），换了字体/DPI 也不会被省略号吃掉。
            _lblUptime.Font = uiSmall;
            int uptimeW = TextRenderer.MeasureText("已运行 00:00:00", uiSmall).Width + Theme.Px(8);
            int uptimeRight = _lblDonate.Left - Theme.Px(10);
            _lblUptime.Bounds = new Rectangle(Math.Max(pad, uptimeRight - uptimeW),
                _statusTop + Theme.Px(3), uptimeW, Theme.Px(20));

            // v0.9.2：网速格紧贴「已运行」左边，宽度按最宽示例实测（不写死，换字体/DPI 也不会被吃掉）
            _lblSpeed.Font = uiSmall;
            int speedW = TextRenderer.MeasureText(NetSpeed.WidthProbe, uiSmall).Width + Theme.Px(8);
            int speedRight = _lblUptime.Left - Theme.Px(12);
            _lblSpeed.Bounds = new Rectangle(Math.Max(pad, speedRight - speedW),
                _statusTop + Theme.Px(3), speedW, Theme.Px(20));

            _lblRight.Font = uiSmall;
            int rightEdge = _lblSpeed.Left - Theme.Px(12);
            int rightLeft = Math.Max(_lblLeft.Right + Theme.Px(12), rightEdge - Theme.Px(400));
            _lblRight.Bounds = new Rectangle(rightLeft, _statusTop + Theme.Px(3),
                Math.Max(Theme.Px(80), rightEdge - rightLeft), Theme.Px(20));

            ResumeLayout(true);
        }

        private void ApplyGridMetrics()
        {
            ApplyMenuMetrics();
            _styles.Clear();   // 字体/内边距都换了，旧样式副本必须作废
            _grid.DefaultCellStyle.Font = Theme.Mono(9.5f, FontStyle.Regular);
            _grid.AlternatingRowsDefaultCellStyle.Font = Theme.Mono(9.5f, FontStyle.Regular);
            _grid.ColumnHeadersDefaultCellStyle.Font = Theme.Ui(9f, FontStyle.Bold);
            // v0.9.3：行高两档 —— 宽松 22（默认）/ 紧凑 18（高分屏一眼看更多行）
            int rowH = _compact ? 18 : 22;
            _grid.RowTemplate.Height = Theme.Px(rowH);
            _grid.ColumnHeadersHeight = Theme.Px(30);
            _grid.DefaultCellStyle.Padding = new Padding(Theme.Px(6), 0, Theme.Px(4), 0);
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.Px(6), 0, Theme.Px(4), 0);

            // v0.5：多了一列「进程类型」，本地/远程各收窄 26px，仍给进程名留 Fill 弹性宽度
            // v0.9.2：再加「运行时长」，宽度刚好放得下「888d 88h」（最长的形式），进程名继续 Fill
            int[] widths = new int[] { 74, 210, 210, 124, 78 };
            // v0.9.3：程序内改列宽不回写 config（防回环），改完再恢复用户记忆的宽度
            _applyingColumnWidths = true;
            try
            {
                for (int i = 0; i < widths.Length; i++) _grid.Columns[i].Width = Theme.Px(widths[i]);
                _grid.Columns[ColUptime].Width = Theme.Px(96);
                _grid.Columns[ColUptime].MinimumWidth = Theme.Px(76);
                _grid.Columns[ColKind].Width = Theme.Px(96);
                _grid.Columns[ColKind].MinimumWidth = Theme.Px(80);
                _grid.Columns[ColProc].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                _grid.Columns[ColProc].MinimumWidth = Theme.Px(150);

                // v0.9.3：恢复用户拖过的列宽（config.ini 存逻辑像素，换 DPI 也等比还原）
                ApplyPersistedColumnWidths();
            }
            finally { _applyingColumnWidths = false; }

            foreach (DataGridViewRow row in _grid.Rows) row.Height = Theme.Px(rowH);
        }

        /// <summary>v0.9.3：把 config.ini 记忆的列宽应用到非 Fill 列（无效/越界键直接忽略）</summary>
        private void ApplyPersistedColumnWidths()
        {
            for (int i = 0; i < _grid.Columns.Count; i++)
            {
                if (i == ColProc) continue;   // Fill 模式，宽度由窗口决定
                int lw = _cfg.GetInt("ColW" + i.ToString(), 0, 24, 1200);
                if (lw > 0) _grid.Columns[i].Width = Theme.Px(lw);
            }
        }

        /// <summary>v0.9.3：用户拖完列宽 → 逻辑像素写回 config.ini（程序内改宽度不触发）</summary>
        private void Grid_ColumnWidthChanged(object sender, DataGridViewColumnEventArgs e)
        {
            if (_applyingColumnWidths) return;
            int idx = e.Column.Index;
            if (idx < 0 || idx >= _grid.Columns.Count || idx == ColProc) return;
            int lw = (int)Math.Round(e.Column.Width / Theme.Scale);
            SavePref("ColW" + idx.ToString(), lw.ToString());
        }

        // ================= 数据 =================
        private void RefreshData() { RefreshData(false); }

        /// <summary>
        /// v0.6：keepSelection=true（自动刷新用）时，按「PID+协议+两端点+进程名」把用户正盯着的
        /// 那一行重新选回来；false（手动 F5）保持 v0.2 以来的行为——刷新后不自动选中。
        /// 滚动位置的保留在 BuildView 里本来就有。
        /// </summary>
        private void RefreshData(bool keepSelection)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _pendingSelKey = keepSelection ? SelectionKey() : null;
                Stopwatch sw = Stopwatch.StartNew();
                List<NetRow> rows = NetTable.QueryAll();
                sw.Stop();
                _lastMs = sw.Elapsed.TotalMilliseconds;
                _all = rows;
                _hay = new string[rows.Count];
                for (int i = 0; i < rows.Count; i++) _hay[i] = Haystack(rows[i]);
                // v0.9.2：补上「运行时长」用的进程启动时间（按 PID 缓存 15 秒，异常进程留 null 显示「-」）
                ProcAge.Annotate(rows);
                BuildView();
                // 类型列先用特征库直判，剩下的进程丢后台读签名（不阻塞刷新）
                ProcBadge.RequestScan(rows);
                // v0.9.3：进程图标（后台提取、按 exe 缓存，读到就刷进程名列）
                ProcIcons.Request(rows);
                // v0.9.3：记录模式开着就把本次新出现的连接追加进当天日志
                if (_logNew) AppendNewConnections(rows);
            }
            catch (Exception ex)
            {
                Theme.Alert(IsHandleCreated ? (IWin32Window)this : null, "刷新失败",
                    "读取系统连接表时出错：\n" + ex.Message);
            }
            finally { _busy = false; }
        }

        /// <summary>当前选中行的身份键：连接两端在毫秒级刷新里足够稳定，PID 复用也不会串</summary>
        internal static string KeyOf(NetRow r)
        {
            if (r == null) return null;
            return r.Pid.ToString() + "|" + r.Protocol + "|" + r.LocalEndPoint
                + "|" + r.RemoteEndPoint + "|" + r.ProcessDisplay;
        }

        private string SelectionKey()
        {
            NetRow r = CurrentRow();
            return r == null ? null : KeyOf(r);
        }

        /// <summary>自动刷新后把键匹配的那一行重新选上并滚进可视区；找不到就按老规矩清空</summary>
        private void RestoreSelection(string key)
        {
            if (key == null || _view.Count == 0)
            {
                ClearSelectionQuiet();
                return;
            }
            int hit = -1;
            for (int i = 0; i < _view.Count; i++)
            {
                if (key == KeyOf(_view[i])) { hit = i; break; }
            }
            if (hit < 0) { ClearSelectionQuiet(); return; }
            try
            {
                _grid.ClearSelection();
                _grid.Rows[hit].Selected = true;
                _grid.CurrentCell = _grid.Rows[hit].Cells[ColProto];
                int first = _grid.FirstDisplayedScrollingRowIndex;
                int last = first < 0 ? -1 : first + Math.Max(1, _grid.DisplayedRowCount(false)) - 1;
                if (first < 0 || hit < first || hit > last)
                    _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, hit - 2);
            }
            catch (Exception) { }
        }

        private static string Haystack(NetRow r)
        {
            // 故意不含 PID：否则搜端口 8080 会顺带命中 PID 8080 的行，看着像乱匹配。
            // 需要按 PID 找时用 pid:1234 语法。
            return (r.Protocol + " " + r.LocalAddress + " " + r.LocalPort.ToString() + " "
                + r.LocalEndPoint + " " + r.RemoteAddress + " " + r.RemotePort.ToString() + " "
                + r.RemoteEndPoint + " " + r.StateText + " "
                + r.ProcessDisplay).ToLowerInvariant();
        }

        private void BuildView()
        {
            string raw = _search.Text.Trim();
            string[] terms = raw.Length == 0 ? new string[0]
                : raw.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < terms.Length; i++) terms[i] = terms[i].ToLowerInvariant();

            // v0.9.3：正则模式 —— 整个输入作为一条正则（忽略大小写），对整行摘要匹配
            System.Text.RegularExpressions.Regex re = null;
            if (_matchMode == 2 && raw.Length > 0)
            {
                try { re = new System.Text.RegularExpressions.Regex(raw, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                catch (ArgumentException)
                {
                    Flash("正则表达式无效，先显示全部行（检查后重试）");
                }
            }

            List<NetRow> list = new List<NetRow>(_all.Count);
            for (int i = 0; i < _all.Count; i++)
            {
                if (_matchMode == 2 && re != null)
                {
                    if (!re.IsMatch(_hay[i])) continue;
                }
                else if (terms.Length > 0)
                {
                    string hay = _hay[i];
                    bool hit = true;
                    for (int t = 0; t < terms.Length; t++)
                    {
                        string term = terms[t];
                        if (term.StartsWith("pid:", StringComparison.Ordinal))
                        {
                            string want = term.Substring(4);
                            int wantPid;
                            bool match = int.TryParse(want, out wantPid)
                                ? _all[i].Pid == wantPid                    // pid:1234 精确等
                                : _all[i].Pid.ToString().Contains(want);     // 只写了 pid:12 按前缀筛
                            if (!match) { hit = false; break; }
                        }
                        else if (_matchMode == 1)
                        {
                            // v0.9.3 精确模式：词必须完整等于某个字段（不再子串命中），
                            // 字段 = 协议 / 两端点 / 状态 / PID / 进程名
                            NetRow r = _all[i];
                            if (!TermEqualsField(r, term)) { hit = false; break; }
                        }
                        else if (hay.IndexOf(term, StringComparison.Ordinal) < 0) { hit = false; break; }
                    }
                    if (!hit) continue;
                }
                list.Add(_all[i]);
            }
            list.Sort(CompareRows);

            // 刷新/过滤后保持滚动位置（用户最烦点了刷新就跳回顶部）
            int topRow = -1;
            try { if (_grid.FirstDisplayedScrollingRowIndex >= 0) topRow = _grid.FirstDisplayedScrollingRowIndex; }
            catch (Exception) { }

            _view = list;
            _grid.RowCount = 0;
            _grid.RowCount = _view.Count;

            for (int i = 0; i < _grid.Columns.Count; i++)
            {
                _grid.Columns[i].HeaderCell.SortGlyphDirection = i == _sortCol
                    ? (_sortAsc ? SortOrder.Ascending : SortOrder.Descending) : SortOrder.None;
            }

            // 关键：给 RowCount 赋值会让 DataGridView 自动把 CurrentCell 设到 (0,0)，
            // 表现为「刷新后第一行变绿」。绑定完必须强制清掉选中与当前单元格。
            // v0.6：自动刷新带着「要恢复的选中行键」进来就恢复选择，否则清空（手动刷新老行为）
            string selKey = _pendingSelKey;
            _pendingSelKey = null;
            if (selKey != null) RestoreSelection(selKey); else ClearSelectionQuiet();

            if (topRow >= 0 && _view.Count > 0)
            {
                try
                {
                    _grid.FirstDisplayedScrollingRowIndex = Math.Min(topRow, _view.Count - 1);
                }
                catch (Exception) { }
            }
            UpdateStatus();
        }

        /// <summary>v0.9.3 精确匹配：一个词完整等于任一字段才算命中（纯函数，便于测试）</summary>
        internal static bool TermEqualsField(NetRow r, string term)
        {
            if (r == null || term == null) return false;
            return string.Equals(r.Protocol ?? "", term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.LocalEndPoint ?? "", term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.RemoteEndPoint ?? "", term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.StateText ?? "", term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.Pid.ToString(), term, StringComparison.OrdinalIgnoreCase)
                || string.Equals(r.ProcessDisplay ?? "", term, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>清空选中与当前单元格（虚拟模式下 CurrentCell=null 偶发抛错，吞掉即可）</summary>
        private void ClearSelectionQuiet()
        {
            try { _grid.ClearSelection(); } catch (Exception) { }
            try { _grid.CurrentCell = null; } catch (Exception) { }
        }

        private int CompareRows(NetRow a, NetRow b)
        {
            int c;
            switch (_sortCol)
            {
                case ColProto:
                    c = string.Compare(a.Protocol, b.Protocol, StringComparison.CurrentCultureIgnoreCase);
                    break;
                case ColLocal:
                    c = a.LocalPort.CompareTo(b.LocalPort);
                    if (c == 0) c = string.Compare(a.LocalAddress, b.LocalAddress, StringComparison.Ordinal);
                    break;
                case ColRemote:
                    c = a.RemotePort.CompareTo(b.RemotePort);
                    if (c == 0) c = string.Compare(a.RemoteAddress, b.RemoteAddress, StringComparison.Ordinal);
                    break;
                case ColState:
                    c = string.Compare(a.StateText, b.StateText, StringComparison.CurrentCultureIgnoreCase);
                    break;
                case ColPid:
                    c = a.Pid.CompareTo(b.Pid);
                    break;
                case ColUptime:
                {
                    // v0.9.2：按「跑得久不久」排。升序＝刚起来的排前面（年龄小的在前），
                    // 读不到启动时间的那些永远垫底，不随升降序翻到顶部（否则一片「-」挡住视线）。
                    bool an = !a.StartedLocal.HasValue, bn = !b.StartedLocal.HasValue;
                    if (an != bn) return an ? 1 : -1;
                    c = an ? 0 : b.StartedLocal.Value.CompareTo(a.StartedLocal.Value);
                    break;
                }
                case ColKind:
                    // 按可信度排（微软核心 → 大厂 → 已签名 → 未知可疑 → 受保护 → 待识别），同档再按名字
                    c = ((int)ProcBadge.KindOf(a)).CompareTo((int)ProcBadge.KindOf(b));
                    if (c == 0)
                        c = string.Compare(a.ProcessDisplay, b.ProcessDisplay, StringComparison.CurrentCultureIgnoreCase);
                    break;
                default:
                    // 中文按拼音：CurrentCultureIgnoreCase 在中文 Windows 上就是拼音序
                    c = string.Compare(a.ProcessDisplay, b.ProcessDisplay, StringComparison.CurrentCultureIgnoreCase);
                    break;
            }
            if (!_sortAsc) c = -c;
            if (c == 0) c = a.LocalPort.CompareTo(b.LocalPort);
            if (c == 0) c = a.Pid.CompareTo(b.Pid);
            if (c == 0) c = string.CompareOrdinal(a.LocalEndPoint, b.LocalEndPoint);
            return c;
        }

        // ================= 表格绘制与交互 =================
        private void Grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            NetRow r = _view[e.RowIndex];
            switch (e.ColumnIndex)
            {
                case ColProto: e.Value = r.Protocol; break;
                case ColLocal: e.Value = r.LocalEndPoint; break;
                case ColRemote: e.Value = r.RemoteEndPoint; break;
                case ColState: e.Value = r.StateText; break;
                case ColPid: e.Value = r.Pid; break;
                case ColProc: e.Value = r.ProcessDisplay; break;
                case ColUptime: e.Value = ProcAge.TextOf(r); break;
                case ColKind: e.Value = ProcBadge.Label(r); break;
            }
        }

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            NetRow r = _view[e.RowIndex];
            Color fore;
            DataGridViewContentAlignment align = DataGridViewContentAlignment.MiddleLeft;
            if (e.ColumnIndex == ColState) fore = StateColor(r);
            else if (e.ColumnIndex == ColProto) fore = r.IsUdp ? Theme.TextWeak : Theme.Text;
            else if (e.ColumnIndex == ColRemote) fore = r.IsUdp ? Theme.TextDim : Theme.Text;
            else if (e.ColumnIndex == ColPid)
            {
                fore = r.Pid <= 4 ? Theme.TextDim : Theme.Text;
                align = DataGridViewContentAlignment.MiddleRight;
            }
            else if (e.ColumnIndex == ColKind) fore = KindColor(ProcBadge.KindOf(r));
            else if (e.ColumnIndex == ColUptime)
            {
                // 拿不到启动时间的行故意压暗：一眼看出这是「读不到」，不是「刚启动」
                fore = r.StartedLocal.HasValue ? Theme.TextWeak : Theme.TextDim;
                align = DataGridViewContentAlignment.MiddleRight;
            }
            else if (e.ColumnIndex != ColProc) return;
            else fore = Theme.Text;   // 进程名本体保持正常前景，图标另画

            // 虚拟模式下单元格共用继承样式，直接改 e.CellStyle 会把颜色串到整列，
            // 所以按「颜色 + 奇偶行 + 对齐」缓存独立的样式副本。
            // v0.9.3：进程名列的样式左内边距加大（给 16x16 图标腾地方），缓存键要带上这把锁。
            bool iconPad = e.ColumnIndex == ColProc;
            e.CellStyle = GetStyle(fore, (e.RowIndex & 1) == 1, align, iconPad);
        }

        private DataGridViewCellStyle GetStyle(Color fore, bool alt, DataGridViewContentAlignment align)
        {
            return GetStyle(fore, alt, align, false);
        }

        private DataGridViewCellStyle GetStyle(Color fore, bool alt, DataGridViewContentAlignment align, bool iconPad)
        {
            string key = fore.ToArgb().ToString() + "|" + (alt ? "1" : "0") + "|" + ((int)align).ToString()
                + (iconPad ? "|icon" : "");
            DataGridViewCellStyle s;
            if (_styles.TryGetValue(key, out s)) return s;
            s = new DataGridViewCellStyle();
            s.BackColor = alt ? Theme.GridAlt : Theme.GridBg;
            s.ForeColor = fore;
            s.SelectionBackColor = Theme.GridSel;
            s.SelectionForeColor = Color.White;
            s.Font = Theme.Mono(9.5f, FontStyle.Regular);
            s.Alignment = align;
            // v0.9.3：进程名列左内边距 = 正常 6px + 图标 16px + 间隙 4px，文字自然从图标右侧起笔
            s.Padding = iconPad
                ? new Padding(Theme.Px(6) + Theme.Px(16) + Theme.Px(4), 0, Theme.Px(4), 0)
                : _grid.DefaultCellStyle.Padding;
            s.WrapMode = DataGridViewTriState.False;
            _styles[key] = s;
            return s;
        }

        /// <summary>进程类型配色：微软蓝 / 大厂绿 / 已签名灰 / 未签名琥珀 / 受保护暗灰</summary>
        private static Color KindColor(ProcKind k)
        {
            switch (k)
            {
                case ProcKind.Microsoft: return Color.FromArgb(111, 168, 255);
                case ProcKind.Vendor: return Color.FromArgb(126, 217, 138);
                case ProcKind.Signed: return Color.FromArgb(154, 154, 154);
                case ProcKind.Unsigned: return Theme.Warn;
                case ProcKind.Protected: return Color.FromArgb(105, 105, 105);
                default: return Color.FromArgb(96, 96, 96);
            }
        }

        private static Color StateColor(NetRow r)
        {
            if (r.IsUdp) return Theme.TextDim;
            switch (r.State)
            {
                case TcpState.Listen: return Color.FromArgb(126, 217, 138);
                case TcpState.Established: return Color.FromArgb(230, 230, 230);
                case TcpState.SynSent:
                case TcpState.SynReceived: return Theme.Warn;
                default: return Theme.TextDim;
            }
        }

        private void Grid_CellToolTipTextNeeded(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            // 只给「进程名」和「进程类型」两列挂说明，其它列悬停不该弹东西
            if (e.ColumnIndex != ColProc && e.ColumnIndex != ColKind) return;
            e.ToolTipText = ProcBadge.Tooltip(_view[e.RowIndex]);
        }

        /// <summary>
        /// v0.9.3：进程名列左侧画 16x16 进程图标（按 exe 路径缓存，后台线程提取）。
        /// 画法：自己铺背景/边框/选中态，图标画进左内边距腾出的空档，
        /// 文字由内容绘制器按加大的 Padding 自然起笔 —— 不做剪裁平移，
        /// 排序/复制/导出零感知。实测坑：e.Paint(rect, ContentForeground) 的 rect
        /// 只当裁剪区用，文字坐标仍按原 CellBounds 算，直接平移会把进程名压住。
        /// </summary>
        private void Grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            if (e.ColumnIndex != ColProc) return;

            e.Paint(e.ClipBounds, DataGridViewPaintParts.Background
                | DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);

            NetRow r = _view[e.RowIndex];
            Icon ic = ProcIcons.IconFor(r.Pid);
            int sz = Theme.Px(16);
            int ix = e.CellBounds.Left + Theme.Px(6);
            int iy = e.CellBounds.Top + (e.CellBounds.Height - sz) / 2;
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawIcon(ic, new Rectangle(ix, iy, sz, sz));

            e.Paint(e.CellBounds, DataGridViewPaintParts.ContentForeground);
            e.Handled = true;
        }

        /// <summary>v0.9.3：图标列收到后台提取完成的通知 → 只重绘进程名列（UI 线程）</summary>
        private void OnIconsUpdated()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    try { _grid.InvalidateColumn(ColProc); } catch (Exception) { }
                });
            }
            catch (Exception) { }
        }

        private void Grid_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _grid.Columns.Count) return;
            if (_grid.Columns[e.ColumnIndex].SortMode == DataGridViewColumnSortMode.NotSortable) return;
            if (_sortCol == e.ColumnIndex) _sortAsc = !_sortAsc;
            else { _sortCol = e.ColumnIndex; _sortAsc = true; }
            BuildView();
            _grid.Focus();
        }

        private void Grid_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && e.RowIndex < _grid.RowCount)
            {
                if (!_grid.Rows[e.RowIndex].Selected)
                {
                    _grid.ClearSelection();
                    _grid.Rows[e.RowIndex].Selected = true;
                }
                _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inSearch = _search.Inner.Focused;
            if (keyData == Keys.F5) { RefreshData(); return true; }
            if (keyData == (Keys.Control | Keys.F))
            {
                _search.Focus();
                _search.Inner.SelectAll();
                return true;
            }
            if (!inSearch)
            {
                if (keyData == (Keys.Control | Keys.C)) { CopySelected(); return true; }
                if (keyData == Keys.Delete) { KillSelected(); return true; }
            }
            if (keyData == Keys.Escape && inSearch)
            {
                _search.Text = "";
                _grid.Focus();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private NetRow CurrentRow()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            int i = _grid.SelectedRows[0].Index;
            if (i < 0 || i >= _view.Count) return null;
            return _view[i];
        }

        // ================= 右键功能 =================
        /// <summary>按行号拼 Tab 分隔文本（可直接粘进 Excel）</summary>
        internal string BuildCopyText()
        {
            List<int> idx = new List<int>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (row.Index >= 0 && row.Index < _view.Count) idx.Add(row.Index);
            idx.Sort();
            return BuildCopyText(idx);
        }

        private string BuildCopyText(List<int> idx)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int i in idx)
            {
                if (i < 0 || i >= _view.Count) continue;
                NetRow r = _view[i];
                sb.Append(r.Protocol).Append('\t').Append(r.LocalEndPoint).Append('\t')
                  .Append(r.RemoteEndPoint).Append('\t').Append(r.StateText).Append('\t')
                  .Append(r.Pid.ToString()).Append('\t').Append(r.ProcessDisplay).Append('\t')
                  .Append(ProcAge.TextOf(r)).Append("\r\n");
            }
            return sb.ToString();
        }

        private void CopySelected()
        {
            List<int> idx = new List<int>();
            foreach (DataGridViewRow row in _grid.SelectedRows)
                if (row.Index >= 0 && row.Index < _view.Count) idx.Add(row.Index);
            if (idx.Count == 0)
            {
                NetRow cur = CurrentRow();
                if (cur == null) return;
                idx.Add(_grid.SelectedRows[0].Index);
            }
            idx.Sort();
            string text = BuildCopyText(idx);
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    // 剪贴板被输入法/其它工具短暂占用很常见，等 40ms 再抢一次
                    System.Threading.Thread.Sleep(40);
                }
            }
            if (last == null)
            {
                Flash("已复制 " + idx.Count + " 行（Tab 分隔，可直接粘到 Excel）");
            }
            else
            {
                Theme.Alert(this, "复制失败", "剪贴板正被其它程序占用，稍后再试。\n\n系统提示：" + last.Message);
            }
        }

        /// <summary>危险进程判定：内核进程 + 关键系统进程一律拦在二次确认之前</summary>
        internal static bool IsProtected(NetRow r, out string reason)
        {
            reason = null;
            if (r == null) return true;
            if (r.Pid <= 4)
            {
                reason = "系统核心进程，为保护系统稳定禁止结束。\n\n"
                    + "PID " + r.Pid.ToString() + " 属于 " 
                    + (r.Pid == 0 ? "Idle（空闲进程）" : (r.Pid == 4 ? "System（Windows 内核）" : "内核进程"))
                    + "，它没有用户态进程体，结束操作会直接让系统蓝屏或立即关机。";
                return true;
            }
            string nm = r.ProcessDisplay.Trim().ToLowerInvariant();
            foreach (string p in ProtectedProcessNames)
            {
                if (nm == p || nm == p + ".exe")
                {
                    reason = "系统核心进程，为保护系统稳定禁止结束。\n\n"
                        + "「" + r.ProcessDisplay + "」（PID " + r.Pid.ToString()
                        + "）是 Windows 关键进程，结束它可能导致蓝屏、断网或立即重启。";
                    return true;
                }
            }
            return false;
        }

        private void KillSelected()
        {
            NetRow r = CurrentRow();
            if (r == null) return;

            string why;
            if (IsProtected(r, out why))
            {
                Theme.Alert(this, "已拦截", why);
                return;
            }

            string msg = "确认结束下面这个进程？\n\n"
                + "进程：" + r.ProcessDisplay + "\n"
                + "PID：" + r.Pid.ToString() + "\n"
                + "连接：" + r.Protocol + "  " + r.LocalEndPoint + " → " + r.RemoteEndPoint + "\n\n"
                + "该进程所有未保存的数据会丢失，它的全部网络连接会立即中断。";
            if (!Theme.Confirm(this, "结束进程", msg, "结束进程", true)) return;

            try
            {
                using (Process p = Process.GetProcessById(r.Pid))
                {
                    // 列表是快照，真动手前按真实进程名再核一次，防止 PID 被系统复用后误杀
                    string realName = "";
                    try { realName = p.ProcessName; } catch (Exception) { }
                    if (realName.Length > 0)
                    {
                        NetRow live = new NetRow();
                        live.Pid = r.Pid;
                        live.ProcessName = realName;
                        string liveWhy;
                        if (IsProtected(live, out liveWhy))
                        {
                            Theme.Alert(this, "已拦截", liveWhy);
                            return;
                        }
                    }
                    p.Kill();
                    p.WaitForExit(2000);
                }
                Flash("已结束 " + r.ProcessDisplay + "（PID " + r.Pid.ToString() + "）");
            }
            catch (Win32Exception ex)
            {
                Theme.Alert(this, "结束失败", "Power 不足：这个进程属于其他账户或受系统保护。\n"
                    + "请右键以管理员身份运行 NetEye 再试。\n\n系统提示：" + ex.Message);
            }
            catch (ArgumentException)
            {
                Theme.Alert(this, "进程已退出", "PID " + r.Pid.ToString() + " 已经不存在了，列表已刷新。");
            }
            catch (Exception ex)
            {
                Theme.Alert(this, "结束失败", ex.Message);
            }
            RefreshData();
        }

        // ================= v0.4：远程 IP 归属地查询 =================
        // 设计取舍：不内嵌任何本地 IP 库（GeoIP 库动辄几十 MB，本工具只有几十 KB）。
        // 只把「远程 IP」这一个字符串交给系统默认浏览器，走网页查询，体积零增长。
        // v0.6 起：exe 同目录有 qqwry.dat 就本地离线解析；v0.7 起：网页查询网址可配置。

        /// <summary>归属地查询的判定结果</summary>
        internal enum IpLookupKind
        {
            Public,     // 公网 IP，可以交给浏览器查
            Listen,     // 本行处于监听状态，没有远程对端
            NoRemote,   // 这一行没有远程地址（UDP / 已断开）
            Local       // 本机回环、局域网、保留地址，查了也没意义
        }

        /// <summary>
        /// 纯判定逻辑（不碰 UI、不开浏览器，便于单元测试）：
        /// 输入一行连接，输出该不该查、查哪个 IP、以及给用户的解释文案。
        /// </summary>
        internal static IpLookupKind ClassifyRemote(NetRow r, out string ip, out string message)
        {
            ip = "";
            message = "";
            if (r == null)
            {
                message = "请先选中一行连接。";
                return IpLookupKind.NoRemote;
            }

            // 1) 监听态优先判：LISTENING 的远程字段恒为 0.0.0.0:0 / [::]:0，没有对端可查
            if (!r.IsUdp && r.State == TcpState.Listen)
            {
                message = "当前处于监听状态，无远程连接。\n\n本机 " + r.LocalEndPoint
                    + " 正在等待外部接入，还没有对端地址。";
                return IpLookupKind.Listen;
            }

            string addr = (r.RemoteAddress ?? "").Trim();

            // 2) 通配 / 空 / UDP：没有远程地址
            if (r.IsUdp || addr.Length == 0 || addr == "*" || addr == "0.0.0.0" || addr == "::")
            {
                message = r.IsUdp
                    ? "这一行没有远程地址。\n\nUDP 是无连接协议，系统不记录对端，无法查归属地。"
                    : "这一行没有远程地址。\n\n连接可能已经断开，点「刷新」后再看。";
                return IpLookupKind.NoRemote;
            }

            // 3) 去掉 IPv6 的 %scope 后缀（fe80::1%12 这种），浏览器查不了带 zone 的地址
            int pct = addr.IndexOf('%');
            if (pct > 0) addr = addr.Substring(0, pct);

            ip = addr;
            if (IsLocalOrReserved(addr))
            {
                message = "本地局域网地址无需查询。\n\n" + addr + " 属于" + DescribeRange(addr)
                    + "，不对外网暴露，没有公网归属地。";
                return IpLookupKind.Local;
            }
            return IpLookupKind.Public;
        }

        /// <summary>本机回环 / 局域网 / 链路本地 / 保留地址（IPv4 与 IPv6 都覆盖）</summary>
        internal static bool IsLocalOrReserved(string addr)
        {
            System.Net.IPAddress ipa;
            if (!System.Net.IPAddress.TryParse(addr, out ipa)) return true;   // 解析不了就当别查，绝不乱开浏览器

            if (ipa.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                return IsLocalV4(ipa.GetAddressBytes());

            // IPv4 映射的 IPv6（::ffff:192.168.1.1）按 v4 判
            if (ipa.IsIPv4MappedToIPv6) return IsLocalV4(ipa.MapToIPv4().GetAddressBytes());
            if (System.Net.IPAddress.IsLoopback(ipa)) return true;            // ::1

            byte[] b = ipa.GetAddressBytes();
            if (b.Length != 16) return true;
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;            // fe80::/10 链路本地
            if ((b[0] & 0xFE) == 0xFC) return true;                            // fc00::/7 唯一本地 ULA
            if (b[0] == 0xFF) return true;                                     // ff00::/8 组播
            return false;
        }

        private static bool IsLocalV4(byte[] b)
        {
            if (b.Length != 4) return true;
            if (b[0] == 10) return true;                                       // 10/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;           // 172.16/12
            if (b[0] == 192 && b[1] == 168) return true;                        // 192.168/16
            if (b[0] == 127) return true;                                       // 127/8 回环
            if (b[0] == 169 && b[1] == 254) return true;                        // 169.254/16 链路本地
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;          // 100.64/10 运营商级 NAT
            if (b[0] == 0 || b[0] >= 224) return true;                          // 0/8 保留、组播、广播
            return false;
        }

        private static string DescribeRange(string addr)
        {
            System.Net.IPAddress ipa;
            if (System.Net.IPAddress.TryParse(addr, out ipa)
                && ipa.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] b = ipa.GetAddressBytes();
                if (b[0] == 10) return "私有网段 10.0.0.0/8";
                if (b[0] == 172) return "私有网段 172.16.0.0/12";
                if (b[0] == 192) return "私有网段 192.168.0.0/16";
                if (b[0] == 127) return "本机回环 127.0.0.0/8";
                if (b[0] == 169) return "链路本地 169.254.0.0/16（自动配置地址）";
                if (b[0] == 100) return "运营商级 NAT 100.64.0.0/10（共享地址）";
                return "保留地址段";
            }
            return "IPv6 内网/链路本地段";
        }

        /// <summary>右键「查询远程 IP 归属地」：本机判断 + 交给系统浏览器</summary>
        private void QueryRemoteIp()
        {
            if (_grid.SelectedRows.Count > 1)
            {
                Theme.Alert(this, "查询归属地", "一次查一行就够了。\n\n请只选中要查询的那一行再试。");
                return;
            }
            NetRow r = CurrentRow();
            if (r == null) return;

            string ip, msg;
            IpLookupKind kind = ClassifyRemote(r, out ip, out msg);

            if (kind == IpLookupKind.Listen) { Theme.Alert(this, "无需查询", msg); return; }
            if (kind == IpLookupKind.NoRemote) { Theme.Alert(this, "无需查询", msg); return; }
            if (kind == IpLookupKind.Local) { Theme.Alert(this, "无需查询", msg); return; }

            // v0.6：exe 同目录有 qqwry.dat 就本地离线解析，不联网不开浏览器
            if (_qqwry.Loaded)
            {
                string loc;
                if (_qqwry.Lookup(ip, out loc))
                {
                    Theme.Alert(this, "IP 归属地（本地离线库）",
                        ip + "\n\n" + loc + "\n\n来源：exe 同目录 qqwry.dat · "
                        + (_qqwry.EntryCount / 10000).ToString() + " 万条记录 · 离线解析");
                    Flash("已离线解析 " + ip + " 的归属地");
                    return;
                }
                // 本地库没收录这个段（库可能偏旧）→ 自动落到下面的浏览器兜底
            }

            // v0.7：网址不再写死，读同目录 config.ini 的 IpQueryUrl；用户改完 ini
            // 再点一次右键就生效（按文件最后写入时间缓存，不做无谓 IO）。
            _cfg.Reload(false);
            string url = AppConfig.BuildUrl(_cfg.IpQueryUrl, ip);
            LastLookupUrl = url;
            string fail = OpenBrowser(url);
            if (fail == null)
            {
                Flash(_cfg.ValueInvalid
                    ? "config.ini 的 IpQueryUrl 无效，已用内置 ip138 打开 " + ip
                    : "已在浏览器打开 " + ip + " 的归属地查询");
                return;
            }
            // 没有默认浏览器 / 被安全软件拦：不崩，给出可手抄的地址
            Theme.Alert(this, "无法打开浏览器",
                "调用系统默认浏览器失败。\n\n系统提示：" + fail
                + "\n\n可手动访问：\n" + url);
        }

        /// <summary>v0.7：打开网址（测试可换掉真实启动，避免测试机上弹浏览器）</summary>
        private static string OpenBrowser(string url)
        {
            Func<string, string> hook = UrlOpener;
            if (hook != null) return hook(url);
            return OpenUrl(url);
        }

        // ================= v0.6：导出结果快照 =================
        private void ExportCsv()
        {
            if (_view.Count == 0)
            {
                Theme.Alert(this, "导出结果", "当前没有可导出的行，先「刷新」或清空搜索条件。");
                return;
            }
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                // v0.9.1：导出与自动归档共用同一个时间戳文件名，两份内容一字不差
                string fileName = "NetEye_快照_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                dlg.Title = "导出结果快照";
                dlg.FileName = fileName;
                // v0.9.3：新增 JSON（自动化/程序处理用）；CSV 依旧是 Excel 双击即开的主力
                dlg.Filter = "CSV 文件 (*.csv)|*.csv|JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*";
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    bool isJson = dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                    int n = isJson ? WriteJsonFile(dlg.FileName, _view) : WriteCsvFile(dlg.FileName, _view);
                    // v0.9.1：顺手在 exe 同级 logs\ 再留一份存档，用户改错名/覆盖了也有底账
                    // （存档恒为 CSV：底账格式固定，方便翻历史）
                    string archived = ArchiveCsv(fileName, _view);
                    Flash("已导出 " + n.ToString() + " 行快照");
                    string extra = archived != null
                        ? "\n\n程序目录已自动存档一份：\n" + archived
                        : "\n\n（程序目录没有写权限，本次自动存档跳过；把 exe 放到可写目录即可自动存）";
                    Theme.Alert(this, "导出完成",
                        "已导出 " + n.ToString() + " 行到：\n" + dlg.FileName
                        + extra
                        + (isJson
                            ? "\n\n编码 UTF-8（JSON 规范不带 BOM），字段名 protocol/local/remote/state/pid/process/uptime/kind。"
                            : "\n\n编码 UTF-8（带 BOM），Excel 双击打开不乱码。"));
                }
                catch (Exception ex)
                {
                    Theme.Alert(this, "导出失败",
                        "写文件出错：\n" + ex.Message
                        + "\n\n如果该文件正被 Excel 打开会写入失败，关掉后重试。");
                }
            }
        }

        /// <summary>v0.9.1：logs 目录可注入（测试指到临时目录；生产恒 null = exe 同级 logs）</summary>
        internal static string LogsDirOverride = null;

        /// <summary>v0.9.1：自动存档目录（默认 exe 同级 logs\）</summary>
        internal static string LogsDir()
        {
            if (LogsDirOverride != null) return LogsDirOverride;
            return System.IO.Path.Combine(Application.StartupPath, "logs");
        }

        /// <summary>
        /// v0.9.1：把这份快照再存一份到 logs\NetEye_快照_时间戳.csv，返回实际落盘路径。
        /// 存档只是「顺手留个底」，所以任何失败（无写权限、磁盘满、目录被占）都只返回 null，
        /// 绝不让用户选好路径的那次正常导出跟着失败。
        /// </summary>
        internal static string ArchiveCsv(string fileName, List<NetRow> rows)
        {
            try
            {
                string dir = LogsDir();
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, fileName);
                if (System.IO.File.Exists(path))
                {
                    // 同一秒连点两次导出：加毫秒后缀另存，绝不覆盖上一份记录
                    path = System.IO.Path.Combine(dir,
                        System.IO.Path.GetFileNameWithoutExtension(fileName)
                        + "_" + DateTime.Now.ToString("fff") + ".csv");
                }
                WriteCsvFile(path, rows);
                return path;
            }
            catch (Exception) { return null; }
        }

        /// <summary>写 CSV：UTF-8 带 BOM + CRLF，Excel 中文零乱码。返回导出行数。</summary>
        internal static int WriteCsvFile(string path, List<NetRow> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("协议,本地地址:端口,远程地址:端口,状态,PID,进程名,运行时长,进程类型\r\n");
            for (int i = 0; i < rows.Count; i++)
            {
                NetRow r = rows[i];
                sb.Append(CsvCell(r.Protocol)).Append(',')
                    .Append(CsvCell(r.LocalEndPoint)).Append(',')
                    .Append(CsvCell(r.RemoteEndPoint)).Append(',')
                    .Append(CsvCell(r.StateText)).Append(',')
                    .Append(r.Pid.ToString()).Append(',')
                    .Append(CsvCell(r.ProcessDisplay)).Append(',')
                    .Append(CsvCell(ProcAge.TextOf(r))).Append(',')
                    .Append(CsvCell(ProcBadge.Label(r))).Append("\r\n");
            }
            // new UTF8Encoding(true) = 带 BOM，Excel 全版本识别
            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return rows.Count;
        }

        /// <summary>CSV 单元格转义：含逗号/引号/换行才加引号，内部引号翻倍（RFC 4180）</summary>
        internal static string CsvCell(string s)
        {
            if (s == null) return "";
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// v0.9.3：写 JSON 快照（UTF-8 无 BOM，JSON 规范要求）。手写转义零依赖。
        /// 字段名用英文（protocol/local/remote/state/pid/process/uptime/kind），
        /// 给自动化脚本消费；中文值原样输出。返回导出行数。
        /// </summary>
        internal static int WriteJsonFile(string path, List<NetRow> rows)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("[\r\n");
            for (int i = 0; i < rows.Count; i++)
            {
                NetRow r = rows[i];
                sb.Append("  {")
                  .Append("\"protocol\": ").Append(JsonString(r.Protocol)).Append(", ")
                  .Append("\"local\": ").Append(JsonString(r.LocalEndPoint)).Append(", ")
                  .Append("\"remote\": ").Append(JsonString(r.RemoteEndPoint)).Append(", ")
                  .Append("\"state\": ").Append(JsonString(r.StateText)).Append(", ")
                  .Append("\"pid\": ").Append(r.Pid.ToString()).Append(", ")
                  .Append("\"process\": ").Append(JsonString(r.ProcessDisplay)).Append(", ")
                  .Append("\"uptime\": ").Append(JsonString(ProcAge.TextOf(r))).Append(", ")
                  .Append("\"kind\": ").Append(JsonString(ProcBadge.Label(r)))
                  .Append("}");
                if (i < rows.Count - 1) sb.Append(',');
                sb.Append("\r\n");
            }
            sb.Append("]\r\n");
            System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return rows.Count;
        }

        /// <summary>JSON 字符串转义：引号/反斜杠/控制字符，其余原样（UTF-8 直接可读）</summary>
        internal static string JsonString(string s)
        {
            StringBuilder sb = new StringBuilder((s == null ? 0 : s.Length) + 8);
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ================= 自动刷新（v0.9.2 起常开，只留频率） =================
        private void ApplyAutoTimer()
        {
            if (_autoTimer == null) return;
            int[] ms = new int[] { 1000, 3000, 5000 };
            int i = _cmbRate == null ? 1 : _cmbRate.SelectedIndex;
            if (i < 0 || i > 2) i = 1;
            _autoTimer.Interval = ms[i];
            _autoTimer.Start();
            // 不再有「开/关」这回事，改频率时给一句反馈就够了（界面上一眼也能看到「每 3 秒」）
            if (_ready) Flash("后台自动刷新：每 " + (i == 0 ? "1" : (i == 1 ? "3" : "5")) + " 秒");
        }

        /// <summary>v0.9.2：当前自动刷新间隔（毫秒），测试断言用</summary>
        internal int AutoIntervalMs { get { return _autoTimer == null ? 0 : _autoTimer.Interval; } }

        /// <summary>v0.9.2：⚙ 设置窗（保存后 config.ini 立即生效，不用重启）</summary>
        private void ShowSettings()
        {
            using (SettingsForm dlg = new SettingsForm(_cfg))
            {
                // v0.9.3：设置窗里更新完离线库 → 主窗这边重载 qqwry.dat。
                // Qqwry.Load 会先把旧的整块内存库置空再读，所以旧缓存自然作废，无需重启。
                // 回调来自后台线程，必须先 BeginInvoke 切回 UI 线程再碰界面。
                dlg.OfflineLibUpdated += delegate
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            LoadQqwrySidecar();
                            Flash("离线 IP 库已更新，归属地查询切到本地离线解析");
                        });
                    }
                    catch (Exception) { }
                };
                DialogResult dr = dlg.ShowDialog(this);
                if (dr == DialogResult.OK)
                {
                    _cfg.Reload(true);
                    UpdateStatus();
                    Flash("查询网址已保存到 config.ini");
                }
            }
        }

        /// <summary>v0.9.2：设置按钮（测试断言位置/文案用）</summary>
        internal FlatButton SettingsButton { get { return _btnSettings; } }

        // 下拉框深色绘制（FlatStyle 下系统仍然画亮色列表，干脆自己画）
        private void CmbRate_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush bg = new SolidBrush(sel ? Theme.AccentDark : Theme.InputBg))
                e.Graphics.FillRectangle(bg, e.Bounds);
            string text = _cmbRate.Items[e.Index].ToString();
            TextRenderer.DrawText(e.Graphics, text, e.Font ?? Font,
                e.Bounds, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        /// <summary>v0.9.3：通用下拉深色绘制（匹配模式下拉用，配色与频率下拉一致）</summary>
        private void CmbSimple_DrawItem(object sender, DrawItemEventArgs e)
        {
            ComboBox cb = sender as ComboBox;
            if (cb == null || e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (SolidBrush bg = new SolidBrush(sel ? Theme.AccentDark : Theme.InputBg))
                e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, cb.Items[e.Index].ToString(), e.Font ?? Font,
                e.Bounds, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ================= v0.5：打开进程所在位置 =================
        /// <summary>
        /// 打开位置前的防呆判定（纯函数，便于测试）。返回 null 表示可以继续定位，
        /// 否则返回给用户看的原因。
        /// </summary>
        internal static string LocateBlockReason(NetRow r)
        {
            if (r == null) return "请先选中一行连接。";
            if (r.Pid <= 4)
            {
                return "PID " + r.Pid.ToString() + " 属于"
                    + (r.Pid == 0 ? " Idle（空闲进程）" : (r.Pid == 4 ? " System（Windows 内核）" : "内核进程"))
                    + "。\n\n它跑的是内核本身，没有独立的 exe 文件，资源管理器里定位不到。";
            }
            return null;
        }

        /// <summary>右键「打开进程所在位置」：拿 PID → 路径 → 资源管理器 /select 定位</summary>
        private void OpenProcessLocation()
        {
            NetRow r = PickOne("打开进程所在位置");
            if (r == null) return;

            // PID 0 / 4 是内核本身，没有独立 exe，定位不了——这也算防呆的一环
            string blocked = LocateBlockReason(r);
            if (blocked != null)
            {
                Theme.Alert(this, "无法定位", blocked);
                return;
            }

            string path, err;
            if (!ProcBadge.ResolveExePath(r.Pid, out path, out err))
            {
                Theme.Alert(this, "无法定位", err + "\n\n进程：" + r.ProcessDisplay
                    + "（PID " + r.Pid.ToString() + "）");
                return;
            }
            if (path.IndexOf('"') >= 0)   // 理论上不可能，防御一下命令行拼接
            {
                Theme.Alert(this, "无法定位", "取到的路径里含有引号，已拒绝拼接命令行。\n\n" + path);
                return;
            }
            if (!System.IO.File.Exists(path))
            {
                Theme.Alert(this, "文件已不在", "进程还在跑，但 exe 文件已经不在了（可能被删除或移动）。\n\n" + path);
                return;
            }

            try
            {
                // /select 会打开上一级目录并把该文件选中，这是资源管理器的官方参数
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
                Flash("已在资源管理器定位 " + System.IO.Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Theme.Alert(this, "打开失败", "调用资源管理器失败。\n\n系统提示：" + ex.Message
                    + "\n\n路径：\n" + path);
            }
        }

        // ================= v0.5：联网搜索此进程 =================
        /// <summary>搜索词（纯函数）：Windows进程 名字 是什么安全吗</summary>
        internal static string SearchQuery(string processName)
        {
            string name = (processName ?? "").Trim();
            if (name.Length == 0) return null;
            return "Windows进程 " + name + " 是什么安全吗";
        }

        internal static string SearchUrl(string processName)
        {
            string q = SearchQuery(processName);
            if (q == null) return null;
            return "https://cn.bing.com/search?q=" + Uri.EscapeDataString(q);
        }

        /// <summary>右键「联网搜索此进程」：把进程名丢给系统浏览器（工具本体依然不联网）</summary>
        private void SearchProcessOnline()
        {
            NetRow r = PickOne("联网搜索此进程");
            if (r == null) return;

            string name = (r.ProcessName ?? "").Trim();
            string q = SearchQuery(name);
            if (q == null)
            {
                Theme.Alert(this, "无法搜索", "这个进程的名字读不到（系统/受保护进程），没法搜索。\n\n"
                    + "可以改用「打开进程所在位置」或查它的端口来定位。");
                return;
            }

            string url = SearchUrl(name);
            string fail = OpenUrl(url);
            if (fail == null)
            {
                Flash("已在浏览器搜索 " + name + " 的说明");
                return;
            }
            Theme.Alert(this, "无法打开浏览器", "调用系统默认浏览器失败。\n\n系统提示：" + fail
                + "\n\n可手动搜索：\n" + q + "\n" + url);
        }

        /// <summary>多个选中时只处理一行（这几个动作都是针对单个进程的）</summary>
        private NetRow PickOne(string action)
        {
            if (_grid.SelectedRows.Count > 1)
            {
                Theme.Alert(this, action, "这个操作一次只处理一行。\n\n请只选中要操作的那一行再试。");
                return null;
            }
            return CurrentRow();
        }

        private static string OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ================= v0.9.3：反查远程 IP 域名 =================
        /// <summary>反查的守门判定（纯函数）：null=可以查，否则返回原因</summary>
        internal static string DnsBlockReason(NetRow r)
        {
            string ip, msg;
            IpLookupKind kind = ClassifyRemote(r, out ip, out msg);
            if (kind == IpLookupKind.Listen || kind == IpLookupKind.NoRemote) return msg;
            return null;   // 公网、内网都允许反查（内网反查的是主机名，很有用）
        }

        /// <summary>
        /// 右键「反查远程 IP 域名」：系统 DNS 的反向解析（PTR），内网主机名也能拿到。
        /// 解析可能要等好几秒，放线程池；结果回 UI 线程弹深色窗。任何失败都只是「没查到」。
        /// </summary>
        private void ReverseDns()
        {
            NetRow r = PickOne("反查远程 IP 域名");
            if (r == null) return;

            string blocked = DnsBlockReason(r);
            if (blocked != null) { Theme.Alert(this, "无法反查", blocked); return; }

            string addr = (r.RemoteAddress ?? "").Trim();
            int pct = addr.IndexOf('%');
            if (pct > 0) addr = addr.Substring(0, pct);   // 去掉 IPv6 %scope，反查不认 zone

            Flash("正在反查 " + addr + " 的域名…");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string host = null;
                try { host = System.Net.Dns.GetHostEntry(addr).HostName; }
                catch (Exception) { host = null; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (string.IsNullOrEmpty(host) || host == addr)
                            Theme.Alert(this, "反查结果",
                                addr + "\n\n没有解析到域名。\n\n可能这台机器没有登记反向记录（PTR），"
                                + "或当前 DNS 无法应答。");
                        else
                            Theme.Alert(this, "反查结果",
                                addr + "\n\n→ " + host + "\n\n来源：系统 DNS 反向解析（工具本体不联网）");
                    });
                }
                catch (Exception) { }   // 窗口正在销毁：结果丢弃即可
            });
        }

        // ================= v0.9.3：防火墙出站拦截 =================
        /// <summary>netsh 参数拼接（纯函数，便于测试断言）</summary>
        internal static string FirewallRuleName(string processDisplay)
        {
            return "[NetEye] " + (processDisplay ?? "") + " 出站拦截";
        }

        /// <summary>右键「防火墙拦截此程序出站」：确认 → netsh advfirewall 加 block 规则</summary>
        private void BlockInFirewall()
        {
            NetRow r = PickOne("防火墙拦截");
            if (r == null) return;

            if (r.Pid <= 4)
            {
                Theme.Alert(this, "无法拦截", "PID " + r.Pid.ToString()
                    + " 是内核进程，没有独立的 exe，防火墙规则拦不住它。");
                return;
            }
            if (!SysInfo.IsAdmin())
            {
                Theme.Alert(this, "需要管理员权限",
                    "添加 Windows 防火墙规则需要管理员权限。\n\n"
                    + "请右键 NetEye →「以管理员身份运行」后再试。");
                return;
            }

            string path, err;
            if (!ProcBadge.ResolveExePath(r.Pid, out path, out err))
            {
                Theme.Alert(this, "无法拦截", err + "\n\n进程：" + r.ProcessDisplay
                    + "（PID " + r.Pid.ToString() + "）");
                return;
            }
            if (path.IndexOf('"') >= 0)
            {
                Theme.Alert(this, "无法拦截", "取到的路径里含有引号，已拒绝拼接命令行。\n\n" + path);
                return;
            }

            string rule = FirewallRuleName(r.ProcessDisplay);
            string msg = "确认用 Windows 防火墙拦截下面这个程序的所有出站连接？\n\n"
                + "程序：" + r.ProcessDisplay + "（PID " + r.Pid.ToString() + "）\n"
                + "路径：" + path + "\n\n"
                + "将创建规则「" + rule + "」（方向=出站，操作=阻止），立即生效。\n"
                + "撤销：Win+R 输入 wf.msc → 出站规则 → 删除名称以 [NetEye] 开头的规则。";
            if (!Theme.Confirm(this, "防火墙拦截", msg, "创建拦截规则", true)) return;

            string netsh = System.IO.Path.Combine(Environment.SystemDirectory, "netsh.exe");
            string args = "advfirewall firewall add rule name=\"" + rule
                + "\" dir=out action=block program=\"" + path + "\" enable=yes";
            string output;
            int code = RunHidden(netsh, args, 15000, out output);
            if (code == 0)
            {
                Flash("已创建防火墙出站拦截：" + r.ProcessDisplay);
            }
            else
            {
                Theme.Alert(this, "拦截失败",
                    "netsh 返回退出码 " + code.ToString() + "。\n\n" + output);
            }
        }

        /// <summary>隐藏窗口跑一个命令行，带超时，收 stdout+stderr（失败时给用户看）</summary>
        private static int RunHidden(string exe, string args, int timeoutMs, out string output)
        {
            output = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = args;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string so = p.StandardOutput.ReadToEnd();
                    string se = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch (Exception) { }
                        output = "命令超时未完成。";
                        return -1;
                    }
                    output = (so + " " + se).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return -1;
            }
        }

        // ================= v0.9.3：记录模式（新连接追加日志） =================
        /// <summary>
        /// 把本次刷新里「上次没见过」的连接追加写进 logs\NetEye_连接_日期.csv。
        /// 判重键 = 协议|本地端点|远程端点|PID。注意：闪断的连接隔了几次刷新又出现，
        /// 会被再次记录 —— 这正是排查短连接流氓软件要的证据。内存去重表超过 15 万条
        /// 自动清空（极端环境防涨爆，最坏情况只是部分连接多记一次）。
        /// </summary>
        private void AppendNewConnections(List<NetRow> rows)
        {
            try
            {
                if (_connSeen.Count > 150000) _connSeen.Clear();
                StringBuilder sb = null;
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                for (int i = 0; i < rows.Count; i++)
                {
                    NetRow r = rows[i];
                    string key = r.Protocol + "|" + r.LocalEndPoint + "|" + r.RemoteEndPoint + "|" + r.Pid.ToString();
                    if (_connSeen.Contains(key)) continue;
                    _connSeen.Add(key);
                    if (sb == null) sb = new StringBuilder();
                    sb.Append(CsvCell(now)).Append(',')
                      .Append(CsvCell(r.Protocol)).Append(',')
                      .Append(CsvCell(r.LocalEndPoint)).Append(',')
                      .Append(CsvCell(r.RemoteEndPoint)).Append(',')
                      .Append(r.Pid.ToString()).Append(',')
                      .Append(CsvCell(r.ProcessDisplay)).Append("\r\n");
                }
                if (sb == null) return;

                string dir = LogsDir();
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir,
                    "NetEye_连接_" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
                bool exists = System.IO.File.Exists(path);
                if (!exists)
                {
                    // 新文件先写表头；带 BOM（Excel 打开不乱码），续写时不重复写 BOM
                    System.IO.File.AppendAllText(path,
                        "记录时间,协议,本地地址:端口,远程地址:端口,PID,进程名\r\n",
                        new UTF8Encoding(true));
                }
                System.IO.File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }   // 记录是锦上添花，没写权限/磁盘满都不打扰主流程
        }

        // ================= v0.8：赞赏彩蛋 =================
        /// <summary>
        /// 点「♥ 赞赏」：优先 exe 同级 donate.png（想换成自己的收款码就放一张），
        /// 没有再退回内嵌 Base64（发布包纯单文件、开箱即显）。
        /// 两条路都断了才提示「赞赏码暂未生成」——赞赏是彩蛋，任何情况都不许报错。
        /// </summary>
        private void LblDonate_Click(object sender, EventArgs e)
        {
            string img = System.IO.Path.Combine(Application.StartupPath, "donate.png");
            if (System.IO.File.Exists(img))
            {
                LastDonateSource = "sidecar";
                using (DonateForm dlg = new DonateForm(img)) dlg.ShowDialog(this);
                return;
            }
            if (DonateImage.Bytes != null && DonateImage.Bytes.Length > 0)
            {
                LastDonateSource = "embedded";
                using (DonateForm dlg = new DonateForm(null)) dlg.ShowDialog(this);
                return;
            }
            LastDonateSource = "missing";
            if (DonateMissingHook != null) { DonateMissingHook(); return; }
            Theme.Alert(this, "感谢您的心意", "感谢您的心意！赞赏码暂未生成。");
        }

        // ================= 状态栏 =================

        /// <summary>
        /// v0.9.1：运行时长格式化。小时用「总小时数」（超过 99 小时照常往上加，不折回），
        /// 分秒两位补零。负数一律按 0 处理，绝不显示 -1:-5。
        /// </summary>
        internal static string FormatUptime(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            long hours = (long)Math.Floor(t.TotalHours);
            return "已运行 " + hours.ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        /// <summary>v0.9.1：每秒刷新一次「已运行 HH:mm:ss」（由 _uptimeTimer 驱动）</summary>
        private void UpdateUptime()
        {
            if (_lblUptime == null) return;
            string s = FormatUptime(_upWatch.Elapsed);
            if (_lblUptime.Text != s) _lblUptime.Text = s;   // 同秒不重设，避免无谓重绘
        }

        /// <summary>
        /// v0.9.2：每秒一次的统一节拍——运行时长 + 全机网速（共用一个 1 秒定时器，
        /// 少一个 Timer 就少一份跨线程/重入的麻烦）。
        /// 网速采样里任何异常都被 NetSpeed 吞掉：状态栏只是锦上添花，绝不允许它影响主功能。
        /// </summary>
        private void OnSecondTick()
        {
            UpdateUptime();
            try
            {
                NetSpeed.Sample();
                if (_lblSpeed != null)
                {
                    string s = NetSpeed.CurrentText();
                    if (_lblSpeed.Text != s) _lblSpeed.Text = s;
                }
            }
            catch (Exception) { }
        }

        /// <summary>v0.9.2：状态栏网速格（断言文字/位置用）</summary>
        internal Label StatusSpeed { get { return _lblSpeed; } }

        /// <summary>v0.9.2 测试钩子：手动走一次每秒节拍（不必真等 1 秒）</summary>
        internal void TestSecondTick() { OnSecondTick(); }

        private void Flash(string msg)
        {
            _lblRight.ForeColor = Theme.Accent;
            _lblRight.Text = msg;
            if (_flash == null)
            {
                _flash = new Timer();
                _flash.Interval = 2600;
                _flash.Tick += delegate
                {
                    _flash.Stop();
                    _lblRight.ForeColor = Theme.TextDim;
                    UpdateStatus();
                };
            }
            _flash.Stop();
            _flash.Start();
        }

        private void UpdateStatus()
        {
            if (_lblLeft == null) return;
            int tcp = 0, udp = 0;
            HashSet<int> pids = new HashSet<int>();
            foreach (NetRow r in _all)
            {
                if (r.IsUdp) udp++; else tcp++;
                pids.Add(r.Pid);
            }
            string s = "共 " + _all.Count.ToString() + " 条 · TCP " + tcp.ToString() + " / UDP " + udp.ToString()
                + " · 进程 " + pids.Count.ToString() + " 个";
            if (_view.Count != _all.Count) s += " · 筛选后 " + _view.Count.ToString() + " 条";
            if (_qqwry.Loaded) s += " · 离线IP库 " + (_qqwry.EntryCount / 10000).ToString() + " 万条";
            _lblLeft.Text = s;
            if (_flash == null || !_flash.Enabled)
                _lblRight.Text = "刷新 " + _lastMs.ToString("F1") + " ms · " + SysInfo.Label();
        }

        // ================= 自动化测试入口（产品流程里用不到） =================
        internal void TestRefresh() { RefreshData(); }
        internal void TestRefreshKeepSelection() { RefreshData(true); }
        internal void TestCopy() { CopySelected(); }
        /// <summary>v0.9.2：界面上已经没有「自动刷新」复选框了（改成常开），这里改成断言定时器真在跑</summary>
        internal bool AutoRefreshAlwaysOn { get { return _autoTimer != null && _autoTimer.Enabled; } }
        internal ComboBox AutoRate { get { return _cmbRate; } }
        internal Timer AutoTimer { get { return _autoTimer; } }
        internal FlatButton ExportButton { get { return _btnExport; } }
        /// <summary>v0.9.2：刷新按钮（断言顶部工具行顺序用）</summary>
        internal FlatButton RefreshButton { get { return _btnRefresh; } }
        /// <summary>v0.9.1：状态栏「已运行 HH:mm:ss」（断言文字/位置用）</summary>
        internal Label StatusUptime { get { return _lblUptime; } }
        /// <summary>v0.9.1：运行时长计时器（断言它真的在每秒跑）</summary>
        internal Timer UptimeTimer { get { return _uptimeTimer; } }
        internal Qqwry Qw { get { return _qqwry; } }
        /// <summary>v0.8：状态栏「♥ 赞赏」入口（断言语义/位置用）</summary>
        internal Label StatusDonate { get { return _lblDonate; } }
        /// <summary>v0.8：最近一次点赞赏实际走的路（sidecar / embedded / missing）</summary>
        internal string LastDonateSource { get; private set; }
        /// <summary>v0.8 测试钩子：非 null 时代替「赞赏码暂未生成」弹窗（生产恒 null）</summary>
        internal static Action DonateMissingHook = null;
        /// <summary>v0.7：最近一次「浏览器查询」真正拼出来的网址（离线解析时为空）</summary>
        internal string LastLookupUrl { get; private set; }
        /// <summary>v0.7：配置对象（断言 IpQueryUrl / FilePath 用）</summary>
        internal AppConfig Cfg { get { return _cfg; } }
        /// <summary>
        /// v0.7 测试钩子：非 null 时代替 Process.Start 打开网址（生产恒 null），
        /// 让测试能把「拼出来的 URL」抓下来断言，而不是真在测试机上弹浏览器。
        /// </summary>
        internal static Func<string, string> UrlOpener = null;
        internal void ReloadConfig() { _cfg.Reload(true); }
        internal bool LoadQqwryFrom(string path)
        {
            bool ok = _qqwry.Load(path);
            if (ok)
            {
                if (_miLookup != null) _miLookup.Text = "查询远程 IP 归属地（本地离线库）";
                UpdateStatus();
            }
            return ok;
        }

        /// <summary>v0.6 测试钩子：把合成行变成唯一视图行并选中，然后走「真实」QueryRemoteIp 全链路</summary>
        internal void TestQueryRemoteIp(NetRow synthetic)
        {
            _all = new List<NetRow>();
            _all.Add(synthetic);
            _hay = new string[] { Haystack(synthetic) };
            _pendingSelKey = null;
            BuildView();
            _grid.ClearSelection();
            _grid.Rows[0].Selected = true;
            _grid.CurrentCell = _grid.Rows[0].Cells[ColProto];
            QueryRemoteIp();
        }
        internal void TestSortBy(int col)
        {
            DataGridViewCellMouseEventArgs args = new DataGridViewCellMouseEventArgs(
                col, -1, 0, 0, new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
            Grid_ColumnHeaderMouseClick(_grid, args);
        }

        internal DataGridView Grid { get { return _grid; } }
        internal SearchBox Search { get { return _search; } }
        internal IList<NetRow> View { get { return _view; } }
        internal IList<NetRow> All { get { return _all; } }
        internal Label StatusLeft { get { return _lblLeft; } }
        internal Label StatusRight { get { return _lblRight; } }
        internal double LastRefreshMs { get { return _lastMs; } }
        internal int SortColumn { get { return _sortCol; } }
        internal bool SortAscending { get { return _sortAsc; } }
        internal string HintText { get { return _lblHint.Text; } }
        // v0.9.3 测试钩子
        internal ComboBox MatchCombo { get { return _cmbMatch; } }
        internal ToolStripMenuItem MenuCompact { get { return _miCompact; } }
        internal ToolStripMenuItem MenuLog { get { return _miLog; } }
        internal ToolStripMenuItem MenuFirewall { get { return _miFirewall; } }
        internal ToolStripMenuItem MenuDns { get { return _miDns; } }
        internal ToolStripMenuItem MenuLookup { get { return _miLookup; } }
        internal bool CompactRows { get { return _compact; } }
        internal bool LogNewEnabled { get { return _logNew; } }
        internal int MatchMode { get { return _matchMode; } }
        /// <summary>v0.9.3：测试钩子 —— 直接给一批合成行走记录模式的落盘路径</summary>
        internal void TestAppendConnections(List<NetRow> rows) { AppendNewConnections(rows); }
        /// <summary>v0.9.3：测试钩子 —— 手动触发图标队列（不必等刷新）</summary>
        internal void TestRequestIcons() { ProcIcons.Request(_all); }

        // 窗口尺寸变化时右对齐的状态文字要跟着走
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_ready && IsHandleCreated) ApplyLayout(false);
        }

        /// <summary>后台读到新签名 → 主线程重画类型列（限流在 ProcBadge 里做）</summary>
        private void OnBadgeUpdated()
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (IsDisposed) return;
                    try { _grid.InvalidateColumn(ColKind); } catch (Exception) { }
                });
            }
            catch (Exception) { }     // 窗口正在销毁，BeginInvoke 会抛，忽略即可
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ProcBadge.Updated -= OnBadgeUpdated;   // 别把窗口对象留在静态事件上
            ProcIcons.Updated -= OnIconsUpdated;   // v0.9.3
            // v0.9.3：程序要退出了，把可能还在跑的离线库下载取消掉（顺手清掉半截 .part）。
            // 不取消的话，进程退出时后台线程会被硬砍，留下一个 20MB 的残文件在目录里。
            LibUpdater.CancelJob();
            if (_autoTimer != null) { _autoTimer.Stop(); _autoTimer.Dispose(); }
            if (_uptimeTimer != null) { _uptimeTimer.Stop(); _uptimeTimer.Dispose(); }   // v0.9.1
            _upWatch.Stop();
            base.OnFormClosed(e);
        }
    }

    /// <summary>系统信息小工具（清单声明了 Win10 兼容，Environment.OSVersion 才准；这里仍以注册表为准）</summary>
    internal static class SysInfo
    {
        private static string _label;

        public static string Label()
        {
            if (_label != null) return _label;
            bool admin = IsAdmin();
            string os = OsName();
            _label = os + " · " + (admin ? "管理员" : "普通用户");
            return _label;
        }

        public static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p =
                    new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        private static string OsName()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object b = k.GetValue("CurrentBuild");
                        int build;
                        if (b != null && int.TryParse(b.ToString(), out build))
                            return (build >= 22000 ? "Win11 " : (build >= 10240 ? "Win10 " : "Win "))
                                + build.ToString();
                    }
                }
            }
            catch (Exception) { }
            return Environment.OSVersion.Version.ToString();
        }
    }
}
