using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NetEye
{
    /// <summary>
    /// v0.7：exe 同目录 config.ini 的极简读取器（纯 C# 手写，零第三方依赖）。
    ///
    /// 目前只有一个配置项 IpQueryUrl：右键「查询远程 IP 归属地」时浏览器打开的网址。
    /// 设计原则：配置属于锦上添花——文件坏了 / 没权限 / 编码乱 / 值非法，
    /// 一律退回内置默认值，绝不打断启动，更不会让右键查询失效。
    ///
    /// 网址拼接规则（见 BuildUrl）：
    ///   1) 值里有 {ip} 占位符 → 直接替换（大小写不敏感，多个占位符全部替换）；
    ///   2) 没有占位符 → 当成前缀，目标 IP 拼在末尾（默认的 ip138 就是这种）；
    ///   3) 只认 http:// 与 https://，其它一律视为非法并用内置默认值。
    /// </summary>
    internal sealed class AppConfig
    {
        /// <summary>内置默认查询网址（ip138，拼 IP 在最末尾）</summary>
        public const string DefaultIpQueryUrl = "https://www.ip138.com/iplookup.asp?ip=";

        private const string KeyIpQueryUrl = "IpQueryUrl";
        private const string FileName = "config.ini";

        /// <summary>测试专用：指向一次性沙箱 ini（生产恒为 null，走 exe 同目录）</summary>
        internal static string PathOverride = null;

        private string _ipQueryUrl = DefaultIpQueryUrl;
        private DateTime _stamp = DateTime.MinValue;

        // v0.9.3：全部键的最新值（同名取最后一条、大小写不敏感）。
        // IpQueryUrl 走专属属性保持 v0.7 语义，其余键（RowCompact / LogNewConn / MatchMode /
        // ColW0..ColW7）用 GetValue 读、SetKey 写，注释与无关行永远原样保留。
        private readonly Dictionary<string, string> _keys =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前生效的查询网址模板（永远是「可用」的，非法值已回落默认）</summary>
        public string IpQueryUrl { get { return _ipQueryUrl; } }

        /// <summary>v0.9.3：读任意键（先按文件时间戳做无谓 IO 缓存，再查内存表）</summary>
        public string GetValue(string key, string fallback)
        {
            Reload(false);
            string v;
            if (!_keys.TryGetValue(key, out v)) return fallback;
            v = (v ?? "").Trim();
            return v.Length == 0 ? fallback : v;
        }

        /// <summary>v0.9.3：读布尔键（1/true/on 开，其余关；非法一律回落默认）</summary>
        public bool GetBool(string key, bool fallback)
        {
            string v = GetValue(key, null);
            if (v == null) return fallback;
            if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        /// <summary>v0.9.3：读整数键（越界/非数字一律回落默认）</summary>
        public int GetInt(string key, int fallback, int min, int max)
        {
            string v = GetValue(key, null);
            if (v == null) return fallback;
            int n;
            if (!int.TryParse(v, out n)) return fallback;
            if (n < min || n > max) return fallback;
            return n;
        }

        /// <summary>配置里写的原始值是否非法（用于界面友好提示）</summary>
        public bool ValueInvalid { get; private set; }

        public string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(PathOverride)) return PathOverride;
                return Path.Combine(Application.StartupPath, FileName);
            }
        }

        // ================= 默认文件内容 =================
        /// <summary>首次运行时写出去的默认 config.ini（与发布包里那份内容一致）</summary>
        public static string DefaultIniText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("; NetEye 网络透视镜 v0.9.3 配置文件（推荐用界面上的「⚙ 设置」改；直接编辑本文件也生效）\r\n");
            sb.Append("; 编码建议 UTF-8；每行一条 key=value，分号开头的行是注释。\r\n");
            sb.Append("; \r\n");
            sb.Append("; IpQueryUrl：右键「查询远程 IP 归属地」时用系统浏览器打开的网址。\r\n");
            sb.Append(";   支持 {ip} 占位符，例如 https://www.ip.cn/?ip={ip}\r\n");
            sb.Append(";   不写占位符也行，目标 IP 会自动拼在网址末尾（默认值就是这种写法）。\r\n");
            sb.Append(";   只支持 http:// 或 https:// 开头的网址，写别的会退回内置 ip138。\r\n");
            sb.Append(";   注意：exe 同目录放了 qqwry.dat 时优先本地离线解析，压根不打开浏览器。\r\n");
            sb.Append(KeyIpQueryUrl + "=" + DefaultIpQueryUrl + "\r\n");
            sb.Append("; \r\n");
            sb.Append("; 以下键由界面自动维护（v0.9.3 起），删掉后下次改动会自动重建：\r\n");
            sb.Append("; RowCompact=0/1        行高：0 宽松 1 紧凑（右键菜单切换）\r\n");
            sb.Append("; MatchMode=0/1/2       搜索匹配：0 包含 1 精确 2 正则\r\n");
            sb.Append("; LogNewConn=0/1        记录模式：1 时把新出现的连接追加到 logs\\NetEye_连接_日期.csv\r\n");
            sb.Append("; ColW0..ColW7=像素      各列宽度（逻辑像素），拖动列宽后自动写入，下次打开原样恢复\r\n");
            return sb.ToString();
        }

        /// <summary>没有配置文件就写一份默认的；返回是否真的创建了文件</summary>
        public bool EnsureFile()
        {
            try
            {
                string p = FilePath;
                if (p == null || p.Length == 0) return false;
                if (File.Exists(p)) return false;
                string dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) return false;
                File.WriteAllText(p, DefaultIniText(), new UTF8Encoding(true));
                return true;
            }
            catch (Exception)
            {
                // 只读目录（比如装在 Program Files）就内存里用默认值，不弹窗不崩溃
                return false;
            }
        }

        /// <summary>
        /// 读取配置。force=false 时按文件最后写入时间做缓存，
        /// 所以用户改完 ini 直接再点一次右键就生效，无需重启。
        /// </summary>
        public void Reload(bool force)
        {
            try
            {
                string p = FilePath;
                if (p == null || p.Length == 0 || !File.Exists(p))
                {
                    _ipQueryUrl = DefaultIpQueryUrl;
                    ValueInvalid = false;
                    _stamp = DateTime.MinValue;
                    _keys.Clear();
                    _keys[KeyIpQueryUrl] = DefaultIpQueryUrl;
                    return;
                }
                DateTime st = File.GetLastWriteTimeUtc(p);
                if (!force && st == _stamp) return;
                _stamp = st;

                string raw = null;
                _keys.Clear();
                foreach (string line in ReadLines(p))
                {
                    string s = line == null ? "" : line.Trim();
                    if (s.Length == 0) continue;
                    if (s[0] == ';' || s[0] == '#' || s[0] == '[') continue;   // 注释 / 节
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = s.Substring(0, eq).Trim();
                    string v = s.Substring(eq + 1).Trim();                     // 同名取最后一条
                    _keys[k] = v;
                    if (!string.Equals(k, KeyIpQueryUrl,
                            StringComparison.OrdinalIgnoreCase)) continue;
                    raw = v;
                }
                if (!_keys.ContainsKey(KeyIpQueryUrl)) _keys[KeyIpQueryUrl] = DefaultIpQueryUrl;

                if (raw == null || raw.Length == 0)
                {
                    _ipQueryUrl = DefaultIpQueryUrl;                          // 没写就用默认
                    ValueInvalid = false;
                    return;
                }
                ValueInvalid = !IsSafeUrl(raw);
                _ipQueryUrl = ValueInvalid ? DefaultIpQueryUrl : raw;
            }
            catch (Exception)
            {
                _ipQueryUrl = DefaultIpQueryUrl;
                ValueInvalid = false;
                _keys.Clear();
                _keys[KeyIpQueryUrl] = DefaultIpQueryUrl;
            }
        }

        /// <summary>
        /// v0.9.2：把「自定义查询网址」写回 config.ini（⚙ 设置窗点保存走这里）。
        ///
        /// 行为约定：
        ///   · 先校验再落盘，非法网址一个字节都不写（避免把好配置改成坏配置）；
        ///   · 已存在的 IpQueryUrl 行原地替换（多条同名全替换），注释、节、其它键原样保留；
        ///   · 文件里没有这一行就在末尾追加；
        ///   · 写 UTF-8 带 BOM + CRLF（Excel/记事本都不会乱码，程序自己也认）；
        ///   · 成功立刻 Reload，界面上马上生效，不用重启。
        /// </summary>
        /// <param name="url">用户输入的网址（可带 {ip} 占位符）</param>
        /// <param name="error">失败时给用户看的完整原因</param>
        public bool SaveIpQueryUrl(string url, out string error)
        {
            error = null;
            string u = url == null ? "" : url.Trim();
            if (u.Length == 0)
            {
                error = "网址不能为空。\n\n想恢复默认的话，点左下角「恢复默认」再保存。";
                return false;
            }
            if (!IsSafeUrl(u))
            {
                error = "这个网址不能用：\n\n" + u
                    + "\n\n只支持 http:// 或 https:// 开头的完整网址，中间不能有空格或换行。";
                return false;
            }
            return SetKey(KeyIpQueryUrl, u, out error);
        }

        /// <summary>
        /// v0.9.3：把任意键写回 config.ini（列宽记忆 / 行高 / 记录模式都走这里）。
        ///
        /// 行为约定（与 SaveIpQueryUrl 一致）：
        ///   · 已存在的同名行原地替换（多条全替换），注释、节、其它键原样保留；
        ///   · 文件里没有这一行就在末尾追加；
        ///   · 写 UTF-8 带 BOM + CRLF；成功立刻 Reload。
        /// key/value 来自程序自身（不含换行），仍防御性拒绝 value 里的换行与开头空白。
        /// </summary>
        public bool SetKey(string key, string value, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(key) || key.Trim().Length != key.Length || key.IndexOf('=') >= 0)
            {
                error = "配置键名非法。";
                return false;
            }
            string v = (value ?? "").Trim();
            if (v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0 || v.IndexOf(';') == 0)
            {
                error = "配置值不能包含换行或以分号开头。";
                return false;
            }

            string p = FilePath;
            if (string.IsNullOrEmpty(p))
            {
                error = "拿不到 config.ini 的路径，无法保存。";
                return false;
            }

            try
            {
                EnsureFile();   // 没有就先建一份带注释的默认文件，别写出一个光秃秃只有一行 key 的 ini
                List<string> lines = new List<string>();
                if (File.Exists(p))
                {
                    string text = ReadTextSmart(p);
                    string[] raw = text.Split('\n');
                    for (int i = 0; i < raw.Length; i++) lines.Add(raw[i].TrimEnd('\r'));
                    while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                }

                bool replaced = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0 || s[0] == ';' || s[0] == '#' || s[0] == '[') continue;
                    int eq = s.IndexOf('=');
                    if (eq <= 0) continue;
                    if (!string.Equals(s.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    lines[i] = key + "=" + v;
                    replaced = true;
                }
                if (!replaced) lines.Add(key + "=" + v);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < lines.Count; i++) sb.Append(lines[i]).Append("\r\n");
                File.WriteAllText(p, sb.ToString(), new UTF8Encoding(true));
            }
            catch (UnauthorizedAccessException ex)
            {
                error = "没有写入权限，改不了 config.ini。\n\n" + p
                    + "\n\n把 NetEye.exe 放到可写目录（例如 D:\\Tools\\NetEye）再试。\n\n系统提示：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "写 config.ini 失败：\n" + ex.Message
                    + "\n\n路径：" + p;
                return false;
            }

            Reload(true);
            return true;
        }

        /// <summary>
        /// v0.9.2：按 BOM 猜编码读成文本（保存时要在原文件上只改一行，不能把注释弄丢）。
        /// 四种编码和 ReadLines 保持一致：UTF-8(带/不带 BOM)、UTF-16 LE/BE、ANSI(GBK)、系统默认。
        /// </summary>
        private static string ReadTextSmart(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
                return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (Exception) { }
            try { return Encoding.GetEncoding(936).GetString(b); }
            catch (Exception) { }
            return Encoding.Default.GetString(b);
        }

        // ================= 网址拼接（纯函数，便于测试） =================        /// <summary>
        /// 把配置里的模板拼成真正要打开的网址：
        /// 有 {ip} 就替换，没有就拼末尾；模板非法时自动用内置默认值。
        /// </summary>
        public static string BuildUrl(string template, string ip)
        {
            string escaped = Uri.EscapeDataString(ip == null ? "" : ip);
            string t = template == null ? "" : template.Trim();
            if (!IsSafeUrl(t)) t = DefaultIpQueryUrl;
            if (t.IndexOf("{ip}", StringComparison.OrdinalIgnoreCase) >= 0)
                return ReplaceNoCase(t, "{ip}", escaped);
            return t + escaped;
        }

        /// <summary>
        /// 只放行 http/https：既是功能需要，也是安全阀——
        /// 防止有人把 ini 改成 file:/// 或本地 exe 路径，让「查归属地」变成任意程序启动器。
        /// 同时拒绝空白字符，避免 URL 后面接参数骗过 ShellExecute。
        /// </summary>
        public static bool IsSafeUrl(string url)
        {
            if (url == null) return false;
            string u = url.Trim();
            if (u.Length < 8 || u.Length > 2000) return false;                 // 至少 "http://a"
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            for (int i = 0; i < u.Length; i++)
            {
                char c = u[i];
                if (c <= ' ' || c == (char)0x7F) return false;                 // 空白/控制字符
            }
            return true;
        }

        private static string ReplaceNoCase(string s, string token, string val)
        {
            StringBuilder sb = new StringBuilder(s.Length + val.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (i + token.Length <= s.Length
                    && string.Compare(s, i, token, 0, token.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    sb.Append(val);
                    i += token.Length;
                }
                else
                {
                    sb.Append(s[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 读文本行：兼容 UTF-8（带/不带 BOM）、UTF-16 LE/BE、以及老记事本存的 ANSI(GBK)，
        /// 中国人手改 ini 大概率就是这四种之一。
        /// </summary>
        private static string[] ReadLines(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
                return Encoding.UTF8.GetString(b, 3, b.Length - 3).Split('\n');
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
                return Encoding.Unicode.GetString(b, 2, b.Length - 2).Split('\n');
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2).Split('\n');
            try { return new UTF8Encoding(false, true).GetString(b).Split('\n'); }
            catch (Exception) { }
            try { return Encoding.GetEncoding(936).GetString(b).Split('\n'); }
            catch (Exception) { }
            return Encoding.Default.GetString(b).Split('\n');
        }
    }
}
