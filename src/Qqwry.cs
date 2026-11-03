using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NetEye
{
    /// <summary>
    /// v0.6：纯真 IP 库（qqwry.dat）纯 C# 精简解析器，零第三方依赖。
    /// exe 同级目录有 qqwry.dat 就自动加载（只读整块进内存，查询零 IO）；
    /// 没有 → 归属地查询自动退回浏览器方案，体积与行为不变。
    /// 格式：索引区 [起始IP(4B)][记录偏移(3B)]，记录区为重定向式国家/区域结构，文本 GBK。
    /// </summary>
    internal sealed class Qqwry
    {
        private byte[] _dat;            // 整个 dat（只读）
        private uint _first, _last;     // 索引区首/末偏移
        private int _count;             // 索引条数
        private Encoding _gbk;

        public bool Loaded { get { return _dat != null; } }
        public string FilePath { get; private set; }
        public long FileSize { get { return _dat == null ? 0 : _dat.Length; } }
        public int EntryCount { get { return _count; } }

        /// <summary>加载失败一律返回 false（文件不存在/格式不对都算），绝不抛异常打断启动</summary>
        public bool Load(string path)
        {
            _dat = null;
            FilePath = "";
            _count = 0;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                byte[] d = File.ReadAllBytes(path);
                if (d.Length < 16) return false;                       // 头 + 至少一条索引 + 一条记录
                uint first = Le32(d, 0), last = Le32(d, 4);
                // 结构自检：末偏移必须在文件内、索引步长 7 字节对齐
                if (first < 8 || last >= (uint)d.Length || last < first) return false;
                long n = ((long)last - first) / 7;
                if (n <= 0 || n > 5000000) return false;
                // 再抽一条索引真读一遍，偏移能落到文件内才认
                uint rec = Le24(d, (int)first + 4);
                if (rec < 8 || rec >= (uint)d.Length) return false;

                _dat = d;
                _first = first;
                _last = last;
                _count = (int)(n + 1);
                FilePath = path;
                _gbk = Encoding.GetEncoding(936);
                return true;
            }
            catch (Exception) { _dat = null; return false; }
        }

        /// <summary>查询 IPv4 归属地（只接受点分十进制）。查不到返回 false，绝不抛异常。</summary>
        public bool Lookup(string ipText, out string location)
        {
            location = "";
            if (!Loaded) return false;
            uint ip;
            if (!IpToUint(ipText, out ip)) return false;
            try
            {
                // 二分：找最大的 起始IP <= ip 的索引条目（纯真库索引按起始IP升序）
                int lo = 0, hi = _count - 1, hit = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    int pos = (int)_first + mid * 7;
                    uint start = Le32(_dat, pos);
                    if (start <= ip) { hit = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                if (hit < 0) return false;                             // 比库里第一条还小

                int rec = (int)Le24(_dat, (int)_first + hit * 7 + 4);
                if (rec < 8 || rec >= _dat.Length) return false;

                // 记录结构（按真实 dat 字节实证）：[结束IP 4B][国家信息]，模式字节在 rec+4
                int c = rec + 4;
                if (c >= _dat.Length) return false;
                int mode = _dat[c];
                if (mode == 1)                                          // 重定向①：跳到独立国家信息块（模式字节在目标 +0）
                {
                    c = (int)Le24(_dat, c + 1);
                    if (c < 8 || c >= _dat.Length) return false;
                    mode = _dat[c];
                }
                string country;
                int areaOff;
                if (mode == 2)                                          // 重定向②：国家跳转，区域原地跟在 +4
                {
                    country = ReadStr((int)Le24(_dat, c + 1));
                    areaOff = c + 4;
                }
                else                                                    // 无重定向：mode 字节就是国家首字符
                {
                    country = ReadStr(c);
                    areaOff = c + _gbk.GetByteCount(country) + 1;       // 国家串 \0 之后就是区域
                }

                string area = ReadArea(areaOff);
                location = Join(country, area);
                return location.Length > 0;
            }
            catch (Exception) { return false; }
        }

        // 区域串可能又是一层重定向（0x01/0x02 + 3 字节偏移）
        private string ReadArea(int off)
        {
            if (off < 8 || off >= _dat.Length) return "";
            int mode = _dat[off];
            if (mode == 1 || mode == 2)
            {
                off = (int)Le24(_dat, off + 1);
                if (off < 8 || off >= _dat.Length) return "";
            }
            return ReadStr(off);
        }

        /// <summary>GBK 串读到 \0 为止；「CZ88.NET」是官方默认占位，等价于没有区域/国家</summary>
        private string ReadStr(int off)
        {
            if (off < 8 || off >= _dat.Length) return "";
            int end = off;
            while (end < _dat.Length && _dat[end] != 0 && end - off < 512) end++;
            if (end >= _dat.Length) return "";
            string s = _gbk.GetString(_dat, off, end - off);
            if (s == "CZ88.NET") return "";
            return s;
        }

        private static string Join(string country, string area)
        {
            if (country.Length == 0 && area.Length == 0) return "";
            if (country.Length == 0) return area;
            if (area.Length == 0) return country;
            // 免得拼出「广东省广州市 广州市电信」这种叠词
            if (area.IndexOf(country, StringComparison.Ordinal) >= 0) return area;
            if (country.IndexOf(area, StringComparison.Ordinal) >= 0) return country;
            return country + " " + area;
        }

        private static uint Le32(byte[] d, int pos)
        {
            return (uint)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16 | d[pos + 3] << 24);
        }

        private static uint Le24(byte[] d, int pos)
        {
            return (uint)(d[pos] | d[pos + 1] << 8 | d[pos + 2] << 16);
        }

        /// <summary>点分十进制 → 主机序数值（a.b.c.d → a<<24|b<<16|c<<8|d）；IPv6/非法串一律 false</summary>
        public static bool IpToUint(string text, out uint ip)
        {
            ip = 0;
            if (string.IsNullOrEmpty(text)) return false;
            if (text.IndexOf(':') >= 0) return false;                  // IPv6 交给浏览器方案
            string[] parts = text.Split('.');
            if (parts.Length != 4) return false;
            uint v = 0;
            for (int i = 0; i < 4; i++)
            {
                int n;
                if (!int.TryParse(parts[i], out n) || n < 0 || n > 255) return false;
                v = (v << 8) | (uint)n;
            }
            ip = v;
            return true;
        }
    }
}
