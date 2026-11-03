using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NetEye
{
    /// <summary>
    /// v0.9.2：进程存续时间（参考 CurrPorts 的 Created 列）。
    ///
    /// 一条连接本身没有「启动时间」，能代表它活了多久的只有「占用它的进程从什么时候开始跑的」——
    /// 所以这里读 Process.StartTime（进程创建时间），运行时长 = 现在 - 创建时间。
    ///
    /// 两个必须防住的地方：
    ///   1) 权限：System / Idle / 受保护进程读 StartTime 会抛 Win32Exception（拒绝访问），
    ///      已退出的会抛 ArgumentException，WOW64 下极少数会抛 InvalidOperationException。
    ///      一律吞掉并返回 null，界面显示「-」，绝不让一行脏数据把整个刷新搞崩。
    ///   2) 性能：几百条连接共用几十个 PID，而且 Process 打开句柄不便宜。
    ///      按 PID 缓存 15 秒（同一次刷新里同一 PID 只查一次），刷新耗时基本不受影响。
    ///
    /// 缓存 15 秒而不是永久：PID 会被系统回收复用，永久缓存会把「新进程」显示成「老进程的年龄」。
    /// </summary>
    internal static class ProcAge
    {
        /// <summary>未知/读不到的占位符（列表和 CSV 都用它，别改成空白）</summary>
        public const string Unknown = "-";

        /// <summary>缓存有效期：15 秒后重新问一次系统（兼顾 PID 复用与刷新开销）</summary>
        internal const int CacheTtlMs = 15000;

        private class Entry
        {
            public DateTime? Start;     // null = 查过但拿不到（受保护/已退出），同样缓存，别每秒重试
            public DateTime StampUtc;   // 这条缓存是什么时候写进去的
        }

        private static readonly Dictionary<int, Entry> _cache = new Dictionary<int, Entry>();

        /// <summary>测试用：清空缓存，让下一次查询真的走系统 API</summary>
        internal static void Reset()
        {
            _cache.Clear();
        }

        /// <summary>当前缓存条目数（测试断言缓存命中用）</summary>
        internal static int CacheCount { get { return _cache.Count; } }

        /// <summary>
        /// 给一批连接行填上进程启动时间。同一个 PID 在一次调用里只查一次，
        /// 异常进程填 null（界面显示「-」）。
        /// </summary>
        internal static void Annotate(List<NetRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            Dictionary<int, DateTime?> bulk = new Dictionary<int, DateTime?>();
            for (int i = 0; i < rows.Count; i++)
            {
                NetRow r = rows[i];
                if (r == null) continue;
                DateTime? st;
                if (!bulk.TryGetValue(r.Pid, out st))
                {
                    st = GetStart(r.Pid);
                    bulk[r.Pid] = st;
                }
                r.StartedLocal = st;
            }
        }

        /// <summary>取某个 PID 的进程启动时间（本地时间）；拿不到返回 null。带 15 秒缓存。</summary>
        internal static DateTime? GetStart(int pid)
        {
            if (pid <= 4) return null;      // 0=Idle、4=System，内核进程没有用户态启动时间可言

            DateTime nowUtc = DateTime.UtcNow;
            Entry e;
            if (_cache.TryGetValue(pid, out e))
            {
                double ageMs = (nowUtc - e.StampUtc).TotalMilliseconds;
                if (ageMs >= 0 && ageMs < CacheTtlMs) return e.Start;
            }

            DateTime? got = QueryUncached(pid);
            _cache[pid] = new Entry { Start = got, StampUtc = nowUtc };
            return got;
        }

        /// <summary>直接问系统（不走缓存）。任何异常都返回 null——这里就是防呆的那道墙。</summary>
        internal static DateTime? QueryUncached(int pid)
        {
            if (pid <= 4) return null;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    DateTime t = p.StartTime;
                    // 系统时间被往回调过时会出现「未来时间」，按 0 处理，别显示成负年龄
                    if (t > DateTime.Now) return DateTime.Now;
                    return t;
                }
            }
            catch (ArgumentException) { return null; }          // 进程已退出
            catch (InvalidOperationException) { return null; }  // 进程已退出 / 无权限
            catch (System.ComponentModel.Win32Exception) { return null; }  // 拒绝访问（受保护进程）
            catch (NotSupportedException) { return null; }      // 远程进程
            catch (Exception) { return null; }                  // 兜底：绝不让 UI 崩
        }

        /// <summary>运行时长文本（自 1970 风格）：45s / 2m 05s / 2h 15m / 3d 04h</summary>
        internal static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalDays >= 1) return ((long)t.TotalDays).ToString() + "d " + t.Hours.ToString("00") + "h";
            if (t.TotalHours >= 1) return ((long)t.TotalHours).ToString() + "h " + t.Minutes.ToString("00") + "m";
            if (t.TotalMinutes >= 1) return ((long)t.TotalMinutes).ToString() + "m " + t.Seconds.ToString("00") + "s";
            return ((long)t.TotalSeconds).ToString() + "s";
        }

        /// <summary>连接行的运行时长文本：拿不到启动时间就是「-」，永远不抛异常</summary>
        internal static string TextOf(NetRow r)
        {
            if (r == null || !r.StartedLocal.HasValue) return Unknown;
            return Format(DateTime.Now - r.StartedLocal.Value);
        }
    }
}
