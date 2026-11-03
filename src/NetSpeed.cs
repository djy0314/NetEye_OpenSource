using System;
using System.Globalization;
using System.Net.NetworkInformation;

namespace NetEye
{
    /// <summary>
    /// v0.9.2：全机网速（状态栏「↑ 12 KB/s  ↓ 150 KB/s」）。
    ///
    /// 数据来源是 .NET 自带的 NetworkInterface（底层就是 GetIfTable2 那一层的累计字节数），
    /// 不装驱动、不需要管理员、不引第三方库：把「所有物理/虚拟网卡」的累计收/发字节数加起来，
    /// 两次采样相减再除以真实间隔，就是全机瞬时速率。
    ///
    /// 几个必须处理的现实情况：
    ///   1) 有些网卡（VPN、虚拟网卡、已拔线的适配器）读统计会抛 NetworkInformationException
    ///      —— 单块网卡失败就跳过它，绝不让整台机器的网速变成「读数失败」。
    ///   2) 只统计 OperationalStatus.Up 的网卡，Loopback / Tunnel 不算（否则隧道会重复计数，
    ///      而回环流量根本不是「网速」）。
    ///   3) 计时用 Stopwatch 真实间隔而不是假设刚好 1000ms —— 系统忙的时候定时器会迟到，
    ///      直接除以 1 秒会把速率算错。
    /// </summary>
    internal static class NetSpeed
    {
        private static ulong _lastRx;
        private static ulong _lastTx;
        private static long _lastTicks;
        private static bool _hasBaseline;

        // v0.9.3：速率历史（网速悬停折线小图用）。只在 UI 线程读写，不加锁。
        private const int HistCap = 120;
        private static readonly double[] _histDown = new double[HistCap];
        private static readonly double[] _histUp = new double[HistCap];
        private static int _histPos;     // 下一帧写入位置
        private static int _histCount;   // 已有样本数（最多 HistCap）

        /// <summary>
        /// v0.9.3：把最近 min(count, cap) 个样本按「旧→新」拷进调用方的缓冲区。
        /// down/up 数组长度至少 HistCap；返回有效样本数（第 0 个最旧）。
        /// </summary>
        internal static int SnapshotHistory(double[] down, double[] up)
        {
            int n = _histCount;
            for (int i = 0; i < n; i++)
            {
                int src = (_histPos - n + i + HistCap * 2) % HistCap;
                if (down != null) down[i] = _histDown[src];
                if (up != null) up[i] = _histUp[src];
            }
            return n;
        }

        private static void PushHistory(double down, double up)
        {
            _histDown[_histPos] = down;
            _histUp[_histPos] = up;
            _histPos = (_histPos + 1) % HistCap;
            if (_histCount < HistCap) _histCount++;
        }

        /// <summary>
        /// IPv4+IPv6 合计统计的入口（.NET 4.7.2+ 才有）。用反射探一次并缓存：
        /// 编译期不写死这个 API，跑在只有 4.0 运行时的机器上也不会 MissingMethodException。
        /// </summary>
        private static readonly System.Reflection.MethodInfo IpStatsMethod =
            typeof(NetworkInterface).GetMethod("GetIPStatistics");

        /// <summary>最近一次采样得到的下载速率（字节/秒）</summary>
        internal static double DownBytesPerSec { get; private set; }

        /// <summary>最近一次采样得到的上传速率（字节/秒）</summary>
        internal static double UpBytesPerSec { get; private set; }

        /// <summary>测试用：清掉基线，下一次调用会重新只记录基准、不产生速率</summary>
        internal static void Reset()
        {
            _lastRx = 0;
            _lastTx = 0;
            _lastTicks = 0;
            _hasBaseline = false;
            DownBytesPerSec = 0;
            UpBytesPerSec = 0;
            _histPos = 0;
            _histCount = 0;
        }

        /// <summary>
        /// 采样一次。第一次调用只记基准并返回 false（此时速率视为 0）；
        /// 之后每次返回 true 并更新 Up/DownBytesPerSec。
        /// 任何一个接口出错都不会让本方法抛异常。
        /// </summary>
        internal static bool Sample()
        {
            ulong rx, tx;
            if (!ReadTotals(out rx, out tx)) return false;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ok = false;
            if (_hasBaseline && _lastTicks > 0 && now > _lastTicks)
            {
                double secs = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (secs > 0.001)
                {
                    // 计数器理论上只增不减；真遇到网卡重连导致回退，就按 0 算，绝不给负数
                    ulong dRx = rx >= _lastRx ? rx - _lastRx : 0;
                    ulong dTx = tx >= _lastTx ? tx - _lastTx : 0;
                    DownBytesPerSec = dRx / secs;
                    UpBytesPerSec = dTx / secs;
                    ok = true;
                }
            }
            _lastRx = rx;
            _lastTx = tx;
            _lastTicks = now;
            _hasBaseline = true;
            // v0.9.3：基线阶段速率为 0 也推一帧，让小图的时间轴从一开始就是均匀的
            PushHistory(ok ? DownBytesPerSec : 0, ok ? UpBytesPerSec : 0);
            return ok;
        }

