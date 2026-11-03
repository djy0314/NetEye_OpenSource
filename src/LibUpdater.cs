using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetEye
{
    /// <summary>
    /// v0.9.3：离线 IP 库（qqwry.dat）原生更新器 —— 纯 C#，不再调用 powershell 脚本。
    ///
    /// 为什么换掉脚本：
    ///   · 「单文件 exe」是 NetEye 的底线，发布包里多一个 .ps1 就已经破坏了它；
    ///   · 企业环境常把 ExecutionPolicy 设成 AllSigned/Restricted，脚本会被直接拦死；
    ///   · 脚本的下载/校验/落盘逻辑在 C# 里重写一遍只有 200 行，零第三方依赖。
    ///
    /// 行为约定（与原脚本逐条对齐，并补了它没有的几项）：
    ///   1. 4 个镜像按顺序试，任一成功即停；建连/响应头 10 秒超时，传输中 20 秒无数据算卡死；
    ///   2. 先写 qqwry.dat.part，通过「>1MB + 头部结构 + 抽样索引」三重校验才允许上台面；
    ///   3. 旧库先备份成 qqwry.dat.bak（File.Replace 原子替换，失败再走两步兜底）；
    ///   4. 任何一步失败都不碰现有的 qqwry.dat，用户手里的旧库永远不会被半个文件覆盖；
    ///   5. 全程可取消（CancellationToken）：关设置窗继续下，关主程序立刻收手。
    ///
    /// 线程模型：内部同步阻塞在 Task.Run 的后台线程上跑，进度/完成都用委托回调到后台线程，
    /// 由调用方（SettingsForm）自己 BeginInvoke 回 UI —— 这样窗口提前销毁也不会把异常甩给线程池。
    /// </summary>
    internal sealed class LibUpdateResult
    {
        /// <summary>是否成功（true 时 qqwry.dat 已经是新库）</summary>
        internal bool OkFlag;
        /// <summary>给用户看的一句话（成功=体积/记录数，失败=原因）</summary>
        internal string DetailText = "";
        /// <summary>成功的镜像 URL（失败时是最后一次的错误摘要，便于用户反馈）</summary>
        internal string MirrorUrl = "";
        /// <summary>新库字节数</summary>
        internal long SizeBytes;
        /// <summary>索引条数（≈ 覆盖的 IP 段数量）</summary>
        internal int IndexCount;
        /// <summary>是否被用户主动取消</summary>
        internal bool WasCanceled;
    }

    internal static class LibUpdater
    {
        /// <summary>镜像顺序：国内 GitHub 加速 → 镜像站 → 官方 raw（与原脚本保持一致）</summary>
        internal static readonly string[] Mirrors = new string[]
        {
            "https://ghfast.top/https://github.com/adysec/IP_database/raw/main/qqwry/qqwry.dat",
            "https://gh-proxy.com/https://github.com/adysec/IP_database/raw/main/qqwry/qqwry.dat",
            "https://raw.gitmirror.com/adysec/IP_database/main/qqwry/qqwry.dat",
            "https://raw.githubusercontent.com/adysec/IP_database/main/qqwry/qqwry.dat"
        };

        internal const string DestName = "qqwry.dat";
        internal const string PartName = "qqwry.dat.part";
        internal const string BakName = "qqwry.dat.bak";

        /// <summary>小于这个体积的一定是错误页/断流，不可能是真库（真库约 25MB）</summary>
        internal const long MinBytes = 1048576L;
        /// <summary>超过这个体积肯定是拿到别的东西了，直接放弃，别把磁盘写满</summary>
        private const long MaxBytes = 134217728L;      // 128MB
        /// <summary>建连 + 拿响应头的上限（慢网也不该超过 10 秒还没开始吐数据）</summary>
        private const int HeadTimeoutMs = 10000;
        /// <summary>传输中途允许的最长静默（超过就当链路已死，换下一个镜像）</summary>
        private const int StallTimeoutMs = 20000;
        /// <summary>进度回调节流：至少每 256KB 报一次，避免 400 次跨线程 BeginInvoke</summary>
        private const int ProgressStep = 262144;
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetEye/0.9.3";

        private static int _busy;
        private static CancellationTokenSource _cts;

        /// <summary>是否已有下载任务在跑（同一时刻只允许一个，防止两路一起写 .part）</summary>
        internal static bool IsBusy
        {
            get { return Interlocked.CompareExchange(ref _busy, 0, 0) != 0; }
        }

        /// <summary>主程序退出时收手：取消当前任务，让它把 .part 清掉再走</summary>
        internal static void CancelJob()
        {
            CancellationTokenSource c = _cts;
            if (c == null) return;
            try { c.Cancel(); }
            catch (Exception) { }        // 已释放/已取消：无所谓
        }

        /// <summary>
        /// 开始下载（立即返回，不阻塞 UI）。
        /// </summary>
        /// <param name="dir">目标目录（通常是 exe 同目录）</param>
        /// <param name="progress">进度回调：(镜像序号 1 基, 已下载字节, 总字节/-1 未知)，后台线程调用</param>
        /// <param name="done">完成回调：后台线程调用，保证调且只调一次</param>
        /// <returns>false = 已有任务在跑，本次未启动</returns>
        internal static bool StartJob(string dir, Action<int, long, long> progress, Action<LibUpdateResult> done)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;

            CancellationTokenSource cts = new CancellationTokenSource();
            _cts = cts;

            Task.Run(delegate
            {
                LibUpdateResult r;
                try
                {
                    r = RunAll(dir, progress, cts.Token);
                }
                catch (Exception ex)
                {
                    r = new LibUpdateResult();
                    r.DetailText = Describe(ex);
                }
                finally
                {
                    // 先松手再放开忙标记：反过来的话，新任务可能在本线程把 _cts 清空之后才写入，
                    // 结果取消句柄被上一轮的收尾覆盖掉（成对顺序别调）。
                    _cts = null;
                    Interlocked.Exchange(ref _busy, 0);
                }
                try { if (done != null) done(r); }
                catch (Exception) { }     // 回调方自己的异常不许冒泡到线程池
            });
            return true;
        }

        /// <summary>
        /// 开始下载前的写入权限探测：直接在目标目录建一次 .part。
        /// 比看 ACL 靠谱（映射盘/只读介质/杀软保护目录的 ACL 常常看不出真相），
        /// 而且顺手把上次残留的半个文件清掉。
        /// </summary>
        internal static bool EnsureWritable(string dir, out string error)
        {
            error = null;
            string probe = Path.Combine(dir, PartName);
            try
            {
                using (FileStream fs = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.WriteByte(0);
                }
                try { File.Delete(probe); }
                catch (Exception) { }     // 删不掉不影响写入能力判定
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                error = "当前目录无写入权限，请将软件放到其他目录重试。\r\n\r\n"
                    + "目录：" + dir
                    + "\r\n\r\n（Program Files、系统盘根目录、只读共享盘都属于这种情况；"
                    + "把 NetEye.exe 拷到 D:\\Tools\\NetEye 这类自己的目录即可。）";
                return false;
            }
            catch (Exception ex)
            {
                error = "当前目录无法写入，离线库更新已取消。\r\n\r\n目录：" + dir
                    + "\r\n\r\n系统提示：" + ex.Message
                    + "\r\n\r\n请将软件放到其他目录重试。";
                return false;
            }
        }

        // ================= 主流程 =================

        private static LibUpdateResult RunAll(string dir, Action<int, long, long> progress, CancellationToken ct)
        {
            string part = Path.Combine(dir, PartName);
            string dest = Path.Combine(dir, DestName);
            string lastError = "";
            int count = Mirrors.Length;

            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) return MakeCanceled();

                string err;
                bool got = false;
                try
                {
                    got = Fetch(Mirrors[i], part, i + 1, progress, ct, out err);
                }
                catch (OperationCanceledException)
                {
                    Cleanup(part);
                    return MakeCanceled();
                }
                catch (Exception ex)
                {
                    err = Describe(ex);
                }
                if (!got)
                {
                    lastError = "镜像 " + (i + 1).ToString() + "：" + (err == null ? "未知错误" : err);
                    Cleanup(part);
                    if (ct.IsCancellationRequested) return MakeCanceled();
                    continue;
                }

                long size;
                int entries;
                if (!Inspect(part, out size, out entries, out err))
                {
                    lastError = "镜像 " + (i + 1).ToString() + " 下到的文件没通过校验：" + err;
                    Cleanup(part);
                    continue;
                }

                try
                {
                    Commit(part, dest);
                }
                catch (Exception ex)
                {
                    Cleanup(part);
                    LibUpdateResult bad = new LibUpdateResult();
                    bad.DetailText = "文件校验通过，但覆盖 qqwry.dat 失败：" + ex.Message
                        + "\r\n\r\n（旧库未被破坏，可关掉占用程序后重试。）";
                    bad.MirrorUrl = Mirrors[i];
                    return bad;
                }

                LibUpdateResult ok = new LibUpdateResult();
                ok.OkFlag = true;
                ok.MirrorUrl = Mirrors[i];
                ok.SizeBytes = size;
                ok.IndexCount = entries;
                ok.DetailText = "已下载 " + Mb(size) + "，索引 " + entries.ToString() + " 条";
                return ok;
            }

            if (ct.IsCancellationRequested) return MakeCanceled();
            LibUpdateResult fail = new LibUpdateResult();
            fail.DetailText = count.ToString() + " 个镜像全部失败，请检查网络/代理后重试。"
                + (lastError.Length > 0 ? "\r\n\r\n最后一次：" + lastError : "");
            return fail;
        }

        /// <summary>
        /// 从单个镜像下载到 .part。HttpClient 的 Timeout 是「整个请求」的总时长，
        /// 25MB 在慢网下 10 秒根本下不完 —— 所以总超时关掉，改成「建连 10 秒 + 单次读 20 秒」两段控制。
        /// </summary>
        private static bool Fetch(string url, string partPath, int idx,
                                  Action<int, long, long> progress, CancellationToken ct, out string error)
        {
            error = null;
            // .NET Framework 4.x 默认协议里可能没有 TLS 1.2，GitHub 系站点会直接握手失败
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch (Exception) { }

            using (HttpClient client = new HttpClient())
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                try { client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent); }
                catch (Exception) { }
                if (progress != null)
                {
                    try { progress(idx, 0, -1); } catch (Exception) { }
                }

                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
                try
                {
                    HttpResponseMessage resp = null;
                    try
                    {
                        using (CancellationTokenSource headCts =
                               CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            headCts.CancelAfter(HeadTimeoutMs);
                            resp = client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead,
                                headCts.Token).GetAwaiter().GetResult();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (ct.IsCancellationRequested) throw;
                        error = "建连超时（" + (HeadTimeoutMs / 1000).ToString() + " 秒没有响应）";
                        return false;
                    }
                    catch (Exception ex)
                    {
                        error = Describe(ex);
                        return false;
                    }

                    using (resp)
                    {
                        if (!resp.IsSuccessStatusCode)
                        {
                            error = "HTTP " + ((int)resp.StatusCode).ToString();
                            return false;
                        }
                        long total = -1;
                        if (resp.Content.Headers.ContentLength.HasValue)
                            total = resp.Content.Headers.ContentLength.Value;
                        if (total > MaxBytes)
                        {
                            error = "文件异常巨大（" + Mb(total) + "）";
                            return false;
                        }
                        if (total > 0 && total <= MinBytes)
                        {
                            error = "文件只有 " + total.ToString() + " 字节，像错误页/断流";
                            return false;
                        }

                        try
                        {
                            using (Stream src = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                            using (FileStream dst = new FileStream(partPath, FileMode.Create,
                                       FileAccess.Write, FileShare.None, 65536))
                            {
                                byte[] buf = new byte[65536];
                                long got = 0, reported = 0;
                                while (true)
                                {
                                    int n;
                                    try
                                    {
                                        using (CancellationTokenSource readCts =
                                               CancellationTokenSource.CreateLinkedTokenSource(ct))
                                        {
                                            readCts.CancelAfter(StallTimeoutMs);
                                            n = src.ReadAsync(buf, 0, buf.Length, readCts.Token)
                                                .GetAwaiter().GetResult();
                                        }
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        if (ct.IsCancellationRequested) throw;
                                        error = "传输卡住了（" + (StallTimeoutMs / 1000).ToString()
                                            + " 秒没收到数据）";
                                        return false;
                                    }

                                    if (n <= 0) break;
                                    dst.Write(buf, 0, n);
                                    got += n;
                                    if (got > MaxBytes)
                                    {
                                        error = "文件异常巨大，已中止";
                                        return false;
                                    }
                                    if (progress != null && (got - reported >= ProgressStep || got == total))
                                    {
                                        reported = got;
                                        try { progress(idx, got, total); } catch (Exception) { }
                                    }
                                }
                                dst.Flush();

                                if (total > 0 && got != total)
                                {
                                    error = "传输不完整（" + got.ToString() + "/" + total.ToString() + " 字节）";
                                    return false;
                                }
                                if (got <= MinBytes)
                                {
                                    error = "文件只有 " + got.ToString() + " 字节，不是有效的离线库";
                                    return false;
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            error = Describe(ex);
                            return false;
                        }
                    }
                }
                finally
                {
                    req.Dispose();
                }
            }
            return true;
        }

        /// <summary>
        /// 结构校验（判据来自 qqwry.dat 本身的格式，不是「看着像」）：
        ///   · 体积必须 &gt; 1MB（错误页/半截文件过不去）；
        ///   · 头 8 字节 = 索引区首/末偏移（小端 uint32）；
        ///   · first &gt;= 8（前面就是这 8 字节）、last &lt; 文件长度、last &gt; first、步长 7 字节整除；
        ///   · 再抽第一条索引读它的 3 字节记录偏移，必须落在文件内（防「头部对、内容空」的假库）。
        /// </summary>
        internal static bool Inspect(string path, out long size, out int entries, out string error)
        {
            size = 0;
            entries = 0;
            error = null;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    size = fs.Length;
                    if (size <= MinBytes)
                    {
                        error = "体积 " + size.ToString() + " 字节 ≤ 1MB";
                        return false;
                    }
                    byte[] head = new byte[8];
                    int read = 0;
                    while (read < head.Length)
                    {
                        int n = fs.Read(head, read, head.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < head.Length)
                    {
                        error = "文件头读不满 8 字节";
                        return false;
                    }

                    uint first = Le32(head, 0);
                    uint last = Le32(head, 4);
                    if (first < 8)
                    {
                        error = "头部 first=" + first.ToString() + "，小于 8";
                        return false;
                    }
                    if (last >= (uint)size)
                    {
                        error = "头部 last=" + last.ToString() + " 超出文件长度 " + size.ToString();
                        return false;
                    }
                    if (last <= first)
                    {
                        error = "头部 last 不大于 first";
                        return false;
                    }
                    uint span = last - first;
                    if (span % 7 != 0)
                    {
                        error = "索引区长度 " + span.ToString() + " 不是 7 的整数倍";
                        return false;
                    }

                    fs.Position = first + 4;
                    byte[] off = new byte[3];
                    read = 0;
                    while (read < off.Length)
                    {
                        int n = fs.Read(off, read, off.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read < off.Length)
                    {
                        error = "第一条索引读不全";
                        return false;
                    }
                    uint rec = Le24(off, 0);
                    if (rec < 8 || rec >= (uint)size)
                    {
                        error = "第一条索引的记录偏移 " + rec.ToString() + " 越界";
                        return false;
                    }

                    entries = (int)(span / 7 + 1);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = Describe(ex);
                return false;
            }
        }

        /// <summary>
        /// 上台面：.part → qqwry.dat，旧库留一份 .bak。
        /// File.Replace 是同卷原子操作（要么旧要么新，不会出现半个文件）；
        /// 万一它因为跨卷/文件属性失败，退回「复制备份 + 删除 + 改名」。
        /// </summary>
        private static void Commit(string part, string dest)
        {
            string bak = Path.Combine(Path.GetDirectoryName(dest) ?? "", BakName);
            if (File.Exists(dest))
            {
                try
                {
                    File.Replace(part, dest, bak, true);
                    return;
                }
                catch (Exception)
                {
                    // 兜底：先备份再替换。备份失败会抛出去，调用方负责保留旧库。
                    File.Copy(dest, bak, true);
                }
                File.Delete(dest);
            }
            File.Move(part, dest);
        }

        private static void Cleanup(string part)
        {
            try { if (File.Exists(part)) File.Delete(part); }
            catch (Exception) { }     // 杀软/索引服务短暂占用：留着下次覆盖也无害
        }

        private static LibUpdateResult MakeCanceled()
        {
            LibUpdateResult r = new LibUpdateResult();
            r.WasCanceled = true;
            r.DetailText = "已取消下载（现有离线库未改动）。";
            return r;
        }

        /// <summary>异常 → 人话（失败提示要能看懂，不是甩一串英文堆栈）</summary>
        private static string Describe(Exception ex)
        {
            if (ex == null) return "未知错误";
            if (ex is OperationCanceledException) return "超时";
            HttpRequestException http = ex as HttpRequestException;
            if (http != null)
            {
                string detail = http.InnerException != null ? http.InnerException.Message : http.Message;
                return "网络请求失败（可能被防火墙/代理拦截）：" + detail;
            }
            IOException io = ex as IOException;
            if (io != null) return "读写文件失败：" + io.Message;
            UnauthorizedAccessException deny = ex as UnauthorizedAccessException;
            if (deny != null) return "没有写入权限：" + deny.Message;
            return ex.Message;
        }

        private static string Mb(long bytes)
        {
            return (bytes / 1048576.0).ToString("0.0") + " MB";
        }

        private static uint Le32(byte[] d, int pos)
        {
            return (uint)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16 | d[pos + 3] << 24);
        }

        private static uint Le24(byte[] d, int pos)
        {
            return (uint)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16);
        }
    }
}
