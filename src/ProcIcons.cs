using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;

namespace NetEye
{
    /// <summary>
    /// v0.9.3：进程图标（「进程名」列左侧 16x16）。
    ///
    /// 设计要点（都对齐 ProcAge / ProcBadge 的既有套路）：
    ///   1) 后台线程提取：Icon.ExtractAssociatedIcon 单次几毫秒，但一次刷新可能冒出
    ///      十几个新 exe，放 UI 线程会有可感知的卡顿——进队列慢慢来，读到就发事件刷列。
    ///   2) 按 exe 路径缓存：同一个 exe（比如几十个 chrome 子进程）只提取一次。
    ///   3) 按 PID 缓存路径解析结果（15 秒 TTL）：PID 会被系统复用，永久缓存会把
    ///      新进程的图标标到老进程头上；路径解析失败也缓存（受保护进程别每帧重试）。
    ///   4) 任何失败都不致命：拿不到图标就画默认的「白纸」图标，绝不抛异常打断绘制。
    /// </summary>
    internal static class ProcIcons
    {
        /// <summary>读到/更新图标后发一次（UI 线程只需 Invalidate 进程名列）</summary>
        public static event Action Updated;

        private const int PathTtlMs = 15000;

        private static readonly object _lock = new object();
        private static readonly Dictionary<string, Icon> _byPath =
            new Dictionary<string, Icon>(StringComparer.OrdinalIgnoreCase);   // exe 路径 → 图标
        private static readonly Dictionary<int, PathEntry> _byPid =
            new Dictionary<int, PathEntry>();                                  // pid → 路径解析结果
        private static readonly Queue<int> _queue = new Queue<int>();
        private static readonly HashSet<int> _queued = new HashSet<int>();
        private static Thread _worker;
        private static Icon _fallback;

        private class PathEntry
        {
            public string Path;        // null = 查过但拿不到
            public DateTime StampUtc;
        }

        /// <summary>测试用：清空缓存与队列</summary>
        internal static void Reset()
        {
            lock (_lock)
            {
                _byPath.Clear();
                _byPid.Clear();
                _queue.Clear();
                _queued.Clear();
            }
        }

        /// <summary>当前缓存图标数（测试断言用）</summary>
        internal static int CachedCount
        {
            get { lock (_lock) return _byPath.Count; }
        }

        /// <summary>队列里还剩多少待提取（测试用）</summary>
        internal static int PendingCount
        {
            get { lock (_lock) return _queue.Count; }
        }

        /// <summary>
        /// 给一批连接行排队提取图标（幂等）。UI 线程调用，绝不阻塞。
        /// </summary>
        public static void Request(IList<NetRow> rows)
        {
            if (rows == null) return;
            bool any = false;
            lock (_lock)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    NetRow r = rows[i];
                    if (r == null || r.Pid <= 4) continue;   // 内核进程没有 exe，不排队
                    if (_queued.Contains(r.Pid)) continue;
                    PathEntry e;
                    if (_byPid.TryGetValue(r.Pid, out e) && e != null
                        && (DateTime.UtcNow - e.StampUtc).TotalMilliseconds < PathTtlMs)
                        continue;                             // 15 秒内查过（无论成败）
                    _queued.Add(r.Pid);
                    _queue.Enqueue(r.Pid);
                    any = true;
                }
            }
            if (!any) return;
            EnsureWorker();
        }

        /// <summary>
        /// 绘制时取图标：拿得到返回对应图标（调用方不许 Dispose），拿不到返回内置兜底图标。
        /// 纯查缓存，绝不碰系统 API —— 绘制路径必须零阻塞。
        /// </summary>
        public static Icon IconFor(int pid)
        {
            lock (_lock)
            {
                PathEntry e;
                if (_byPid.TryGetValue(pid, out e) && e != null && !string.IsNullOrEmpty(e.Path))
                {
                    Icon ic;
                    if (_byPath.TryGetValue(e.Path, out ic) && ic != null) return ic;
                }
            }
            return FallbackIcon();
        }

        /// <summary>该 PID 是否已有可画的真图标（false = 还没读到，画兜底）</summary>
        internal static bool HasIcon(int pid)
        {
            lock (_lock)
            {
                PathEntry e;
                if (_byPid.TryGetValue(pid, out e) && e != null && !string.IsNullOrEmpty(e.Path))
                    return _byPath.ContainsKey(e.Path);
                return false;
            }
        }

        /// <summary>兜底图标：16x16 灰白「白纸」，进程没图标时也有个占位，不出现空档</summary>
        private static Icon FallbackIcon()
        {
            if (_fallback != null) return _fallback;
            try
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(96, 96, 96)))
                            g.FillRectangle(b, 3, 1, 10, 14);
                        using (Pen p = new Pen(Color.FromArgb(150, 150, 150)))
                            g.DrawRectangle(p, 3, 1, 10, 14);
                    }
                    _fallback = Icon.FromHandle(bmp.GetHicon());   // 故意不销毁：进程内常驻
                }
            }
            catch (Exception)
            {
                try { _fallback = SystemIcons.Application; } catch (Exception) { _fallback = null; }
            }
            return _fallback;
        }

        private static void EnsureWorker()
        {
            lock (_lock)
            {
                if (_worker != null && _worker.IsAlive) return;
                _worker = new Thread(WorkLoop);
                _worker.IsBackground = true;
                _worker.Name = "NetEye.Icons";
                _worker.Start();
            }
        }

        private static void WorkLoop()
        {
            for (int guard = 0; guard < 4096; guard++)
            {
                int pid;
                lock (_lock)
                {
                    if (_queue.Count == 0) return;
                    pid = _queue.Dequeue();
                }

                string path = null, err;
                try
                {
                    // 复用 ProcBadge 的两级解析（MainModule → QueryFullProcessImageName），
                    // 它自己带缓存，这里 15 秒 TTL 只是防重复问
                    if (ProcBadge.ResolveExePath(pid, out path, out err)) { /* path 有效 */ }
                    else path = null;
                }
                catch (Exception) { path = null; }

                lock (_lock)
                {
                    _byPid[pid] = new PathEntry { Path = path, StampUtc = DateTime.UtcNow };
                    _queued.Remove(pid);
                }

                if (!string.IsNullOrEmpty(path))
                {
                    lock (_lock)
                    {
                        if (!_byPath.ContainsKey(path))
                        {
                            // 先记占位防止同一 exe 在队列里被重复提取
                            _byPath[path] = null;
                        }
                        else if (_byPath[path] != null)
                        {
                            FireUpdated();      // 已有图标，只需刷新
                            continue;
                        }
                    }
                    Icon ic = null;
                    try { ic = Icon.ExtractAssociatedIcon(path); }
                    catch (Exception) { ic = null; }
                    lock (_lock)
                    {
                        if (ic != null) _byPath[path] = ic;
                        else _byPath.Remove(path);   // 提取失败：下帧走兜底，下次刷新可重试
                    }
                }
                FireUpdated();
            }
        }

        private static void FireUpdated()
        {
            Action h = Updated;
            if (h != null)
            {
                try { h(); } catch (Exception) { }
            }
        }
    }
}