        /// <summary>累加所有「在线且非回环/非隧道」网卡的收发字节数；读不到统计的网卡直接跳过</summary>
        internal static bool ReadTotals(out ulong rx, out ulong tx)
        {
            rx = 0;
            tx = 0;
            bool any = false;
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception) { return false; }
            if (nics == null) return false;

            for (int i = 0; i < nics.Length; i++)
            {
                NetworkInterface ni = nics[i];
                if (ni == null) continue;
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    // 优先用 GetIPStatistics（IPv4+IPv6 合计，.NET 4.7.2 起才有）：
                    // 只取 IPv4 会在纯 IPv6 线路上显示 0。老系统上这个方法不存在，
                    // 反射一探就知道，探测不到就退回 GetIPv4Statistics（.NET 2.0 就有）。
                    ulong irx = 0, itx = 0;
                    bool got = false;
                    System.Reflection.MethodInfo mi = IpStatsMethod;
                    if (mi != null)
                    {
                        try
                        {
                            object st = mi.Invoke(ni, null);
                            if (st != null)
                            {
                                irx += ToU64(st.GetType().GetProperty("BytesReceived").GetValue(st, null));
                                itx += ToU64(st.GetType().GetProperty("BytesSent").GetValue(st, null));
                                got = true;
                            }
                        }
                        catch (Exception) { }   // 单块网卡统计读失败：退回 IPv4
                    }
                    if (!got)
                    {
                        IPv4InterfaceStatistics s4 = ni.GetIPv4Statistics();
                        if (s4 != null) { irx += ToU64(s4.BytesReceived); itx += ToU64(s4.BytesSent); }
                    }

                    rx += irx;
                    tx += itx;
                    any = true;
                }
                catch (Exception) { }   // 虚拟网卡/VPN 常见：单块跳过
            }
            return any;
        }

        /// <summary>统计值统一转 ulong（框架里是 long，反射取出来是 object，两种都得认）</summary>
        private static ulong ToU64(object v)
        {
            if (v == null) return 0;
            try
            {
                if (v is ulong) return (ulong)v;
                if (v is long) { long l = (long)v; return l > 0 ? (ulong)l : 0; }
                if (v is uint) return (uint)v;
                if (v is int) { int i = (int)v; return i > 0 ? (ulong)i : 0; }
                return Convert.ToUInt64(v, CultureInfo.InvariantCulture);
            }
            catch (Exception) { return 0; }
        }

        /// <summary>
        /// 速率文本：0 B/s、512 B/s、12 KB/s、1.4 MB/s、2.0 GB/s。        /// 1 KB 以下保留整数字节；KB 级取整（跟 CurrPorts 那种一眼能读的风格一致）；
        /// MB/GB 级留一位小数，免得大流量时全是「1 MB/s」看不出差别。
        /// </summary>
        internal static string FormatRate(double bytesPerSec)
        {
            if (bytesPerSec < 0 || double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec))
                bytesPerSec = 0;
            if (bytesPerSec < 1024) return ((long)Math.Round(bytesPerSec)).ToString() + " B/s";
            double kb = bytesPerSec / 1024.0;
            if (kb < 1024)
            {
                long r = (long)Math.Round(kb);
                if (r >= 1024) return "1.0 MB/s";       // 1023.6 KB 四舍五入后别显示成 1024 KB/s
                return r.ToString() + " KB/s";
            }
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("F1", CultureInfo.InvariantCulture) + " MB/s";
            return (mb / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " GB/s";
        }

        /// <summary>状态栏一整格：↑ 上传  ↓ 下载（箭头在前，跟论坛老哥给的样式一致）</summary>
        internal static string FormatPair(double up, double down)
        {
            return "↑ " + FormatRate(up) + "  ↓ " + FormatRate(down);
        }

        /// <summary>当前速率的一整格文本</summary>
        internal static string CurrentText()
        {
            return FormatPair(UpBytesPerSec, DownBytesPerSec);
        }

        /// <summary>
        /// 状态栏宽度基准串：数字全用 8，字母选最宽的一种单位，保证数字变大变小时格子不抖。
        /// </summary>
        internal const string WidthProbe = "↑ 888.8 MB/s  ↓ 888.8 MB/s";
    }
}
