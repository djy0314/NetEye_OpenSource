using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace NetEye
{
    /// <summary>
    /// 进程类型（数值即排序权重，从可信到可疑）。
    /// 判定顺序：内置特征库（名字）→ 数字签名（后台异步）→ 读不到就标受保护。
    /// </summary>
    internal enum ProcKind
    {
        Microsoft = 0,   // 微软核心：Windows 自带组件 / 微软签名
        Vendor = 1,      // 第三方大厂：腾讯、阿里、谷歌、联想这类有正式签名的商业软件
        Signed = 2,      // 已签名：有数字签名，但不是上面两类
        Unsigned = 3,    // 未知可疑：文件没有数字签名
        Protected = 4,   // 受保护：读不到 exe 路径（系统进程 / 权限不足）
        Pending = 5      // 待识别：后台正在读签名，几百毫秒后自动补上
    }

    /// <summary>一个进程的识别结果（缓存单元）</summary>
    internal sealed class ProcSig
    {
        public string Name;     // 小写、不带 .exe，用于防 PID 复用串号
        public string Path;     // exe 完整路径，可能为空
        public ProcKind Kind;
        public string Signer;   // 证书主体里取出的公司名，可能为空
    }

    /// <summary>
    /// 进程智能识别（全程离线）。
    ///
    /// 性能实测（本机 609 进程 / 124 个唯一 exe）：
    ///   QueryFullProcessImageName 全量 26 ms（0.04 ms/次）→ 可以随手调；
    ///   X509Certificate.CreateFromSignedFile 平均 27.6 ms、最慢 1221 ms、全量 3.4 秒
    ///   → 绝不能在刷新或绘制里同步做，否则界面直接卡。
    /// 所以策略是：先用内置特征库（零 IO）把常见进程标出来，剩下的丢后台线程慢慢读签名，
    /// 读到一批就 Invalidate 一次，用户几乎无感。
    /// </summary>
    internal static class ProcBadge
    {
        // ---------- 文案 ----------
        public const string LabelMicrosoft = "微软核心";
        public const string LabelVendor = "第三方大厂";
        public const string LabelSigned = "已签名";
        public const string LabelUnsigned = "未知可疑";
        public const string LabelProtected = "受保护";
        public const string LabelPending = "待识别";

        public static string LabelOf(ProcKind k)
        {
            switch (k)
            {
                case ProcKind.Microsoft: return LabelMicrosoft;
                case ProcKind.Vendor: return LabelVendor;
                case ProcKind.Signed: return LabelSigned;
                case ProcKind.Unsigned: return LabelUnsigned;
                case ProcKind.Protected: return LabelProtected;
                default: return LabelPending;
            }
        }

        /// <summary>Windows 自带组件（名字命中就直判微软核心，不看签名——省一次 27ms 的 IO）</summary>
        private static readonly string[] MicrosoftNames = new string[]
        {
            "system", "idle", "system idle process", "registry", "memcompression", "secure system",
            "smss", "csrss", "wininit", "services", "lsass", "lsaiso", "winlogon", "logonui",
            "svchost", "dwm", "fontdrvhost", "spoolsv", "taskhostw", "sihost", "runtimebroker",
            "dllhost", "conhost", "ctfmon", "explorer", "searchindexer", "searchprotocolhost",
            "searchfilterhost", "smartscreen", "securityhealthservice", "securityhealthsystray",
            "msmpeng", "nissrv", "audiodg", "wmiprvse", "unsecapp", "msiexec", "tiworker",
            "trustedinstaller", "wuauclt", "usoclient", "backgroundtaskhost", "applicationframehost",
            "systemsettings", "shellexperiencehost", "startmenuexperiencehost", "textinputhost",
            "lockapp", "userinit", "sppsvc", "wudfhost", "dashost", "taskmgr", "notepad", "mstsc",
            "regedit", "mmc", "rundll32", "regsvr32", "werfault", "msedge", "onedrive", "ms-teams",
            "teams", "smartscreenproxy", "consent", "devicecensus", "compattelrunner",
            // 微软自家产品：名字进白名单，省一次签名 IO（实测签名主体也是 Microsoft）
            "sqlservr", "code", "sqlbrowser", "sqlwriter",
            "windowsinternal.composableshell.experiences.textinput.inputapp"
        };

        /// <summary>大厂进程名（零 IO 的先验提示；有签名时以后台读到的结果为准）</summary>
        private static readonly string[] VendorNames = new string[]
        {
            "chrome", "firefox", "opera", "brave", "360se", "360chrome", "sogouexplorer", "qqbrowser",
            "wechat", "weixin", "wxwork", "qq", "tim", "dingtalk", "feishu", "lark", "wps", "et", "wpp",
            "ksolaunch", "baidunetdisk", "thunder", "steam", "idea64", "pycharm64", "webstorm",
            "goland64", "clion64", "devenv", "navicat", "todesk", "sunloginclient", "anydesk",
            "teamviewer", "vmware-vmx", "virtualboxvm", "node", "java", "javaw", "python", "pythonw",
            "git", "bash", "nginx", "mysqld", "redis-server", "docker desktop",
            "doubao", "qoder", "workbuddy", "huorong", "hipstray", "wsctrl", "usysdiag"
        };

        /// <summary>证书主体（O= 公司名）里的大厂关键词，全部小写匹配</summary>
        private static readonly string[] VendorSignerKeys = new string[]
        {
            "tencent", "alibaba", "alipay", "ant group", "google", "nvidia", "intel corporation",
            "advanced micro devices", "amd inc", "realtek", "lenovo", "kingsoft", "zhuhai",
            "baidu", "duyou", "bytedance", "北京春田", "huawei", "xiaomi", "qi an xin", "奇安信",
            "火绒", "huorong", "qihoo", "sogou", "netease", "网易", "adobe", "jetbrains", "valve",
            "mozilla", "oracle", "apple inc", "dell", "samsung", "zoom video", "slack", "spotify",
            "telegram", "docker", "vmware", "citrix", "logitech", "anydesk", "teamviewer", "oray",
            "贝锐", "premiumsoft", "mongodb", "postman", "unity technologies", "epic games",
            "electronic arts", "ubisoft", "riot games", "xunlei", "迅雷", "youdao", "sangfor",
            "深信服", "kaspersky", "eset", "avast", "avg technologies", "bitdefender", "mcafee",
            "symantec", "norton", "trend micro", "malwarebytes", "openjs foundation",
            "johannes schindelin", "globalsign", "comodo"
        };

        /// <summary>内置中文说明（进程名 → 一句话解释），悬停时显示</summary>
        private static readonly Dictionary<string, string> Explain = BuildExplain();

        private static Dictionary<string, string> BuildExplain()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.Ordinal);
            d["system"] = "Windows 内核（PID 4）。它没有独立的 exe 进程体，跑的就是内核本身。";
            d["idle"] = "空闲进程（PID 0）。CPU 闲着的时间都记在它头上。";
            d["registry"] = "注册表配置单元进程，属于内核的一部分。";
            d["memcompression"] = "内存压缩。把不常用的内存压缩后留在 RAM 里，少走硬盘交换。";
            d["smss"] = "会话管理器，Windows 启动后第一个用户态进程。";
            d["csrss"] = "客户端/服务端运行时子系统，负责控制台窗口和进程线程管理。";
            d["wininit"] = "启动初始化进程，负责拉起 services.exe、lsass.exe 等核心服务。";
            d["services"] = "服务控制管理器，所有 Windows 服务都由它托管。";
            d["lsass"] = "本地安全认证，管登录凭证和密码策略。动它系统会强制重启。";
            d["lsaiso"] = "LSA 隔离进程，给 lsass 提供隔离环境（Credential Guard）。";
            d["svchost"] = "系统服务宿主，多个实例正常。一个 svchost 里跑一组服务，十几个是常态。";
            d["winlogon"] = "登录窗口宿主，处理登录、注销、锁屏。";
            d["logonui"] = "登录界面 UI，锁屏的时候才会出现。";
            d["dwm"] = "桌面窗口管理器，负责窗口合成、毛玻璃、动画。关掉屏幕立刻黑。";
            d["fontdrvhost"] = "字体驱动宿主，负责字体渲染。";
            d["taskhostw"] = "任务宿主，跑计划任务和部分后台 API。";
            d["sihost"] = "Shell 基础设施宿主，给开始菜单、任务栏提供支撑。";
            d["runtimebroker"] = "UWP 应用权限代理。商店应用要访问麦克风、文件都得经过它。";
            d["dllhost"] = "COM+ 代理宿主，跑缩略图、属性页这类组件服务。";
            d["conhost"] = "控制台窗口宿主，给命令行程序套一层窗口。";
            d["ctfmon"] = "输入法框架进程，中文输入法全靠它。";
            d["explorer"] = "资源管理器（桌面 + 文件窗口）。结束它桌面图标和任务栏就没了。";
            d["searchindexer"] = "Windows 搜索索引，扫文件建索引，磁盘狂转多半是它。";
            d["searchprotocolhost"] = "搜索协议宿主，执行具体的索引/搜索任务。";
            d["searchfilterhost"] = "搜索过滤器宿主，解析 PDF/Office 内容建索引。";
            d["smartscreen"] = "SmartScreen 信誉检查，判断下载的 exe 是否危险。";
            d["securityhealthservice"] = "Windows 安全中心服务（Defender 状态那块）。";
            d["msmpeng"] = "Microsoft Defender 反病毒引擎本体，扫文件时 CPU 会飙。";
            d["nissrv"] = "Defender 的网络检查服务。";
            d["audiodg"] = "音频设备图形隔离，管音频混音和音效。";
            d["spoolsv"] = "打印后台处理程序，不打印的时候可以停。";
            d["wmiprvse"] = "WMI 提供程序宿主，很多管理工具靠它取硬件和系统信息。";
            d["unsecapp"] = "WMI 的回调代理进程。";
            d["msiexec"] = "Windows Installer，安装/卸载 msi 包时出现。";
            d["tiworker"] = "Windows 模块安装器工作进程，装更新的时候跑。";
            d["trustedinstaller"] = "更新组件安装服务。别结束，会弄坏系统更新。";
            d["taskmgr"] = "任务管理器。";
            d["notepad"] = "记事本。";
            d["mstsc"] = "远程桌面客户端。";
            d["regedit"] = "注册表编辑器。";
            d["chrome"] = "Google Chrome 浏览器。多个实例是正常的渲染/GPU 子进程。";
            d["msedge"] = "Microsoft Edge 浏览器。";
            d["firefox"] = "Firefox 浏览器。";
            d["wechat"] = "微信（Windows 版）。多开会看到多个 WeChat.exe。";
            d["weixin"] = "微信（Windows 版新版进程名）。";
            d["wxwork"] = "企业微信。";
            d["qq"] = "QQ。";
            d["dingtalk"] = "钉钉。";
            d["feishu"] = "飞书。";
            d["lark"] = "飞书（国际版）。";
            d["wps"] = "WPS Office 主程序。";
            d["et"] = "WPS 表格。";
            d["wpp"] = "WPS 演示。";
            d["baidunetdisk"] = "百度网盘，常驻后台做同步。";
            d["thunder"] = "迅雷下载。";
            d["steam"] = "Steam 游戏平台客户端。";
            d["code"] = "Visual Studio Code。";
            d["idea64"] = "IntelliJ IDEA（64 位）。";
            d["pycharm64"] = "PyCharm（64 位）。";
            d["devenv"] = "Visual Studio 主进程。";
            d["navicat"] = "Navicat 数据库客户端。";
            d["todesk"] = "ToDesk 远程控制。";
            d["sunloginclient"] = "向日葵远程控制。";
            d["anydesk"] = "AnyDesk 远程控制。";
            d["teamviewer"] = "TeamViewer 远程控制。";
            d["vmware-vmx"] = "VMware 虚拟机主进程，一台虚拟机一个。";
            d["virtualboxvm"] = "VirtualBox 虚拟机进程。";
            d["node"] = "Node.js 运行时，前端构建和本地服务常用。";
            d["java"] = "Java 虚拟机，企业系统后端、IDEA 构建都靠它。";
            d["javaw"] = "Java 虚拟机（无控制台窗口版本）。";
            d["python"] = "Python 解释器。";
            d["pythonw"] = "Python 解释器（无控制台窗口版本）。";
            d["git"] = "Git for Windows。";
            d["bash"] = "bash shell（Git Bash / WSL）。";
            d["nginx"] = "Nginx Web 服务器 / 反向代理。";
            d["mysqld"] = "MySQL 数据库服务。";
            d["redis-server"] = "Redis 内存数据库。";
            d["sqlservr"] = "SQL Server 数据库引擎。";
            d["onedrive"] = "OneDrive 云同步。";
            d["doubao"] = "豆包（字节跳动 AI 助手）。";
            d["qoder"] = "Qoder（阿里云 AI 编程工具）。";
            d["workbuddy"] = "WorkBuddy 主程序。";
            d["huorong"] = "火绒安全软件。";
            return d;
        }

        // ---------- 缓存与后台扫描 ----------
        private static readonly ConcurrentDictionary<int, ProcSig> _pidSig = new ConcurrentDictionary<int, ProcSig>();
        private static readonly ConcurrentDictionary<string, ProcSig> _pathSig =
            new ConcurrentDictionary<string, ProcSig>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<int, string> _queued = new ConcurrentDictionary<int, string>();
        private static readonly Queue<KeyValuePair<int, string>> _queue = new Queue<KeyValuePair<int, string>>();
        private static readonly object _qLock = new object();
        private static Thread _worker;
        private static int _lastRaiseTick;

        /// <summary>后台补到新结果时触发（工作线程上调用，UI 层自己 BeginInvoke 回主线程）</summary>
        public static event Action Updated;

        internal static void ResetForTest()
        {
            lock (_qLock)
            {
                _queue.Clear();
                _worker = null;
            }
            _pidSig.Clear();
            _pathSig.Clear();
            _queued.Clear();
        }

        // ---------- 对外：类型 / 标签 / 悬停说明 ----------

        public static ProcKind KindOf(NetRow r)
        {
            if (r == null) return ProcKind.Protected;
            string nm = Norm(r.ProcessName);
            if (nm.Length == 0) return ProcKind.Protected;       // 名字都读不到，别猜
            if (IsMicrosoftName(nm)) return ProcKind.Microsoft;

            ProcSig s = Lookup(r.Pid, nm);
            if (s != null) return s.Kind;

            if (IsVendorName(nm)) return ProcKind.Vendor;         // 零 IO 先验，签名读到后以缓存为准
            return ProcKind.Pending;
        }

        public static string Label(NetRow r)
        {
            return LabelOf(KindOf(r));
        }

        /// <summary>悬停说明：名字 + 类型 + 中文解释 + 签名者 + 路径</summary>
        public static string Tooltip(NetRow r)
        {
            if (r == null) return null;
            string nm = Norm(r.ProcessName);
            StringBuilder sb = new StringBuilder(320);

            if (nm.Length == 0)
            {
                sb.Append("[系统/受保护进程]").Append(" · ").Append(LabelProtected).Append('\n');
                sb.Append("进程名和 exe 路径都读不到，通常是 SYSTEM 账户下的系统进程。\n");
                sb.Append("普通权限下看不到它的可执行文件，属正常现象。");
                return sb.ToString();
            }

            ProcKind k = KindOf(r);
            ProcSig sig = Lookup(r.Pid, nm);
            sb.Append(r.ProcessDisplay).Append(" · ").Append(LabelOf(k)).Append('\n');

            string exp;
            if (Explain.TryGetValue(nm, out exp)) sb.Append(exp);
            else sb.Append("特征库里没有收录这个进程。请结合下面的路径和签名自己判断，拿不准就右键「联网搜索此进程」。");
            sb.Append('\n').Append('\n');

            sb.Append("PID ").Append(r.Pid.ToString());
            if (sig != null && !string.IsNullOrEmpty(sig.Signer))
                sb.Append("　签名者：").Append(sig.Signer);
            else if (k == ProcKind.Unsigned)
                sb.Append("　无数字签名");

            string path;
            if (sig != null && !string.IsNullOrEmpty(sig.Path)) path = sig.Path;
            else path = QueryPath(r.Pid);
            if (!string.IsNullOrEmpty(path)) sb.Append('\n').Append(path);
            return sb.ToString();
        }

        // ---------- 对外：打开所在位置用的路径解析 ----------

        /// <summary>
        /// 取 exe 路径：先按需求走 Process.MainModule.FileName，
        /// 权限不足再退到 QueryFullProcessImageName（低权限也能读的通道）。
        /// </summary>
        public static bool ResolveExePath(int pid, out string path, out string error)
        {
            path = "";
            error = "";

            ProcSig cached;
            if (_pidSig.TryGetValue(pid, out cached) && cached != null && !string.IsNullOrEmpty(cached.Path))
            {
                path = cached.Path;
                return true;
            }

            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    path = p.MainModule.FileName;
                }
                if (!string.IsNullOrEmpty(path)) return true;
            }
            catch (ArgumentException)
            {
                error = "该进程已经退出了，列表马上刷新。";
                return false;
            }
            catch (InvalidOperationException)
            {
                error = "该进程已经退出了，列表马上刷新。";
                return false;
            }
            catch (Exception)
            {
                path = "";       // Win32Exception：权限不足 / 受系统保护，走兜底
            }

            path = QueryPath(pid);
            if (!string.IsNullOrEmpty(path)) return true;

            error = "无法获取该进程路径，可能需要管理员权限或该进程受系统保护。";
            return false;
        }

        // ---------- 对外：后台扫描 ----------

        /// <summary>
        /// 把「还没识别」的进程丢给后台线程读签名（幂等，重复调用不会重复扫）。
        /// 只有微软白名单跳过——它的名字本身就是权威结论；
        /// 大厂名单只是「先给个标签」，仍然要进队列用签名复核：
        /// 实测本机 sqlservr（SQL Server）名字看着像第三方，签名主体其实是 Microsoft。
        /// </summary>
        public static void RequestScan(IList<NetRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            bool any = false;
            for (int i = 0; i < rows.Count; i++)
            {
                NetRow r = rows[i];
                if (r == null || r.Pid <= 0) continue;
                string nm = Norm(r.ProcessName);
                if (nm.Length == 0) continue;
                if (IsMicrosoftName(nm)) continue;
                if (Lookup(r.Pid, nm) != null) continue;

                string q;
                if (_queued.TryGetValue(r.Pid, out q) && q == nm) continue;
                _queued[r.Pid] = nm;
                lock (_qLock) { _queue.Enqueue(new KeyValuePair<int, string>(r.Pid, nm)); }
                any = true;
            }
            if (!any) return;
            EnsureWorker();
        }

        private static void EnsureWorker()
        {
            lock (_qLock)
            {
                if (_worker != null) return;
                Thread t = new Thread(WorkerLoop);
                t.IsBackground = true;                 // 关窗口就跟着退，绝不留后台线程
                t.Priority = ThreadPriority.BelowNormal;
                t.Name = "neteye-procbadge";
                _worker = t;
                t.Start();
            }
        }

        private static void WorkerLoop()
        {
            bool didWork = false;
            while (true)
            {
                KeyValuePair<int, string> item;
                lock (_qLock)
                {
                    if (_queue.Count == 0)
                    {
                        // 只清自己这一代的引用，别把后来新建的 worker 覆盖成 null
                        if (object.ReferenceEquals(_worker, Thread.CurrentThread)) _worker = null;
                        break;
                    }
                    item = _queue.Dequeue();
                }
                try { ScanOne(item.Key, item.Value); didWork = true; }
                catch (Exception) { }                  // 单个进程出问题绝不影响整批
            }
            if (didWork)
            {
                _lastRaiseTick = 0;                    // 收尾这一波一定要通知，别被限流吞掉
                RaiseUpdated();
            }
        }

        private static void ScanOne(int pid, string name)
        {
            ProcSig sig = new ProcSig();
            sig.Name = name;
            string path = QueryPath(pid);
            sig.Path = path;

            if (string.IsNullOrEmpty(path))
            {
                sig.Kind = ProcKind.Protected;         // 读不到路径（系统进程/权限不足）
            }
            else
            {
                ProcSig cached;
                if (_pathSig.TryGetValue(path, out cached) && cached != null)
                {
                    sig.Kind = cached.Kind;
                    sig.Signer = cached.Signer;
                }
                else
                {
                    ProcSig fresh = ReadSignature(path);
                    fresh.Name = name;
                    sig.Kind = fresh.Kind;
                    sig.Signer = fresh.Signer;
                    _pathSig[path] = fresh;
                }
            }
            _pidSig[pid] = sig;
            if (_pidSig.Count > 4000) _pidSig.Clear();  // 长会话防膨胀

            RaiseUpdated();
        }

        /// <summary>限流：最多每 120ms 通知 UI 一次，避免几百次 Invalidate</summary>
        private static void RaiseUpdated()
        {
            int now = Environment.TickCount;
            int last = _lastRaiseTick;
            if (now - last < 120 && last != 0) return;
            _lastRaiseTick = now;
            Action h = Updated;
            if (h == null) return;
            try { h(); } catch (Exception) { }
        }

        // ---------- 签名 ----------

        /// <summary>读数字签名。不联网、不校验证书链，只取主体判断「谁签的」。</summary>
        private static ProcSig ReadSignature(string path)
        {
            ProcSig s = new ProcSig();
            s.Path = path;
            s.Kind = ProcKind.Unsigned;

            // 网络路径不碰：读签名要走网络 IO，可能卡几秒
            if (path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                s.Signer = "";
                return s;
            }
            try
            {
                if (!File.Exists(path)) return s;      // 文件已被删（进程还在跑）→ 按未签名处理
                X509Certificate cert = X509Certificate.CreateFromSignedFile(path);
                string subject = cert == null ? null : cert.Subject;
                s.Signer = SignerFromSubject(subject);
                s.Kind = ClassifySubject(subject);
            }
            catch (Exception)
            {
                s.Kind = ProcKind.Unsigned;            // CryptographicException：没有签名
            }
            return s;
        }

        /// <summary>按证书主体判类型（纯函数，测试可直接断言）</summary>
        internal static ProcKind ClassifySubject(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return ProcKind.Unsigned;
            string s = subject.ToLowerInvariant();
            if (s.IndexOf("microsoft", StringComparison.Ordinal) >= 0) return ProcKind.Microsoft;
            for (int i = 0; i < VendorSignerKeys.Length; i++)
                if (s.IndexOf(VendorSignerKeys[i], StringComparison.Ordinal) >= 0) return ProcKind.Vendor;
            return ProcKind.Signed;
        }

        /// <summary>从证书主体里取出 O=（公司名），取不到退 CN=</summary>
        internal static string SignerFromSubject(string subject)
        {
            if (string.IsNullOrEmpty(subject)) return "";
            string cn = "";
            string[] parts = subject.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = Unquote(p.Substring(2));
                    if (v.Length > 0) return v;
                }
                if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) && cn.Length == 0)
                    cn = Unquote(p.Substring(3));
            }
            return cn;
        }

        private static string Unquote(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                v = v.Substring(1, v.Length - 2);
            return v.Replace("\"\"", "\"").Trim();
        }

        // ---------- 名字归一与特征库匹配 ----------

        internal static string Norm(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.Trim().ToLowerInvariant();
            if (s.EndsWith(".exe", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 4);
            return s;
        }

        internal static bool IsMicrosoftName(string name)
        {
            string nm = Norm(name);
            for (int i = 0; i < MicrosoftNames.Length; i++)
                if (MicrosoftNames[i] == nm) return true;
            return false;
        }

        internal static bool IsVendorName(string name)
        {
            string nm = Norm(name);
            for (int i = 0; i < VendorNames.Length; i++)
                if (VendorNames[i] == nm) return true;
            return false;
        }

        /// <summary>特征库里有没有中文说明（测试与悬停都用它）</summary>
        internal static bool HasExplain(string name)
        {
            return Explain.ContainsKey(Norm(name));
        }

        internal static string ExplainOf(string name)
        {
            string v;
            return Explain.TryGetValue(Norm(name), out v) ? v : "";
        }

        private static ProcSig Lookup(int pid, string nm)
        {
            ProcSig s;
            if (_pidSig.TryGetValue(pid, out s) && s != null && s.Name == nm) return s;
            return null;
        }

        // ---------- 路径（低权限通道） ----------
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>QueryFullProcessImageName：0.04ms 一次，普通权限能读到自己账户下所有进程的路径</summary>
        internal static string QueryPath(int pid)
        {
            if (pid <= 0) return "";
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) return "";
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                return sb.ToString(0, size);
            }
            catch (Exception) { return ""; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }
    }
}
