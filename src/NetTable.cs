using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace NetEye
{
    /// <summary>TCP 连接状态（对应 Windows SDK iptypes.h 的 MIB_TCP_STATE 枚举，数值不可改）</summary>
    internal enum TcpState
    {
        Closed = 1,
        Listen = 2,
        SynSent = 3,
        SynReceived = 4,
        Established = 5,
        FinWait1 = 6,
        FinWait2 = 7,
        CloseWait = 8,
        Closing = 9,
        LastAck = 10,
        TimeWait = 11,
        DeleteTcb = 12
    }

    /// <summary>一条连接的展示模型（与协议无关，UI 层直接用）</summary>
    internal class NetRow
    {
        public string Protocol;          // TCP / TCPv6 / UDP / UDPv6
        public string LocalAddress;
        public int LocalPort;
        public string RemoteAddress;
        public int RemotePort;
        public TcpState State;
        public int Pid;
        public string ProcessName;       // 取不到时为空串
        public string StateLabel;        // UDP 没有连接状态，用它覆盖显示（TCP 为 null）

        /// <summary>
        /// v0.9.2：占用这条连接的进程的启动时间（本地时间）。null = 读不到
        /// （System/受保护进程、已退出），界面显示「-」，排序时永远垫底。
        /// 由 ProcAge.Annotate 在刷新时统一填，虚拟模式取单元格只做减法，不再回查系统。
        /// </summary>
        public DateTime? StartedLocal;

        /// <summary>读不到进程名时的兜底文案：绝不因为权限不足让界面出空白或崩掉</summary>
        public const string UnknownProcess = "[系统/受保护进程]";

        public bool IsUdp { get { return Protocol != null && Protocol.StartsWith("UDP"); } }
        public string StateText { get { return StateLabel != null ? StateLabel : NetTable.StateName(State); } }
        public string LocalEndPoint { get { return NetTable.FormatEndPoint(LocalAddress, LocalPort); } }
        public string RemoteEndPoint
        {
            get
            {
                // UDP 无连接，netstat 同样显示 *:*
                if (IsUdp) return "*:*";
                return NetTable.FormatEndPoint(RemoteAddress, RemotePort);
            }
        }
        public string ProcessDisplay
        {
            get { return string.IsNullOrEmpty(ProcessName) ? UnknownProcess : ProcessName; }
        }
    }

    /// <summary>
    /// 连接表取数：P/Invoke GetExtendedTcpTable（iphlpapi.dll）。
    /// 这是 Windows Vista 以后官方提供的 API，普通用户权限即可读取完整连接表（含所属 PID），
    /// 不需要管理员、不装驱动、不引用任何第三方库。
    /// </summary>
    internal static class NetTable
    {
        // iptypes.h / winsock2.h 里的常量
        private const int AF_INET = 2;
        private const int AF_INET6 = 23;
        private const int TCP_TABLE_OWNER_PID_ALL = 5;
        private const int UDP_TABLE_OWNER_PID = 1;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPTABLE_OWNER_PID
        {
            public int dwNumEntries;     // 表头只有一个 DWORD，行数据紧跟在后面
        }

        /// <summary>IPv4 的一行，6 个 DWORD，24 字节</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint dwState;
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwRemoteAddr;
            public uint dwRemotePort;
            public uint dwOwningPid;
        }

        /// <summary>IPv6 的一行：两个 16 字节地址 + 各自 scope id + 端口 + 状态 + PID，56 字节</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucLocalAddr;
            public uint dwLocalScopeId;
            public uint dwLocalPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucRemoteAddr;
            public uint dwRemoteScopeId;
            public uint dwRemotePort;
            public uint dwState;
            public uint dwOwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize,
            bool bOrder, int ulAf, int tableClass, uint reserved);

        // ---------- UDP 表（GetExtendedUdpTable）----------
        // UDP 没有连接状态和远程端点，行结构比 TCP 少两个字段
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDPROW_OWNER_PID
        {
            public uint dwLocalAddr;
            public uint dwLocalPort;
            public uint dwOwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_UDP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] ucLocalAddr;
            public uint dwLocalScopeId;
            public uint dwLocalPort;
            public uint dwOwningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize,
            bool bOrder, int ulAf, int tableClass, uint reserved);

        /// <summary>取全部 TCP 连接（IPv4 + IPv6），按本地端口升序</summary>
        public static List<NetRow> QueryTcp()
        {
            List<NetRow> rows = new List<NetRow>();
            rows.AddRange(QueryTcpV4());
            rows.AddRange(QueryTcpV6());
            rows.Sort(delegate (NetRow a, NetRow b) { return a.LocalPort.CompareTo(b.LocalPort); });
            return rows;
        }

        /// <summary>取全部 UDP 端点（IPv4 + IPv6）</summary>
        public static List<NetRow> QueryUdp()
        {
            List<NetRow> rows = new List<NetRow>();
            rows.AddRange(QueryUdpV4());
            rows.AddRange(QueryUdpV6());
            rows.Sort(delegate (NetRow a, NetRow b) { return a.LocalPort.CompareTo(b.LocalPort); });
            return rows;
        }

        /// <summary>UI 用：TCP + UDP 一次取全，按协议分组、组内按本地端口升序</summary>
        public static List<NetRow> QueryAll()
        {
            List<NetRow> rows = new List<NetRow>();
            rows.AddRange(QueryTcp());
            rows.AddRange(QueryUdp());
            return rows;
        }

        private static List<NetRow> QueryUdpV4()
        {
            List<NetRow> rows = new List<NetRow>();
            int size = 0;
            uint ret = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0) return rows;
            if (size <= 0) return rows;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedUdpTable(buf, ref size, false, AF_INET, UDP_TABLE_OWNER_PID, 0);
                if (ret != 0) return rows;

                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(MIB_UDPROW_OWNER_PID));
                long p = buf.ToInt64() + Marshal.SizeOf(typeof(MIB_TCPTABLE_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    MIB_UDPROW_OWNER_PID r = (MIB_UDPROW_OWNER_PID)Marshal.PtrToStructure(
                        new IntPtr(p), typeof(MIB_UDPROW_OWNER_PID));
                    NetRow row = new NetRow();
                    row.Protocol = "UDP";
                    row.LocalAddress = IPv4Text(r.dwLocalAddr);
                    row.LocalPort = PortOf(r.dwLocalPort);
                    row.RemoteAddress = "*";
                    row.RemotePort = 0;
                    row.StateLabel = "—";
                    row.State = TcpState.Closed;
                    row.Pid = (int)r.dwOwningPid;
                    row.ProcessName = ProcessNameOf(row.Pid);
                    rows.Add(row);
                    p += rowSize;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        private static List<NetRow> QueryUdpV6()
        {
            List<NetRow> rows = new List<NetRow>();
            int size = 0;
            uint ret = GetExtendedUdpTable(IntPtr.Zero, ref size, false, AF_INET6, UDP_TABLE_OWNER_PID, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0) return rows;
            if (size <= 0) return rows;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedUdpTable(buf, ref size, false, AF_INET6, UDP_TABLE_OWNER_PID, 0);
                if (ret != 0) return rows;

                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(MIB_UDP6ROW_OWNER_PID));
                long p = buf.ToInt64() + Marshal.SizeOf(typeof(MIB_TCPTABLE_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    MIB_UDP6ROW_OWNER_PID r = (MIB_UDP6ROW_OWNER_PID)Marshal.PtrToStructure(
                        new IntPtr(p), typeof(MIB_UDP6ROW_OWNER_PID));
                    NetRow row = new NetRow();
                    row.Protocol = "UDPv6";
                    row.LocalAddress = IPv6Text(r.ucLocalAddr, r.dwLocalScopeId);
                    row.LocalPort = PortOf(r.dwLocalPort);
                    row.RemoteAddress = "*";
                    row.RemotePort = 0;
                    row.StateLabel = "—";
                    row.State = TcpState.Closed;
                    row.Pid = (int)r.dwOwningPid;
                    row.ProcessName = ProcessNameOf(row.Pid);
                    rows.Add(row);
                    p += rowSize;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        private static List<NetRow> QueryTcpV4()
        {
            List<NetRow> rows = new List<NetRow>();
            int size = 0;
            // 第一次调用只拿所需缓冲区大小（必然返回 122 ERROR_INSUFFICIENT_BUFFER）
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0)
                throw new Win32Exception((int)ret, "GetExtendedTcpTable(IPv4) 探测缓冲区失败");
            if (size <= 0) return rows;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) throw new Win32Exception((int)ret, "GetExtendedTcpTable(IPv4) 取表失败");

                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCPROW_OWNER_PID));
                long p = buf.ToInt64() + Marshal.SizeOf(typeof(MIB_TCPTABLE_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    MIB_TCPROW_OWNER_PID r = (MIB_TCPROW_OWNER_PID)Marshal.PtrToStructure(
                        new IntPtr(p), typeof(MIB_TCPROW_OWNER_PID));
                    NetRow row = new NetRow();
                    row.Protocol = "TCP";
                    row.LocalAddress = IPv4Text(r.dwLocalAddr);
                    row.LocalPort = PortOf(r.dwLocalPort);
                    row.RemoteAddress = IPv4Text(r.dwRemoteAddr);
                    row.RemotePort = PortOf(r.dwRemotePort);
                    row.State = (TcpState)r.dwState;
                    row.Pid = (int)r.dwOwningPid;
                    row.ProcessName = ProcessNameOf(row.Pid);
                    rows.Add(row);
                    p += rowSize;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        private static List<NetRow> QueryTcpV6()
        {
            List<NetRow> rows = new List<NetRow>();
            int size = 0;
            uint ret = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0) return rows;   // 机器禁用 IPv6 时静默降级
            if (size <= 0) return rows;

            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = GetExtendedTcpTable(buf, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
                if (ret != 0) return rows;

                int count = Marshal.ReadInt32(buf);
                int rowSize = Marshal.SizeOf(typeof(MIB_TCP6ROW_OWNER_PID));
                long p = buf.ToInt64() + Marshal.SizeOf(typeof(MIB_TCPTABLE_OWNER_PID));
                for (int i = 0; i < count; i++)
                {
                    MIB_TCP6ROW_OWNER_PID r = (MIB_TCP6ROW_OWNER_PID)Marshal.PtrToStructure(
                        new IntPtr(p), typeof(MIB_TCP6ROW_OWNER_PID));
                    NetRow row = new NetRow();
                    row.Protocol = "TCPv6";
                    row.LocalAddress = IPv6Text(r.ucLocalAddr, r.dwLocalScopeId);
                    row.LocalPort = PortOf(r.dwLocalPort);
                    row.RemoteAddress = IPv6Text(r.ucRemoteAddr, r.dwRemoteScopeId);
                    row.RemotePort = PortOf(r.dwRemotePort);
                    row.State = (TcpState)r.dwState;
                    row.Pid = (int)r.dwOwningPid;
                    row.ProcessName = ProcessNameOf(row.Pid);
                    rows.Add(row);
                    p += rowSize;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return rows;
        }

        /// <summary>
        /// Windows 把地址按网络字节序（大端）塞进 DWORD。x86/x64 是小端，
        /// 读出来的 uint 低位反而存着地址的第一个字节，所以必须按字节还原，
        /// 不能直接 new IPAddress(uint)（那会得到 1.0.0.127 这种倒序地址）。
        /// </summary>
        private static string IPv4Text(uint addr)
        {
            return (addr & 0xFF).ToString() + "." + ((addr >> 8) & 0xFF).ToString() + "."
                + ((addr >> 16) & 0xFF).ToString() + "." + ((addr >> 24) & 0xFF).ToString();
        }

        /// <summary>端口在 DWORD 的低 16 位，同样是网络字节序，需要 ntohs。</summary>
        private static int PortOf(uint dwPort)
        {
            return (int)(ushort)IPAddress.NetworkToHostOrder((short)(dwPort & 0xFFFF));
        }

        private static string IPv6Text(byte[] bytes, uint scopeId)
        {
            if (bytes == null || bytes.Length != 16) return "::";
            IPAddress ip = new IPAddress(bytes);
            // 链路本地地址带 scope id（如 fe80::1%12），netstat 也是这么显示的
            if (ip.IsIPv6LinkLocal && scopeId != 0) return ip.ToString() + "%" + scopeId.ToString();
            return ip.ToString();
        }

        public static string FormatEndPoint(string addr, int port)
        {
            if (addr != null && addr.IndexOf(':') >= 0) return "[" + addr + "]:" + port.ToString();
            return addr + ":" + port.ToString();
        }

        public static string StateName(TcpState s)
        {
            switch (s)
            {
                case TcpState.Closed: return "CLOSED";
                case TcpState.Listen: return "LISTENING";
                case TcpState.SynSent: return "SYN_SENT";
                case TcpState.SynReceived: return "SYN_RECEIVED";
                case TcpState.Established: return "ESTABLISHED";
                case TcpState.FinWait1: return "FIN_WAIT_1";
                case TcpState.FinWait2: return "FIN_WAIT_2";
                case TcpState.CloseWait: return "CLOSE_WAIT";
                case TcpState.Closing: return "CLOSING";
                case TcpState.LastAck: return "LAST_ACK";
                case TcpState.TimeWait: return "TIME_WAIT";
                case TcpState.DeleteTcb: return "DELETE_TCB";
                default: return "UNKNOWN(" + ((int)s).ToString() + ")";
            }
        }

        // ---------- 进程名解析 ----------
        // 实测（本机 610 个进程）：Process.GetProcessById 单次 15.6ms（全表 9.5 秒，绝不能用在刷新里）；
        // Process.GetProcesses() 一次快照只要 16~22ms 且能拿到 System / svchost / lsass 这些受保护进程的名字，
        // 所以走「批量快照 + 短 TTL」；快照里没有的新进程再用 QueryFullProcessImageName 补（0.05ms/次）。
        private static readonly object _snapLock = new object();
        private static Dictionary<int, string> _snapshot;
        private static int _snapStamp;
        private const int SnapshotTtlMs = 800;

        public static string ProcessNameOf(int pid)
        {
            if (pid <= 0) return "";
            Dictionary<int, string> snap = Snapshot();
            string name;
            if (snap.TryGetValue(pid, out name)) return name;

            // 快照里没有：多半是刚启动的进程，用 API 快速补一刀
            name = NameFromApi(pid);
            if (name.Length > 0)
            {
                lock (_snapLock) { snap[pid] = name; }
            }
            return name;
        }

        private static Dictionary<int, string> Snapshot()
        {
            lock (_snapLock)
            {
                int now = Environment.TickCount;
                if (_snapshot != null && unchecked(now - _snapStamp) >= 0
                    && unchecked(now - _snapStamp) < SnapshotTtlMs)
                    return _snapshot;

                Dictionary<int, string> map = new Dictionary<int, string>(720);
                try
                {
                    Process[] all = Process.GetProcesses();
                    foreach (Process p in all)
                    {
                        try
                        {
                            string n = p.ProcessName;
                            if (!string.IsNullOrEmpty(n)) map[p.Id] = n;
                        }
                        catch (Exception) { }       // 单个进程出错不影响整批
                        finally { p.Dispose(); }
                    }
                }
                catch (Exception) { }
                _snapshot = map;
                _snapStamp = now;
                return map;
            }
        }

        /// <summary>低权限也能读路径的方式：OpenProcess(QUERY_LIMITED_INFORMATION) + QueryFullProcessImageName</summary>
        private static string NameFromApi(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (!QueryFullProcessImageName(h, 0, sb, ref size)) return "";
                string name = System.IO.Path.GetFileName(sb.ToString(0, size));
                if (name.Length > 4 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - 4);
                return name;
            }
            catch (Exception) { return ""; }
            finally { CloseHandle(h); }
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags,
            StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
