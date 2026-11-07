using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
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
        private const int MaxEntries = 120;
        private const int MaxDistance = 3;

        public static string Dir
        {
            get
            {
                // 放当前文件夹（绿色版/便携），不再塞进 AppData
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shotcache");
            }
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
                + (AppConfig.AiLayout ? "1" : "0") + "|" + AppConfig.MtMaskAlpha
                + "|" + AppConfig.AiModel + (AppConfig.OcrFusion ? "|fuse" : "") + "|pv3";
        }

        public static ulong Hash(Bitmap bmp)
        {
            if (bmp == null) return 0;
            using (Bitmap small = new Bitmap(9, 8, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(bmp, new Rectangle(0, 0, 9, 8));
                }
                ulong h = 0;
                int bit = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        Color a = small.GetPixel(x, y);
                        Color b = small.GetPixel(x + 1, y);
                        int la = (a.R * 77 + a.G * 151 + a.B * 28) >> 8;
                        int lb = (b.R * 77 + b.G * 151 + b.B * 28) >> 8;
                        if (la > lb) h |= (1UL << bit);
                        bit++;
                    }
                return h;
            }
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
                string key = ConfigKey();
                string[] files = Directory.GetFiles(Dir, "h*.json");
                ulong bestH = 0;
                string best = null;
                int bestD = int.MaxValue;
                for (int i = 0; i < files.Length; i++)
                {
                    string name = Path.GetFileNameWithoutExtension(files[i]);
                    int us = name.IndexOf('_');
                    if (us != 17 || name.Length < 26) continue;
                    string keyPart = name.Substring(us + 1);
                    if (keyPart != KeyHash(key)) continue;
                    ulong fh;
                    if (!ulong.TryParse(name.Substring(1, 16), System.Globalization.NumberStyles.HexNumber, null, out fh)) continue;
                    int d = Distance(h, fh);
                    if (d < bestD) { bestD = d; bestH = fh; best = files[i]; }
                }
                if (best == null || bestD > MaxDistance) return false;
                string png = Path.ChangeExtension(best, ".png");
                if (!File.Exists(png)) return false;
                Entry e = new Entry();
                e.Hash = bestH;
                e.Json = File.ReadAllText(best, Encoding.UTF8);
                string mt = Path.ChangeExtension(best, ".mt.txt");
                if (File.Exists(mt)) { try { e.MtText = File.ReadAllText(mt, Encoding.UTF8); } catch (Exception) { } }
                e.Image = LoadUnlocked(png);
                if (e.Image == null) return false;
                // 二次校验：整屏平均亮度差太多，说明只是排版相近、内容已经变了
                if (Math.Abs(MeanLuma(crop) - MeanLuma(e.Image)) > 6.0) return false;
                entry = e;
                return true;
            }
            catch (Exception) { return false; }
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
                string baseName = "h" + hash.ToString("x16") + "_" + KeyHash(ConfigKey());
                string jp = Path.Combine(Dir, baseName + ".json");
                string pp = Path.Combine(Dir, baseName + ".png");
                string mp = Path.Combine(Dir, baseName + ".mt.txt");
                File.WriteAllText(jp, json, new UTF8Encoding(false));
                overlay.Save(pp, ImageFormat.Png);
                if (!string.IsNullOrEmpty(mtText)) File.WriteAllText(mp, mtText, new UTF8Encoding(false));
                else { try { if (File.Exists(mp)) File.Delete(mp); } catch (Exception) { } }
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
            }
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
                if (files.Length <= MaxEntries) return;
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b));
                });
                for (int i = 0; i < files.Length - MaxEntries; i++)
                {
                    try { File.Delete(files[i]); } catch (Exception) { }
                    try { File.Delete(Path.ChangeExtension(files[i], ".png")); } catch (Exception) { }
                }
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
