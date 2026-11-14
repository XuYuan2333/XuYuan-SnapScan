using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QRSnap
{
    // 重复截图缓存：同一屏（允许轻微差异）再翻一次，直接贴上次的成品图，不再 OCR / 翻译。
    // 用 64 位 dHash 做近似判断，Hamming <= 3 就当同一屏；用配置指纹隔离不同模式。
    internal static class ShotCache
    {
        internal sealed class Entry
        {
            public ulong Hash;
            public string Json;
            public string MtText;
            public Bitmap Image;
        }

        private static readonly object Gate = new object();
        // 64 位指纹允许 6 位差异：同一屏在重绘、光标闪烁、滚动条这类小变化下
        // 实测距离在 1~6，而内容不同的画面通常在 20 以上，所以 6 既有命中率也不误贴
        private const int MaxDistance = 6;

        // 上限走配置（设置里的「缓存上限」），0 或异常值回到默认 120
        private static int MaxEntries
        {
            get
            {
                int v = AppConfig.NormalizeShotCacheMax(AppConfig.ShotCacheMax);
                return v <= 0 ? 120 : v;
            }
        }

        // 目录里的条目索引：命中判断只在内存里比 64 位指纹，
        // 不用每次都列目录 + 逐个解析文件名（条目多时差别明显）
        private sealed class Idx { public ulong Hash; public string Path; public string KeyPart; }
        private static readonly List<Idx> _idx = new List<Idx>();
        private static bool _idxBuilt;

        public static string Dir
        {
            get
            {
                // 放当前文件夹（绿色版/便携），不再塞进 AppData
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shotcache");
            }
        }

        // 预览图（彩色 WebP）单独一个子目录：只用于展示 / 上传，不参与 OCR
        public static string PreviewDir
        {
            get { return Path.Combine(Dir, "preview"); }
        }

        public static void SavePreview(ulong hash, byte[] webp, string note)
        {
            if (webp == null || webp.Length == 0) return;
            try
            {
                Directory.CreateDirectory(PreviewDir);
                string baseName = "p" + (hash == 0UL ? DateTime.Now.Ticks.ToString("x") : hash.ToString("x16"))
                    + "_" + KeyHash(ConfigKey());
                string wp = Path.Combine(PreviewDir, baseName + ".webp");
                string tp = Path.Combine(PreviewDir, baseName + ".txt");
                File.WriteAllBytes(wp, webp);
                File.WriteAllText(tp, note ?? "", new UTF8Encoding(false));
                TrimOldPreviews();
            }
            catch (Exception) { }
        }

        private static void TrimOldPreviews()
        {
            try
            {
                string[] files = Directory.GetFiles(PreviewDir, "*.webp");
                if (files.Length <= 40) return;
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });
                for (int i = 0; i < files.Length - 40; i++)
                {
                    try { File.Delete(files[i]); } catch (Exception) { }
                    try { File.Delete(Path.ChangeExtension(files[i], ".txt")); } catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        private static string LegacyDir
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "XuYuan-SnapScan", "shotcache");
            }
        }

        public static string ConfigKey()
        {
            return AppConfig.NormalizeEngine(AppConfig.Engine) + "|"
                + AppConfig.NormalizeMtProfile(AppConfig.MtProfile) + "|"
                + AppConfig.NormalizeMtMode(AppConfig.MtMode) + "|"
                + (AppConfig.AiMt ? "1" : "0") + (AppConfig.AiOcr ? "1" : "0")
                + (AppConfig.AliMt ? "tmt" : "lc")
                + (AppConfig.AiLayout ? "1" : "0") + "|" + AppConfig.MtMaskAlpha
                + "|" + AppConfig.AiModel + (AppConfig.OcrFusion ? "|fuse" : "") + "|fv2";
        }

        // 指纹取自「送 OCR 的那张灰度图」这条链路：先整体缩到 9x8，再按 BT.601 转亮度、
        // 做一次亮度拉伸（深色主题 / 曝光差异也能对齐），最后取相邻像素差成 64 位。
        // 全程一次 LockBits 读完，不再逐像素 GetPixel。
        public static ulong Hash(Bitmap bmp)
        {
            if (bmp == null) return 0;
            try
            {
                using (Bitmap small = new Bitmap(9, 8, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(bmp, new Rectangle(0, 0, 9, 8));
                    }
                    byte[] lum = new byte[72];
                    BitmapData bd = small.LockBits(new Rectangle(0, 0, 9, 8), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        int stride = bd.Stride;
                        byte[] row = new byte[Math.Abs(stride)];
                        for (int y = 0; y < 8; y++)
                        {
                            IntPtr p = new IntPtr(bd.Scan0.ToInt64() + (long)y * stride);
                            Marshal.Copy(p, row, 0, row.Length);
                            for (int x = 0; x < 9; x++)
                            {
                                int o = x * 3;
                                lum[y * 9 + x] = (byte)((row[o + 2] * 299 + row[o + 1] * 587 + row[o] * 114) / 1000);
                            }
                        }
                    }
                    finally { small.UnlockBits(bd); }

                    int lo = 255, hi = 0;
                    for (int i = 0; i < 72; i++) { if (lum[i] < lo) lo = lum[i]; if (lum[i] > hi) hi = lum[i]; }
                    if (hi - lo >= 8)
                    {
                        int span = hi - lo;
                        for (int i = 0; i < 72; i++) lum[i] = (byte)((lum[i] - lo) * 255 / span);
                    }

                    ulong h = 0;
                    int bit = 0;
                    for (int y = 0; y < 8; y++)
                        for (int x = 0; x < 8; x++)
                        {
                            if (lum[y * 9 + x] > lum[y * 9 + x + 1]) h |= (1UL << bit);
                            bit++;
                        }
                    return h;
                }
            }
            catch (Exception) { return 0; }
        }

        private static double MeanLuma(Bitmap bmp)
        {
            if (bmp == null) return 0;
            try
            {
                long sum = 0; int n = 0;
                int sx = Math.Max(1, bmp.Width / 24), sy = Math.Max(1, bmp.Height / 24);
                for (int y = 0; y < bmp.Height; y += sy)
                    for (int x = 0; x < bmp.Width; x += sx)
                    {
                        Color c = bmp.GetPixel(x, y);
                        sum += (c.R * 77 + c.G * 151 + c.B * 28) >> 8;
                        n++;
                    }
                return n == 0 ? 0 : sum / (double)n;
            }
            catch (Exception) { return 0; }
        }

        public static int Distance(ulong a, ulong b)        {
            ulong x = a ^ b;
            int n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }

        public static bool TryGet(Bitmap crop, out Entry entry)
        {
            entry = null;
            try
            {
                if (crop == null || !Directory.Exists(Dir)) return false;
                ulong h = Hash(crop);
                string key = KeyHash(ConfigKey());
                ulong bestH = 0;
                string best = null;
                int bestD = int.MaxValue;
                lock (Gate)
                {
                    if (!_idxBuilt) BuildIndex();
                    for (int i = 0; i < _idx.Count; i++)
                    {
                        Idx ix = _idx[i];
                        if (!string.Equals(ix.KeyPart, key, StringComparison.Ordinal)) continue;
                        int d = Distance(h, ix.Hash);
                        if (d < bestD) { bestD = d; bestH = ix.Hash; best = ix.Path; }
                    }
                }
                if (best == null || bestD > MaxDistance) return false;
                string png = Path.ChangeExtension(best, ".png");
                if (!File.Exists(png)) { _idxBuilt = false; return false; }
                Entry e = new Entry();
                e.Hash = bestH;
                e.Json = File.ReadAllText(best, Encoding.UTF8);
                string mt = Path.ChangeExtension(best, ".mt.txt");
                if (File.Exists(mt)) { try { e.MtText = File.ReadAllText(mt, Encoding.UTF8); } catch (Exception) { } }
                e.Image = LoadUnlocked(png);
                if (e.Image == null) { _idxBuilt = false; return false; }
                // 二次校验：指纹只比 64 位，这里再用平均亮度挡一次「排版像但内容变了」
                if (Math.Abs(MeanLuma(crop) - MeanLuma(e.Image)) > 6.0) return false;
                entry = e;
                return true;
            }
            catch (Exception) { _idxBuilt = false; return false; }
        }

        public static void Save(Bitmap crop, Bitmap overlay, string json)
        {
            Save(crop == null ? 0UL : Hash(crop), overlay, json);
        }

        public static void Save(ulong hash, Bitmap overlay, string json, string mtText = null)
        {
            try
            {
                if (hash == 0UL || overlay == null || string.IsNullOrEmpty(json)) return;
                Directory.CreateDirectory(Dir);
                string keyPart = KeyHash(ConfigKey());
                string baseName = "h" + hash.ToString("x16") + "_" + keyPart;
                string jp = Path.Combine(Dir, baseName + ".json");
                string pp = Path.Combine(Dir, baseName + ".png");
                string mp = Path.Combine(Dir, baseName + ".mt.txt");
                File.WriteAllText(jp, json, new UTF8Encoding(false));
                overlay.Save(pp, ImageFormat.Png);
                if (!string.IsNullOrEmpty(mtText)) File.WriteAllText(mp, mtText, new UTF8Encoding(false));
                else { try { if (File.Exists(mp)) File.Delete(mp); } catch (Exception) { } }
                lock (Gate)
                {
                    if (_idxBuilt)
                    {
                        bool dup = false;
                        for (int i = 0; i < _idx.Count; i++)
                        {
                            if (_idx[i].Hash == hash && string.Equals(_idx[i].KeyPart, keyPart, StringComparison.Ordinal)) { _idx[i].Path = jp; dup = true; break; }
                        }
                        if (!dup)
                        {
                            Idx ix = new Idx();
                            ix.Hash = hash;
                            ix.Path = jp;
                            ix.KeyPart = keyPart;
                            _idx.Add(ix);
                        }
                    }
                }
                TrimOld();
            }
            catch (Exception) { }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                DeleteDir(Dir);
                DeleteDir(LegacyDir);
                _idx.Clear();
                _idxBuilt = true;
            }
        }

        // 目录 → 内存索引：只做一次，之后命中和保存都只碰内存
        private static void BuildIndex()
        {
            _idx.Clear();
            _idxBuilt = true;
            try
            {
                if (!Directory.Exists(Dir)) return;
                string[] files = Directory.GetFiles(Dir, "h*.json");
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    int us = name.IndexOf('_');
                    if (us != 17 || name.Length < 26) continue;
                    ulong fh;
                    if (!ulong.TryParse(name.Substring(1, 16), System.Globalization.NumberStyles.HexNumber, null, out fh)) continue;
                    Idx ix = new Idx();
                    ix.Hash = fh;
                    ix.Path = files[i];
                    ix.KeyPart = name.Substring(us + 1);
                    _idx.Add(ix);
                }
            }
            catch (Exception) { }
        }

        private static void DeleteDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                // 先删文件，再删目录；个别文件被占用也不影响其他文件
                foreach (string f in Directory.GetFiles(dir))
                    try { File.Delete(f); } catch (Exception) { }
                foreach (string d in Directory.GetDirectories(dir))
                    try { Directory.Delete(d, true); } catch (Exception) { }
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        public static string Info()
        {
            try
            {
                if (!Directory.Exists(Dir)) return "0 张";
                string[] files = Directory.GetFiles(Dir, "*.png");
                long bytes = 0;
                for (int i = 0; i < files.Length; i++)
                    try { bytes += new FileInfo(files[i]).Length; } catch (Exception) { }
                if (bytes < 1024L * 1024L) return files.Length + " 张 / " + Math.Max(1, bytes / 1024) + " KB";
                return files.Length + " 张 / " + (bytes / 1024 / 1024) + " MB";
            }
            catch (Exception) { return "0 张"; }
        }

        private static void TrimOld()
        {
            try
            {
                string[] files = Directory.GetFiles(Dir, "*.json");
                int max = MaxEntries;
                if (files.Length <= max) return;
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });
                for (int i = 0; i < files.Length - max; i++)
                {
                    try { File.Delete(files[i]); } catch (Exception) { }
                    try { File.Delete(Path.ChangeExtension(files[i], ".png")); } catch (Exception) { }
                    try { File.Delete(Path.ChangeExtension(files[i], ".mt.txt")); } catch (Exception) { }
                }
                _idxBuilt = false;
            }
            catch (Exception) { }
        }

        private static string KeyHash(string s)
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h.ToString("x8");
        }

        private static Bitmap LoadUnlocked(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(raw))
                    return new Bitmap(ms);
            }
            catch (Exception) { return null; }
        }
    }
}
