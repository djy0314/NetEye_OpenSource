using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NetEye
{
    /// <summary>
    /// NetEye 第一步探针：只验证底层取数能力，不做 UI。
    /// 用法：
    ///   NetEyeProbe.exe            打印前 20 条 TCP 连接 + 统计
    ///   NetEyeProbe.exe --all      打印全部
    ///   NetEyeProbe.exe --filter=douyin   按进程名/端口过滤
    ///   NetEyeProbe.exe --selftest 跑数据自检（异常返回 2）
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            bool all = false, selftest = false;
            string filter = null;
            foreach (string a in args)
            {
                if (string.Equals(a, "--all", StringComparison.OrdinalIgnoreCase)) all = true;
                else if (string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)) selftest = true;
                else if (a.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase)) filter = a.Substring(9);
            }

            Console.WriteLine("");
            Console.WriteLine("NetEye 探针 · 直接读 Windows TCP 连接表（GetExtendedTcpTable）");
            Console.WriteLine("权限: " + (IsAdmin() ? "管理员" : "普通用户") + "    系统: " + OsName());
            Console.WriteLine("");

            // 冷启动一次预热，测量第二次（避免 JIT 干扰耗时统计）
            NetTable.QueryTcp();

            Stopwatch sw = Stopwatch.StartNew();
            List<NetRow> rows = NetTable.QueryTcp();
            sw.Stop();
            double firstMs = sw.Elapsed.TotalMilliseconds;

            // 重复 20 次，单独计时统计最小/平均/最大值（含进程名解析的真实开销）
            double minMs = double.MaxValue, maxMs = 0, totalMs = 0;
            for (int i = 0; i < 20; i++)
            {
                Stopwatch t = Stopwatch.StartNew();
                NetTable.QueryTcp();
                t.Stop();
                double ms = t.Elapsed.TotalMilliseconds;
                totalMs += ms;
                if (ms < minMs) minMs = ms;
                if (ms > maxMs) maxMs = ms;
            }
            double avgMs = totalMs / 20.0;

            Console.WriteLine("取数耗时: 冷启动 " + firstMs.ToString("F1") + " ms | 热态 20 次 平均 "
                + avgMs.ToString("F1") + " / 最小 " + minMs.ToString("F2") + " / 最大 " + maxMs.ToString("F2") + " ms");
            Console.WriteLine("内存占用: " + (GC.GetTotalMemory(false) / 1024).ToString() + " KB（托管堆，含 "
                + rows.Count + " 条连接对象）");
            Console.WriteLine("连接总数: " + rows.Count + "（IPv4 "
                + CountProto(rows, "TCP") + " / IPv6 " + CountProto(rows, "TCPv6") + "）");
            Console.WriteLine("");

            PrintStateSummary(rows);

            List<NetRow> shown = rows;
            if (!string.IsNullOrEmpty(filter))
            {
                string f = filter.ToLowerInvariant();
                shown = new List<NetRow>();
                foreach (NetRow r in rows)
                {
                    if (r.ProcessName.ToLowerInvariant().IndexOf(f) >= 0
                        || r.LocalPort.ToString().IndexOf(f) >= 0
                        || r.RemotePort.ToString().IndexOf(f) >= 0
                        || r.LocalAddress.IndexOf(f) >= 0
                        || r.RemoteAddress.IndexOf(f) >= 0)
                        shown.Add(r);
                }
                Console.WriteLine("过滤 \"" + filter + "\" 命中 " + shown.Count + " 条：");
            }

            int limit = all ? shown.Count : Math.Min(20, shown.Count);
            Console.WriteLine("列出 " + limit + " 条（--all 看全部）：");
            Console.WriteLine("");
            Console.WriteLine(Pad("协议", 7) + Pad("本地地址:端口", 27) + Pad("远程地址:端口", 27)
                + Pad("状态", 15) + Pad("PID", 8) + "进程");
            Console.WriteLine(new string('-', 96));
            for (int i = 0; i < limit; i++)
            {
                NetRow r = shown[i];
                Console.WriteLine(Pad(r.Protocol, 7) + Pad(r.LocalEndPoint, 27) + Pad(r.RemoteEndPoint, 27)
                    + Pad(r.StateText, 15) + Pad(r.Pid.ToString(), 8)
                    + (r.ProcessName.Length > 0 ? r.ProcessName
                        : (r.Pid == 0 ? "-（内核持有，无属主）" : "<无权限读取>")));
            }
            Console.WriteLine("");

            if (selftest)
            {
                int fails = SelfTest(rows);
                Console.WriteLine(fails == 0 ? "SELFTEST: ALL PASS（" + rows.Count + " 条样本）"
                    : "SELFTEST: " + fails + " FAIL");
                return fails == 0 ? 0 : 2;
            }
            return 0;
        }

        /// <summary>内建断言：判据全部来自 Windows 协议规范，不靠「看起来对」</summary>
        private static int SelfTest(List<NetRow> rows)
        {
            int fails = 0;
            Action<bool, string> ok = delegate (bool cond, string label)
            {
                if (!cond) fails++;
                Console.WriteLine((cond ? "PASS | " : "FAIL | ") + label);
            };

            ok(rows.Count > 0, "至少取到 1 条连接（实际 " + rows.Count + "）");

            int listen = 0, badPort = 0, badPid = 0, badState = 0, badAddr = 0;
            int named = 0, ownable = 0, orphanActive = 0;
            foreach (NetRow r in rows)
            {
                if (r.State == TcpState.Listen) listen++;
                if (r.LocalPort < 0 || r.LocalPort > 65535
                    || r.RemotePort < 0 || r.RemotePort > 65535) badPort++;
                if (r.Pid < 0) badPid++;
                if ((int)r.State < 1 || (int)r.State > 12) badState++;
                if (!ValidAddr(r.LocalAddress) || !ValidAddr(r.RemoteAddress)) badAddr++;
                // TIME_WAIT / CLOSED 这类已无归属的套接字由内核持有，PID 本身就是 0，
                // 不能算进「进程名解析率」的分母，否则判据失真
                if (r.Pid > 0)
                {
                    ownable++;
                    if (r.ProcessName.Length > 0) named++;
                }
                else if (r.State == TcpState.Established || r.State == TcpState.Listen)
                {
                    orphanActive++;
                }
            }
            ok(listen > 0, "存在 LISTENING 连接（" + listen + " 条，本机必然有监听端口）");
            ok(badPort == 0, "全部端口落在 0..65535（越界 " + badPort + " 条）");
            ok(badPid == 0, "PID 全部非负（异常 " + badPid + " 条）");
            ok(badState == 0, "状态值全部落在 1..12 的 MIB_TCP_STATE 区间（异常 " + badState + " 条）");
            ok(badAddr == 0, "地址字段全部可解析（异常 " + badAddr + " 条）");
            ok(ownable > 0 && named * 100 >= ownable * 95,
                "有属主连接（PID>0）的进程名解析率 ≥95%（" + named + "/" + ownable + "）");
            ok(orphanActive == 0,
                "ESTABLISHED / LISTENING 必有属主 PID（无主异常 " + orphanActive + " 条）");

            // 字节序回归：IPv4 不能出现倒序地址（真实机器上不会出现 1.0.0.127 这种公网/保留段）
            int reversed = 0;
            foreach (NetRow r in rows)
            {
                if (r.Protocol == "TCP" && r.LocalAddress.StartsWith("1.0.0.")) reversed++;
            }
            ok(reversed == 0, "IPv4 字节序正确（无 1.0.0.x 倒序假地址）");
            return fails;
        }

        private static bool ValidAddr(string addr)
        {
            if (string.IsNullOrEmpty(addr)) return false;
            if (addr.IndexOf(':') >= 0) return true;           // IPv6 交给 IPAddress 解析过

            string[] parts = addr.Split('.');
            if (parts.Length != 4) return false;
            foreach (string p in parts)
            {
                int v;
                if (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return false;
                if (v < 0 || v > 255) return false;
            }
            return true;
        }

        private static int CountProto(List<NetRow> rows, string proto)
        {
            int n = 0;
            foreach (NetRow r in rows) if (r.Protocol == proto) n++;
            return n;
        }

        private static void PrintStateSummary(List<NetRow> rows)
        {
            Dictionary<TcpState, int> dict = new Dictionary<TcpState, int>();
            foreach (NetRow r in rows)
            {
                int c;
                dict.TryGetValue(r.State, out c);
                dict[r.State] = c + 1;
            }
            StringBuilder sb = new StringBuilder("状态分布: ");
            foreach (KeyValuePair<TcpState, int> kv in dict)
                sb.Append(NetTable.StateName(kv.Key)).Append("=").Append(kv.Value).Append("  ");
            Console.WriteLine(sb.ToString());
            Console.WriteLine("");
        }

        /// <summary>按显示宽度补空格（中文占两列，这里只有 ASCII，够用）</summary>
        private static string Pad(string s, int width)
        {
            if (s == null) s = "";
            if (s.Length >= width) return s + " ";
            return s.PadRight(width);
        }

        /// <summary>
        /// 拿真实系统版本。Environment.OSVersion 在没有 Win10 兼容性清单时会降级成 6.2，
        /// 所以读注册表 CurrentBuild 判断（build ≥ 22000 即 Windows 11）。
        /// </summary>
        private static string OsName()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (k != null)
                    {
                        object p = k.GetValue("ProductName");
                        object b = k.GetValue("CurrentBuild");
                        object u = k.GetValue("UBR");
                        if (p != null && b != null)
                        {
                            string name = p.ToString();
                            int build;
                            if (int.TryParse(b.ToString(), out build) && build >= 22000)
                                name = name.Replace("Windows 10", "Windows 11");
                            return name + "  build " + b.ToString() + "." + (u == null ? "0" : u.ToString());
                        }
                    }
                }
            }
            catch { }
            return Environment.OSVersion.VersionString;
        }

        private static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsIdentity id =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal p =
                    new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
